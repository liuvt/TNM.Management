# TNM.Management v6 – Giao diện CRUD và khai báo tồn kho theo SKU

## Thay đổi
- Ẩn biểu mẫu nhập liệu mặc định tại các trang quản trị; nút thao tác bật/tắt từng form. Danh sách vẫn hiển thị liên tục.
- `/suppliers`: màn hình riêng tra cứu / thêm / cập nhật thông tin NCC, quyền `system.master_data.manage`.
- `/warehouse-catalog`: tạo/sửa kho, khai báo/sửa vị trí trong kho (MAIN mặc định), tôn trọng quyền `system.master_data.manage` và `inventory.location.manage`.
- `/products`: khai báo SKU, loại, ĐVT và các dòng tồn đầu theo **từng kho / vị trí / số lượng**. Có thể thêm nhiều dòng, mọi dòng dương được ghi loại `OPENING` trong cùng một SQLite transaction với sản phẩm mới.
- `/products`: xem tồn vận hành gộp theo kho, chi tiết SKU / kho / vị trí; ghi phiếu nhập bổ sung (`RECEIVE`) cho hàng không IMEI có quyền.
- `/quantity-stock` và `/quantity-counts`: đổi kho thì đặt lại vị trí tránh giữ ID của kho khác.
- Ngăn chuyển sản phẩm đã có sổ kho số lượng sang nhóm dịch vụ/combo/IMEI.
- Giữ bản tối ưu cookie: không nhúng tất cả quyền/Role vào cookie; phân quyền đọc từ SQLite.

## Không thay đổi
- Không migrate hoặc xóa database, không tự cộng dữ liệu kiểm kê Excel lịch sử vào tồn thực tế.
- Không tự khai báo số lượng hàng quản lý IMEI: phải nhập IMEI thực tế ở `/inventory`.
- Không tự kết chuyển số lượng từ bảng ảnh chụp kiểm kê 09/10/2026.
- Owner, Role, user permission, session stamp và liên kết `App_Data` giữ nguyên.

## Phân quyền
- Tạo/sửa NCC, kho: `system.master_data.manage`.
- Tạo/sửa vị trí: `inventory.location.manage`.
- Tạo sản phẩm: `products.create`, sửa: `products.update`.
- Nhập tồn đầu cùng sản phẩm: cần **đồng thời** `products.create` và `inventory.quantity.move`. Nhập thêm cho SKU hiện có: `inventory.quantity.move`.
- Chỉ truy vấn / ghi tại kho thuộc phạm vi tài khoản.

## Triển khai Ubuntu
1. Sao lưu `/var/www/tnm-datas` (đặc biệt SQLite và keyring), dừng `tnm.service`.
2. Build trên Windows với `dotnet build -c Release`, chạy `deploy/linux/Publish-TNM-Linux.ps1`.
3. Giải nén bản publish lên `/var/www/tnm-management`, không chép đè `App_Data` hoặc `/var/www/tnm-datas`.
4. Phân quyền code `root:tnm`, data `tnm:tnm` rồi khởi động `tnm.service`.
5. Thử: `/suppliers` → thêm NCC; `/warehouse-catalog` → tạo kho/vị trí; `/products` → thêm SKU cùng tồn đầu; `/quantity-stock` → đối chiếu chứng từ; `/purchases` → chọn NCC.

## Đã kiểm tra
- Python/SQLite contract test: `python scripts/test_catalog_warehouse_ui.py`.
- Kiểm tra tập tin và cấu trúc ZIP.
- Chưa thể thực hiện `dotnet build` và test tương tác Blazor do môi trường xử lý không có .NET SDK.
