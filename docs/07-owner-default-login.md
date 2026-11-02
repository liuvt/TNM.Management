# Owner mặc định và khôi phục

- Database SQLite mới (TNM_SEED_DEMO=0 hay 1): tạo `owner` với mật khẩu dùng thử `123456@Dixanh`. Mật khẩu được lưu dưới dạng PasswordHasher hash, không lưu cleartext trong SQLite.
- Cờ `must_change_password=1` buộc đổi mật khẩu sau lần đăng nhập đầu và quyền nghiệp vụ được từ chối trước khi đổi.
- Thao tác `/account/change-password` yêu cầu mật khẩu hiện tại, mật khẩu mới >=12 ký tự gồm chữ và số và khác mật khẩu hiện tại. Dùng CSRF token, session cookie và băm mật khẩu mới. Sau khi đổi, security stamp thay đổi và tất cả cookie cũ mất hiệu lực.
- Database đã có mật khẩu: startup không đổi mật khẩu. Offline `--reset-owner-default` chỉ chấp nhận bàn phím tương tác và yêu cầu gõ RESET. Offline `--reset-owner-password` cho phép đặt mật khẩu mới không hiện chữ trên terminal.
- Truy cập web bằng http://localhost:5236/account/login. Cảnh báo HTTPS redirect đã được vô hiệu trong chế độ HTTP localhost. Với mạng LAN/production, cấu hình HTTPS proxy, Secure Cookie và giới hạn cổng, không dùng mật khẩu mẫu.
- Không xóa database khi khôi phục mật khẩu; sao lưu App_Data trước cập nhật.
