# Third-party notices

Montogo's own source code is licensed under the GNU AGPLv3 (see [LICENSE](LICENSE)).
In addition, Montogo redistributes the following third-party component **unmodified**
in its Windows installer.

## virtual-display-rs — Windows virtual display driver

- **Project:** https://github.com/MolotovCherry/virtual-display-rs
- **Version:** v0.3.1 (pinned and hash-verified; see
  [`installer/windows/driver-pin.md`](installer/windows/driver-pin.md))
- **Copyright:** © MolotovCherry and the virtual-display-rs contributors
- **License:** GNU Affero General Public License v3.0 (AGPL-3.0).
  Full text: [`licenses/virtual-display-rs-v0.3.1-LICENSE`](licenses/virtual-display-rs-v0.3.1-LICENSE)
- **Corresponding source:** the exact, unmodified source for the redistributed binary is the
  tagged upstream release <https://github.com/MolotovCherry/virtual-display-rs/tree/v0.3.1>.
  The installer downloads that release at build time via
  [`installer/windows/fetch-driver.ps1`](installer/windows/fetch-driver.ps1), refusing any
  payload whose SHA-256 does not match the pinned value.

Montogo includes none of the driver's source code: it is a separate program that Montogo
controls at arm's length over a Windows named pipe, and it is distributed unmodified.
