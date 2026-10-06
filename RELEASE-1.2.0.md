# PhotoTone 1.2.0

PhotoTone kiểm tra bản phát hành mới trên GitHub và cho cập nhật ngay trong app sau khi người dùng đồng ý. Thêm icon riêng được tạo bằng image generation, đồng bộ với giao diện tối của app.

- Khi mở app, kiểm tra bản ổn định mới nhất ở nền; chỉ hiện lời nhắc khi có bản mới và bàn làm việc đang rảnh. Có nút kiểm tra thủ công ở chân cửa sổ.
- Chọn **Cập nhật và mở lại**, **Để sau** hoặc **Xem trên GitHub**. Không tải/cài trước khi đồng ý; có thể hủy khi đang tải.
- Kiểm tra SHA-256 GitHub, checksum ZIP/EXE, cấu trúc gói và phiên bản Windows x64. Lưu bàn làm việc trước khi đóng, thay EXE rồi mở lại; lỗi thay/khởi chạy được hoàn nguyên khi có thể.
- Giữ cấu hình, API key, ảnh nguồn, cache kết quả và file xuất. Không yêu cầu quyền quản trị; thư mục chứa EXE cần cho phép ghi.
- Icon ngôi nhà, cửa sổ sáng và ánh sao dùng màu mint/navy; tích hợp vào EXE, thanh tác vụ, cửa sổ và thanh tiêu đề trong app. ICO có 8 cỡ từ 16 đến 256 px, giữ nền trong suốt.

## Kiểm chứng

Build Release không lỗi/cảnh báo. **40/40 kiểm thử đạt**, gồm các kiểm thử 1.1 và thêm phiên bản GitHub, lỗi mạng/giới hạn, tải/checksum, chặn URL/ZIP sai, file bị khóa và hoàn nguyên khi mở bản mới thất bại.

Smoke test render giao diện và cửa sổ cập nhật ở 96/144 DPI. Kiểm thử trình cập nhật thực trên bản EXE riêng xác nhận đóng tiến trình cha, thay binary khác checksum và mở lại thành công; không truy cập bàn làm việc người dùng. Các kiểm thử không gọi API sinh ảnh có phí.

**Nâng từ 1.1 trở về trước:** tải ZIP này, giải nén rồi mở PhotoTone.exe. Những bản cũ chưa có updater; từ 1.2 trở đi có thể cập nhật trong app. Runtime đã kèm theo, không cần cài .NET.
