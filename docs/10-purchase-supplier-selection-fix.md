# TNM.Management v6: Sửa chọn nhà cung cấp/PO

## Phạm vi

- Trang: `Components/Pages/Purchases.razor` (`/purchases`).
- CSS bổ sung: `wwwroot/css/app.css` (giữ tông Coral UI).
- Không sửa SQL schema, seed, đăng nhập, quyền Owner hoặc đường dẫn SQLite.

## Lỗi gốc từ source

1. Trong `Data/seed_empty.sql`, không có dòng tạo NCC. Trên DB mới, `Ops.Suppliers()` trả danh sách rỗng, nhưng bản cũ vẫn hiển thị select trống, không hướng dẫn người dùng.
2. `warehouse` dùng `@bind` nhưng `location` không được đặt lại khi kho thay đổi. Vị trí cũ có thể không thuộc kho mới; bản cũ âm thầm thay bằng vị trí đầu tiên khi lập PO.
3. Không có kiểm tra đủ các mã được chọn trước khi bật nút lập PO; thông báo lỗi chưa hướng dẫn nguyên nhân cụ thể.

## Đã sửa

- Hiển thị thông báo trống riêng cho NCC, SKU phù hợp, kho, vị trí.
- User có `MasterDataManage` tạo NCC tại chỗ. User thiếu quyền được hướng dẫn liên hệ quản trị.
- Có nút `Làm mới danh mục` (không phải tải lại ứng dụng).
- Danh sách SKU chỉ lấy `GOODS`, không IMEI, đang hoạt động (mặc định 74 SKU từ Excel 09/10).
- Chọn kho tự đồng bộ lại vị trí, chặn ID vị trí thuộc kho khác.
- Không cho gửi PO khi lựa chọn chưa hợp lệ; vẫn xác thực và kiểm tra quyền tại server.
- Đơn mua tạo ở trạng thái `DRAFT`. Chỉ tăng tồn khi nhấn `Xác nhận nhận đủ`.

## Kiểm thử SQLite

```bash
python3 scripts/test_purchases_selector.py
python3 scripts/test_v6_sqlite.py
```

Kiểm tra DB sạch không có NCC, 74 SKU phù hợp, tạo NCC, tạo PO, nhận 5 và tăng tồn đúng 5; khóa ngoại OK.
Chưa build Blazor trên môi trường không có .NET SDK; cần chạy `dotnet build` trên Windows trước khi publish VPS.

## Triển khai Ubuntu không mất dữ liệu

Trên máy build có .NET 9 SDK:

```powershell
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File .\deploy\linux\Publish-TNM-Linux.ps1
```

Upload `tnm-linux-publish.zip` lên VPS. Kiểm tra ZIP không chứa `App_Data`, dừng `tnm.service`, backup `tnm-demo.db`, rồi giải nén vào `/var/www/tnm-management` (giữ symlink `App_Data -> /var/www/tnm-datas`). Khởi động lại `tnm.service`. Không cần reset database.

Nếu mọi dropdown và nút bấm đều không tương tác (không chỉ NCC trống), kiểm tra DevTools > Network > `/_blazor` / `/_framework/blazor.web.js` và WebSocket phía Nginx.
