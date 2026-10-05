# PhotoTone 1.0.1 — sửa lỗi kích thước ảnh

Tùy chọn **Gốc** trước đây gửi thẳng `size=2560x1709`, gây HTTP 400 trên model chỉ nhận các kích thước cố định, như `openai/gpt-5-image`.

- **Gốc** nay dùng độ phân giải model hỗ trợ đủ lớn, hoặc kích thước nằm trong danh sách được phép; không gửi kích thước tùy ý.
- Với GPT-5 Image và ảnh 2560 × 1709, app báo không đủ độ phân giải ngay trên từng ảnh trước khi gửi API. Chọn model có 4K, ví dụ `google/gemini-3.1-flash-image`, để xử lý ảnh này.
- Danh sách độ phân giải không còn giữ mức không được model mới hỗ trợ; app yêu cầu chọn lại, không âm thầm đổi model hoặc giảm số pixel.
- Toàn bộ batch được kiểm tra trước khi gửi; nếu có ảnh không phù hợp, chưa ảnh nào bị gửi hoặc phát sinh phí từ thao tác đó.
- Chế độ kiểm tra giao diện không đọc/ghi API key hoặc cấu hình người dùng.

Vẫn xuất đúng kích thước ảnh gốc và từ chối kết quả thiếu pixel, không upscale bù. Đã thêm hồi quy cho chính lỗi GPT-5 Image trong ảnh báo lỗi, kiểm tra không có HTTP request khi kích thước bị chặn, lựa chọn 4K và danh sách kích thước cố định.

Kiểm thử: 16/16 tự động đạt; catalog OpenRouter trực tiếp xác nhận lựa chọn Gốc của Gemini dùng 4K và GPT-5 Image bị chặn cho ảnh 2560 × 1709. Không chạy yêu cầu sinh ảnh có tính phí trong quá trình sửa lỗi.
