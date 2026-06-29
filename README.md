# Montogo

Montogo turns a MacBook into a second monitor for a Windows PC over local WiFi/LAN. The Windows machine captures a virtual display, H.264-encodes it with hardware acceleration where available, and streams it encrypted over UDP. The Mac renders the stream full-screen. No extra hardware is required.

## Status

- **Windows app:** complete — virtual display lifecycle, DXGI capture, H.264 encoding (NVENC/Quick Sync/AMF with software fallback), AES-256-GCM encrypted UDP transport, PSK authentication, LAN interface binding.
- **Mac app:** not yet started.

## Quick start (Windows)

1. Install [virtual-display-rs](https://github.com/MolotovCherry/virtual-display-rs) and start the `VddUserSession` service.
2. Place `virtual-display-driver-cli.exe` in a `driver\` folder next to `Montogo.App.exe`.
3. Run `Montogo.App.exe`. The tray icon shows an 8-character connection code (e.g. `7X4K-9M2P`).
4. Enter that code into the Mac app to pair. The stream starts automatically once the Mac handshakes.

See `DECISIONS.md` for architecture details and `windows/Montogo.Protocol/PROTOCOL.md` for the wire protocol.
