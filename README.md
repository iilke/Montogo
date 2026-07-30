# Montogo

Montogo turns a MacBook into a wireless second monitor for a Windows PC over your local network. Windows creates a virtual display, captures it, H.264-encodes it (hardware-accelerated where available), and streams it encrypted over UDP. The Mac decrypts, decodes, and renders it full-screen. No cables, no capture card, no extra hardware.

The link is one-directional — Windows sends pixels, the Mac displays them. Keyboard and mouse stay on Windows; the Mac is a passive screen. There is no audio.

## Requirements

**Windows**
- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (or newer) to build/run
- [virtual-display-rs](https://github.com/MolotovCherry/virtual-display-rs) driver installed
- For 60 fps: a GPU with a hardware H.264 encoder (NVIDIA NVENC, Intel Quick Sync, or AMD AMF). Without one, Montogo falls back to a software encoder at 30 fps.

**Mac**
- macOS 13 (Ventura) or newer
- Xcode (to build the app)

**Network**
- Both machines on the **same Wi-Fi / LAN**, able to reach each other directly. Guest networks and "client isolation" on the router will block the connection.

## Installation

### Windows

1. **Install the virtual display driver.** Install [virtual-display-rs](https://github.com/MolotovCherry/virtual-display-rs) and make sure its `VddUserSession` service is running:
   ```
   virtual-display-driver-cli.exe install
   virtual-display-driver-cli.exe start
   ```
2. **Give Montogo the driver CLI.** Copy `virtual-display-driver-cli.exe` into `windows/Montogo.App/driver/`. The build copies it next to the app automatically for every configuration. (Alternatively, set `DriverCliPath` to its absolute path in `%APPDATA%\Montogo\settings.json`.)
3. **Run the app** — use a Release build; Debug is too slow for real-time video:
   ```bash
   dotnet run -c Release --project windows/Montogo.App
   ```
   Montogo lives in the system tray. On first launch it generates an 8-character connection code (e.g. `7X4K-9M2P`) and shows it, along with your LAN IP, in the tray menu. Only one instance runs at a time.

### Mac

1. Open `mac/Montogo/Montogo.xcodeproj` in Xcode.
2. Build and run (⌘R).

## First connection

1. On Windows, hover the tray icon or open its menu. It shows the **connection code** and, in the status line, the PC's **LAN IP** (e.g. `Waiting for Mac… (LAN: 192.168.0.17)`).
2. On the Mac, enter that **code** and **IP**, then tap **Connect**.
3. The stream starts automatically once the handshake completes. Press **⌃⌘F** for full screen.
4. Extend your Windows desktop onto the new virtual display (Windows **Display settings** → the second monitor) and drag windows onto it — they appear on the Mac.

If the Mac hangs on "Connecting…", the Windows tray tells you why: `Wrong code from …` means the code doesn't match; no reaction at all usually means the IP is wrong or the two devices can't reach each other on the network.

## Known limitations

- **Manual IP entry.** There is no service discovery yet, so you type the PC's LAN IP. It changes when DHCP reassigns it — use a DHCP reservation to keep it stable.
- **Fixed resolution.** The virtual display is 1920×1080. Not yet configurable.
- **One Mac at a time**, and one Windows instance at a time.
- **No audio.** Sound stays on the Windows machine.
- **Reconnect after network loss** can take a moment; the encoder issues a fresh keyframe when a client (re)connects.
- **No packet retransmission.** On a clean LAN this is fine (≈0% loss). On a lossy link, a dropped packet freezes the picture until the next keyframe (~1 s).
- **Display-mode changes** on the captured output currently stop the stream (surfaced in the tray); restart to recover.
- **Security is LAN-scoped.** The 8-character code is a 40-bit shared secret — appropriate for a trusted home/office network, not for exposure to the open internet.

## Documentation

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — technical overview of the whole pipeline and why each piece exists.
- [`DECISIONS.md`](DECISIONS.md) — the running architecture & decision record (per-component detail).
- [`windows/Montogo.Protocol/PROTOCOL.md`](windows/Montogo.Protocol/PROTOCOL.md) — the exact wire protocol (v2).
