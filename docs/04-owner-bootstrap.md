# Bootstrap Owner — TNM Management v6 (DefaultLogin)

Khi SQLite mới chưa có mật khẩu Owner, ứng dụng tạo `owner` với mật khẩu dùng thử `123456@Dixanh`. Hash được tạo bởi `PasswordHasher<Account>`; không ghi mật khẩu thô vào database. Lần đăng nhập đầu bắt buộc đổi tại `/account/change-password`, yêu cầu mật khẩu hiện tại, mật khẩu mới dài >=12 ký tự có chữ và số.

Owner có đủ 6 Role, được tự động gán Role mới; được bảo vệ khỏi khóa/xóa/thu hồi Role.

```powershell
$env:TNM_SEED_DEMO = '0'
dotnet restore
dotnet build
dotnet run --urls 'http://localhost:5236'
```

Nếu database đã tồn tại, startup không thay đổi password hash. Muốn khôi phục, dừng ứng dụng, sao lưu `App_Data/tnm-demo.db`, chạy `dotnet run --no-launch-profile -- --reset-owner-default`, gõ `RESET`, rồi khởi động lại. Khôi phục offline này đánh dấu phải đổi mật khẩu và vô hiệu hóa các session cũ.

Ngoài ra có lệnh `--reset-owner-password` để thay mật khẩu bằng terminal.

Mật khẩu mặc định là public, **chỉ phục vụ demo trên máy local**. Đưa vào LAN hoặc production cần HTTPS, mã hóa cookie đúng cấu hình proxy, đổi mật khẩu riêng ngay và sao lưu SQLite.
