# Prompt Chuẩn Import Hồ Sơ Thiết Bị (Định Dạng 12 Cột Chuẩn — TNM.Management)

Sử dụng nguyên văn Prompt dưới đây mỗi khi cần kiểm định và import file danh sách thiết bị theo mẫu 12 cột (`DATA_TB_TNM.xlsx` hoặc `.csv`) vào hệ thống **TNM.Management** trong tương lai:

---

## 📋 PROMPT COPY-PASTE SỬ DỤNG TRONG TƯƠNG LAI

```text
Đọc kỹ luồng nghiệp vụ hiện hành của hệ thống TNM.Management (2 trụ cột chính: Thiết bị `/devices` ↔ Đơn hàng `/sales-orders` [SO7 Lắp đặt / TM7 Thương mại / GH7 Gia hạn] đồng bộ thông suốt với Kho `/inventory` và Kế toán `/finance`).

Thực hiện Kiểm định ràng buộc dữ liệu đầu vào (Dry-Run) và Import file thiết bị đính kèm theo đúng 8 nhóm quy tắc bắt buộc sau:

1. RÀNG BUỘC CẤU TRÚC FILE & HEADER CHUẨN 12 CỘT (CHỐNG LỆCH CỘT):
   - Đọc tất cả các Sheet có dữ liệu theo thứ tự từ trái sang phải (ví dụ: Sheet tổng hợp gốc trước, Sheet `Update` cập nhật sau).
   - Bắt buộc kiểm tra đúng thứ tự 12 cột tiêu đề trước khi đọc dữ liệu:
     [1] STT | [2] Biển số xe/Số khung | [3] Số EMEI Thiết bị | [4] Tên Thiết bị | [5] Tên Đại lý | [6] Tên Khách hàng | [7] SĐT Khách hàng | [8] Trạng thái thiết bị | [9] Ngày kích hoạt | [10] Ngày hết hạn | [11] Ngày gia hạn gần nhất (nếu có) | [12] Số lần gia hạn.
   - Chặn ngay nếu bất kỳ Sheet nào sai thứ tự cột hoặc thiếu cột; tự động bỏ qua dòng tiêu đề lặp lại giữa bảng (tuyệt đối không để chữ 'BIỂNSỐ' / 'IMEI' lọt vào bảng `stock_units`).

2. RÀNG BUỘC & LÀM SẠCH MÃ IMEI (`Số EMEI Thiết bị`):
   - Tự động cắt khoảng trắng, gỡ dấu phẩy phân cách hàng nghìn do Excel định dạng số (VD: `"12,509,001,381"` → `12509001381`), chuyển chữ in hoa ASCII.
   - Chỉ chấp nhận IMEI khớp biểu thức `^[A-Z0-9\-]{6,32}$`; từ chối và báo lỗi rõ số dòng nếu IMEI rỗng, `0`, `#REF!`, `NaN` hoặc chứa ký tự có dấu.
   - Nếu trùng IMEI trong cùng file hoặc giữa Sheet gốc và Sheet `Update`: tự động gộp theo nguyên tắc ưu tiên Biển số mới / Ngày hết hạn xa hơn ở Sheet `Update`, nhưng bảo toàn `Số lần gia hạn` lớn hơn và `Ngày gia hạn gần nhất` khác rỗng của Sheet gốc (không để số `0` ở Sheet sau xóa mất lịch sử gia hạn cũ).

3. RÀNG BUỘC & KHÔI PHỤC BIỂN SỐ XE / SỐ KHUNG (`Biển số xe/Số khung`):
   - Làm sạch `#REF!`, `NaN`, dấu chấm thừa cuối biển số (`50H72133.` → `50H72133`), chuẩn hóa chữ in hoa.
   - Tự động khôi phục biển số xe điện/vận tải có chữ `E` bị Excel biến thành số mũ khoa học theo công thức toán học chính xác: `mE+N` → `{round(m*10):02d}E{(N-1):05d}` (VD: `6.70E+169` → `67E00168`, `6.8E+259` → `68E00258`, `2.10E+42` → `21E00041`).
   - Khi cập nhật thiết bị có sẵn:
     + Nếu DB đang lưu số khung có số `0` ở đầu (`000331`) mà Excel làm mất số `0` (`331`), phải giữ nguyên `000331`.
     + Nếu DB đang lưu tạm số khung 6 số (`846096`) ở cột Biển số và file mới cập nhật Biển số chính thức (`50F23226`), chuyển số khung cũ sang `vin_no` và cập nhật `plate_no = 50F23226`.

4. RÀNG BUỘC DANH MỤC TÊN THIẾT BỊ (`Tên Thiết bị`):
   - Chỉ chấp nhận danh sách trắng (Whitelist) khớp đúng mã sản phẩm chuẩn trong hệ thống:
     + `XANHGPS` → `SP000001` (Định vị Xanh GPS)
     + `NASAGPS` → `DX00002` / `SP000019` (nếu `stock_units` đã tồn tại thì giữ đúng `product_id` của `stock_units` để thỏa mãn Trigger `validate_activation_product`)
     + `TDCGPS`  → `SP000005` (Định vị TDC)
     + `AICAM`   → `SP000012` (Camera AI - mobicam)
     + `mGPS`    → `SP000006` (Định vị TDC - MGPS)
     + `CAM4CH`  → `CAM4CH` (Cam AI 4CH)
   - Từ chối ngay mọi dòng có `Tên Thiết bị` ngoài danh mục trên.

5. RÀNG BUỘC ÁNH XẠ ĐẠI LÝ / HTX / KHÁCH HÀNG & SĐT:
   - Làm sạch `#REF!`, `NaN`, `0`; tự động khôi phục số `0` đầu cho SĐT di động 9 số bị Excel cắt (`912345678` → `0912345678`).
   - Ánh xạ chính xác các tên viết tắt/biến thể trên server GPS về đúng danh mục Khách hàng chuẩn (`customers`) và đúng Nhóm giá (`DL-NHOM-1`, `DL-NHOM-2`, `DL-NHOM-3`, `B2B`, `SR`, `RETAIL`).
   - Nếu cột Đại lý rỗng hoặc là tên cá nhân lẻ (không chứa từ khóa `HTX`, `HỢP TÁC XÃ`, `ĐL`, `ĐẠI LÝ`, `CTY`, `CÔNG TY`, `TNHH`, `CỔ PHẦN`), gán về `Khách lẻ trực tiếp` (`RETAIL`). Nếu là HTX/Đại lý/Công ty mới thực sự, tự động khởi tạo mã KH mới đúng nhóm giá.

6. RÀNG BUỘC NGÀY THÁNG & CHỐNG ĐẢO THÁNG/NGÀY US-LOCALE EXCEL:
   - Hỗ trợ đọc chuẩn `dd/MM/yyyy`, số OADate Excel và chuỗi ISO.
   - Tự động phát hiện và đảo ngược chuẩn xác các cột/ô ngày bị Excel US-locale (`MM/dd/yyyy`) đảo `ngày ↔ tháng` khi `ngày <= 12` (VD: `12/10/2026` bị biến thành `2026-12-10` trong cùng lô có `27/10/2026`).
   - Kiểm tra miền năm hợp lệ `2018..2036`; cảnh báo rõ nếu có xe bảo hành/khóa thuê bao có `Ngày hết hạn < Ngày kích hoạt`.

7. RÀNG BUỘC BẢO TOÀN ĐỒNG NHẤT LIÊN PHÂN HỆ (ĐƠN HÀNG – KHO – THIẾT BỊ – KẾ TOÁN):
   - KHO & ĐƠN ĐANG MỞ (`IN_STOCK` Protection): Tuyệt đối KHÔNG tự ý trừ kho hoặc chuyển `INSTALLED` đối với các IMEI đang nằm trong tồn kho khả dụng (`IN_STOCK` / `CONSIGNED` / `TECHNICIAN`) hoặc đang gắn trên Đơn hàng chờ duyệt (`PENDING` / `APPROVED` / `DISPATCHED` / `REPORTED`), để đảm bảo kho không lệch và đơn hàng bấm Hoàn thành không bị báo trùng IMEI.
   - KHỚP ĐƠN ĐÃ HOÀN THÀNH (`CHỜ-NHẬP-IMEI`): Nếu IMEI trùng với `stock_units` đã `INSTALLED` thuộc Đơn hàng `COMPLETED` (`SO7`/`TM7` xuất sỉ hoặc chưa điền IMEI), tự động tạo dòng `activations` gắn trực tiếp vào `stock_unit_id` & `v7_order_id` của đơn đó, điền bổ sung `imei` + `plate_no` + `activation_id` lên `v7_order_lines`, giữ nguyên `customer_id` của đơn, KHÔNG trừ kho lần 2 và KHÔNG sinh hóa đơn trùng.
   - BẢO VỆ ĐƠN GIA HẠN ERP (`GH7`): Không bao giờ ghi đè làm giảm `expires_at`, `last_renewed_at`, `renewal_count` thấp hơn dữ liệu bảng `renewals` đã phát sinh trong ERP; không đổi `customer_id` của xe đã gắn đơn ERP hoặc đã được Owner chuyển đơn vị.
   - KẾ TOÁN (`/finance` Zero Fake Debt): Thiết bị khách hàng mới import tạo `stock_units(status='INSTALLED', supplier_id=NULL)` và cập nhật `activations.renewal_count` / `last_renewed_at` — tuyệt đối KHÔNG sinh hóa đơn `PURCHASE` NCC ảo hay hóa đơn `SALE`/`RENEWAL` phải thu ảo; đối soát `ReconcileAccountingData()` sau import phải đạt `0` sai lệch.

8. QUY TRÌNH THỰC THI AN TOÀN:
   - Sao lưu database trước khi chạy (`.backup`), chạy kiểm định Dry-Run trước, thực thi qua `DeviceImportService` trong 1 giao dịch nguyên tử (Atomic Transaction), chạy kiểm tra bất biến liên phân hệ sau import và xuất báo cáo tổng hợp chi tiết (Số dòng đọc, Thêm mới, Khớp đơn hoàn thành, Cập nhật, Giữ nguyên, Bảo vệ tồn kho/đơn chờ duyệt, Tự sửa lỗi định dạng Excel, Dòng lỗi bị chặn).
```
