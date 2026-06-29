# Montogo — Architecture & Decision Record

Developer reference for continuing this project. Written to restore context after a context-window reset.

---

## Project Overview

Montogo turns a MacBook into a second monitor for a Windows PC over local WiFi/LAN.

- **Windows side:** Captures a virtual display, H.264-encodes it, sends it over UDP.
- **Mac side:** Receives and decodes the stream, renders it full-screen via Metal. Passive receiver only — no input is sent back.
- **No hardware required** beyond the two machines being on the same LAN.
- **No audio.** Audio stays on the Windows machine.

---

## Repository Structure

```
Montogo/
  windows/              — entire Windows application
    Montogo.slnx        — solution file (.slnx format, .NET 10 SDK default)
    Montogo.App/        — system tray entry point
    Montogo.Driver/     — virtual display driver management
    Montogo.Capture/    — DXGI Desktop Duplication
    Montogo.Encoding/   — H.264 MediaFoundation encoder
    Montogo.Transport/  — UDP chunked sender
    Montogo.Protocol/   — shared packet structs + canonical spec
    Montogo.Tests/      — xUnit test project
  mac/                  — Swift app (not yet started)
  DECISIONS.md          — this file
```

---

## Windows App: Tech Stack

| Concern | Choice | Reason |
|---|---|---|
| Runtime | .NET 8 (`net8.0-windows`) | Stable LTS; SDK is .NET 10 but TFM is net8 |
| UI | WinForms (`NotifyIcon` + `ApplicationContext`) | Tray-only app; WPF is overkill |
| Win32 P/Invoke | CsWin32 0.3.298 (source generator) | Generates managed COM wrappers; no manual vtable structs |
| Test framework | xUnit 2.9.3 | Standard; nothing special needed |

### CsWin32 usage pattern

- Each project that needs Win32 APIs has `NativeMethods.txt` listing the required types/functions.
- CsWin32 generates **managed `[ComImport]` interfaces**, not unsafe COM vtable structs.
- `AllowUnsafeBlocks` is enabled in Capture (needed for `pData` pointer access and `AcquireNextFrame` fixed-pointer variant).
- `EmitCompilerGeneratedFiles` was enabled temporarily to inspect generated signatures; can be left on or removed.
- COM QI is done via C# casting: `(IDXGIOutput1)output`, `(IDXGIDevice)device`, etc. No `QueryInterface` calls.
- `Marshal.ReleaseComObject` is called explicitly on all D3D11/DXGI objects in `DxgiCapture.CaptureLoop` (single `finally` block, reverse acquisition order). This frees GPU resources immediately when the pipeline stops rather than waiting for GC finalization.
- `IUnknown` must NOT be in `NativeMethods.txt` — CsWin32 warns it is implicit in the runtime (PInvoke003).

---

## Virtual Display Driver

**Driver used:** [virtual-display-rs](https://github.com/MolotovCherry/virtual-display-rs)

The driver creates virtual monitors visible to Windows and DXGI. The user installs the driver separately; Montogo only manages the virtual monitors, not the driver service itself.

### Installation (user's machine)

```
virtual-display-driver-cli.exe install   # installs the driver
virtual-display-driver-cli.exe start     # starts VddUserSession service
```

The `VddUserSession` Windows service must already be running before Montogo starts.

### CLI commands used by Montogo

```
virtual-display-driver-cli.exe add <WxH>@<fps>   # e.g. add 1920x1080@60
virtual-display-driver-cli.exe remove <id>        # id from list output
virtual-display-driver-cli.exe remove-all
virtual-display-driver-cli.exe persist            # survive reboot
virtual-display-driver-cli.exe list               # stdout: ANSI-decorated monitor list
```

### CLI path resolution (two-step, in order)

1. `driver\virtual-display-driver-cli.exe` next to the running exe (deployment default).
2. `DriverCliPath` key in `%APPDATA%\Montogo\settings.json` (user override).

Implemented in `Montogo.App/CliPathResolver.cs`. `DriverLocator.TryFindRelative()` in `Montogo.Driver` handles step 1 and is reusable without the App layer.

---

## Montogo.Driver

**What it does:** wraps the CLI via `Process`/`ProcessStartInfo`. Does not interact with the driver service.

**Key types:**

- `VirtualDisplayManager(string cliPath)` — `AddMonitorAsync`, `RemoveMonitorAsync`, `RemoveAllAsync`, `PersistAsync`, `ListMonitorsAsync`. All async, accept `CancellationToken`.
- `MonitorInfo` — record parsed from `list` output (`Id`, `Width`, `Height`, `RefreshRate`). Static `ParseCliOutput(string)` strips ANSI escape codes, uses a simple state machine.
- `DriverLocator` — static helper for CLI path discovery.

**Why shell out instead of a native SDK?** virtual-display-rs exposes its IPC only through the CLI. No .NET SDK exists for it.

**Stdout/stderr are both read concurrently** before `WaitForExitAsync` to avoid deadlock when either pipe buffer fills.

---

## Montogo.App

**Entry point:** `Program.cs` → `Application.Run(new TrayApplicationContext())`.

**`TrayApplicationContext`:** WinForms `ApplicationContext` subclass. Owns the `NotifyIcon` and wires the full pipeline. Lifecycle on startup (`InitAsync`):

1. Resolve the CLI path via `CliPathResolver`.
2. `RemoveAllAsync` + `AddMonitorAsync(1920, 1080, 60)` — app owns the virtual display lifecycle.
3. Poll DXGI for up to 3 s (20 × 150 ms) for the new display to appear.
4. Probe for a hardware H.264 encoder (`H264Encoder.IsHardwareEncoderAvailable()`); set fps/bitrate accordingly.
5. Enter `HandshakeLoopAsync` — bind UDP to the LAN IP, wait for the Mac.
6. On a valid authenticated handshake: create `UdpSender`, send `HandshakeResponse` (with nonce prefix), call `StartPipeline`.

On exit (`ExitApplication`): cancel the CTS, stop capture, complete the channel, drain the pipeline task (2 s timeout), dispose encoder and sender, call `RemoveAllAsync`.

**`AppSettings`:** serialized to/from `%APPDATA%\Montogo\settings.json` via `System.Text.Json`. Two fields: `DriverCliPath` (nullable, falls back to relative path) and `SharedSecret` (the 8-character connection code; generated once on first launch, never changes unless the file is deleted).

**`SecurityContext`:** created at startup from the connection code. Derives `EncKey` and `AuthKey` via HKDF-SHA256, constructs the `AesGcm` cipher used by `UdpSender`. Exposes `ConnectionCode` (displayed in the tray menu), `Cipher` (given to `UdpSender`), and `ValidateToken` (called in the handshake loop). Implements `IDisposable` to release the `AesGcm` instance.

**`CliPathResolver`:** resolution order: (1) `driver\virtual-display-driver-cli.exe` next to the exe, (2) `DriverCliPath` in settings. Validates that the settings path ends with `DriverLocator.CliName` (case-insensitive) before accepting it.

**DoS protection:** once the pipeline is running, all further `HandshakeRequest` packets are dropped silently (`if (_capture is not null) continue`). A new session requires restarting the app.

---

## Montogo.Capture

### Design

- **Display selection:** `CaptureOptions.OutputIndex` is a global index matching the order from `DxgiCapture.EnumerateDisplays()`. Enumeration walks adapters via `IDXGIFactory1.EnumAdapters1` (outer), then outputs via `IDXGIAdapter.EnumOutputs` (inner), assigning sequential global indices. Both methods have `[PreserveSig]` and return `HRESULT` directly — check `.Value == 0x887A0002` for end-of-enumeration.
- **Frame delivery:** `Action<CapturedFrame>? FrameCaptured` callback, fired on the capture thread. Consumer must be fast or hand off immediately.
- **Frame format:** raw BGRA bytes (`DXGI_FORMAT_B8G8R8A8_UNORM`) via CPU staging texture. Row-pitch padding is handled (copies row-by-row if `RowPitch != width * 4`).
- **Cursor-only frames are skipped:** `DXGI_OUTDUPL_FRAME_INFO.LastPresentTime == 0` means no new desktop content; `ReleaseFrame` is still called but the frame is not emitted.
- **`[SupportedOSPlatform("windows8.0")]`** on `DxgiCapture` — DXGI Desktop Duplication requires Win 8+.

### Key API notes (CsWin32 0.3.298)

```csharp
// Factory — no unsafe, generic helper:
PInvoke.CreateDXGIFactory1<IDXGIFactory1>(out IDXGIFactory1 factory).ThrowOnFailure();

// D3D11 device — pass non-null adapter, DriverType MUST be UNKNOWN:
PInvoke.D3D11CreateDevice(
    (IDXGIAdapter)adapter, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN, default,
    D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
    [D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0],
    PInvoke.D3D11_SDK_VERSION,
    out ID3D11Device device, out ID3D11DeviceContext context).ThrowOnFailure();

// QI via C# cast:
var output1 = (IDXGIOutput1)output;

// GetDesc returns struct directly, no args:
DXGI_OUTPUT_DESC desc = output.GetDesc();
string name = desc.DeviceName.ToString(); // __char_32.ToString() strips null terminators

// DuplicateOutput — device parameter is object (MarshalAs IUnknown):
output1.DuplicateOutput(device, out IDXGIOutputDuplication duplication);

// AcquireNextFrame — extension method with out params (no unsafe needed):
duplication.AcquireNextFrame(timeoutMs, out DXGI_OUTDUPL_FRAME_INFO info, out IDXGIResource resource);
// Throws COMException; check ex.HResult for:
//   0x887A0027 = DXGI_ERROR_WAIT_TIMEOUT  → continue loop
//   0x887A0026 = DXGI_ERROR_ACCESS_LOST   → display mode changed, need recreate

// Staging texture — CPUAccessFlags is D3D11_CPU_ACCESS_FLAG enum (not uint):
var stageDesc = new D3D11_TEXTURE2D_DESC {
    ..., Usage = D3D11_USAGE.D3D11_USAGE_STAGING, BindFlags = 0,
    CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ, MiscFlags = 0
};
device.CreateTexture2D(in stageDesc, (D3D11_SUBRESOURCE_DATA?)null, out ID3D11Texture2D staging);

// Map/Unmap — extension methods, take ID3D11Resource (must cast from ID3D11Texture2D):
context.Map((ID3D11Resource)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, out D3D11_MAPPED_SUBRESOURCE mapped);
// mapped.pData is unsafe void* — needs unsafe block
context.Unmap((ID3D11Resource)staging, 0);

// CopyResource — destination first, source second:
context.CopyResource((ID3D11Resource)staging, (ID3D11Resource)desktop);
```

---

## Montogo.Protocol

Canonical spec lives in `windows/Montogo.Protocol/PROTOCOL.md`. The Swift side implements against this spec manually (no code sharing between platforms).

**Current version: 2** (v2 added PSK auth token + AES-256-GCM encryption). Windows rejects v1 packets — the version byte is checked in `IsHandshakeRequest` before the packet is processed.

### Summary

| Aspect | Value |
|---|---|
| Transport | UDP unicast |
| Byte order | Little-endian throughout |
| Windows listen port | 47921 (video stream + handshake) |
| Discovery | mDNS service type `_montogo._udp.local.` |
| Max chunk payload | 1400 bytes plaintext (wire payload = plaintext + 16-byte GCM tag) |

### Packet types

| Type | Value | Direction |
|---|---|---|
| VideoChunk | 0x01 | Windows → Mac |
| Heartbeat | 0x10 | Both |
| HandshakeRequest | 0x11 | Mac → Windows |
| HandshakeResponse | 0x12 | Windows → Mac |

**Common 4-byte header prefix:** Magic `0x474D` ("GM") + Version (1 byte) + PacketType (1 byte).

**VideoChunk header:** 28 bytes. `PayloadLength` = ciphertext length + 16 (GCM tag). `SequenceNum` is also the nonce base (low 4 bytes). `Flags` bit 0 = IDR frame. Chunk 0 of every IDR frame carries SPS+PPS NALUs followed by the IDR slice.

**HandshakeRequest:** 40 bytes. Includes `ClientId` (random UUID per session) and `Token` (16-byte HMAC-SHA256 truncated — see Security section below). Windows validates the token before accepting the handshake.

**HandshakeResponse:** 36 bytes. Echoes `ClientId`, provides `NegotiatedVersion`, `DisplayWidth`, `DisplayHeight`, `TargetFps`, and `NoncePrefix` (8-byte per-session random prefix for GCM nonces).

**Missing chunks:** silently drop the entire frame after 100 ms. No retransmit; next IDR resyncs decoder.

**C# packet structs** live in `Montogo.Protocol/Packets.cs`. They use `[StructLayout(LayoutKind.Sequential, Pack=1)]` and match the wire format byte-for-byte.

---

## Security

Full spec in `PROTOCOL.md`. Summary of the implementation:

### Connection code

Windows generates **5 cryptographically random bytes** on first launch (`RandomNumberGenerator.GetBytes(5)`) and encodes them as an **8-character base-32 string** using alphabet `23456789ABCDEFGHJKLMNPQRSTUVWXYZ` (omits `0`/`O`/`1`/`I` to prevent transcription errors). This is the shared secret — the user types it into the Mac app once. Stored in `AppSettings.SharedSecret`; displayed in the tray menu as `Code: XXXX-XXXX`.

Entropy is 40 bits (~1 trillion combinations). This is acceptable for a personal LAN device since physical LAN access and 40-bit brute-force resistance is the threat model. Documented explicitly in `PROTOCOL.md`.

### Key derivation

`SecurityContext` derives two 32-byte keys from the 5-byte IKM via HKDF-SHA256 (no salt; domain-separated by info string):

```
EncKey  = HKDF-SHA256(ikm, info="montogo-enc-v1",  length=32)
AuthKey = HKDF-SHA256(ikm, info="montogo-auth-v1", length=32)
```

### Authentication

Every `HandshakeRequest` carries a 16-byte token: `HMAC-SHA256(AuthKey, ClientId.bytes)[0..15]`. Windows computes the expected token and compares via `CryptographicOperations.FixedTimeEquals` (constant-time). Packets with invalid tokens are dropped silently.

### Stream encryption

Every `VideoChunk` payload is encrypted with AES-256-GCM (16-byte tag). Nonce = 8-byte per-session random prefix + 4-byte LE `SequenceNum`. The prefix is generated fresh by `UdpSender` at construction (`RandomNumberGenerator.GetBytes(8)`) and sent to the Mac in `HandshakeResponsePacket.NoncePrefix`. This prevents cross-session nonce reuse even though `EncKey` is derived from the stored connection code and persists across restarts.

### LAN binding

`LanIpResolver.GetLanAddress()` selects the most appropriate local IPv4 address: must be `OperationalStatus.Up`, non-loopback, non-virtual (filters Hyper-V, WSL, VirtualBox by NIC name/description), and in a private range (10/8, 172.16/12, 192.168/16). Falls back to `IPAddress.Any` with a tray warning if no private LAN address is found.

---

## Montogo.Encoding

H.264 encoder via MediaFoundation MFT with a hardware GPU fallback chain. `IsHardwareEncoderAvailable()` is called at startup (before pipeline start) to decide fps and bitrate. The result determines what `TrayApplicationContext` advertises in the `HandshakeResponse`.

| Path | Encoder | fps | Bitrate |
|---|---|---|---|
| Hardware | NVENC → Quick Sync → AMF (first available) | 60 | 10 Mbps |
| Software fallback | Microsoft H.264 MFT (always present on Win 7+) | 30 | 5 Mbps |

### API surface

```csharp
// Probe before constructing EncoderOptions — result affects fps/bitrate choice
bool hw = H264Encoder.IsHardwareEncoderAvailable(); // does its own MFStartup/MFShutdown

var enc = new H264Encoder(new EncoderOptions(Width, Height, Fps, BitrateBps));
enc.FrameEncoded += frame => { /* frame.Data, frame.IsKeyFrame, frame.TimestampUs */ };
enc.Initialize();
bool isHW = enc.IsHardwareAccelerated; // true if a GPU encoder was selected
enc.SubmitFrame(bgraSpan, timestampUs); // synchronous; fires FrameEncoded inline
enc.Dispose();
```

### Key decisions

- **Hardware fallback chain:** `CreateEncoder` tries each CLSID in order (NVENC, Quick Sync, AMF) via `Type.GetTypeFromCLSID` + `Activator.CreateInstance` + `SetupTypesOn`. If the MFT instantiates but rejects the media types (wrong driver, unsupported resolution), `SetupTypesOn` throws and the next candidate is tried. Microsoft software MFT is the guaranteed final fallback.
- **MFT instantiation via CLSID:** avoids `MFTEnumEx` and the `IMFActivate_unmanaged**` complexity. One CLSID probe = one `try/catch`; cheap enough to do at startup.
- **Type negotiation order:** output type (H.264) must be set before input type (NV12). MFT rejects the reverse order.
- **Color conversion:** BGRA→NV12 in C# on CPU (BT.601 limited-range integer math). UV plane is sampled from the top-left pixel of each 2×2 block (fast; good enough for H.264 at screen-sharing quality).
- **CsWin32 pattern:** `IMFAttributes_Extensions` provides managed `(in Guid key, ...)` wrappers for `SetGUID`/`SetUINT32`/`SetUINT64` on both `IMFMediaType` and `IMFSample` (both inherit `IMFAttributes`). Lock/Unlock via `IMFMediaBuffer_Extensions`.
- **Output drain:** after each `ProcessInput`, loop `ProcessOutput` until `COMException(0xC00D6D72)` (`MF_E_TRANSFORM_NEED_MORE_INPUT`). `pSample = null` in `MFT_OUTPUT_DATA_BUFFER` tells the MFT to allocate its own output buffer.
- **Keyframe detection:** `outSample.GetUINT32(MFSampleExtension_CleanPoint)` on the output sample; wrapped in try-catch since the attribute is absent on non-IDR frames.

---

## Montogo.Transport

UDP chunked sender. Takes an encoded H.264 frame, splits it into ≤1400-byte plaintext chunks, encrypts each with AES-256-GCM, and sends to the Mac on port 47921.

### API surface

```csharp
// cipher comes from SecurityContext.Cipher; must be created before HandshakeResponse is sent
var sender = new UdpSender(AesGcm cipher);
ulong prefix = sender.NoncePrefix; // include in HandshakeResponsePacket.NoncePrefix
sender.SetTarget(new IPEndPoint(macIp, ProtocolConstants.VideoPort));
await sender.SendFrameAsync(frame.Data, frameId, timestampUs, isIdr, ct);
sender.Dispose();
```

### Key decisions

- **Single rented buffer per frame:** `ArrayPool<byte>.Shared.Rent(HeaderSize + MaxChunkPayload + GcmTagSize)` allocated once per `SendFrameAsync`, reused across all chunks, returned in `finally`.
- **Span-across-await:** C# 12 forbids ref-struct locals across `await` points. All `Span<byte>` usage (header write, encrypt call) lives in a synchronous `BuildAndEncrypt` helper; `SendFrameAsync` awaits only the `UdpClient.SendAsync` call.
- **`MemoryMarshal.Write`:** writes the `VideoChunkHeader` struct directly into the rented buffer. Safe because the struct is unmanaged `Pack=1` sequential.
- **Nonce prefix:** 8 random bytes generated at `UdpSender` construction (`RandomNumberGenerator.GetBytes(8)`). Exposed as `NoncePrefix` (ulong LE). The caller must read this and include it in `HandshakeResponsePacket.NoncePrefix` before calling `SetTarget`. Per-session randomness eliminates cross-session nonce reuse even though `EncKey` is derived from the stored connection code.
- **Nonce per chunk:** `nonce[0..7]` = prefix (set once per `SendFrameAsync`), `nonce[8..11]` = `SequenceNum` (LE uint32, updated in `BuildAndEncrypt`). The 32-bit counter wraps after ~4.3 billion chunks; at 60 fps with ~15 chunks/frame this takes ~55 days — no practical risk.
- **`AesGcm` thread safety:** `AesGcm` is not thread-safe. `UdpSender` must be called from a single thread/task. In the pipeline this is guaranteed: `FrameEncoded` fires synchronously on the pipeline task thread via `.GetAwaiter().GetResult()`.
- **Sequence number:** plain `uint _sequenceNum++` — no atomics needed; single-producer pipeline.
- **Destination IP:** `SetTarget` must be called before sending. In the pipeline, the IP comes from the Mac's `HandshakeRequest` UDP source address.

`LanIpResolver` (also in `Montogo.Transport`) provides `GetLanAddress()` — see the Security section above.

---

## Input routing — deliberately absent

**The UDP channel is one-directional: Windows → Mac only.** The Mac is a passive display; the Windows mouse and keyboard remain on Windows.

Input routing (Mac → Windows via `SendInput`) was removed for two reasons:

1. **Security:** `SendInput` can inject arbitrary keystrokes and mouse events. Any process on the LAN that can reach the Windows UDP port could control the machine. Eliminating the listener eliminates this attack surface entirely — there is no port to exploit.
2. **Scope:** Montogo is a second monitor, not a KVM. The user does not want to route input.

`Montogo.Input` was deleted from the solution. `MouseEvent` (0x02), `KeyEvent` (0x03), and `InputPort` (47922) were removed from the protocol. The Mac app will never send anything to Windows.

---

## Montogo.Tests

Single xUnit project referencing both `Montogo.Driver` and `Montogo.Capture`. Currently covers:

- `MonitorParserTests` — `MonitorInfo.ParseCliOutput` parsing logic (no real hardware needed).
- `VirtualDisplayManagerTests` — integration tests against the real CLI (require driver installed + service running).
- `DxgiCaptureTests` — `DxgiCapture.EnumerateDisplays()` sanity checks (require a physical display).

Test classes that call DXGI APIs are attributed `[SupportedOSPlatform("windows8.0")]`.

---

## Decisions Not Yet Made

- **Mac rendering:** Metal or just an `AVSampleBufferDisplayLayer` on top of a full-screen window.
- **mDNS discovery implementation:** Bonjour on Mac, `DnsServiceRegister` or a library on Windows. Currently the Mac needs to know the Windows IP manually.
- **Installer / distribution:** how the driver CLI gets bundled with the Windows app.
- **ACCESS_LOST recovery:** `DxgiCapture` currently throws on `DXGI_ERROR_ACCESS_LOST` (display mode change). Production code should catch this, release and recreate the duplication object, and continue streaming.
- **Mac app:** not started. PROTOCOL.md has the full implementation checklist for the Swift side.
