#!/usr/bin/env bash
# Build GPMPay Webhook Local cho Linux bằng Native AOT → file chạy ngay, không cần cài .NET.
#
# Yêu cầu (chạy trên máy Linux):
#   - .NET SDK 10+   https://dotnet.microsoft.com/download
#   - clang + zlib   Ubuntu/Debian: sudo apt install clang zlib1g-dev
#                    Fedora:        sudo dnf install clang zlib-devel
#                    Arch:          sudo pacman -S clang zlib
#
# Cách dùng:
#   ./scripts/build-linux.sh           # theo kiến trúc máy đang build (x64 / arm64)
#   ./scripts/build-linux.sh arm64     # biên dịch chéo arm64 (cần sysroot/toolchain arm64)
#
# Kết quả:
#   artifacts/linux-<arch>/GpmPayWebhookLocal          (thư mục chạy ngay, kèm install.sh)
#   artifacts/GpmPayWebhookLocal-linux-<arch>.tar.gz
#
# Khi chạy app cần: espeak-ng (giọng đọc), libnotify-bin (thông báo, tuỳ chọn),
# và môi trường desktop có khay hệ thống (KDE, XFCE, Cinnamon; GNOME cần extension AppIndicator).
set -euo pipefail

case "$(uname -m)" in
  x86_64|amd64)  HOST_ARCH=x64 ;;
  aarch64|arm64) HOST_ARCH=arm64 ;;
  *)             HOST_ARCH="$(uname -m)" ;;
esac
ARCH="${1:-$HOST_ARCH}"
CONFIGURATION="${CONFIGURATION:-Release}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/GpmPayWebhookLocal/GpmPayWebhookLocal.csproj"
ARTIFACTS="$ROOT/artifacts"
RID="linux-$ARCH"
OUT="$ARTIFACTS/$RID"

[[ "$(uname -s)" == "Linux" ]] || { echo "Script này phải chạy trên Linux (Native AOT không biên dịch chéo hệ điều hành)." >&2; exit 1; }
command -v dotnet >/dev/null || { echo "Không tìm thấy dotnet. Cài .NET SDK 10: https://dotnet.microsoft.com/download" >&2; exit 1; }
command -v clang >/dev/null  || { echo "Thiếu clang (Native AOT cần). VD: sudo apt install clang zlib1g-dev" >&2; exit 1; }

echo "==> Publish Native AOT $RID ($CONFIGURATION)"
rm -rf "$OUT"
dotnet publish "$PROJECT" -c "$CONFIGURATION" -r "$RID" -o "$OUT" -p:PublishAot=true
find "$OUT" \( -name '*.dbg' -o -name '*.pdb' \) -delete
chmod +x "$OUT/GpmPayWebhookLocal"

# icon + file .desktop + script cài vào menu ứng dụng
cp "$ROOT/GpmPayWebhookLocal/Assets/app.png" "$OUT/gpmpay-webhook-local.png"

cat > "$OUT/install.sh" <<'INSTALL'
#!/usr/bin/env bash
# Thêm GPMPay Webhook Local vào menu ứng dụng của user hiện tại (không cần sudo).
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
mkdir -p "$APPS" "$ICONS"
cp "$DIR/gpmpay-webhook-local.png" "$ICONS/gpmpay-webhook-local.png"
cat > "$APPS/gpmpay-webhook-local.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=GPMPay Webhook Local
Comment=Nhận webhook GPM Pay và đọc số tiền qua loa
Exec="$DIR/GpmPayWebhookLocal"
Icon=gpmpay-webhook-local
Terminal=false
Categories=Office;Finance;
StartupWMClass=GpmPayWebhookLocal
DESKTOP
command -v update-desktop-database >/dev/null && update-desktop-database "$APPS" >/dev/null 2>&1 || true
echo "Đã thêm vào menu ứng dụng: GPMPay Webhook Local"
command -v espeak-ng >/dev/null || echo "Gợi ý: cài giọng đọc tiếng Việt → sudo apt install espeak-ng"
INSTALL
chmod +x "$OUT/install.sh"

TAR="$ARTIFACTS/GpmPayWebhookLocal-$RID.tar.gz"
rm -f "$TAR"
tar -C "$ARTIFACTS" -czf "$TAR" "$RID"

echo
echo "Xong:"
echo "  $OUT/GpmPayWebhookLocal   (chạy trực tiếp, hoặc ./install.sh để thêm vào menu)"
echo "  $TAR"
