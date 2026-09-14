#!/usr/bin/env bash
# Builds the Stream Arc TV Linux packages for one architecture.
#
#   linux/package/make-packages.sh <version> <x64|arm64> <out-dir> [publish-dir]
#
# Produces Stream-Arc-TV-<version>-linux-<arch>.tar.gz (a portable folder: unpack anywhere and run
# StreamArcTV) and streamarctv_<version>_<amd64|arm64>.deb (installs to /opt/streamarctv with a
# menu entry; depends on the distribution's VLC packages). Needs dotnet, tar and dpkg-deb.
set -euo pipefail

VERSION="${1:?version}"
ARCH="${2:?x64|arm64}"
OUT="${3:?out dir}"
PUBLISH="${4:-}"

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
PROJECT="$ROOT/linux/StreamArcTV/StreamArcTV.csproj"
case "$ARCH" in
  x64)   RID=linux-x64;   DEB_ARCH=amd64 ;;
  arm64) RID=linux-arm64; DEB_ARCH=arm64 ;;
  *) echo "unknown arch $ARCH (x64 or arm64)" >&2; exit 2 ;;
esac

mkdir -p "$OUT"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# 1. Publish (self-contained: the .NET runtime is included, VLC comes from the distribution).
if [[ -z "$PUBLISH" ]]; then
  PUBLISH="$WORK/publish"
  dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:Version="$VERSION" -p:DebugType=none -p:DebugSymbols=false -o "$PUBLISH"
fi
chmod +x "$PUBLISH/StreamArcTV"

# 2. Portable folder.
TARDIR="$WORK/tar/StreamArcTV"
mkdir -p "$TARDIR"
cp -R "$PUBLISH"/. "$TARDIR/"
cp "$ROOT/desktop/Shared/Assets/app_icon.png" "$TARDIR/streamarctv.png"
sed 's|^Exec=.*|Exec=StreamArcTV|; s|^Icon=.*|Icon=streamarctv|' "$HERE/streamarctv.desktop" > "$TARDIR/streamarctv.desktop"
cat > "$TARDIR/README.txt" <<TXT
Stream Arc TV $VERSION for Linux ($ARCH)

1. Install VLC from your distribution (sudo apt install vlc / sudo dnf install vlc / sudo pacman -S vlc).
2. Run ./StreamArcTV from this folder. Keep the folder somewhere you can write to: the app updates
   itself in place.
Optional menu entry: edit streamarctv.desktop so Exec and Icon hold this folder's full paths, then
copy it to ~/.local/share/applications/.
TXT
TAR="$OUT/Stream-Arc-TV-${VERSION}-linux-${ARCH}.tar.gz"
rm -f "$TAR"
tar -C "$WORK/tar" -czf "$TAR" StreamArcTV

# 3. Debian package.
DEB="$WORK/deb"
mkdir -p "$DEB/DEBIAN" "$DEB/opt/streamarctv" "$DEB/usr/bin" "$DEB/usr/share/applications" \
         "$DEB/usr/share/icons/hicolor/1024x1024/apps" "$DEB/usr/share/pixmaps"
cp -R "$PUBLISH"/. "$DEB/opt/streamarctv/"
ln -s /opt/streamarctv/StreamArcTV "$DEB/usr/bin/streamarctv"
cp "$HERE/streamarctv.desktop" "$DEB/usr/share/applications/streamarctv.desktop"
cp "$ROOT/desktop/Shared/Assets/app_icon.png" "$DEB/usr/share/icons/hicolor/1024x1024/apps/streamarctv.png"
cp "$ROOT/desktop/Shared/Assets/app_icon.png" "$DEB/usr/share/pixmaps/streamarctv.png"
SIZE_KB=$(du -sk "$DEB/opt" "$DEB/usr" | awk '{s+=$1} END {print s}')
cat > "$DEB/DEBIAN/control" <<CTL
Package: streamarctv
Version: $VERSION
Section: video
Priority: optional
Architecture: $DEB_ARCH
Installed-Size: $SIZE_KB
Depends: libvlc5, vlc-plugin-base, vlc-plugin-video-output, libx11-6, libice6, libsm6, libfontconfig1
Recommends: vlc, libnotify-bin
Maintainer: Computer Garage <noreply@github.com>
Homepage: https://github.com/ComputerGarage1837/StreamArcTV
Description: Stream Arc TV - live TV and video on demand client
 Stream Arc TV is a client for authorized Xtream Codes services: a TV guide
 with live preview and multi-view, a video-on-demand home, downloads and
 scheduled recordings, playing through VLC's library.
CTL
cat > "$DEB/DEBIAN/postinst" <<'PI'
#!/bin/sh
set -e
if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database -q /usr/share/applications || true; fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true; fi
exit 0
PI
cp "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/postrm"
chmod 755 "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/postrm"
DEBOUT="$OUT/streamarctv_${VERSION}_${DEB_ARCH}.deb"
rm -f "$DEBOUT"
dpkg-deb --build --root-owner-group "$DEB" "$DEBOUT" >/dev/null
echo "Built $TAR ($(stat -c%s "$TAR") bytes) and $DEBOUT ($(stat -c%s "$DEBOUT") bytes)"
