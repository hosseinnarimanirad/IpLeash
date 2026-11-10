#!/bin/bash
# Builds IpLeash.app for macOS: a self-contained .NET publish wrapped in an app bundle, ad-hoc
# signed so it launches on this machine, plus a .dmg for copying elsewhere.
#
#   ./build-macos.sh            # Apple silicon (osx-arm64)
#   ./build-macos.sh osx-x64    # Intel
#
# Output: artifacts/macos/<rid>/IpLeash.app and artifacts/macos/IpLeash-<version>-<rid>.dmg
set -euo pipefail

RID="${1:-osx-arm64}"
VERSION="1.0.0"
ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$ROOT/src/IpLeash.Mac/IpLeash.Mac.csproj"
PACKAGING="$ROOT/src/IpLeash.Mac/Packaging"
OUT="$ROOT/artifacts/macos"
PUBLISH="$OUT/$RID/publish"
APP="$OUT/$RID/IpLeash.app"

echo "==> Publishing ($RID)"
rm -rf "$PUBLISH" "$APP"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:UseAppHost=true -p:Version="$VERSION" -o "$PUBLISH"

echo "==> Assembling IpLeash.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
sed "s/__VERSION__/$VERSION/g" "$PACKAGING/Info.plist" > "$APP/Contents/Info.plist"
cp "$PACKAGING/IpLeash.icns" "$APP/Contents/Resources/IpLeash.icns"
printf 'APPL????' > "$APP/Contents/PkgInfo"

echo "==> Signing (ad-hoc)"
# Ad-hoc is enough to run on this Mac. For distribution, sign with a Developer ID and notarize.
codesign --force --deep --sign - --timestamp=none "$APP"
codesign --verify --deep --strict "$APP"

echo "==> Creating disk image"
DMG="$OUT/IpLeash-$VERSION-$RID.dmg"
STAGE="$(mktemp -d)"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
rm -f "$DMG"
hdiutil create -volname "IpLeash" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"

echo
echo "Built: $APP"
echo "       $DMG"
