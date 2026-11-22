# Nhật ký sửa lỗi và nâng cấp tính năng (22/11/2026)

Tài liệu này tổng hợp toàn bộ các lỗi đã khắc phục, tính năng mới được bổ sung và kết quả rà soát 4 tầng + kiểm thử cuối cùng trong dự án **Claude Usage Tracker**.

---

## 1. Sửa lỗi khay hệ thống (System Tray) & Cập nhật qua Terminal

### 1.1. Sửa lỗi click chuột phải làm HUD re-new và menu biến mất
* **`csharp/Services/ForegroundAppWatcherService.cs`**:
  - Dùng Win32 `GetClassName` bỏ qua cửa sổ Taskbar (`Shell_TrayWnd`, `NotifyIconOverflowWindow`) để tương tác với khay hệ thống không kích hoạt ẩn/hiện HUD.
  - Tách riêng tiến trình `ClaudeUsageTracker`: giữ nguyên trạng thái HUD hiện tại khi click vào HUD/Settings/Stats/Menu.
  - Dùng `Interlocked.Exchange` chống chạy chồng chéo (re-entrancy) giữa các tick timer.
* **`csharp/App.xaml.cs`**:
  - Thêm cờ `_isMenuOpen` theo dõi `MouseDown(Right)`, `Opening`, `Opened`, `Closed` (kèm safety-net tự reset sau 600ms nếu menu không mở).
  - Tạm hoãn cập nhật `NotifyIcon.Text` / `Icon` và chặn `ShowFromWatcher()` / `ToggleHud()` khi menu đang mở.

### 1.2. Cập nhật qua Terminal / CMD / PowerShell (không cần tải từ web)
* Tạo **`update.bat`** tại thư mục gốc repo: tự động dừng app cũ, chạy `git pull`, biên dịch `dotnet build -c Release` và khởi động lại app.
* **`csharp/App.xaml.cs`**:
  - Tự nhận diện môi trường: nếu chạy trong Git repository thì mở CMD chạy `update.bat`; nếu chạy bản cài đặt standalone thì mở PowerShell (bật sẵn TLS 1.2, `-UseBasicParsing`, kiểm tra mã băm SHA-256, giải nén và khởi động lại).
  - Thêm cờ `_checkingUpdate` chống bấm đúp kiểm tra bản mới nhiều lần.

### 1.3. Làm rõ cơ chế đổi model Sonnet / Haiku ở ngưỡng 85% / 95%
* **Giới hạn nền tảng**: Claude Desktop không thể can thiệp từ ngoài; tính năng tự đổi model chỉ áp dụng cho **phiên MỚI của Claude Code CLI** (qua `~/.claude/settings.json`). Phiên CLI đang mở cần gõ `/model haiku` hoặc `/model sonnet`.
* Cập nhật thông điệp hướng dẫn rõ ràng trong `AlertService.cs`, `MiniHudWindow.xaml.cs`, và `SettingsWindow.xaml`.

---

## 2. Tính năng mới: Quản lý & Tiết kiệm Quota

### 2.1. Thanh lệnh nhanh 1-chạm trên HUD & Phím tắt toàn cục
* **`csharp/Services/TerminalCommandService.cs`**:
  - Tự nhớ cửa sổ Terminal / VS Code gần nhất (`LastTargetHwnd`) hoặc tìm tiến trình terminal dự phòng (và dispose toàn bộ `Process` handle).
  - Đợi nhả phím `Ctrl`/`Shift`/`Alt` (`GetAsyncKeyState`) trước khi dán để tránh kẹt tổ hợp khi gọi từ Global Hotkey.
  - Dán lệnh qua Clipboard + `Ctrl+V` + `Enter` (tự động dùng `Shift+Insert` cho Git Bash `mintty`), chờ 200ms trước khi khôi phục Clipboard cũ, có retry khi Clipboard bị khoá.
  - Miễn nhiễm 100% với lỗi nhảy dấu tiếng Việt của Unikey/EVKey.
* **Giao diện HUD (`MiniHudWindow.xaml`, `.cs`)**: Thêm 3 nút nhanh `⚡ Haiku`, `🧹 Compact`, `🔄 Clear`.
* **Global Hotkeys (`App.xaml.cs`)**: `Ctrl+Shift+H` (`/model haiku`) và `Ctrl+Shift+C` (`/compact`).

### 2.2. Đồng hồ đo tốc độ tiêu thụ (Burn-Rate Speedometer) & Bảo vệ Quota
* **Tốc độ đốt (`UsageInfo.BurnRatePerHour`)**: Hiển thị ngay trên HUD theo 3 mức: 🟢 Bình thường (`<15%/h`), 🟡 Khá nhanh (`15–30%/h`), 🔥 Rất nhanh (`≥30%/h`).
* **Bảo vệ Hạn ngạch Tuần (`StatsService.ComputeWeeklyBudgetNote`)**: Tính hạn mức dùng an toàn mỗi ngày dựa theo % tuần còn lại và mốc reset tuần.
* **Ước lượng tin nhắn còn lại (`StatsService.EstimateRemainingPromptsNote`)**: Quy đổi % chu kỳ 5h còn lại thành số tin nhắn ước lượng theo Opus / Sonnet / Haiku.
* **Phát hiện Agent lặp vô tận (`StatsService.DetectInfiniteLoop`)**: Cảnh báo đỏ (cooldown 10 phút) khi một phiên đốt $\ge 10\%$ quota chỉ trong 5 phút.
* **Âm thanh cảnh báo (`EnableSoundAlert`)**: Tùy chọn phát tiếng chuông hệ thống khi gặp cảnh báo mức Critical.

---

## 3. Phân tích chuyên sâu, Báo cáo & Bộ công cụ Tối ưu Token

### 3.1. Phân tích Quota theo Thư mục Dự án (Per-Project Breakdown)
* **`statusline/statusline.js`**: Ghi nhận thêm tên thư mục dự án (`project`) vào `cli-usage.json` và `cli-history.jsonl`.
* **`UsageSample.cs`, `HistoryService.cs`, `StatsService.cs`, `InsightsService.cs`**: Tổng hợp tỉ trọng đốt quota theo từng dự án (24h và 7 ngày).
* **`StatsWindow.xaml`, `.cs`**: Hiển thị mục **DỰ ÁN ĐỐT QUOTA (CLI)**, bọc nội dung trong `ScrollViewer` + kẹp toạ độ `Top` chống tràn màn hình nhỏ.

### 3.2. Xuất báo cáo & Dọn rác hệ thống
* **Xuất báo cáo (`StatsWindow`)**: Nút `📄 Xuất báo cáo` xuất file Markdown (`.md`) hoặc CSV (`.csv`, có escape dấu ngoặc kép).
* **Dọn Cache & Khôi phục cấu hình (`SettingsWindow`)**:
  - Nút `🧹 Dọn Cache & Log cũ`: Xoá file `.tmp`, `.old`, `error.log` và thư mục tạm.
  - Nút `↩ Khôi phục ~/.claude/settings.json gốc (.bak)`: Khôi phục cấu hình Claude Code ban đầu.

### 3.3. Bộ công cụ Tối ưu Token (`TokenToolkitService.cs` & Menu Tray)
* **🖼 Nén ảnh Clipboard**: Thu gọn ảnh >1280px về chiều ngang tối đa 1280px bằng `WriteableBitmap` độc lập trong RAM trước khi ghi lại Clipboard, tiết kiệm 50–70% Vision token.
* **🔌 Kiểm tra "Thuế" MCP Server**: Quét `claude_desktop_config.json` và `~/.claude.json` (cả cấp toàn cục lẫn từng dự án trong `projects`) để đếm MCP Server đang bật và ước lượng token schema mỗi lượt chat.
* **📁 Quét dự án (`CLAUDE.md` & `.claudeignore`)**: Kiểm tra độ dài `CLAUDE.md` và hỗ trợ 1-click tạo `.claudeignore` chuẩn.
* **📋 Kho Prompt mẫu tiết kiệm token**: Copy nhanh các prompt ngắn gọn vào Clipboard.
* **👁 Chế độ riêng tư (`PrivacyMode`) & Độ mờ HUD (`HudOpacity`)**: Ẩn HUD/Toast khi chia sẻ màn hình và tuỳ chỉnh độ trong suốt HUD (50–100%).

---

## 4. Kết quả rà soát 4 tầng & Kiểm thử cuối cùng

### Các điểm tinh chỉnh thêm qua rà soát 4 tầng:
1. **Tầng 1 (`ClaudeUsageService.cs`, `UsageFileWatcherService.cs`)**:
   - Sửa lỗi `ApplyCliResetTimes` quên mượn `sevenDayResetsAt` khi nguồn Desktop mới hơn CLI (khiến HUD bị mất dòng Reset tuần & Ngân sách tuần).
   - Bảo vệ `_debounceTimer.Change` bằng cờ `_disposed` và `try/catch (ObjectDisposedException)` khi thoát app.
2. **Tầng 2 (`TerminalCommandService.cs`)**: Tăng thời gian chờ sau dán lên `200ms` để đảm bảo terminal kịp đọc Clipboard trước khi hoàn trả Clipboard cũ.
3. **Tầng 3 (`MiniHudWindow.xaml.cs`)**: Kẹp `Top = Math.Max(work.Top, ...)` trong `SizeChanged` để thanh tiêu đề HUD không bị đẩy vượt quá mép trên màn hình.
4. **Tầng 4 (`statusline/statusline.js`, `SelfTest/Program.cs`)**: Chuẩn hoá `projectName` khi ở thư mục gốc ổ đĩa, kiểm tra đổi dự án trong `same`, và bổ sung test hồi quy cho `ApplyCliResetTimes`.

### Kết quả kiểm tra cuối cùng:
- **Biên dịch (`dotnet build csharp/ClaudeUsageTracker.csproj -c Release`)**: **0 Warning, 0 Error** (Build succeeded).
- **Kiểm thử tự động (`dotnet run --project csharp/SelfTest`)**: **134/134 test PASS**.
