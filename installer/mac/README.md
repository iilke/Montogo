# Montogo — Mac packaging

Builds a distributable **`Montogo.dmg`** without an Apple Developer account.

## Build

On a Mac with Xcode installed:

```bash
installer/mac/build-dmg.sh
```

This produces `installer/mac/dist/Montogo.dmg` containing a **universal** (Apple Silicon +
Intel), **ad-hoc-signed** `Montogo.app`, plus an `Applications` symlink so users install by
dragging the app onto Applications. Deployment target: macOS 13 (Ventura) or newer.

## First launch on another Mac (one-time Gatekeeper step)

Because the app is **not** signed with an Apple Developer ID or notarized, Gatekeeper blocks
it the first time with *"Montogo can't be opened because Apple cannot check it for malicious
software."* This is expected for the free path. Bypass it once:

- **Right-click** (or Control-click) `Montogo.app` → **Open** → **Open**, or
- **System Settings → Privacy & Security** → scroll to the Montogo notice → **Open Anyway**, or
- from Terminal: `xattr -dr com.apple.quarantine /Applications/Montogo.app`

After that first launch, it opens normally.

The app also asks for **Local Network** access on first run (to receive the video) — allow it.

## Removing the warning (optional, paid)

To ship without any Gatekeeper prompt you need an **Apple Developer account ($99/yr)**:
sign with a *Developer ID Application* certificate and notarize with `notarytool`, then
staple. That's the only change required — the build is otherwise identical. Parked for now.

## Notes

- The app is **not sandboxed**; entitlements only declare local-network client/server.
- A prettier DMG (custom background, positioned icons) can be added later with `create-dmg`;
  this uses plain `hdiutil` for zero extra dependencies.
