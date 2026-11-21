# Claude Usage Tracker (Windows)

Công cụ khay hệ thống (system tray) hiển thị mức dùng hạn ngạch Claude (chu kỳ 5 giờ + tuần) mà **không cần bấm vào trang Usage**, kèm thống kê, dự báo và cảnh báo giúp dùng Claude tiết kiệm hơn.

Tool chỉ **đọc dữ liệu cục bộ** — không gọi API, không đụng cookie/token.

## Dữ liệu lấy từ đâu

| Nguồn | Có gì | Cập nhật khi nào |
|---|---|---|
| `%APPDATA%\Claude\plan-usage-history.json` (Claude Desktop tự ghi) | % 5h, % tuần, overage | ~15 phút/lần khi Desktop đang poll |
| `%APPDATA%\ClaudeUsageTracker\cli-usage.json` + `cli-history.jsonl` (do `statusline.js` ghi) | % 5h/tuần, **giờ reset chính xác**, model, % ngữ cảnh | mỗi tin nhắn + mỗi 60 giây khi có phiên Claude Code đang mở |

Tool lấy **% từ nguồn mới nhất** và mượn **giờ reset** từ file CLI (mốc tuyệt đối, vẫn đúng khi file hơi cũ).

> Giới hạn cố hữu: mỗi nguồn chỉ biết số liệu tại lúc **chính nó** hoạt động. Số hiển thị có thể trễ so với panel thật bằng đúng khoảng từ lần ghi cuối. HUD luôn ghi rõ tuổi dữ liệu ("2 phút trước") và nút *Làm mới* báo thẳng "có số mới" hay "chưa có số mới" — nó chỉ đọc lại file, không ép Claude hỏi server.

## Tính năng

- **HUD** tự hiện khi Claude Desktop / terminal / VS Code đang ở foreground, tự ẩn khi sang app khác. Click chuột trái vào icon tray để ẩn/hiện thủ công.
- **Icon tray đổi màu** theo mức dùng (xanh / vàng / đỏ), và được đẩy lên một mức khi có cảnh báo đang tồn tại.
- **Dự báo**: "với nhịp 30%/giờ sẽ chạm 100% lúc 17:12 — trước giờ reset 25m".
- **Cảnh báo** (balloon tip): vượt ngưỡng 5h/tuần (mặc định 80%, 95%), dự báo chạm trần trước reset, ngữ cảnh phiên ≥ 70%. Mỗi cảnh báo chỉ bắn một lần, trạng thái lưu ở `alert-state.json` nên không báo lặp sau khi restart.
- **📈 Thống kê** (nút trong HUD / menu tray): model nào đốt quota (ước lượng, 24h / 7 ngày), dự báo, ngữ cảnh phiên, biểu đồ lịch sử 24h / 7 ngày.
- **Cài đặt**: trạng thái 2 nguồn dữ liệu, chu kỳ quét, ngưỡng "dữ liệu cũ", khởi động cùng Windows, bật/tắt cảnh báo và ngưỡng.

## Cài đặt

### 1. Cài `statusline.js` cho Claude Code CLI (khuyến nghị)

Copy `statusline/statusline.js` vào `~/.claude/statusline.js`, rồi thêm vào `~/.claude/settings.json`:

```json
"statusLine": {
  "type": "command",
  "command": "node \"C:\\Users\\<bạn>\\.claude\\statusline.js\"",
  "padding": 1,
  "refreshInterval": 60
}
```

Cần Node.js (Claude Code đã yêu cầu sẵn). Script chỉ ghi file cục bộ; không gọi mạng.
Không cài cũng được — tool vẫn chạy bằng dữ liệu Claude Desktop, nhưng mất giờ reset chính xác, thống kê theo model và cảnh báo ngữ cảnh.

### 2. Build và chạy tool

```powershell
cd csharp
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ../bin
```

Nên chép `bin\*` sang một thư mục cố định (ví dụ `%LOCALAPPDATA%\ClaudeUsageTracker\app`) rồi chạy từ đó, để việc build lại không làm hỏng bản đang tự khởi động cùng Windows. Bật "Khởi động cùng Windows" trong Cài đặt (ghi registry `HKCU\...\Run`, không cần admin).

> Không dùng `--self-contained` + single-file cho app WPF này: bản đó crash ngay khi mở.

## Cấu trúc

```text
csharp/                  Ứng dụng WPF (.NET)
  Services/              Đọc dữ liệu, theo dõi file/foreground, thống kê, cảnh báo
  Views/                 HUD, Thống kê, Cài đặt, Toast
  SelfTest/              Kiểm thử logic (không cần gói ngoài)
statusline/statusline.js Script statusLine cho Claude Code
python/                  Bản thử nghiệm cũ, không còn được cập nhật
```

## Kiểm thử

```powershell
dotnet run --project csharp/SelfTest
```

Chạy các ca cho thống kê theo model, dự báo (kể cả nhiễu ±1% giữa 2 nguồn và qua lần reset), cảnh báo không báo lặp, và gộp lịch sử.

Biến môi trường để thử mà không đụng dữ liệu thật: `CLAUDE_USAGE_TRACKER_DIR` (thư mục dữ liệu của tool, dùng cho cả `statusline.js`) và `CLAUDE_USAGE_TRACKER_DESKTOP_HISTORY` (đường dẫn thay cho file Desktop).

## Lưu ý về thống kê model

Phần "model nào đốt quota" là **ước lượng**: lấy chênh lệch % giữa hai lần ghi liên tiếp (cách nhau ≤ 10 phút) rồi gán cho model của lần ghi sau. Quota dùng chung toàn tài khoản nên lượng dùng từ Desktop/web cũng có thể bị tính nhầm sang model đang chạy. Chỉ có dữ liệu khi dùng Claude Code CLI (Desktop không ghi tên model).
