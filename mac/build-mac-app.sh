#!/usr/bin/env bash
# Membuat DepupOffice.app (bisa diklik dua kali dari Finder / dipindah ke Applications).
# Pemakaian:  ./build-mac-app.sh            → untuk Mac ini (Apple Silicon atau Intel)
#             ./build-mac-app.sh x64        → paksa Intel
#             ./build-mac-app.sh arm64      → paksa Apple Silicon (M1–M4)
set -euo pipefail
cd "$(dirname "$0")"

ARCH="${1:-$(uname -m)}"
case "$ARCH" in
  arm64|aarch64) RID="osx-arm64" ;;
  x64|x86_64)    RID="osx-x64" ;;
  *) echo "Arsitektur tidak dikenal: $ARCH"; exit 1 ;;
esac

if ! command -v dotnet >/dev/null 2>&1; then
  echo "❌ .NET 8 SDK belum terpasang. Pasang dengan:  brew install --cask dotnet-sdk"
  echo "   atau unduh dari https://dotnet.microsoft.com/download/dotnet/8.0"
  exit 1
fi

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' DepupOffice.Mac/DepupOffice.Mac.csproj | head -1)"
OUT="build"
PUB="$OUT/publish-$RID"
APP="$OUT/Depup Office.app"

echo "▶ Mem-publish untuk $RID …"
dotnet publish DepupOffice.Mac/DepupOffice.Mac.csproj -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -p:UseAppHost=true -o "$PUB"

echo "▶ Menyusun $APP …"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUB/." "$APP/Contents/MacOS/"
cp DepupOffice.Mac/Assets/Depup.icns "$APP/Contents/Resources/Depup.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Depup Office</string>
  <key>CFBundleDisplayName</key><string>Depup Office</string>
  <key>CFBundleIdentifier</key><string>cloud.depup.office.mac</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>DepupOffice</string>
  <key>CFBundleIconFile</key><string>Depup</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.productivity</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>FREE • Offline • Depup Studio</string>
  <key>NSAppleEventsUsageDescription</key><string>Depup Office memakai Finder untuk memindahkan berkas ke Trash.</string>
  <key>NSDocumentsFolderUsageDescription</key><string>Depup Office menyimpan hasil dokumen di Documents/DepupOffice.</string>
  <key>NSDownloadsFolderUsageDescription</key><string>Depup Office membuka dokumen yang Anda pilih dari Downloads.</string>
  <key>NSDesktopFolderUsageDescription</key><string>Depup Office membuka dokumen yang Anda pilih dari Desktop.</string>
</dict>
</plist>
PLIST

chmod +x "$APP/Contents/MacOS/DepupOffice"
# Tanda tangan ad-hoc (wajib agar bisa jalan di Apple Silicon).
codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || echo "⚠️  codesign gagal (aplikasi mungkin tetap jalan)."
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true

echo "✅ Selesai: $APP"

# Berkas .dmg (seret ke Applications) bila DMG=1, mis. di CI
if [ "${DMG:-0}" = "1" ]; then
  ARCHNAME="${RID#osx-}"
  DMGDIR="$OUT/dmg-$RID"
  rm -rf "$DMGDIR"; mkdir -p "$DMGDIR"
  cp -R "$APP" "$DMGDIR/"
  ln -s /Applications "$DMGDIR/Applications"
  DMGFILE="$OUT/DepupOffice-v$VERSION-macos-$ARCHNAME.dmg"
  rm -f "$DMGFILE"
  hdiutil create -volname "Depup Office" -srcfolder "$DMGDIR" -ov -format UDZO "$DMGFILE" >/dev/null
  echo "✅ DMG: $DMGFILE"
else
  echo "   Seret ke folder Applications bila ingin dipasang."
  open "$OUT" 2>/dev/null || true
fi
