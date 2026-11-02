# TNM.Management · UI Redesign (Coral workspace)

## Mục tiêu
Thiết kế lại giao diện theo tinh thần SaaS tối giản, lấy cảm hứng bảng màu cam san hô/hồng của n8n; **không sao chép logo n8n**. Thương hiệu luôn là **TNM.Management**.

## Phạm vi thay đổi
- `Components/Layout/MainLayout.razor`: logo TNM dạng nút kết nối, sidebar trắng có nhóm chức năng, topbar tinh gọn, user và hành động tài khoản, mobile off-canvas. Giữ nguyên điều kiện quyền `Accounts.HasPermission`.
- `Components/Layout/AppIcon.razor`: biểu tượng SVG nội bộ, không cần CDN hoặc gói icon.
- `wwwroot/css/app.css`: CSS variables và bộ style đồng nhất cho 18 màn hình Razor gồm dashboard, thống kê, form, bảng, chip, ma trận quyền, trạng thái, responsive.
- `Security/AuthPages.cs`: đăng nhập, đổi mật khẩu, đăng xuất, không đủ quyền. Giữ `__RequestVerificationToken`, tên trường, action URLs. Nút xem/ẩn mật khẩu client-side.
- `Components/App.razor`: cache-bust CSS để trình duyệt không giữ lại theme cũ.
- `Components/Pages/Home.razor`: biểu tượng SVG trên shortcut.

## Design tokens
| Token | Value | Ý nghĩa |
|---|---|---|
| `--brand` | `#ff614a` | Nút primary / active navigation |
| `--accent` | `#eb4b77` | Nút kết nối logo / highlight |
| `--bg` | `#f8f8fa` | Background ứng dụng |
| `--surface` | `#ffffff` | Panel, form, sidebar |
| `--ink` | `#252637` | Chữ chính |
| `--muted` | `#747782` | Chữ phụ |
| `--line` | `#eaeaf0` | Divider |

Typography: Inter nếu đã cài sẵn, fallback Segoe UI/system-ui; không tải font ngoài, phù hợp VPS nội bộ.

## Kiểm tra sau build
1. `/account/login`: kiểm tra đăng nhập và CSRF, sai password, xem/ẩn password.
2. `/account/change-password`: người dùng bị buộc đổi password mặc định, đổi và đăng nhập lại.
3. `/`: Dashboard thẻ thống kê, biểu tượng shortcut.
4. `/products`, `/quantity-stock`, `/orders`: bảng tràn ngang, form, button.
5. `/admin/users`: ma trận quyền và dropdown.
6. Mobile 375px/768px: mở sidebar, chọn menu đóng sidebar, bảng cuộn ngang.
7. Sau publish, tải lại trình duyệt `Ctrl+F5` và kiểm tra CSS mới.

## Giữ nguyên
Không sửa các câu lệnh SQLite, repository, logic nghiệp vụ, cookies, chính sách phân quyền, database hay scripts publish Linux.
