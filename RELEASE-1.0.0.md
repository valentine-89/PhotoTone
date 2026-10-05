# PhotoTone 1.0.0 — Windows x64

Ứng dụng .NET/WPF chỉnh ảnh nội thất hàng loạt qua OpenRouter Image API.

- Nhúng ảnh mẫu `ok.jpg`, prompt tiếng Việt và ba ảnh gốc của phiên làm việc, đủ 2560 × 1709 pixel.
- Chọn/kéo nhiều ảnh; chạy 1–4 ảnh đồng thời; xem ảnh gốc và kết quả; dừng hàng đợi.
- Tải danh sách model từ OpenRouter; mặc định `google/gemini-3.1-flash-image`, 4K.
- Kiểm tra số pixel thực tế: từ chối ảnh nhỏ hơn hoặc sai tỷ lệ, không phóng lớn bù; xuất đúng kích thước ảnh gốc bằng JPEG/PNG sRGB.
- API key mã hóa DPAPI; không nhúng key; không tự gọi API khi mở app, không tự gửi lại hoặc đổi model khi lỗi.
- Giữ nguyên file gốc; kết quả và nhật ký theo từng ảnh, gồm chi phí API nếu có.

Giải nén ZIP và chạy `PhotoTone.exe`. Runtime đã kèm theo; không cần cài .NET hay quyền quản trị. Nhập API key của bạn để xử lý ảnh.

## Kiểm chứng

Build Release không lỗi/cảnh báo. 13/13 kiểm thử tự động đạt, gồm kích thước đầu vào/đầu ra, ngăn upscale, EXIF, DPAPI, API mock, concurrency, hủy và không tự retry. Bản EXE đã chạy self-test và render giao diện. Đã kiểm tra catalog OpenRouter trực tiếp: 37 model chỉnh ảnh đủ khả năng nhận hai ảnh tham chiếu tại thời điểm kiểm tra.

Chưa kiểm thử model tạo ảnh có tính phí vì máy chưa cấu hình API key OpenRouter. Kích thước đúng không bảo đảm AI giữ nguyên mọi chi tiết vật thể; cần xem kết quả trước khi sử dụng. Ảnh bị từ chối do thiếu pixel vẫn có thể đã phát sinh phí từ provider.
