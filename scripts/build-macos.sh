#!/usr/bin/env bash
# Build GPMPay Webhook Local cho macOS bằng Native AOT → "GPMPay Webhook Local.app" mở chạy ngay.
#
# Yêu cầu (chạy trên máy Mac):
#   - .NET SDK 10+            https://dotnet.microsoft.com/download
#   - Xcode Command Line Tools: xcode-select --install
#
# Cách dùng:
#   ./scripts/build-macos.sh              # universal (Apple Silicon + Intel) — mặc định
#   ./scripts/build-macos.sh arm64        # chỉ Apple Silicon
#   ./scripts/build-macos.sh x64          # chỉ Intel
#
# Kết quả:
#   artifacts/macos/GPMPay Webhook Local.app
#   artifacts/GpmPayWebhookLocal-macos-<arch>.zip
#
# App được ký ad-hoc (không có Apple Developer ID). Lần đầu mở: chuột phải vào app → Open,
# hoặc: xattr -dr com.apple.quarantine "GPMPay Webhook Local.app"
set -euo pipefail

ARCH="${1:-universal}"
CONFIGURATION="${CONFIGURATION:-Release}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/GpmPayWebhookLocal/GpmPayWebhookLocal.csproj"
ARTIFACTS="$ROOT/artifacts"
APP_NAME="GPMPay Webhook Local"
EXE_NAME="GpmPayWebhookLocal"
BUNDLE_ID="com.gpmsoftwares.gpmpay-webhook-local"

[[ "$(uname -s)" == "Darwin" ]] || { echo "Script này phải chạy trên macOS (Native AOT không biên dịch chéo hệ điều hành)." >&2; exit 1; }
command -v dotnet >/dev/null || { echo "Không tìm thấy dotnet. Cài .NET SDK 10: https://dotnet.microsoft.com/download" >&2; exit 1; }
xcode-select -p >/dev/null 2>&1 || { echo "Thiếu Xcode Command Line Tools. Chạy: xcode-select --install" >&2; exit 1; }

case "$ARCH" in
  universal) RIDS=(osx-arm64 osx-x64) ;;
  arm64)     RIDS=(osx-arm64) ;;
  x64)       RIDS=(osx-x64) ;;
  *) echo "Kiến trúc không hợp lệ: $ARCH (universal | arm64 | x64)" >&2; exit 1 ;;
esac

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
VERSION="${VERSION:-1.0.0}"

# ── 1. Publish Native AOT cho từng kiến trúc
for rid in "${RIDS[@]}"; do
  echo "==> Publish Native AOT $rid ($CONFIGURATION)"
  rm -rf "$ARTIFACTS/$rid"
  dotnet publish "$PROJECT" -c "$CONFIGURATION" -r "$rid" -o "$ARTIFACTS/$rid" -p:PublishAot=true
  find "$ARTIFACTS/$rid" \( -name '*.dSYM' -o -name '*.dbg' -o -name '*.pdb' \) -prune -exec rm -rf {} +
done

# ── 2. Dựng .app bundle
OUT="$ARTIFACTS/macos"
APP="$OUT/$APP_NAME.app"
rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

if [[ ${#RIDS[@]} -eq 2 ]]; then
  echo "==> Gộp universal binary (lipo)"
  ARM="$ARTIFACTS/osx-arm64"; X64="$ARTIFACTS/osx-x64"
  for f in "$ARM"/*; do
    name="$(basename "$f")"
    if [[ -d "$f" ]]; then
      cp -R "$f" "$APP/Contents/MacOS/"
    elif file "$f" | grep -q 'Mach-O'; then
      if lipo -archs "$f" | grep -q x86_64 && lipo -archs "$f" | grep -q arm64; then
        cp "$f" "$APP/Contents/MacOS/$name"            # đã là universal (thư viện native của Avalonia/Skia)
      else
        lipo -create "$f" "$X64/$name" -output "$APP/Contents/MacOS/$name"
      fi
    else
      cp "$f" "$APP/Contents/MacOS/$name"
    fi
  done
else
  cp -R "$ARTIFACTS/${RIDS[0]}/." "$APP/Contents/MacOS/"
fi
chmod +x "$APP/Contents/MacOS/$EXE_NAME"

# ── 3. Icon .icns từ Assets/app.png
ICON_SRC="$ROOT/GpmPayWebhookLocal/Assets/app.png"
ICONSET="$(mktemp -d)/app.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256; do
  sips -z $size $size "$ICON_SRC" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  if [[ $double -le 256 ]]; then
    sips -z $double $double "$ICON_SRC" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
  fi
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns" || echo "   (bỏ qua icon: iconutil lỗi)"

# ── 4. Info.plist
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>                 <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>          <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>           <string>$BUNDLE_ID</string>
    <key>CFBundleExecutable</key>           <string>$EXE_NAME</string>
    <key>CFBundleIconFile</key>             <string>AppIcon</string>
    <key>CFBundlePackageType</key>          <string>APPL</string>
    <key>CFBundleShortVersionString</key>   <string>$VERSION</string>
    <key>CFBundleVersion</key>              <string>$VERSION</string>
    <key>CFBundleDevelopmentRegion</key>    <string>vi</string>
    <key>LSMinimumSystemVersion</key>       <string>12.0</string>
    <key>NSHighResolutionCapable</key>      <true/>
    <key>LSApplicationCategoryType</key>    <string>public.app-category.finance</string>
</dict>
</plist>
PLIST

# ── 5. Ký ad-hoc (bắt buộc để chạy được trên Apple Silicon)
echo "==> Ký ad-hoc"
codesign --force --deep --sign - "$APP"

# ── 6. Nén zip (giữ nguyên quyền + chữ ký)
ZIP="$ARTIFACTS/GpmPayWebhookLocal-macos-$ARCH.zip"
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"

echo
echo "Xong:"
echo "  $APP"
echo "  $ZIP"
