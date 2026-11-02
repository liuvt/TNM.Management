# TNM v6 Owner - Đăng nhập mặc định và đổi mật khẩu

**Chạy lần đầu với database SQLite mới:** `owner` / `123456@Dixanh`. Người dùng **phải đổi ngay mật khẩu** trước khi truy cập các phân hệ. Mật khẩu chỉ được dùng khi database chưa có hash Owner, không tự ghi đè password của database hiện có.

**Database đã tạo trước đây:** không tự đổi mật khẩu Owner. Để khôi phục về mật khẩu mặc định tại **PowerShell tương tác trên máy chủ**, dừng ứng dụng rồi chạy `dotnet run --no-launch-profile -- --reset-owner-default`, gõ `RESET` xác nhận, sau đó `dotnet run --urls "http://localhost:5236"`. Việc này thu hồi các phiên đăng nhập cũ. Hoặc dùng `--reset-owner-password` để đặt mật khẩu mới bằng bàn phím (an toàn hơn).

**Địa chỉ:** `http://localhost:5236/account/login`. Nếu chưa thấy giao diện: chạy một PowerShell thứ hai với `Invoke-WebRequest http://localhost:5236/account/login -UseBasicParsing` để phân biệt lỗi kết nối với lỗi đăng nhập. Chạy HTTP chỉ trên máy local; môi trường LAN/VPS phải cấu hình HTTPS.

**Đổi mật khẩu:** `http://localhost:5236/account/change-password` (mật khẩu hiện tại + mật khẩu mới + xác nhận). Sau khi đổi thành công, đăng nhập lại bằng mật khẩu mới.

---

# TNM Management v6 — Fresh Installation / Blazor Interactive Server

**Nền tảng:** .NET 9 (`net9.0`), Blazor Interactive Server, SQLite (`Microsoft.Data.Sqlite` 9.0.3), Cookie Authentication, PBKDF2, Role + per-User permission overrides. **Đây là mã nguồn demo phát triển**, cần build và kiểm thử nghiệp vụ/bảo mật trước khi chạy chính thức.


> **Bản sửa build ngày 19/10/2026:** Đã sửa 13 chuỗi SQL nhiều dòng gây `CS8997` và 360 lỗi dây chuyền trong `Security/AuthRepository.cs`. Xem `docs/05-buildfix-authrepository.md`. Kiểm thử SQLite đã đạt; vẫn cần chạy `dotnet build` trên Windows để xác nhận biên dịch ứng dụng.

## Giao diện Coral Workspace (v6.1)

Theme giao diện được thiết kế lại theo tông cam san hô/hồng kiểu n8n, nhưng giữ thương hiệu TNM.Management. Bao gồm giao diện đăng nhập, đổi mật khẩu, sidebar, dashboard, bảng dữ liệu, form và responsive. Không thay đổi dữ liệu SQLite hoặc quyền nghiệp vụ. Xem `docs/08-ui-coral-redesign.md`.


## Khôi phục mật khẩu Owner đã tồn tại

Dừng ứng dụng, sao lưu `App_Data/tnm-demo.db`, rồi chạy `dotnet run --no-launch-profile -- --reset-owner-password` tại thư mục project. Nhập mật khẩu mới hai lần trên terminal (không hiển thị). Không xóa SQLite; phiên đăng nhập cũ bị thu hồi. Xem `docs/06-reset-owner-password.md`. **Không dùng `scripts/reset-demo.ps1`** vì script đó xóa database.

## 1. Cài mới hoàn toàn — KHÔNG CẦN DATABASE

1. Cài .NET 9 SDK trên Windows. Giải nén ZIP và mở PowerShell tại thư mục chứa `TNM.Management.csproj`.
2. Dùng chế độ **database sạch** và chạy ứng dụng:

```powershell
$env:TNM_SEED_DEMO = '0'
dotnet restore
dotnet build
dotnet run --no-launch-profile --urls 'http://localhost:5236'
```

3. Mở **http://localhost:5236/account/login**. Đăng nhập `owner` / `123456@Dixanh`, hệ thống sẽ tự mở `/account/change-password` và **bắt buộc đổi mật khẩu** (>=12 ký tự, gồm chữ và số).
4. SQLite mới: `App_Data/tnm-demo.db`. Khởi tạo Owner, 119 SKU trong file kiểm kho, 133 dòng ảnh chụp kiểm kê SG/PQ và các vị trí SG/BH/VT. **Không tự tạo tồn kho vật lý, IMEI, khách hàng, đơn hàng, công nợ hay chứng từ giả.**
5. Sau khi đổi mật khẩu: đăng nhập lại, quản trị trên `/admin/master-data` và các màn hình khác.

**Với database đã có hash Owner**, khởi động lại sẽ KHÔNG ghi đè mật khẩu hiện có. Muốn reset về `123456@Dixanh` để đăng nhập: dừng server, sao lưu DB, chạy:

```powershell
dotnet run --no-launch-profile -- --reset-owner-default
# Gõ RESET khi được hỏi. Đây là lệnh offline, không phải URL trên web.
dotnet run --urls 'http://localhost:5236'
```

Hoặc sử dụng `--reset-owner-password` để đặt mật khẩu mới ngay từ console, không cần bước buộc đổi lần đầu.

## 2. Chế độ DEMO tùy chọn

Trong một thư mục cài **chưa tồn tại** `App_Data/tnm-demo.db`:

```powershell
$env:TNM_SEED_DEMO = '1'
# Không cần đặt biến môi trường mật khẩu; OWNER luôn khởi tạo một lần.

dotnet run --urls 'http://localhost:5236'
```

Danh mục Excel kiểm kho **09/10/2026** (119 SKU, 133 dòng kiểm kê SG/PQ) được nhập ở **cả hai chế độ**. Chỉ khi bật `TNM_SEED_DEMO=1` hệ thống mới tạo thêm khách hàng/đơn hàng/IMEI giả và một giao dịch tồn đầu **100 đơn vị** cho hàng mẫu. Không trộn database demo với dữ liệu kinh doanh thật.

## Khởi tạo danh mục sản phẩm và ảnh chụp kiểm kho TNM 09/10/2026

- Source: `Sản phẩm - Kiểm kho TNM ngày 09 10(1).xlsx`, sheet **Kho SG** (119 dòng) và **Kho PQ** (14 dòng).
- Mỗi lần ứng dụng khởi động, nếu dữ liệu nhập chưa đủ, `Services/DemoDb.Initialize()` thực thi `Data/catalog_2026_10_09.sql` trong giao dịch có `INSERT OR IGNORE`.
- Ghi vào các bảng: `product_groups`, `products`, `product_catalog_metadata`, `warehouses`, `warehouse_locations`, `stock_count_batches`, `stock_count_lines`. Bảng `stock_count_reviews` vẫn có thể cập nhật trạng thái kiểm tra mà không sửa dữ liệu Excel.
- Một SKU tồn tại ở nhiều sheet được tạo **một** sản phẩm, mỗi kho tạo một dòng kiểm kê riêng; các mã số như `20301` lưu theo `TEXT` để truy xuất đúng SKU.
- Cột **Tồn kho** lưu `book_qty` (kể cả số âm), **Thực tế** lưu `actual_qty` (ô trắng thành SQL `NULL`), SG/BH/VT lưu ở các trường `sg_qty`, `bh_qty`, `vt_qty`. Ghi chú ở cột J của Kho SG được giữ nguyên.
- `SP000005`: sổ **5**, thực kiểm **4**, SG **5**, ghi chú **Đang gửi bảo hành 1 cái**. Không tự sửa sai lệch.
- **Đặc biệt:** Đây là **ảnh chụp kiểm kê lịch sử** chứ KHÔNG phải tồn kho vận hành. SQL không sinh `quantity_movements` hoặc `stock_units`, vì 99 dòng SG chưa kiểm và có vùng phân bổ không khớp (SP000005). Chỉ nhập số tồn mở đầu sau khi duyệt với kho.
- Đăng nhập xem `/products` và `/stock-count`. Tài khoản giới hạn kho chỉ xem và đối soát các kho được cấp trong `/stock-count`.
- Đã có database v6 cũ: **không xóa DB**; bản mới tự bổ sung danh mục và dòng nguồn còn thiếu, không ghi đè SKU hoặc đối soát đã chỉnh.
- Test: `python scripts/test_tnm_catalog_seed.py` và `python scripts/test_v6_sqlite.py` (chạy trên máy có Python 3).

## 3. Các màn hình chính

| Đường dẫn | Nội dung | Phân quyền |
|---|---|---|
| `/` | Dashboard cũ (tổng quan) | `dashboard.view` |
| `/products` | Sản phẩm, hàng hóa, thiết bị, dịch vụ và giá vốn | `products.*` |
| `/customers` | Khách hàng, chỉnh sửa và bảng giá | `customers.*`, `prices.*` |
| `/quantity-stock` | Nhập/xuất/điều chuyển **số lượng không IMEI**; sổ tồn theo kho/vị trí | `inventory.quantity.*` |
| `/quantity-counts` | Kiểm kê theo vị trí, ghi thực kiểm, quản lý phê duyệt | `stock_count.quantity.*` |
| `/purchases` | Tạo đơn mua NCC và nhận số lượng | `purchase.*` |
| `/inventory` | Quản lý IMEI, ký gửi, xuất kỹ thuật (luồng cũ) | `inventory.*` |
| `/orders` | Đơn hàng nguồn TNM/NCC, phân công, sửa đơn chưa kích hoạt, kích hoạt | `orders.*` |
| `/warranties` | Tiếp nhận, gửi NCC, trả về kho, lịch sử trạng thái | `inventory.warranty` |
| `/devices` | Theo dõi thiết bị, cảnh báo thời hạn, gia hạn (luồng cũ) | `devices.*` |
| `/finance` | Công nợ, thanh toán theo hóa đơn (luồng cũ) | `finance.*` |
| `/admin/master-data` | Khai báo NCC / nhân viên / kho | `system.master_data.manage` |
| `/admin/users` | User → Role mẫu → cho phép/từ chối quyền theo chức năng | `users.*` |
| `/admin/warehouses` | Scope **kho** theo User | `users.warehouse_scope.manage` |
| `/export/quantity.csv` | Xuất nhật ký số lượng theo phạm vi kho được cấp | `inventory.quantity.view` |

## 4. Nguyên tắc kỹ thuật v6

- Sổ `quantity_movements` chỉ thêm dòng (`append-only`); SQL trigger cấm sửa/xóa dòng và âm tồn. Số lượng tính bởi `SUM(delta_qty)` theo SKU/kho/vị trí. Vị trí kho SG-BH-VT được quản lý riêng; **không cộng chồng snapshot** Excel với sổ kho động.
- Điều chuyển được ghi **2 bút toán** trong 1 transaction. Thiếu tồn → toàn bộ transaction rollback.
- Kiểm kê ghi tồn sổ lúc nhập thực kiểm. Duyệt một lần; nếu tồn hiện hành khác số tại thời điểm đếm → buộc kiểm lại, **không tự điều chỉnh âm thầm**.
- Đơn hàng chọn nguồn NCC, kỹ thuật viên, lịch lắp, bảng giá đúng khách; không có giá bán thì báo lỗi, không mặc định bán bằng giá vốn. Chỉ sửa đơn ở DRAFT/SCHEDULED, lưu `order_change_history`.
- Role/User quyền riêng v5 giữ lại; bổ sung quyền xem và thao tác cho v6. **Owner (duy nhất)** có toàn quyền bất biến, được gán đủ mọi Role kể cả Role mới; Role ADMIN cũng có toàn quyền chức năng đã triển khai. Thay đổi phạm vi kho sẽ làm mất hiệu lực phiên cũ.
- Tài khoản bị giới hạn kho có quyền với module kho số lượng / kiểm kê / mua hàng **theo kho được gán**. Các màn hình tổng hợp cũ chưa lọc theo kho (Dashboard, Kho IMEI, Đơn hàng, Thiết bị, Công nợ, Báo cáo) được **chặn hoàn toàn** thay vì hiển thị nhầm dữ liệu; cột tồn SG/PQ trong danh mục sản phẩm cũng bị che và endpoint xuất CSV cũ bị chặn. Đây là cơ chế an toàn tạm thời, chưa phải data scope toàn hệ thống.
- Cookie Auth cần HTTPS khi dùng LAN/IIS. Mật khẩu hash bằng PBKDF2; đổi quyền có security stamp.

## 5. Hạng mục v6 **chưa** hoàn thiện

Lưu ý: mô hình dữ liệu đã chuẩn bị một số bảng mà **chưa có luồng UI**. Chưa bao gồm: đơn nhiều SKU, gia hạn linh hoạt, hạch toán phải trả khi nhận PO, đối soát sao kê ngân hàng / Excel XLSX chuẩn kế toán, tính hoa hồng tự động, cảnh báo định kỳ tự động, BOM trừ tồn, phân trang SQL cho tất cả trang, giới hạn dữ liệu khách hàng và hạch toán giá vốn chính thức. Xem `docs/02-tien-do-34-hang-muc.md`.

## 6. Kiểm thử và vận hành

- Kiểm tra schema SQLite: `python scripts/test_v6_sqlite.py` (Python 3 dùng thư viện chuẩn).
- Sao lưu an toàn SQLite: `powershell -File scripts/Backup-TNM-v6.ps1` (yêu cầu Python 3).
- Build thực tế trên Windows: `dotnet restore && dotnet build` (hoặc hai dòng PowerShell).
- Chạy test thao tác: nhập 100 → xuất 20 → kiểm 75 → duyệt chênh lệch -5; lặp duyệt phải bị chặn; giao dịch âm tồn phải bị từ chối.
- Sử dụng nội bộ: đặt HTTPS, cấu hình IIS/Reverse Proxy, sao lưu `App_Data` **kể cả `.db`, `.db-wal` và `.db-shm` bằng cơ chế SQLite Backup API/VACUUM INTO**, không sao chép DB đang chạy bằng thao tác file thường.
- Nếu máy chạy không thành công, gửi toàn bộ log `dotnet build` và file `Program.cs` / file bị báo lỗi để sửa chính xác.

**Tình trạng kiểm chứng:** test SQL/schema đã chạy trong môi trường tạo source; môi trường đó không có .NET SDK nên **chưa xác nhận được C# / Razor biên dịch và ứng dụng chạy end-to-end**. Không đưa thẳng vào production.

Chi tiết init sản phẩm và kiểm kho: [`docs/09-excel-stocktake-bootstrap.md`](docs/09-excel-stocktake-bootstrap.md).
