> Lưu ý: Tài liệu BuildFix cũ bên dưới chỉ để tham khảo. Bản Owner-DefaultLogin mới KHÔNG cần biến TNM_OWNER_PASSWORD khi tạo SQLite. Xem docs/07-owner-default-login.md.

# TNM Management v6 Owner — Bản sửa lỗi build 360 lỗi (19/10/2026)

## Lỗi gốc

`Security/AuthRepository.cs(60,114): error CS8997: Unterminated raw string literal`

Tổng cộng 13 chuỗi SQL nhiều dòng trong `AuthRepository.cs` được viết theo mẫu không hợp lệ:

```csharp
Exec(c, """SELECT ...
  FROM ...""");
```

Chuỗi `"""` trên mở và đóng cùng dòng với nội dung, nhưng phần nội dung trải qua nhiều dòng; cú pháp `raw string literal` trong C# không cho phép trường hợp này. Các lỗi CS1003, CS1525, CS1056, CS1012 ... ở những dòng sau chủ yếu là lỗi dây chuyền.

Đã chuyển thành `@"..."` (verbatim multiline string), giữ nguyên toàn bộ SQL, tham số `$id`/`$new` và nghiệp vụ Owner. Có tổng cộng **13 vị trí đã sửa**.

## Cách cập nhật

**Cách 1:** Giải nén `TNM-Management-v6-Owner-BuildFix.zip` vào thư mục riêng, sau đó build lại.

**Cách 2:** Nếu đã chỉnh sửa source hiện có, chép đè đúng **một file** `Security/AuthRepository.cs` từ bản sửa vào source cũ. Không chép đè thư mục `App_Data`.

```powershell
# Đứng tại thư mục chứa TNM.Management.csproj
dotnet clean
dotnet restore
dotnet build
# Chỉ chạy khi build thành công
dotnet run --urls 'http://localhost:5236'
```

Nếu chưa có database và chạy lần đầu:

```powershell
$secure = Read-Host 'Mat khau OWNER' -AsSecureString
$env:TNM_OWNER_PASSWORD = [System.Net.NetworkCredential]::new('', $secure).Password
$env:TNM_SEED_DEMO = '0'
```

## Kết quả kiểm tra và giới hạn

- `python scripts/test_v6_sqlite.py`: PASS (schema, tài khoản khởi tạo, số lượng tồn, ràng buộc giao dịch).
- `python scripts/test_owner_bootstrap.py`: PASS (owner, 6 Role, tự gán Role mới, các thao tác bị cấm, khóa ngoại).
- ZIP được kiểm tra CRC toàn bộ.
- **Không có .NET SDK trong môi trường đóng gói**, vì vậy chưa thể xác nhận `dotnet build` và kiểm thử Razor trên Windows. Nếu còn lỗi, gửi lại kết quả `dotnet build` mới; không dùng log cũ do nhiều lỗi là dây chuyền.
