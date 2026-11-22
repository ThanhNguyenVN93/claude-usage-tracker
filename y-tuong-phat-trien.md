# Danh sách ý tưởng nâng cấp tiếp theo — Claude Usage Tracker

Tổng hợp các tính năng hữu ích tiềm năng (chưa triển khai), phân nhóm theo mức độ tác động thực tế tới việc tiết kiệm Quota, tốc độ làm việc và trải nghiệm hàng ngày với **Claude Desktop + Claude Code CLI**.

---

## 🔥 Nhóm A: Tác động trực tiếp nhất (Tiết kiệm Quota & Tốc độ mỗi ngày)

1. **Đếm ngược Prompt Cache 5 phút (`Cache TTL Timer`)**
   - Prompt Cache của Claude tự hết hạn sau **5 phút** không tương tác.
   - Hiển thị đồng hồ đếm ngược trên HUD (ví dụ: `♻ Cache còn: 1m 20s`) và nhắc nhẹ ở 30 giây cuối để tránh mất trắng cache khi đang giữ context lớn (>100K token).

2. **Đo "Chỉ số Cache Hit" (`Cache Efficiency Score`)**
   - Đọc `cache_read_input_tokens` và `cache_creation_input_tokens` từ `statusline.js` để chấm điểm mức độ tái sử dụng cache của phiên hiện tại (ví dụ: `♻ Cache Hit: 91%`).

3. **Tự động gửi `continue` cho CLI khi vừa Reset (`Auto-Continue CLI`)**
   - Tương tự checkbox `Auto-continue when limits reset` của Claude Desktop: khi quota từ 100% vừa reset về `< 5%`, tool tự focus terminal đang chờ và gửi `continue` + Enter để làm tiếp công việc dang dở.

4. **Nút hành động trực tiếp trên Popup Toast (`Interactive Toast`)**
   - Gắn thẳng nút `[🧹 Compact]` hoặc `[⚡ Đổi Haiku]` ngay trên góc thông báo Toast cảnh báo 95% hoặc context >70% $\rightarrow$ bấm xử lý ngay trong 1 giây, không cần mở HUD.

5. **Giao diện Bật/Tắt nhanh từng MCP Server (`MCP Switcher UI`)**
   - Hiện danh sách checkbox từng MCP Server (GitHub, Playwright, Postgres...) kèm số token ước tính để tắt tạm các MCP chưa dùng tới chỉ bằng 1 click (có sao lưu file JSON an toàn).

---

## 🛠️ Nhóm B: Tiện ích quy trình làm việc & Riêng tư

6. **Tự động bật Chế độ riêng tư khi họp (`Auto-Privacy`)**
   - Tự nhận diện `Zoom`, `ms-teams`, `obs64`, hoặc tab `Google Meet` đang mở $\rightarrow$ tự động ẩn HUD và tắt Toast để tránh lộ tên dự án/chi phí khi share màn hình; tắt họp xong tự khôi phục lại.

7. **Mở lại nhanh 5 phiên Claude Code gần nhất (`--resume` Launcher)**
   - Menu *"🕒 Phiên gần đây"* liệt kê tên dự án + thời gian, click vào là tự mở Terminal tại đúng thư mục đó và chạy thẳng `claude --resume <sessionId>`.

8. **Nhắc "Mồi đồng hồ 5h sớm" (`Cycle Pre-Warm Reminder`)**
   - Nhắc vào khung giờ tuỳ chọn (ví dụ 7:00 sáng) gửi 1 tin nhắn ngắn để kích hoạt chu kỳ 5h sớm $\rightarrow$ đến trưa (12:00) là quota tự reset đầy 100% cho buổi chiều.

9. **Bắt lỗi API phổ biến & Gợi ý sửa 1-chạm (`Error Pattern Helper`)**
   - Nhận diện lỗi `529 Overloaded` / `Prompt too long` / `429` và hiện Toast gợi ý nút sửa ngay (ví dụ: Opus quá tải $\rightarrow$ nút `[⚡ Đổi Sonnet]`).

10. **Viên thuốc đếm ngược mini khi chạm trần 100% (`Auto Reset-Pill`)**
    - Khi quota chạm 100%, tự thu gọn thành một thanh pill tí hon `⏳ Reset: 18m` ở góc màn hình và tự biến mất ngay khi reset xong.

---

## ⚙️ Nhóm C: Tuỳ biến nâng cao

11. **Tuỳ chỉnh phím tắt cho Lệnh nhanh trong Cài đặt**
    - Cho phép đổi tổ hợp `Ctrl+Shift+H` / `Ctrl+Shift+C` trực tiếp trong `SettingsWindow` giống phím tắt HUD/Stats.

12. **Lệnh CLI xem nhanh từ Terminal (`cut status`)**
    - Gõ lệnh ngắn trong terminal bất kỳ để xem % quota, burn rate và giờ reset mà không cần chạm chuột.

13. **Đồng bộ cấu hình & lịch sử nhiều máy**
    - Lưu/đồng bộ `config.json` và `cli-history.jsonl` qua thư mục OneDrive / Google Drive giữa máy công ty và máy ở nhà.

14. **Chuyển nhanh sang API Key dự phòng (`Pay-as-you-go Fallback`)**
    - Bật tạm `ANTHROPIC_API_KEY` khi gói tháng chạm trần 100% và tự nhắc tắt ngay khi gói tháng reset để tránh tốn tiền oan.
