# Montogo

**Turn a MacBook into a wireless second monitor for a Windows PC — over plain Wi-Fi, no cables, no capture card, no extra hardware.**

<!-- HERO PHOTO — replace this block with your setup photo, e.g. docs/setup.jpg -->
> 📸 **Setup photo** _(coming soon)_ — _a shot of the Windows PC with the MacBook running as its second screen; drop it in at `docs/setup.jpg`._

Windows creates a virtual 1080p60 display, captures it, hardware-encodes it as **HEVC (H.265)**, encrypts every packet, and streams it over UDP to the Mac, which decrypts, hardware-decodes, and renders it full-screen with Metal. The link is one-directional — Windows sends pixels, the Mac is a passive screen. Keyboard, mouse, and **audio stay on Windows**: only video is streamed, so sound from a window you move onto the Mac still plays from the Windows PC's own speakers/output.

<!-- DIAGRAM 1 — replace this block with docs/diagrams/system-architecture.png -->
> 🖼️ **System architecture** _(diagram coming soon)_
> _Will show: the Windows PC (virtual display → capture → HEVC encode → per-packet encrypt → UDP) streaming one-directionally over the LAN to the Mac (receive → decrypt → HEVC decode → Metal render). Handshake + link-quality feedback flow back Mac → Windows on the same socket._

---

## Highlights

- **HEVC (H.265), hardware end-to-end** — NVENC/Quick Sync/AMF encode on Windows, VideoToolbox decode on the Mac. ~2× the efficiency of H.264, so more picture fits a Wi-Fi link.
- **Low latency by design** — synchronous decode, vsync rendering, and adaptive pacing keep it responsive on a healthy Wi-Fi link.
- **Encrypted & authenticated** — AES-256-GCM on every packet, an authenticated ephemeral-ECDH handshake, and **forward secrecy** (see [Security](#security)).
- **Adapts to the link** — the bitrate learns your Wi-Fi's real capacity and rides it; a lost frame re-syncs in ~1 round-trip instead of freezing for a second.
- **Zero cloud** — no server, no account, no pairing service. Two apps on one LAN and a short code you type once.

---

## How it works

One frame's journey from the Windows desktop to the Mac screen:

<!-- DIAGRAM 2 — replace this block with docs/diagrams/frame-pipeline.png -->
> 🖼️ **Frame lifecycle** _(diagram coming soon)_
> _Will show the pipeline stages: virtual display → DXGI Desktop Duplication (BGRA + cursor) → HEVC encode (ARGB32 straight to NVENC, CBR) → chunk ≤1400 B + Reed–Solomon FEC + AES-256-GCM → UDP → reassemble/FEC-recover → decrypt → VideoToolbox decode → Metal YCbCr→RGB render._

1. **Virtual display** — there's no physical second monitor, so Montogo creates one with the [virtual-display-rs](https://github.com/MolotovCherry/virtual-display-rs) driver. Windows sees a real 1920×1080 @ 60 Hz output.
2. **Capture** — DXGI Desktop Duplication grabs the frame and composites the cursor. On a static desktop the last frame is re-emitted at the target fps so the stream never stalls; capture is rate-capped so fast motion can't flood the encoder.
3. **Encode** — a Media Foundation HEVC MFT (the GPU's hardware encoder) encodes the raw DXGI bytes directly, in CBR with a tight buffer so each frame fits the bitrate budget.
4. **Transport** — each frame is split into ≤1400-byte chunks, protected with Reed–Solomon FEC parity, and AES-256-GCM-encrypted per chunk (the chunk header is authenticated too). Everything goes over a single UDP port.
5. **Receive & decode** — the Mac reassembles chunks (rebuilding any lost by Wi-Fi from FEC), decrypts, and feeds VideoToolbox's hardware HEVC decoder in strict order.
6. **Render** — decoded frames go straight to a Metal view; the fragment shader does YCbCr→RGB on the GPU.

### Adapting to the network

<!-- DIAGRAM 4 — replace this block with docs/diagrams/adaptive-bitrate.png -->
> 🖼️ **Adaptive bitrate loop** _(diagram coming soon)_
> _Will show the control loop: Mac measures loss + reports it → Windows AIMD backs off / probes up, plus a send-queue congestion signal the loss path can't see, converging on a learned "soft ceiling" near the link's real capacity._

Wi-Fi capacity swings, so the bitrate isn't fixed. The Mac reports packet loss a few times a second; Windows also watches its own send queue (which reveals congestion the Mac can't see, since a locally-dropped frame shows up as 0% wire loss). The encoder converges the bitrate just under the link's real capacity instead of repeatedly overshooting — and when a frame is lost, the Mac asks for an immediate keyframe so a hiccup is a blink, not a freeze.

---

## Security

Montogo runs on your local network, and the link is hardened with modern cryptography — an authenticated ephemeral key exchange, forward secrecy, and AES-256-GCM on every packet.

<!-- DIAGRAM 3 — replace this block with docs/diagrams/security-handshake.png -->
> 🖼️ **Pairing & handshake** _(diagram coming soon)_
> _Will show the sequence: Windows shows a 13-char code → user types it on the Mac → both derive an HMAC auth key from it → Mac and Windows exchange ephemeral P-256 public keys, each message HMAC-authenticated → both derive a fresh AES-256-GCM session key from the ECDH shared secret → encrypted video flows._

- **Pairing code** — a 13-character code (64-bit secret, e.g. `7X4K-9M2P-QRST`) shown on Windows and typed on the Mac once. It only **authenticates** the handshake; it never encrypts the video directly.
- **Ephemeral key exchange** — every session runs an authenticated **P-256 ECDH** exchange; the AES-256-GCM video key comes from that shared secret, not the code. This gives **forward secrecy**: traffic captured today can't be decrypted later even if the code leaks, and a passive eavesdropper can't recover the key at all.
- **Authenticated everything** — both handshake messages are HMAC-SHA256-authenticated (so a spoofed or replayed handshake is useless), and every video chunk is AES-256-GCM sealed with its header authenticated as associated data (so routing/reassembly fields can't be tampered with undetected).
- **At rest** — the code is stored encrypted: Windows DPAPI (current-user) and the macOS Keychain. It can be rotated from the Windows tray.

> See [`windows/Montogo.Protocol/PROTOCOL.md`](windows/Montogo.Protocol/PROTOCOL.md) for the exact wire format (protocol v7).

---

## Requirements

**Windows**
- Windows 10 or 11
- A GPU with a **hardware HEVC encoder** (NVIDIA NVENC, Intel Quick Sync, or AMD AMF) — required; there is no software fallback.
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (or newer) to build
- The [virtual-display-rs](https://github.com/MolotovCherry/virtual-display-rs) driver installed, with its `VddUserSession` service running

**Mac**
- macOS 13 (Ventura) or newer, with a VideoToolbox HEVC decoder (any modern Mac)
- Xcode to build

**Network**
- Both machines on the **same Wi-Fi / LAN**, able to reach each other directly (guest networks and AP/client isolation will block it). **5 GHz is strongly recommended** — a stable, higher-capacity link is the single biggest factor in picture quality and smoothness.

---

## Quick start

### Windows
1. Install [virtual-display-rs](https://github.com/MolotovCherry/virtual-display-rs) and start its service:
   ```
   virtual-display-driver-cli.exe install
   virtual-display-driver-cli.exe start
   ```
2. Put `virtual-display-driver-cli.exe` in `windows/Montogo.App/driver/` (the build copies it next to the app). Or set `DriverCliPath` in `%APPDATA%\Montogo\settings.json`.
3. Run a **Release** build (Debug is too slow for real-time video):
   ```bash
   dotnet run -c Release --project windows/Montogo.App
   ```
   Montogo lives in the system tray and shows a **13-character connection code** and your **LAN IP**.

### Mac
1. Open `mac/Montogo/Montogo.xcodeproj` in Xcode.
2. Build and run (⌘R).

### First connection
1. Read the **code** and **LAN IP** from the Windows tray.
2. Enter both on the Mac and tap **Connect**. The stream starts once the handshake completes; **⌃⌘F** for full screen.
3. In Windows **Display settings**, extend your desktop onto the new virtual display and drag windows onto it.

The Mac remembers the pairing (code in the Keychain, IP in preferences) and auto-reconnects on later launches. Rotate the secret any time with **Reset connection code** in the tray.

---

## Limitations

- **Manual IP entry** — no service discovery yet; you type the PC's LAN IP once (a DHCP reservation keeps it stable).
- **Fixed 1920×1080** — not yet configurable.
- **One Mac and one Windows instance** at a time.
- **Video only** — audio isn't streamed to the Mac; it keeps playing from the Windows PC's own output.
- **Hardware HEVC required on Windows** — no software-encoder fallback.
- **Built for a trusted LAN** — designed for a home/office network, not for direct exposure to the open internet (no NAT traversal or discovery).

---

## Tech stack

| | Windows | Mac |
|---|---|---|
| Language | C# / .NET 8 | Swift / SwiftUI |
| Role | capture · encode · encrypt · send | receive · decrypt · decode · render |
| Native APIs | DXGI, Direct3D 11, Media Foundation (via CsWin32) | Darwin sockets, VideoToolbox, Metal, CryptoKit |
| Codec | HEVC (H.265), hardware MFT | HEVC, VideoToolbox |
| Crypto | AES-256-GCM, HKDF, HMAC, P-256 ECDH | same (CryptoKit) |

The two apps share **no code** — only the wire protocol in [`PROTOCOL.md`](windows/Montogo.Protocol/PROTOCOL.md), which each implements natively.
