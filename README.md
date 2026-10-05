# PhotoTone

Ứng dụng Windows .NET 8/WPF để chỉnh nhiều ảnh nội thất bằng model AI qua OpenRouter Image API. Bản phát hành có runtime, không cần cài .NET hay quyền quản trị.

## Sử dụng

1. Mở `PhotoTone.exe`, nhập API key OpenRouter của bạn rồi nhấn **Tải model**.
2. Chọn model và độ phân giải. Mặc định là `google/gemini-3.1-flash-image`, **4K**, một ảnh đồng thời.
3. Thêm/kéo nhiều ảnh; chọn ảnh mẫu và chỉnh prompt nếu cần.
4. Chọn thư mục, JPEG hoặc PNG; nhấn **Xử lý đã chọn**. Từng ảnh được gửi riêng với cùng ảnh mẫu và prompt. Nhấn **Dừng** để hủy phía ứng dụng.
5. Xem **Ảnh gốc / Kết quả**. Các file nguồn không bị ghi đè. Một ảnh đã hoàn tất sẽ không tự chạy lại; bỏ khỏi danh sách rồi thêm lại khi muốn tạo phiên bản khác.

API URL có thể thay đổi cho dịch vụ có cùng hợp đồng **OpenRouter Image API** (`GET /images/models`, `POST /images`). Đây không phải adapter cho mọi API chat tương thích OpenAI. Đổi endpoint sẽ xóa key khỏi ô nhập để tránh gửi nhầm. App không tự đổi model, nhà cung cấp hoặc API dự phòng; `allow_fallbacks=false`.

## Bộ mẫu nhúng

- `ok.jpg`: mẫu màu/ánh sáng hoàn thiện của phiên làm việc.
- `raw.jpg`, `raw1.jpg`, `raw2.jpg`: ba ảnh gốc 2560 × 1709.
- `default-prompt.txt`: hướng dẫn chỉnh sáng, cân bằng trắng, lệch màu cục bộ, canh line và bảo toàn cảnh vật.

Dữ liệu mẫu được trích xuất một lần vào `%LOCALAPPDATA%\PhotoTone\Samples`. Cấu hình và API key mã hóa DPAPI CurrentUser nằm trong `%LOCALAPPDATA%\PhotoTone\settings.json`. App không có sẵn key và không chạy API khi vừa mở. Ảnh và prompt chỉ được gửi đến endpoint/model đã chọn khi nhấn xử lý. Chi phí do tài khoản API của người dùng chịu; không gửi lại tự động khi lỗi hoặc timeout.

## Cam kết kích thước và giới hạn

App gửi ảnh gốc ở độ phân giải đầy đủ (chuẩn hóa hướng EXIF, chuyển profile màu sang sRGB và mã hóa PNG), không dùng thumbnail của giao diện làm đầu vào AI. Mặc định yêu cầu 4K. Lựa chọn **Gốc** chọn mức độ phân giải được model công bố đủ lớn cho ảnh nguồn (ví dụ 2560 × 1709 → 4K), hoặc kích thước cố định trong danh sách được phép. Không gửi tùy ý `size=2560x1709` tới model không hỗ trợ. App kiểm tra nội dung file thực trả về, không tin kích thước trong prompt. JPEG/PNG xuất có profile sRGB.

`openai/gpt-5-image` trả về giới hạn 1024 × 1024, 1024 × 1536 và 1536 × 1024 trong phản hồi lỗi API được ghi nhận ngày 2026-10-05. Với ảnh 2560 × 1709, app sẽ chặn trước khi gọi API; hãy chọn model có 4K như `google/gemini-3.1-flash-image`. App không tự đổi model. Catalog của OpenRouter chưa công bố danh sách kích thước cố định của model này, nên app bổ sung giới hạn từ phản hồi đã kiểm chứng; model không công bố kích thước khác sẽ bị chặn thay vì đoán.

- Kết quả có một chiều nhỏ hơn ảnh gốc: đánh dấu lỗi, không xuất và không phóng lớn bù.
- Sai tỷ lệ khung hình trên 0,75%: đánh dấu lỗi. Sai khác nhỏ do chuẩn làm tròn của model được căn giữa/crop tối thiểu khi xuất.
- Kết quả lớn hơn: hạ về đúng kích thước **ảnh gốc**, không giảm thấp hơn ảnh gốc. Ví dụ ảnh gốc 2560 × 1709 luôn xuất 2560 × 1709.
- File JSON đi kèm ghi kích thước ảnh gốc, kết quả provider, file xuất, model, prompt, mã băm ảnh gốc và chi phí API đã báo. Nhật ký batch ghi cả các ảnh lỗi.

Giữ số pixel không bảo đảm AI giữ nguyên mọi chi tiết: đây vẫn là model chỉnh/sinh ảnh. Cần xem lại vật thể, hoa văn, chữ và hình học. Nếu AI đã trả ảnh nhưng bị kiểm tra kích thước từ chối, yêu cầu vẫn có thể tính phí. Khi hủy hoặc mất mạng, xem trạng thái/chi phí thực tế tại nhà cung cấp trước khi chạy lại.

Đầu vào JPEG/PNG/TIFF/BMP, tối đa 40 MB/file và 100 MP; ảnh động hoặc TIFF nhiều trang chỉ dùng trang đầu. Giao diện dùng preview nhỏ để tiết kiệm RAM; file gửi/xuất dùng độ phân giải gốc. Tối đa 4 yêu cầu đồng thời. Chưa hỗ trợ camera RAW/HEIC/WebP trong bộ giải mã Windows của app.

## Build và kiểm thử

Yêu cầu .NET SDK 8 trên Windows, chạy PowerShell 7:

```powershell
pwsh -File .\scripts\build.ps1 -Publish
```

`--self-test <report.json>` chạy kiểm thử không gọi API thật: hợp đồng request, upload đủ pixel, giới hạn kích thước, EXIF, DPAPI, export, concurrency, hủy và lỗi HTTP. `--smoke-test <screenshot.png>` render giao diện thật rồi thoát. Không mở server/listener nên không yêu cầu cho phép firewall.

## Tài liệu API

- [OpenRouter Image API](https://openrouter.ai/docs/guides/overview/multimodal/image-generation)
- [Danh sách model và khả năng hiện hành](https://openrouter.ai/api/v1/images/models)

Danh sách model được tải từ API mỗi khi người dùng yêu cầu hoặc bắt đầu một batch chưa có danh sách. Không hardcode model dự phòng.
