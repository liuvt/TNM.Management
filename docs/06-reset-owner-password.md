> Bản mới có thêm lệnh `--reset-owner-default` để khôi phục về mật khẩu được yêu cầu; buộc đổi ngay sau đăng nhập. Lệnh `--reset-owner-password` vẫn hoạt động.

# Khôi phục mật khẩu Owner trên TNM v6 (không mất dữ liệu)

Áp dụng cho **database đã tồn tại** `App_Data/tnm-demo.db` trên máy Windows. Không xóa SQLite và **không chạy** `scripts/reset-demo.ps1`.

1. Dừng web TNM (`Ctrl+C` tại PowerShell đang chạy `dotnet run`, hoặc dừng tiến trình/server IIS).
2. **Sao lưu** `App_Data/tnm-demo.db` (nếu chạy SQLite WAL thì tắt ứng dụng hẳn trước khi sao lưu).
3. Trong thư mục dự án có `TNM.Management.csproj`, chạy:

   ```powershell
   dotnet build
   dotnet run --no-launch-profile -- --reset-owner-password
   ```

   Hoặc chạy `./scripts/Reset-OwnerPassword.ps1` nếu chính sách PowerShell cho phép.
4. Nhập **hai lần** mật khẩu mới, tối thiểu 12 ký tự gồm chữ và số. Mật khẩu không được hiển thị, không đưa vào tham số CLI, URL hoặc file cấu hình.
5. Sau khi báo thành công, chạy web trở lại:

   ```powershell
   dotnet run --urls 'http://localhost:5236'
   ```

   Đăng nhập `owner` bằng mật khẩu mới. Tất cả cookie phiên trước bị vô hiệu do `security_stamp` đổi; số lần đăng nhập sai và khóa 15 phút được đặt về 0.

**Quan trọng:** Chỉnh `$env:TNM_OWNER_PASSWORD` **không đổi** mật khẩu Owner đã tạo, vì biến này chỉ dùng bootstrap tài khoản. Công cụ chỉ thực hiện reset offline từ terminal chạy trực tiếp tại máy có quyền ghi database. Không có endpoint HTTP reset mật khẩu. Giao dịch SQLite đảm bảo cập nhật hash + security stamp + audit log cùng lúc.

**Nếu thấy 'Không thấy database'**: kiểm tra thư mục dự án và `App_Data`; không dùng nhầm source mới tạo database trống. Nếu database thực tế nằm ở bản cài khác, thao tác trong đúng thư mục có DB.

**Giới hạn kiểm thử:** Đã kiểm tra câu lệnh SQL bằng SQLite; cần `dotnet build` và thử chạy trên Windows với SDK .NET 9 để xác nhận tích hợp.
