#!/usr/bin/env bash
# Build SimpleAnalyzer.app and a DMG for macOS.
# Usage: scripts/make-dmg.sh [rid]   (osx-arm64 for Apple silicon, the default; osx-x64 for Intel)
# Output: artifacts/mac/SimpleAnalyzer.app and artifacts/SimpleAnalyzer-<version>-<arch>.dmg
#
# Self-contained (no .NET install needed on the Mac it runs on). Ad-hoc signed, not
# notarized: the first time, open it with right-click > Open, or allow it in
# System Settings > Privacy & Security.
set -euo pipefail
cd "$(dirname "$0")/.."
# Homebrew's dotnet bundles its own runtime, linked to Homebrew's libraries, which
# wouldn't run on other Macs. Use Microsoft's when it's installed.
[ -x /usr/local/share/dotnet/dotnet ] && export PATH="/usr/local/share/dotnet:$PATH"
rid="${1:-osx-arm64}"
arch="${rid#osx-}"
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/SimpleAnalyzer/SimpleAnalyzer.csproj)
out=artifacts/mac
app="$out/SimpleAnalyzer.app"
stage="$out/stage"

rm -rf "$out"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$stage"

echo "== app ($rid)"
dotnet publish src/SimpleAnalyzer/SimpleAnalyzer.csproj -c Release -r "$rid" --self-contained true \
  -p:UseAppHost=true -o "$app/Contents/MacOS"
find "$app/Contents" -name '*.pdb' -delete

cp src/SimpleAnalyzer/Assets/AppIcon.icns "$app/Contents/Resources/"
cp LICENSE "$app/Contents/Resources/"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>SimpleAnalyzer</string>
  <key>CFBundleDisplayName</key><string>SimpleAnalyzer</string>
  <key>CFBundleIdentifier</key><string>com.kc2rgw.simpleanalyzer</string>
  <key>CFBundleExecutable</key><string>SimpleAnalyzer</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key>
  <string>SimpleAnalyzer connects to your radio's TCI server, which may be on another computer on your network.</string>
</dict>
</plist>
PLIST

echo "== sign (ad-hoc)"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

echo "== dmg"
ditto "$app" "$stage/SimpleAnalyzer.app"
ln -s /Applications "$stage/Applications"
dmg="artifacts/SimpleAnalyzer-$version-$arch.dmg"
rm -f "$dmg"
hdiutil create -volname "SimpleAnalyzer $version" -srcfolder "$stage" -format UDZO -ov "$dmg" >/dev/null
rm -rf "$stage"
du -sh "$app" "$dmg"
echo "dmg -> $dmg"
