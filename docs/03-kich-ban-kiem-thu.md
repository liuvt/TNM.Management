# Kịch bản nghiệm thu TNM v6

1. **Fresh install:** Xóa/cất database trong môi trường test, chọn TNM_SEED_DEMO=0 và khởi động. Có owner/Role, 119 mã sản phẩm Excel và 133 dòng kiểm kê SG/PQ; không có chứng từ/IMEI/đơn hàng giả.
2. **Login:** yêu cầu mật khẩu tối thiểu 12 ký tự, chứa chữ và số; đăng nhập sai bị từ chối; khóa tài khoản thu hồi phiên.
3. **SKU:** Tạo SKU hàng hóa **không IMEI**; kiểm tra kho số lượng có sản phẩm này. Tạo SKU dịch vụ thì **không được** nhập kho số lượng.
4. **Tồn kho:** Nhập 100; xuất 20 → tồn 80. Thử xuất 81 → không sinh bút toán nào. Điều chuyển 10 sang kho PQ → SG giảm 10, PQ tăng 10.
5. **Kiểm kê:** Tạo phiên SG, nhập thực tế thấp hơn 5, phê duyệt để ghi ADJUST; lần duyệt thứ hai bị từ chối. Nếu có phát sinh xuất/nhập giữa lúc đếm và lúc duyệt, phê duyệt phải bị từ chối.
6. **PO:** Tạo NCC, PO 20 hàng không IMEI, xác nhận nhận đủ; sổ kho phát sinh PURCHASE_RECEIVE 20 và không cho nhận lại.
7. **Đơn hàng:** Khai báo SKU IMEI, bảng giá cho KH, NCC và NV kỹ thuật; tạo đơn source NCC, sửa lịch & kỹ thuật, kiểm tra lịch sử; kích hoạt bằng IMEI đúng SKU, không dùng lại IMEI.
8. **Bảo hành:** Tiếp nhận một IMEI, gửi NCC, nhận lại về kho SG; xuất hiện đầy đủ `warranty_events`.
9. **Phân quyền:** Tạo User, cấp `inventory.quantity.view` + `.move`, scope SELECTED chỉ PQ; không xem được sổ SG/không xuất SG/không điều chuyển từ SG; bị chặn các màn hình legacy không scope.
10. **CSV:** `/export/quantity.csv` chỉ chứa giao dịch của các kho được phép; xuất CSV công nợ của role không được cấp phải bị từ chối.
11. **Bảo mật ứng dụng:** xác minh CSRF ở login/logout, thiết lập HTTPS, kiểm tra headers và các API trực tiếp; bảo đảm cách thu hồi phiên không rò quyền qua Blazor circuit cũ.
12. **Build:** `dotnet clean`, `dotnet restore`, `dotnet build`, `dotnet run` trên SDK .NET 9 và ghi lại lỗi (nếu có) để sửa trước khi nghiệm thu.
