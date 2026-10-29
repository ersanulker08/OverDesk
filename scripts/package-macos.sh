#!/usr/bin/env bash
# Builds OverDesk.app (self-contained, no .NET install needed) into ./dist.
# Usage: scripts/package-macos.sh [osx-arm64|osx-x64]
# As a real app, OverDesk gets its own Accessibility / Screen Recording / Automation permissions
# instead of borrowing the terminal's.
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"
OUT="$ROOT/dist"
APP="$OUT/OverDesk.app"
PUBLISH="$OUT/publish-$RID"

rm -rf "$APP" "$PUBLISH"
dotnet publish "$ROOT/src/Overdesk.App" -c Release -r "$RID" --self-contained true -o "$PUBLISH"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"

# Bundled tools (DevKit, Converter): each tool folder ships its manifest and bin/ next to the app.
for tool in "$ROOT"/tools/*/; do
  name="$(basename "$tool")"
  dotnet build "$tool" -c Release
  mkdir -p "$APP/Contents/MacOS/tools/$name"
  cp "$tool/overdesk.plugin.json" "$APP/Contents/MacOS/tools/$name/"
  cp -R "$tool/bin" "$APP/Contents/MacOS/tools/$name/"
done

# App icon from the rendered logo.
ICONSET="$OUT/OverDesk.iconset"
rm -rf "$ICONSET" && mkdir -p "$ICONSET"
for size in 16 32 64 128 256 512; do
  sips -z $size $size "$ROOT/assets/overdesk-logo.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) "$ROOT/assets/overdesk-logo.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/OverDesk.icns"
rm -rf "$ICONSET"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>OverDesk</string>
  <key>CFBundleDisplayName</key><string>OverDesk</string>
  <key>CFBundleIdentifier</key><string>io.github.overdesk</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>Overdesk.App</string>
  <key>CFBundleIconFile</key><string>OverDesk</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSAppleEventsUsageDescription</key>
  <string>OverDesk reads and controls what Music or Spotify is playing for the Now Playing card.</string>
</dict>
</plist>
PLIST

# Ad-hoc signature: enough for permissions to stick to this build on this Mac.
codesign --force --deep --sign - "$APP"
rm -rf "$PUBLISH"
echo "Built $APP"
