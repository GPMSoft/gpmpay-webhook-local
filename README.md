# GPM Pay Webhook Local

App desktop đa nền tảng (**Windows · macOS · Linux**) nhận webhook giao dịch GPM Pay ngay trên máy tính và đọc số tiền qua loa.
Viết bằng **Avalonia 12 + .NET 10**, build **Native AOT** → 1 file chạy ngay, không cần cài .NET.

## Cấu trúc

```
GpmPayWebhookLocal.slnx            ← mở bằng Visual Studio / Rider / VS Code
Directory.Build.props              ← version, thông tin chung
scripts/
  build-windows.cmd / .ps1         ← build Windows (x64 / arm64)
  build-macos.sh                   ← build macOS (.app universal / arm64 / x64)
  build-linux.sh                   ← build Linux (x64 / arm64)
.github/workflows/build.yml        ← CI build cả 3 hệ điều hành
docs/gpmpay-ai-skill.md
GpmPayWebhookLocal/                ← project chính
  Program.cs, App.axaml            ← khởi động, chống mở 2 bản
  Views/                           ← MainWindow (giao diện "loa"), Tray (khay hệ thống)
  Controls/                        ← StrokeIcon, SpeakerGrille (lưới loa), Tween (hiệu ứng)
  Styles/Theme.axaml               ← màu, icon, ControlTheme cho nút / ô nhập / thanh trượt
  Services/                        ← logic dùng chung: webhook server, tunnel, API GPM Pay, giọng đọc
  Platform/                        ← phần riêng từng OS: giọng đọc, thông báo, tự khởi động, mã hoá token
  Assets/                          ← icon, font Be Vietnam Pro
```

## Cách hoạt động
1. Mở HTTP server nội bộ (Kestrel) tại `127.0.0.1:<port>` (mặc định 47123).
2. Chạy Cloudflare Quick Tunnel (`cloudflared`, tự tải đúng OS/kiến trúc nếu chưa có) → lấy URL `https://xxx.trycloudflare.com`.
3. Đăng ký webhook trên GPM Pay với URL `https://xxx.trycloudflare.com/gpmpay-local/<InstanceId>` (xác thực HMAC).
   - `/gpmpay-local/<InstanceId>` là **dấu đánh dấu** webhook của máy này.
   - Mỗi lần tạo lại URL: tìm webhook có dấu → `PATCH` sang URL mới, xoá các webhook thừa; nếu cập nhật lỗi thì tạo mới rồi xoá cũ.
4. Cứ 30 giây kiểm tra URL tunnel; lỗi 3 lần liên tiếp hoặc cloudflared bị tắt → tự tạo lại URL + webhook.
5. Có giao dịch tiền **vào** → đọc "Đã nhận … đồng".

## Khác biệt theo hệ điều hành

| | Windows | macOS | Linux |
|---|---|---|---|
| Thư mục dữ liệu | `%LocalAppData%\GpmPayWebhookLocal` | `~/Library/Application Support/GpmPayWebhookLocal` | `~/.local/share/GpmPayWebhookLocal` |
| Mã hoá API token | DPAPI (theo user Windows) | AES-GCM, khoá riêng `chmod 600` | AES-GCM, khoá riêng `chmod 600` |
| Giọng hệ thống | Microsoft An (WinRT) | `say` — giọng Linh | Speech Dispatcher (`spd-say`) |
| Phát âm thanh | MediaPlayer (WinRT) | `afplay` | `paplay` / `pw-play` / `aplay` |
| Khởi động cùng máy | `HKCU\…\Run` | LaunchAgent `~/Library/LaunchAgents` | `~/.config/autostart/*.desktop` |
| Khay / thông báo | system tray · toast | menu bar · Notification Center | StatusNotifierItem · `notify-send` |

Biến môi trường `GPMPAY_LOCAL_DATA_DIR` đổi thư mục dữ liệu (tiện khi dev/test, không đụng cấu hình thật).

> **Chuyển từ bản WPF cũ (Windows):** dùng chung thư mục dữ liệu và định dạng `settings.json`, nên token, InstanceId,
> webhook secret, lịch sử giao dịch được giữ nguyên. Tắt hẳn bản cũ trước khi mở bản mới để không chạy 2 tunnel.

### Giọng đọc tiếng Việt
Chọn trong **Cấu hình → Giọng đọc**:
- **Tự động** (mặc định): giọng tiếng Việt của hệ điều hành nếu có (tự nhiên nhất), không thì **Có sẵn**.
- **Có sẵn**: ghép 125 cụm âm thanh nhúng trong app (`Assets/Voice`, ~1,7MB) thành câu "Đã nhận … đồng".
  Không cần cài gì, giọng giống nhau trên mọi hệ điều hành; câu khác số tiền thì tự chuyển sang giọng hệ điều hành.
  - Cụm lớn (cả số 1–99, "X trăm", "linh Y", "nghìn"…), cắt ra từ câu nói liền để giữ ngữ điệu:
    "Đã nhận | một triệu | hai trăm | năm mươi | nghìn | đồng".
  - Bộ âm thanh hiện tại tạo bằng giọng Microsoft An: `dotnet run tools/generate-voice.cs` (chạy trên Windows có giọng tiếng Việt).
  - Muốn đổi giọng: thay các file WAV (PCM 16-bit mono 16 kHz, cùng tên; danh sách cụm trong `voice.txt`).
- **Windows / macOS / Speech Dispatcher**: giọng hệ điều hành.
  - Windows: Settings → Time & language → Speech → Add voices → Tiếng Việt (Microsoft An).
  - macOS: System Settings → Accessibility → Spoken Content → System voice → Manage Voices… → Tiếng Việt (Linh).

### Linux — gói cần có khi chạy
Phát âm thanh dùng `paplay` / `pw-play` / `aplay` (distro desktop thường có sẵn). Thông báo cần `notify-send`:
```bash
sudo apt install libnotify-bin
```
Khay hệ thống cần môi trường desktop hỗ trợ StatusNotifierItem (KDE, XFCE, Cinnamon…; GNOME cần extension *AppIndicator*).

## Build

Yêu cầu chung: **.NET SDK 10** trở lên. Native AOT **không biên dịch chéo hệ điều hành** — bản cho OS nào phải build trên OS đó
(hoặc dùng GitHub Actions: `.github/workflows/build.yml` build cả 3).

| OS | Công cụ cần thêm | Lệnh | Kết quả |
|---|---|---|---|
| Windows | Visual Studio / Build Tools, workload *Desktop development with C++* | `scripts\build-windows.cmd` (hoặc `.ps1 -Arch x64\|arm64\|all`) | `artifacts\win-x64\GpmPayWebhookLocal.exe` + `.zip` |
| macOS | Xcode Command Line Tools (`xcode-select --install`) | `./scripts/build-macos.sh [universal\|arm64\|x64]` | `artifacts/macos/GPMPay Webhook Local.app` + `.zip` |
| Linux | `clang`, `zlib1g-dev` | `./scripts/build-linux.sh [x64\|arm64]` | `artifacts/linux-x64/GpmPayWebhookLocal` + `.tar.gz` |

Chạy khi dev (không AOT): `dotnet run --project GpmPayWebhookLocal`, hoặc F5 trong Visual Studio.

macOS: app được ký ad-hoc. Lần đầu mở nếu bị Gatekeeper chặn: chuột phải → **Open**, hoặc
`xattr -dr com.apple.quarantine "GPMPay Webhook Local.app"`.

Linux: giải nén rồi chạy `./GpmPayWebhookLocal`, hoặc `./install.sh` để thêm vào menu ứng dụng.
Bản AOT cần glibc **không cũ hơn** máy build. CI build trong container Ubuntu 22.04 (glibc 2.35) → chạy được trên
Ubuntu 22.04+, Debian 12+, Fedora 36+. Build tay trên distro mới hơn thì bản build chỉ chạy được trên distro mới tương đương.

## Giấy phép bên thứ ba
- **Avalonia** — MIT.
- **cloudflared** — Apache-2.0. Tải lúc chạy từ GitHub releases của Cloudflare (hoặc dùng bản có sẵn trên máy).
- **Giọng "Có sẵn"** (`Assets/Voice`) — hiện tạo từ giọng Microsoft An của Windows để dùng thử.
  Trước khi phát hành rộng, cần kiểm tra điều khoản phân phối lại âm thanh của giọng đó, hoặc thay bằng giọng có giấy phép rõ ràng.
- **Be Vietnam Pro** — SIL Open Font License 1.1. Nhúng trong app (`Assets/Fonts`), giấy phép kèm theo ở `licenses/BeVietnamPro-OFL.txt`.
