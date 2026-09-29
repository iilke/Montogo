#!/usr/bin/env bash
#
# Builds Montogo.app (universal, ad-hoc signed) and packages it into a drag-to-install
# Montogo.dmg — for distribution WITHOUT an Apple Developer account.
#
# Run on macOS with Xcode (or Command Line Tools) installed:
#     installer/mac/build-dmg.sh
#
# The result is unsigned by a Developer ID, so on first launch users must bypass
# Gatekeeper once (right-click -> Open, or System Settings -> Privacy & Security ->
# Open Anyway). See installer/mac/README.md.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/../.." && pwd)"

PROJECT="$REPO/mac/Montogo/Montogo.xcodeproj"
SCHEME="Montogo"
CONFIG="Release"
APP_NAME="Montogo"
VOL_NAME="Montogo"

BUILD_DIR="$SCRIPT_DIR/build"
DIST_DIR="$SCRIPT_DIR/dist"
DERIVED="$BUILD_DIR/DerivedData"
STAGE="$BUILD_DIR/dmg"
DMG="$DIST_DIR/Montogo.dmg"

# xcodebuild needs the full Xcode, not just Command Line Tools.
if ! xcodebuild -version >/dev/null 2>&1; then
  echo "error: xcodebuild is not usable. The active developer dir is probably the Command"
  echo "Line Tools, not Xcode. Point it at Xcode (one time), then re-run this script:"
  echo ""
  echo "    sudo xcode-select -s /Applications/Xcode.app/Contents/Developer"
  echo ""
  exit 1
fi

rm -rf "$BUILD_DIR" "$DIST_DIR"
mkdir -p "$BUILD_DIR" "$DIST_DIR" "$STAGE"

echo "== 1/4  Building $APP_NAME.app (universal arm64 + x86_64, $CONFIG) =="
# Ad-hoc code signing (identity "-") so it runs on Apple Silicon without a Developer ID.
xcodebuild \
  -project "$PROJECT" \
  -scheme "$SCHEME" \
  -configuration "$CONFIG" \
  -derivedDataPath "$DERIVED" \
  -destination 'generic/platform=macOS' \
  ARCHS="arm64 x86_64" ONLY_ACTIVE_ARCH=NO \
  CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY="-" DEVELOPMENT_TEAM="" \
  CODE_SIGNING_REQUIRED=YES CODE_SIGNING_ALLOWED=YES \
  clean build

APP="$DERIVED/Build/Products/$CONFIG/$APP_NAME.app"
[ -d "$APP" ] || { echo "error: build product not found at $APP"; exit 1; }

echo "== 2/4  Verifying signature + architectures =="
codesign --verify --deep --strict --verbose=2 "$APP"
echo -n "architectures: "; lipo -info "$APP/Contents/MacOS/$APP_NAME"

echo "== 3/4  Staging DMG contents =="
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"   # drag-to-install target

echo "== 4/4  Creating $DMG =="
hdiutil create -volname "$VOL_NAME" -srcfolder "$STAGE" -ov -format UDZO "$DMG"

echo ""
echo "Done -> $DMG"
echo "Size: $(du -h "$DMG" | cut -f1)"
echo ""
echo "Note: this build is ad-hoc signed (no Apple Developer ID), so the first launch"
echo "on another Mac needs a one-time Gatekeeper bypass. See installer/mac/README.md."
