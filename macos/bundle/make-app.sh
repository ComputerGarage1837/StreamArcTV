#!/usr/bin/env bash
# Builds the Stream Arc TV app bundle, disk image and zip for one architecture.
#
#   macos/bundle/make-app.sh <version> <arm64|x64> <out-dir> [publish-dir]
#
# Steps: dotnet publish (unless a publish folder is given), assemble "Stream Arc TV.app", copy
# LibVLC out of the official VLC.app disk image into Contents/Frameworks/libvlc (thinned to the
# one architecture), build the .icns, ad-hoc sign the bundle, run the app's --selftest, then
# write Stream-Arc-TV-<version>-macOS-<arch>.dmg and .zip into <out-dir>. Needs macOS.
set -euo pipefail

VERSION="${1:?version}"
ARCH="${2:?arm64|x64}"
OUT="${3:?out dir}"
PUBLISH="${4:-}"
VLC_VERSION="${VLC_VERSION:-3.0.21}"
VLC_URL="${VLC_URL:-https://get.videolan.org/vlc/${VLC_VERSION}/macosx/vlc-${VLC_VERSION}-universal.dmg}"
CACHE="${VLC_CACHE:-${RUNNER_TEMP:-/tmp}/streamarc-vlc}"

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
PROJECT="$ROOT/macos/StreamArcTV/StreamArcTV.csproj"
case "$ARCH" in
  arm64) RID=osx-arm64; LIPO_ARCH=arm64 ;;
  x64)   RID=osx-x64;   LIPO_ARCH=x86_64 ;;
  *) echo "unknown arch $ARCH (arm64 or x64)" >&2; exit 2 ;;
esac

mkdir -p "$OUT" "$CACHE"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# 1. Publish (self-contained, framework and runtime included).
if [[ -z "$PUBLISH" ]]; then
  PUBLISH="$WORK/publish"
  dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:Version="$VERSION" -p:DebugType=none -p:DebugSymbols=false -o "$PUBLISH"
fi

# 2. Bundle skeleton.
APP="$WORK/Stream Arc TV.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$APP/Contents/Frameworks/libvlc"
cp -R "$PUBLISH"/. "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/StreamArcTV"
BUILD_NUMBER="$(python3 -c "v='$VERSION'.split('.'); v+=['0']*(3-len(v)); print(int(''.join(c for c in v[0] if c.isdigit()) or 0)*10000+int(''.join(c for c in v[1] if c.isdigit()) or 0)*100+int(''.join(c for c in v[2] if c.isdigit()) or 0))")"
sed -e "s/__VERSION__/$VERSION/" -e "s/__BUILD__/$BUILD_NUMBER/" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
echo -n "APPL????" > "$APP/Contents/PkgInfo"

# 3. Icon (.icns from the branding PNG).
ICONSET="$WORK/AppIcon.iconset"
mkdir -p "$ICONSET"
SRC_ICON="$ROOT/desktop/Shared/Assets/app_icon.png"
for s in 16 32 64 128 256 512; do
  sips -z $s $s "$SRC_ICON" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  d=$((s*2)); sips -z $d $d "$SRC_ICON" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

# 4. LibVLC from the official VLC.app (cached download).
DMG="$CACHE/vlc-${VLC_VERSION}-universal.dmg"
if [[ ! -s "$DMG" ]]; then
  echo "Downloading $VLC_URL"
  curl -fsSL --retry 4 -o "$DMG" "$VLC_URL"
fi
MNT="$WORK/vlc-mount"
mkdir -p "$MNT"
hdiutil attach -nobrowse -readonly -mountpoint "$MNT" "$DMG" >/dev/null
VLCAPP="$MNT/VLC.app"
if [[ ! -d "$VLCAPP/Contents/MacOS/lib" ]]; then hdiutil detach "$MNT" >/dev/null || true; echo "VLC.app layout not recognised" >&2; exit 1; fi
FW="$APP/Contents/Frameworks/libvlc"
# ditto --arch keeps only this architecture's slice of every Mach-O file (half the size).
ditto --arch "$LIPO_ARCH" "$VLCAPP/Contents/MacOS/lib" "$FW/lib"
ditto --arch "$LIPO_ARCH" "$VLCAPP/Contents/MacOS/plugins" "$FW/plugins"
if [[ -d "$VLCAPP/Contents/MacOS/share" ]]; then ditto "$VLCAPP/Contents/MacOS/share" "$FW/share"; fi
hdiutil detach "$MNT" >/dev/null
# The plugin cache belongs to the original layout; VLC rebuilds its own.
rm -f "$FW/plugins/plugins.dat"
# libvlccore on macOS also looks for plugins in lib/vlc/plugins next to itself; point that there too.
mkdir -p "$FW/lib/vlc"
ln -sfn ../../plugins "$FW/lib/vlc/plugins"
echo "libvlc: $(otool -L "$FW/lib/libvlc.dylib" | head -3 | tr '\n' ' ')"

# 5. Ad-hoc signature (Apple silicon refuses unsigned code; no certificate is needed for this).
find "$APP/Contents/Frameworks" "$APP/Contents/MacOS" -type f \( -name '*.dylib' -o -perm -u+x \) -print0 | xargs -0 -n 50 codesign --force --sign - --timestamp=none 2>/dev/null || true
codesign --force --deep --sign - --timestamp=none "$APP"
codesign --verify --deep --strict "$APP"

# 6. Self-test: the bundled app must find and start its LibVLC (only for the runner's own architecture).
if [[ "${SELFTEST:-1}" == "1" ]]; then
  NATIVE="$(uname -m)"
  if [[ "$NATIVE" == "$LIPO_ARCH" ]]; then
    "$APP/Contents/MacOS/StreamArcTV" --selftest
  else
    echo "Skipping --selftest for $ARCH on a $NATIVE runner"
  fi
fi

# 7. Packages.
ZIP="$OUT/Stream-Arc-TV-${VERSION}-macOS-${ARCH}.zip"
DMGOUT="$OUT/Stream-Arc-TV-${VERSION}-macOS-${ARCH}.dmg"
rm -f "$ZIP" "$DMGOUT"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
STAGE="$WORK/dmg"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "Stream Arc TV" -srcfolder "$STAGE" -ov -format UDZO -quiet "$DMGOUT"
cp -R "$APP" "$OUT/"
echo "Built $ZIP ($(stat -f%z "$ZIP") bytes) and $DMGOUT ($(stat -f%z "$DMGOUT") bytes)"
