# PhotoTone 1.1

Ứng dụng Windows .NET 8/WPF chỉnh ảnh nội thất hàng loạt qua OpenRouter Image API. Giải nén ZIP và mở `PhotoTone.exe`; đã có runtime, không cần quyền quản trị.

## Bàn làm việc

1. Mở **Cấu hình**, nhập API key OpenRouter, tải/chọn model, độ phân giải, ảnh mẫu và prompt ban đầu. Mặc định `google/gemini-3.1-flash-image`, 4K, một ảnh đồng thời; có thể chọn 1–4.
2. **Thêm ảnh**, **Thêm thư mục** hoặc thả nhiều file/thư mục vào app. Thư mục con được quét, liên kết thư mục được bỏ qua; file trùng đường dẫn không được thêm lần nữa. App báo các file không đọc được và tiếp tục với file hợp lệ.
3. Chọn checkbox và bấm **Xử lý**. Mỗi ảnh gọi API riêng. Có thể thêm ảnh và xếp thêm lượt khi các ảnh khác đang chạy. **Dừng** ngừng hàng đợi và hủy các yêu cầu đang chạy phía app; không tự gửi lại khi lỗi hoặc mất mạng.
4. Mỗi hàng hiển thị ảnh gốc và kết quả cạnh nhau. Bấm ảnh để mở lớn; cuộn chuột để zoom, kéo để di chuyển, chọn **100%** hoặc **Vừa cửa sổ**, Escape để đóng.
5. Đóng rồi mở app sẽ khôi phục bàn làm việc. Ảnh mẫu không tự thêm vào phiên mới; dùng **Cấu hình → Thêm bộ ảnh mẫu** nếu cần.

Danh sách dùng virtualization và preview nhỏ; bản đầy đủ chỉ đọc khi xử lý, xuất hoặc mở lớn. Thêm ảnh không tự gọi API.

## Chỉnh bổ sung

Bấm **Chỉnh bổ sung** ở ảnh đã có kết quả. Lượt mới lấy kết quả hiện tại làm đầu vào.

- Viết prompt để chỉnh toàn ảnh, hoặc vẽ một/nhiều box trên tab **Kết quả · T**.
- **Thêm ảnh** để đưa ảnh tham chiếu vào tab A1, A2… Có thể dùng toàn ảnh hoặc vẽ nhiều vùng nguồn A1.1, A1.2…
- Kéo vùng đã chọn để di chuyển; kéo các góc để đổi kích thước. **Xóa box** hoặc Delete xóa vùng đang chọn. Dùng **Di chuyển ảnh**, chuột giữa/phải hoặc giữ Space để kéo ảnh; cuộn chuột để zoom.
- Ví dụ: chọn vùng mây A1.1 trên ảnh tham chiếu, chọn cửa sổ T1 trên kết quả, nhập `Bổ sung mây A1.1 vào cửa sổ T1`.
- Ví dụ không cần ảnh phụ: chọn vùng tường T1, nhập `Cân bằng trắng lại bức tường ở T1`.
- Bấm **Xử lý** để đưa lượt chỉnh vào cùng hàng đợi. Một ảnh chỉ có một lượt đang chờ/chạy.

Box hướng dẫn AI, không phải mask khóa pixel. AI có thể thay đổi ngoài box. App gửi ảnh chính sạch, bản hướng dẫn có box nếu có vùng đích, và các ảnh/vùng tham chiếu được chọn; prompt chỉnh bổ sung độc lập với prompt chỉnh màu ban đầu. Vùng nguồn được cắt sau khi chuẩn hóa EXIF và giữ độ phân giải thật. App không vẽ box lên ảnh kết quả; cần xem lại đầu ra thực tế của model.

Số ảnh đầu vào bao gồm ảnh chính, bản hướng dẫn và từng vùng tham chiếu; app chặn trước API nếu vượt khả năng model, không tự bỏ ảnh hoặc chuyển model. Ảnh trả về chỉ thay thế kết quả sau khi kiểm tra và lưu thành công. Lỗi/hủy giữ nguyên kết quả cũ. **Hoàn tác** quay lại một bản trước, không gọi API; không giữ lịch sử ảnh không giới hạn. Lượt lỗi/gián đoạn có thể mở lại bằng **Chỉnh bổ sung** để sửa và gửi lại chủ động.

## Xuất và dọn dẹp

**Xuất tất cả** chốt các phiên bản đang có tại lúc bấm nút, cho chọn JPEG/PNG và thư mục. Xuất mọi ảnh đã có kết quả, không phụ thuộc checkbox. Các kết quả mới hoàn tất sau thời điểm đó thuộc lần xuất tiếp theo. File xuất dùng hậu tố `_edited`, thêm số nếu trùng tên; không ghi đè file nguồn hoặc file có sẵn. Lỗi một ảnh không ngăn xuất các ảnh còn lại.

Kết quả chỉ được lưu vào cache trước khi bấm xuất. **Dọn dẹp** xóa ảnh khỏi bàn làm việc, kết quả tạm và bản hoàn tác, giữ ảnh nguồn, file đã xuất và cấu hình. Khi còn kết quả chưa xuất, app xác nhận một lần. Dọn dẹp bị khóa trong lúc nhập, chuẩn bị, xử lý hoặc xuất ảnh.

Cache/manifest nằm tại `%LOCALAPPDATA%\PhotoTone\Workspace`; ảnh kết quả dùng PNG để không nén JPEG lặp qua các lượt chỉnh. File nguồn không bị sao chép vào cache: cần giữ chúng ở vị trí đã nhập. App kiểm tra dấu nhận diện file trước khi xử lý để phát hiện nguồn thay đổi/mất. Chỉ một phiên PhotoTone được mở bàn làm việc này cùng lúc. File đang xử lý hoặc được giữ cho lần xuất được bảo vệ khỏi dọn cache.

Cấu hình và key DPAPI CurrentUser tại `%LOCALAPPDATA%\PhotoTone\settings.json`; manifest không chứa key. Cấu hình từ 1.0.x vẫn được đọc, bao gồm mức đồng thời. Thư mục xuất cũ chỉ là gợi ý cho lần xuất; 1.1 không tự xuất sau xử lý. Lượt đang chạy lúc app bị ngắt được đánh dấu gián đoạn, không tự gọi lại API khi mở.

## Độ phân giải và API

- Đầu vào JPEG/PNG/TIFF/BMP, tối đa 40 MB/file và 100 MP; TIFF nhiều trang dùng trang đầu. Chưa hỗ trợ RAW/HEIC/WebP.
- Gửi đủ độ phân giải ảnh chính; chuẩn hóa EXIF, chuyển màu sRGB và mã hóa PNG. PNG gửi đi vượt 40 MB bị chặn, không giảm pixel để gửi.
- **Gốc** chọn mức độ phân giải/kích thước model công bố đủ lớn; không gửi kích thước tùy ý. Mức chọn thủ công quá nhỏ cũng bị chặn trước API.
- Kết quả thiếu pixel hoặc sai tỷ lệ quá 0,75% bị từ chối; không upscale bù. Kết quả lớn hơn được hạ về đúng kích thước ảnh gốc, crop căn giữa tối thiểu khi có sai số làm tròn.
- JPEG/PNG xuất giữ kích thước ảnh gốc và profile sRGB. Manifest giữ dấu nhận diện nguồn, model, prompt, vùng chọn, mã yêu cầu và chi phí API khi có.

API URL phải dùng hợp đồng OpenRouter Image API (`GET /images/models`, `POST /images`), không phải API chat bất kỳ. Đổi endpoint xóa key khỏi ô nhập; app không có sẵn key, không tự gọi API sinh ảnh khi mở. Luôn `allow_fallbacks=false`; không tự đổi model/provider hoặc retry yêu cầu tính phí.

Mỗi lượt xử lý/chỉnh bổ sung có thể tính phí từ tài khoản API. Kích thước đúng không bảo đảm AI giữ nguyên mọi chi tiết, đồ vật hoặc hình học; xem lại kết quả trước khi sử dụng. Kết quả bị app từ chối sau khi provider đã xử lý vẫn có thể tính phí.

Tài liệu: [OpenRouter Image API](https://openrouter.ai/docs/guides/overview/multimodal/image-generation), [catalog model](https://openrouter.ai/api/v1/images/models).

## Build và kiểm thử

Yêu cầu .NET SDK 8 trên Windows và PowerShell 7:

```powershell
pwsh -File .\scripts\build.ps1 -Publish
pwsh -File .\scripts\package.ps1
```

`--self-test <report.json>` chạy kiểm thử API giả lập, nhập 500 ảnh, hàng đợi, vùng chọn, cache, xuất, hoàn tác và lỗi. `--smoke-test <screenshot.png>` render giao diện chính, chỉnh bổ sung, ảnh tham chiếu và zoom; kiểm tra virtualization với 500 hàng và xuất ảnh render 96/144 DPI. Các chế độ này không đọc/ghi API key hoặc cấu hình người dùng, không mở listener hoặc gọi API sinh ảnh có phí. Smoke test dùng ảnh mẫu có sẵn để kiểm tra bố cục, không phải kết quả AI thực tế.

Build/test/mock và ảnh render không thay thế nghiệm thu chất lượng model thật hoặc thao tác kéo box trên các màn hình/DPI khác nhau. Hai tình huống mây/cửa sổ và cân bằng trắng tường vẫn cần kiểm tra bằng lượt AI thật được cho phép.

## Giấy phép

[MIT License](LICENSE) — Copyright (c) 2026 valentine-89.
