# PhotoTone 1.1.0

Bàn làm việc nay nhận cả thư mục ảnh, trình bày mỗi job bằng cặp ảnh gốc/kết quả và cho chỉnh tiếp kết quả bằng prompt, nhiều ảnh tham chiếu và vùng chọn.

- Kéo thả file/thư mục, quét thư mục con; preview chỉ tải cho hàng đang hiển thị.
- Bấm ảnh mở lớn, zoom/pan, 100% và vừa cửa sổ.
- Hàng đợi chung 1–4 yêu cầu API đồng thời; thêm việc khi đang chạy, không chạy hai lượt trên cùng ảnh.
- Chỉnh bổ sung với box T1… và vùng nguồn A1.1…; box hướng dẫn AI, không khóa vùng ngoài box. Hoàn tác một bản trước, không gọi API.
- Khôi phục bàn làm việc sau khi đóng app; kết quả và bản trước lưu PNG trong cache riêng. Lỗi/hủy giữ kết quả hiện tại.
- Chỉ xuất khi bấm **Xuất tất cả**; chốt phiên bản tại lúc bấm, chọn JPEG/PNG/thư mục, tự tránh trùng tên. **Dọn dẹp** giữ ảnh nguồn và ảnh đã xuất.
- Giữ API key/model/prompt và mức đồng thời của bản trước. Bỏ tự thêm ảnh mẫu và tự xuất sau xử lý.
- Giữ kiểm tra pixel, EXIF, sRGB, DPAPI, không upscale, không tự retry/fallback. Chặn mức độ phân giải thủ công quá nhỏ trước API.

## Kiểm chứng

Build Release không lỗi/cảnh báo; **33/33 kiểm thử đạt** trên bản EXE. Bộ kiểm thử tự động bao gồm nhập 500 ảnh, FIFO/concurrency 1–4, dừng/chạy lại, payload thêm mây và cân bằng trắng tường, EXIF crop, giới hạn ảnh tham chiếu, lỗi ghi manifest, khôi phục, hoàn tác, xuất trùng tên, dọn lại file bị khóa và bảo vệ file nguồn/cache đang dùng.

Smoke test render giao diện ở 96/144 DPI và kiểm tra bàn làm việc 500 hàng chỉ tạo các hàng nhìn thấy. Báo cáo self-test của bản EXE được đính kèm release. Kiểm thử không gửi yêu cầu sinh ảnh có phí. Hai ví dụ chỉnh bổ sung đã kiểm tra bằng API giả lập; chất lượng AI thật và thao tác trên các màn hình/DPI thực tế cần nghiệm thu riêng.

Giải nén ZIP và chạy `PhotoTone.exe`. Runtime đã kèm theo, không cần cài .NET hoặc quyền quản trị.
