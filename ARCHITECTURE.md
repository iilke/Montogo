# Montogo — Architecture

A technical overview of how Montogo works end to end. This is the "why and how it fits together" document; [`DECISIONS.md`](DECISIONS.md) has the per-component detail and [`windows/Montogo.Protocol/PROTOCOL.md`](windows/Montogo.Protocol/PROTOCOL.md) is the exact wire format. Written for someone who knows the codebase but forgets the details.

---

## 1. Mental model

Montogo is a **one-directional, low-latency screen streamer** for a trusted LAN:

```
Windows PC  ── H.264 over encrypted UDP ──▶  MacBook (passive display)
```

Windows owns a *virtual* monitor (no physical panel), captures it, encodes it, and sends it. The Mac decodes and renders it full-screen. Input never flows back — the Mac is a screen, not a KVM. There is no audio. Everything runs over ordinary Wi-Fi with no server, no cloud, no pairing service.

Two design constraints drive most decisions:

- **Latency over quality.** A second monitor has to feel attached. The target is a glass-to-glass delay small enough that cursor motion feels direct. Measured Mac-side pipeline latency (reassemble → decode → render) is ~10 ms; the encoder adds one frame (~16 ms); the rest is network transit.
- **Trusted LAN, not the internet.** The threat model is "someone else on your Wi-Fi," not a global adversary. That justifies a short human-typeable secret and a simple PSK scheme instead of a full key exchange.

---

## 2. Two apps, no shared code

| | Windows | Mac |
|---|---|---|
| Language | C# / .NET 8 (`net8.0-windows`) | Swift / SwiftUI |
| Role | capture + encode + send | receive + decode + render |
| UI | WinForms tray icon | SwiftUI window |
| Native glue | CsWin32 (DXGI, D3D11, Media Foundation) | Darwin sockets, VideoToolbox, Metal, CryptoKit |

The two sides share **no code** — they share a **protocol spec** (`PROTOCOL.md`) that each implements against by hand. This is deliberate: a cross-platform binding layer would add far more complexity than the ~5 packet structs it would save, and both platforms are little-endian so the wire structs map cleanly to native types on each side. The cost is that the spec is the single source of truth and must be kept exact; the win is that each side uses its platform's best-fit native APIs directly.

The Windows app is split into focused projects so each concern is testable and replaceable in isolation:

```
Montogo.App        tray UI, lifecycle, handshake loop, wiring        (the orchestrator)
Montogo.Driver     virtual-display-rs CLI wrapper
Montogo.Capture    DXGI Desktop Duplication
Montogo.Encoding   H.264 via Media Foundation MFT
Montogo.Transport  UDP chunking + AES-GCM + LAN IP resolution
Montogo.Protocol   packet structs + constants + PROTOCOL.md (canonical spec)
Montogo.Tests      xUnit
```

The Mac app is one target, organized by folder: `Network/`, `Security/`, `Decode/`, `Render/`, `UI/`, `Diagnostics/`.

---

## 3. The pipeline — following one frame

```
 WINDOWS                                                     MAC
 ┌──────────────┐                                            ┌──────────────┐
 │ virtual disp │  virtual-display-rs driver                 │              │
 └──────┬───────┘                                            │              │
        │ DXGI Desktop Duplication                           │              │
 ┌──────▼───────┐                                            │              │
 │ DxgiCapture  │  BGRA frame + cursor composited            │              │
 └──────┬───────┘                                            │              │
        │ bounded channel (cap 2, DropOldest)                │              │
 ┌──────▼───────┐                                            │              │
 │ H264Encoder  │  BGRA→NV12, hardware H.264 (CBR)           │              │
 └──────┬───────┘                                            │              │
        │ FrameEncoded (Annex B access unit)                 │              │
 ┌──────▼───────┐   chunk ≤1400B, AES-256-GCM per chunk      ┌──────────────┐
 │ UdpSender    │ ─────────────  UDP :47921  ──────────────▶ │ UDPReceiver  │
 └──────────────┘                                            └──────┬───────┘
                                                                    │ decrypt + reassemble
                                                             ┌──────▼───────┐
                                                             │ FrameAssembler│
                                                             └──────┬───────┘
                                                                    │ complete frame → decode queue
                                                             ┌──────▼───────┐
                                                             │ H264Decoder  │  VideoToolbox (async)
                                                             └──────┬───────┘
                                                                    │ CVPixelBuffer (NV12)
                                                             ┌──────▼───────┐
                                                             │ VideoRenderer│  Metal YCbCr→RGB
                                                             └──────────────┘
```

### 3.1 Virtual display (`Montogo.Driver`)

There is no physical second monitor, so Montogo creates a virtual one via the third-party **virtual-display-rs** driver. `VirtualDisplayManager` shells out to `virtual-display-driver-cli.exe` (the driver exposes only a CLI, no SDK) to `remove-all` then `add 1920x1080@60` at startup, and `remove-all` on exit. Windows sees a real 1920×1080 @ 60 Hz output that DXGI can duplicate. The `VddUserSession` service must already be running; Montogo manages *monitors*, not the service.

### 3.2 Capture (`Montogo.Capture`)

`DxgiCapture` uses **DXGI Desktop Duplication** — the standard low-overhead screen-capture path. It runs on its own thread: `AcquireNextFrame` → `CopyResource` into a CPU-readable staging texture → `Map` → copy out BGRA (`DXGI_FORMAT_B8G8R8A8_UNORM`, handling row-pitch padding).

Two non-obvious behaviors:

- **Static-desktop re-emit.** DXGI only delivers a frame when the desktop *changes*. On an idle screen `AcquireNextFrame` times out forever — which would starve the stream and trip the Mac's 3 s watchdog. So on timeout the loop re-emits the previous frame paced to the target fps. The stream is always live.
- **Cursor compositing.** Desktop Duplication never draws the hardware cursor into the image; it reports position and a separate shape bitmap. The loop keeps `lastFrame` as the *clean* desktop and paints the cursor onto each emitted copy, so a cursor moving over a static desktop is redrawn at its new position rather than smeared. All three DXGI shape formats (color, masked-color, monochrome AND/XOR) are handled.

Frames flow into a **bounded channel (capacity 2, `DropOldest`)** that decouples the capture thread from encode/send: if the pipeline briefly falls behind, the oldest frame is dropped rather than building unbounded latency.

### 3.3 Encode (`Montogo.Encoding`)

`H264Encoder` wraps a **Media Foundation H.264 MFT**. At startup it enumerates hardware encoders with `MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, HARDWARE)` and uses the first one (NVENC / Quick Sync / AMF — whatever the GPU driver registered), falling back to the Microsoft software MFT. Hardware → 60 fps @ 10 Mbps; software → 30 fps @ 5 Mbps.

Per frame: **BGRA→NV12** color conversion (BT.601 limited range) on the CPU, written as an unsafe pointer loop parallelized over row-pairs — the naive managed version cost tens of ms/frame and sank the frame rate on its own. The NV12 buffer is fed to the MFT.

Two things that were hard-won:

- **Hardware MFTs are asynchronous transforms.** They must be unlocked (`MF_TRANSFORM_ASYNC_UNLOCK`) and then driven by their event queue: wait for `METransformNeedInput` before `ProcessInput`, drain on `METransformHaveOutput`. The software MFT uses the plain synchronous loop.
- **CBR rate control has to be forced.** `MF_MT_AVG_BITRATE` alone is a soft hint the NVENC MFT ignores for complex content (it will emit ~170 Mbps). Explicit `ICodecAPI` CBR + mean/max bitrate + a tight VBV buffer, applied *after* `SetOutputType`, actually caps per-frame size. The GOP is fixed at ~1 keyframe/sec on NVENC (it ignores GOP-size controls); a keyframe is *forced* on client (re)connect so a new viewer decodes immediately.

Output is an **Annex B access unit** (start-code-delimited NALs). For an IDR frame that is SPS + PPS + slice; for a P-frame it is (optionally an AUD) + slice.

### 3.4 Transport (`Montogo.Transport`)

`UdpSender` splits the encoded frame into **≤1400-byte plaintext chunks** (well under Ethernet MTU once the 28-byte header and 16-byte GCM tag are added), encrypts **each chunk** with AES-256-GCM, and sends them as UDP datagrams to the Mac on port 47921. Each chunk carries a header with a per-chunk `SequenceNum`, the `FrameId`, `ChunkIndex`/`ChunkTotal`, and an IDR flag.

Critical detail: **video is sent from the same socket that answered the handshake** (source port 47921). macOS's stateful firewall only passes inbound UDP that looks like a reply to the Mac's outbound handshake; video from an ephemeral source port is dropped silently. So `UdpSender` reuses the bound listener socket rather than opening its own.

### 3.5 Receive + decode + render (Mac)

`UDPReceiver` is a Swift `actor` owning **one non-blocking POSIX UDP socket** bound to 47921. A `DispatchSource` drains every queued datagram per wake-up into a batch and feeds it through an `AsyncStream` to a single high-priority pump task — strict FIFO into the actor. Order matters: out-of-order chunks completing frames out of order corrupt H.264 decode. Each chunk is decrypted (`StreamDecryptor`), and `FrameAssembler` groups chunks by `FrameId` until a frame is complete (or drops it after 100 ms).

A complete frame is dispatched to a **dedicated serial decode queue** (off the MainActor, which otherwise stalled decode behind UI work). `H264Decoder` splits the Annex B access unit into **all** its NALs, (re)builds the format description from SPS/PPS on IDR frames, and converts every NAL to length-prefixed AVCC for a `CMSampleBuffer` fed to an async `VTDecompressionSession`. VideoToolbox picks the hardware decoder. Decoded `CVPixelBuffer`s (NV12) go to `VideoRenderer`, a Metal `MTKView` whose fragment shader does YCbCr→RGB.

> The project's worst bug lived in this stage: the P-frame path once treated a multi-NAL access unit as a single NAL, so VideoToolbox silently rejected every P-frame and only keyframes rendered (~1 fps). The whole thing *looked* like a 170 Mbps encoder problem from the outside — the Mac's "frame size" metric was really just measuring the keyframes, the only frames that survived. It was found by instrumenting packet loss (0%) and decode submit/error counts (all P-frames erroring). Lesson baked into the code and `PROTOCOL.md` step 7.

---

## 4. The security chain

Everything derives from one short secret the user carries between the two apps.

```
8-char connection code  (e.g. 7X4K-9M2P)     ← displayed on Windows, typed on Mac
        │ base-32 decode (alphabet omits 0/O/1/I)
        ▼
5 raw bytes  =  40-bit shared secret (IKM)
        │ HKDF-SHA256, domain-separated by info string
        ├──▶ EncKey  = HKDF(ikm, info="montogo-enc-v1",  32B)   → AES-256-GCM
        └──▶ AuthKey = HKDF(ikm, info="montogo-auth-v1", 32B)   → HMAC-SHA256
```

- **Connection code** — 5 cryptographically random bytes generated once on Windows, persisted in `settings.json`, shown in the tray. 40 bits of entropy: brute-forcing it requires being on the LAN and hammering the UDP port ~10¹² times. Acceptable for a personal device; explicitly *not* internet-grade. Both sides derive identical keys because HKDF is deterministic.
- **Authentication** — the Mac proves it knows the secret without sending it: every `HandshakeRequest` carries `Token = HMAC-SHA256(AuthKey, ClientId)[0..15]`. Windows recomputes it and compares in constant time. A wrong code produces a wrong token and the handshake is dropped (and surfaced locally as "Wrong code" so a typo is visible).
- **Stream encryption** — every video chunk is AES-256-GCM sealed with `EncKey`. The 12-byte nonce is an **8-byte per-session random prefix** (generated fresh by `UdpSender` each run, delivered once in the `HandshakeResponse`) followed by the **4-byte little-endian `SequenceNum`**. The prefix is what makes it safe to reuse a persistent `EncKey` across restarts: each session gets a distinct nonce space, so `(key, nonce)` is never reused even though the counter restarts at 0. GCM also authenticates each chunk — a tampered or corrupt chunk fails the tag check and is dropped.

The whole scheme is a **pre-shared-key** design, not a key exchange. There is no forward secrecy and no protection against an attacker who already knows the code — both acceptable for "trusted people on my Wi-Fi." What it *does* buy: nobody without the code can authenticate, inject, or decrypt the stream.

**At rest,** the code is never stored in plaintext: Windows encrypts it with DPAPI (`ProtectedData`, current-user scope) in `settings.json`; the Mac keeps it in the **Keychain** (the non-sensitive PC IP goes to `UserDefaults`). The code can be rotated from the Windows tray ("Reset connection code"), which regenerates the secret, tears down the live stream, and forces the Mac to re-pair. Rejected-token handshakes are logged (last 10) and viewable from the tray, so a wrong code or a probing device is visible rather than silent.

---

## 5. Networking design

- **One UDP port, 47921, on both sides.** Windows binds it (to a specific private-range LAN interface via `LanIpResolver`, not `0.0.0.0`) and both handshake and video flow over it. The Mac binds it too and receives everything on one socket. Connectionless UDP is the right fit — dropped frames should be skipped, not retransmitted, for a live stream — and a single socket avoids the source-port firewall problem (§3.4).
- **Discovery is manual.** The user reads the LAN IP off the tray and types it on the Mac. mDNS is the obvious next feature; until then a DHCP reservation keeps the IP stable.
- **Handshake + liveness.** The Mac retries `HandshakeRequest` every 500 ms until it gets a response, then sends a `Heartbeat` every second. The video stream itself is the Mac's liveness signal (the static re-emit guarantees packets keep arriving); a 3 s watchdog with no valid packet declares the connection lost and restarts handshaking. Windows currently ignores heartbeats.
- **Reconnect.** Windows validates the token on *every* request. A valid token while already streaming (a relaunched or reconnecting Mac) triggers a **re-handshake**: re-target the sender, resend the response with the existing nonce prefix, and force a keyframe — no pipeline restart. This replaced an earlier "drop all handshakes once running" rule that left relaunched Macs stuck forever.
- **Auto-reconnect (Mac).** After a successful handshake the Mac saves the pairing (code → Keychain, IP → `UserDefaults`) and, on next launch, skips setup and connects immediately. If it doesn't connect within ~8 s it falls back to the setup screen with the values pre-filled; "Forget This Connection" clears the pairing.

---

## 6. Why each major component

| Component | Choice | Why |
|---|---|---|
| Virtual display | virtual-display-rs (external driver) | Only practical way to get a headless monitor Windows/DXGI treat as real. Managed via its CLI (no SDK exists). |
| Capture | DXGI Desktop Duplication | The standard low-overhead GPU capture API; gives us the frame on the GPU and cursor metadata. |
| Color convert | CPU BGRA→NV12 (parallel, unsafe) | Encoder wants NV12; doing it on CPU keeps the encoder path simple. Parallelized because it was the frame-rate bottleneck. |
| Encode | Media Foundation H.264 MFT | Ships with Windows; `MFTEnumEx` transparently gives the GPU's hardware encoder with a guaranteed software fallback. |
| Transport | Raw UDP + AES-256-GCM | Live video wants "skip, don't retransmit." TCP's reliability is the wrong tradeoff. GCM adds confidentiality + integrity per chunk cheaply. |
| Key handling | PSK + HKDF + HMAC + GCM | Matches the trusted-LAN threat model without the complexity of a key exchange; one typeable code drives everything. |
| Mac socket | Raw Darwin POSIX UDP socket | Network.framework's connection-oriented `NWConnection` fought the connectionless single-socket design; a plain socket is simpler and gives full control of ordering and draining. |
| Decode | VideoToolbox (async) | The Mac's hardware H.264 decoder; ~6 ms/frame with the real-time hint. |
| Render | Metal `MTKView` + YCbCr shader | Zero-copy from the decoder's `CVPixelBuffer` via the CoreVideo Metal texture cache; the shader does color conversion on the GPU. |

---

## 7. Threading model

**Windows** — several cooperating threads, each single-purpose:
- Capture thread (`DxgiCapture`) → bounded channel.
- Pipeline task drains the channel → `H264Encoder.SubmitFrame` (encode) → `FrameEncoded` fires synchronously → `UdpSender.SendFrameAsync` (chunk + encrypt + send). Encode and send are serial on this one task, so `AesGcm` (not thread-safe) and the sender's sequence counter need no locking.
- Handshake loop task listens on the UDP socket, concurrently with the pipeline sending on the same socket (full-duplex).
- WinForms UI thread owns the tray; status updates marshal to it.

**Mac** — actor + queues:
- `DispatchSource` (userInteractive) drains the socket → `AsyncStream`.
- One `.high` pump task consumes the stream on the `UDPReceiver` actor: decrypt + reassemble.
- A dedicated serial decode queue runs VideoToolbox off the MainActor.
- VideoToolbox's async callback → renderer; `MTKView` draws on the main thread.

---

## 8. How failures surface

The system is built so silent failure is rare — most problems announce themselves:

- **Driver missing / display absent** → tray error, pipeline never starts.
- **Capture thread dies** (e.g. `DXGI_ERROR_ACCESS_LOST`) → `CaptureFailed` → tray "Capture error".
- **Encode/send exception** → caught in the pipeline task → tray "Pipeline error".
- **Wrong connection code** → Windows tray "Wrong code from <ip>"; the Mac just keeps retrying.
- **Wrong IP / network isolation** → the Mac's handshakes never arrive; it sits on "Connecting…" and the 3 s watchdog keeps it retrying.
- **Corrupt/lost chunk** → GCM tag fails or the frame never completes → the frame is dropped; the next keyframe resyncs.
- **Per-stage latency / loss** → the Mac's `FrameTimingLog` prints reassembly/queue/decode/render times, fps, packet-loss %, and decode error counts every 60 frames (toggle with `FrameTimingLog.enabled`).
