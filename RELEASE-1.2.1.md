# PhotoTone 1.2.1

Thiết lập OpenRouter đơn giản hơn: không phải điền địa chỉ API, có hướng dẫn lấy key ngay trong app và chỉ cần lưu key một lần.

- Cố định API tại `https://openrouter.ai/api/v1`, bỏ ô sửa URL. Chặn máy chủ và đường dẫn API khác trước khi gửi HTTP.
- Nhắc **Thiết lập API key** khi chưa có key. Cấu hình mở sẵn hướng dẫn đăng ký/đăng nhập, tạo key, dán vào app và lưu; có nút mở OpenRouter, API Keys và trang nạp credits.
- Key được lưu mã hóa trong `%LOCALAPPDATA%\PhotoTone\settings.json` và tự nạp lần sau. Giữ key OpenRouter đã lưu; không dùng nhầm key của endpoint khác.
- Nút Lưu luôn hiện ở chân cửa sổ Cấu hình, kể cả khi mở hướng dẫn hoặc cuộn nội dung.

Build Release không lỗi/cảnh báo; **43/43 kiểm thử đạt**. Smoke test kiểm tra luồng Lưu → đóng → mở lại ô key, hướng dẫn lúc chưa có key, giao diện 96/144 DPI và trình cập nhật trên bản EXE riêng. Không gửi yêu cầu sinh ảnh có phí.

Đang dùng 1.2.0: bấm phiên bản ở chân cửa sổ để kiểm tra và chọn **Cập nhật và mở lại**, hoặc tải ZIP bên dưới.
