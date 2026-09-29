# Pinned dependency — virtual-display-rs driver

Montogo bundles the third-party [virtual-display-rs](https://github.com/MolotovCherry/virtual-display-rs)
driver (MIT-licensed) in its Windows installer. It is **pinned** to one reviewed release and
verified by hash — we never fetch "latest".

| | |
|---|---|
| Release | **v0.3.1** |
| Asset | `virtual-desktop-driver-installer-x64.zip` |
| Source | https://github.com/MolotovCherry/virtual-display-rs/releases/download/v0.3.1/virtual-desktop-driver-installer-x64.zip |
| Size | 3,267,304 bytes |
| **SHA-256** | `B3E3A5AB9B49BD56A7E753120CDDB1D913479BC5C0DFA781C3D43392DDE2FB75` |
| Contains | `virtual-display-driver-0.3.1-x86_64.msi`, `DriverCertificate.cer`, `install-cert.bat` |

The binaries are **not committed** (git-ignored under `driver/`). Run [`fetch-driver.ps1`](fetch-driver.ps1)
to download this exact asset; it refuses to proceed if the SHA-256 doesn't match the value above.

The installer trusts `DriverCertificate.cer` into the machine's **Root** and **TrustedPublisher**
stores (mirroring the upstream `install-cert.bat`), then installs the MSI silently.

**Updating the pin is deliberate:** review and test a newer upstream release, then update the
release tag + SHA-256 here. See the supply-chain pinning rationale in `DECISIONS.md`.
