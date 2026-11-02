using System.Text.Encodings.Web;

namespace TNM.Management.Security;

/// <summary>
/// Standalone cookie-auth screens. These endpoints return HTML (not Razor), so
/// the authentication visual theme is intentionally self-contained and offline.
/// Keep antiforgery tokens and POST targets unchanged.
/// </summary>
public static class AuthPages
{
    private static string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");

    public static string LocalUrl(string? value) => !string.IsNullOrWhiteSpace(value) && value.StartsWith('/') &&
        !value.StartsWith("//") && !value.Contains('\\') ? value : "/";

    private static string Frame(string title, string content) => $$$"""
<!doctype html>
<html lang="vi">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width,initial-scale=1" />
<meta name="theme-color" content="#faf9f9" />
<title>{{{E(title)}}} · TNM.Management</title>
<style>
:root{color-scheme:light;--coral:#ff614a;--coral-dark:#e64b36;--pink:#e84a78;--ink:#272737;--muted:#878895;--line:#e6e6ed}
*{box-sizing:border-box}
html,body{margin:0;min-height:100%;font-family:Inter,"Segoe UI",ui-sans-serif,system-ui,-apple-system,Arial,sans-serif;-webkit-font-smoothing:antialiased}
body{min-height:100vh;min-height:100dvh;background:radial-gradient(ellipse at 2% 2%,#fff0f1 0%,transparent 28%),radial-gradient(ellipse at 98% 98%,#fff0e8 0%,transparent 30%),#faf9f9;color:var(--ink);display:grid;place-items:center;padding:30px 18px}
a{color:var(--coral);text-decoration:none}a:hover{text-decoration:underline}
.auth-shell{width:100%;max-width:405px;position:relative;z-index:1}
.brand{display:flex;align-items:center;justify-content:center;gap:11px;margin-bottom:25px}.brand-logo{width:40px;height:40px;display:grid;place-items:center;color:var(--pink)}.brand-name{font-size:21px;font-weight:800;letter-spacing:-.85px;color:#272737}.brand-name em{font-style:normal;color:var(--coral)}
section{background:#fff;border:1px solid #ecebf0;border-radius:16px;padding:32px;box-shadow:0 16px 60px #2624360b,0 2px 8px #26243604}
.overline{font-size:10px;letter-spacing:1.4px;text-transform:uppercase;font-weight:770;color:var(--pink);margin-bottom:7px}
h1{margin:0 0 6px;font-size:25px;font-weight:760;letter-spacing:-.9px;line-height:1.3;color:#272737}
p{font-size:12px;color:var(--muted);line-height:1.7;margin:0 0 23px}
label{display:block;margin-bottom:17px;font-size:12px;font-weight:670;color:#484957}
.field-wrap{position:relative;margin-top:7px}
input{width:100%;min-width:0;height:44px;border:1px solid #e0e1e8;border-radius:9px;padding:10px 12px 10px 39px;color:#292b3a;background:#fff;outline:none;font:500 12px Inter,"Segoe UI",system-ui,sans-serif;transition:border-color .16s,box-shadow .16s}
input:focus{border-color:#ff9c89;box-shadow:0 0 0 3px #ff614a17}input::placeholder{color:#b0b1bb}input:disabled{background:#fafafa;color:#777987}
.field-icon{position:absolute;left:13px;top:12px;color:#a7a8b3;pointer-events:none}
.password-field input{padding-right:46px}
.toggle-visibility{position:absolute;right:7px;top:6px;width:33px;height:33px;border:0;background:transparent;border-radius:7px;display:grid;place-items:center;color:#9b9ba8;cursor:pointer}.toggle-visibility:hover{background:#fff1ed;color:var(--coral)}
button.primary{display:flex;align-items:center;justify-content:center;gap:8px;width:100%;margin-top:6px;min-height:43px;border:1px solid var(--coral);border-radius:9px;background:var(--coral);color:#fff;font:720 12.5px Inter,"Segoe UI",system-ui,sans-serif;cursor:pointer;transition:.16s}
button.primary:hover{background:var(--coral-dark);box-shadow:0 7px 19px #ff614a2d}button.primary:focus-visible,.toggle-visibility:focus-visible{outline:3px solid #ff614a55;outline-offset:2px}
.assistance{margin:18px 0 0!important;text-align:center;font-size:11px!important}.assistance a{font-weight:700}
.meta{margin-top:19px;text-align:center;color:#9b9ca6;font-size:10.5px}.meta strong{color:#72737e;font-weight:690}
.success,.error{border-radius:8px;padding:11px 12px;font-size:11.8px;line-height:1.6;margin:0 0 18px}.success{background:#ebf9f0;color:#207751;border:1px solid #d6efdf}.error{background:#fff0ed;color:#a73a2d;border:1px solid #ffded7}
.password-rule{font-size:11px;color:#9d8090;line-height:1.6;margin-top:-2px;margin-bottom:17px}
@media(max-width:480px){body{padding:20px 12px}.brand{margin-bottom:21px}section{padding:25px 22px;border-radius:14px}h1{font-size:23px}}
@media(prefers-reduced-motion:reduce){*,*:before,*:after{transition:none!important}}
</style>
</head>
<body>
<div class="auth-shell">
<header class="brand" aria-label="TNM Management">
<span class="brand-logo" aria-hidden="true"><svg width="40" height="40" viewBox="0 0 40 40" fill="none"><path d="M9 20h11l8-10M20 20l8 10" stroke="currentColor" stroke-width="3.3" stroke-linecap="round"/><circle cx="8" cy="20" r="4.4" fill="#fff" stroke="currentColor" stroke-width="3"/><circle cx="29" cy="9" r="4.4" fill="#fff" stroke="currentColor" stroke-width="3"/><circle cx="29" cy="31" r="4.4" fill="#fff" stroke="currentColor" stroke-width="3"/></svg></span>
<span class="brand-name">TNM<em>.</em>Management</span>
</header>
<section>{{{content}}}</section>
<footer class="meta"><strong>TNM Workspace</strong> · Quản trị vận hành</footer>
</div>
<script>
document.querySelectorAll('[data-toggle-password]').forEach(function(btn){btn.addEventListener('click',function(){var el=document.getElementById(btn.getAttribute('data-target'));if(!el)return;var visible=el.type==='password';el.type=visible?'text':'password';btn.setAttribute('aria-label',visible?'Ẩn mật khẩu':'Hiện mật khẩu');btn.setAttribute('aria-pressed',visible?'true':'false');});});
</script>
</body>
</html>
""";

    private static string FieldIcon(string kind) => kind switch
    {
        "user" => "<svg class=\"field-icon\" width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.7\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><circle cx=\"12\" cy=\"8\" r=\"4\"/><path d=\"M4 21v-2a8 8 0 0 1 16 0v2\"/></svg>",
        _ => "<svg class=\"field-icon\" width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.7\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><rect x=\"5\" y=\"10\" width=\"14\" height=\"11\" rx=\"2\"/><path d=\"M8 10V7a4 4 0 0 1 8 0v3\"/></svg>"
    };
    private const string ShowPasswordButton = "<button type=\"button\" class=\"toggle-visibility\" data-toggle-password aria-label=\"Hiện mật khẩu\" aria-pressed=\"false\"><svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.8\" stroke-linecap=\"round\"><path d=\"M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12Z\"/><circle cx=\"12\" cy=\"12\" r=\"3\"/></svg></button>";

    public static string Login(string csrf, string? returnUrl, bool failed, bool changed = false) => Frame("Đăng nhập", $$"""
<div class="overline">Chào mừng trở lại</div>
<h1>Đăng nhập</h1>
<p>Truy cập không gian quản lý TNM của bạn.</p>
{{(failed ? "<div class=\"error\" role=\"alert\">Tài khoản hoặc mật khẩu chưa đúng, hoặc tài khoản đang bị khóa.</div>" : "")}}
{{(changed ? "<div class=\"success\" role=\"status\">Đổi mật khẩu thành công. Vui lòng đăng nhập lại.</div>" : "")}}
<form action="/account/login" method="post">
<input type="hidden" name="__RequestVerificationToken" value="{{E(csrf)}}"/>
<input type="hidden" name="returnUrl" value="{{E(LocalUrl(returnUrl))}}"/>
<label for="username">Tài khoản<div class="field-wrap">{{FieldIcon("user")}}<input id="username" name="username" type="text" placeholder="Nhập tài khoản" autocomplete="username" required maxlength="64" autofocus /></div></label>
<label for="password">Mật khẩu<div class="field-wrap password-field">{{FieldIcon("lock")}}<input id="password" name="password" type="password" placeholder="Nhập mật khẩu" autocomplete="current-password" required />{{ShowPasswordButton.Replace("data-toggle-password", "data-toggle-password data-target=\"password\"")}}</div></label>
<button type="submit" class="primary">Đăng nhập <span aria-hidden="true">→</span></button>
</form>
""");

    public static string ChangePassword(string csrf, string? username, string? error, bool required) => Frame("Đổi mật khẩu", $$"""
<div class="overline">Bảo mật tài khoản</div>
<h1>Đổi mật khẩu</h1>
<p>{{(required ? "Bạn cần đổi mật khẩu mặc định để tiếp tục." : "Cập nhật mật khẩu đăng nhập của bạn.")}}</p>
{{(string.IsNullOrEmpty(error) ? "" : "<div class=\"error\" role=\"alert\">" + E(error) + "</div>")}}
<form action="/account/change-password" method="post">
<input type="hidden" name="__RequestVerificationToken" value="{{E(csrf)}}"/>
<label for="account">Tài khoản<div class="field-wrap">{{FieldIcon("user")}}<input id="account" type="text" value="{{E(username)}}" disabled /></div></label>
<label for="currentPassword">Mật khẩu hiện tại<div class="field-wrap">{{FieldIcon("lock")}}<input id="currentPassword" name="currentPassword" type="password" autocomplete="current-password" required /></div></label>
<label for="newPassword">Mật khẩu mới<div class="field-wrap">{{FieldIcon("lock")}}<input id="newPassword" name="newPassword" type="password" autocomplete="new-password" minlength="12" required /></div></label>
<label for="confirmPassword">Xác nhận mật khẩu<div class="field-wrap">{{FieldIcon("lock")}}<input id="confirmPassword" name="confirmPassword" type="password" autocomplete="new-password" minlength="12" required /></div></label>
<div class="password-rule">Tối thiểu 12 ký tự, gồm chữ và số. Không dùng lại mật khẩu mặc định.</div>
<button type="submit" class="primary">Lưu mật khẩu mới <span aria-hidden="true">→</span></button>
</form>
<p class="assistance"><a href="/account/logout">Đăng xuất</a></p>
""");

    public static string Logout(string csrf) => Frame("Đăng xuất", $$"""
<div class="overline">Bảo mật phiên làm việc</div>
<h1>Đăng xuất?</h1>
<p>Bạn muốn kết thúc phiên làm việc hiện tại?</p>
<form action="/account/logout" method="post"><input type="hidden" name="__RequestVerificationToken" value="{{E(csrf)}}"/>
<button type="submit" class="primary">Đăng xuất <span aria-hidden="true">→</span></button></form>
<p class="assistance"><a href="/">Trở lại trang chủ</a></p>
""");

    public static string Denied() => Frame("Không đủ quyền", "<div class=\"overline\">Quyền truy cập</div><h1>Không đủ quyền</h1><p>Tài khoản hiện tại chưa được cấp quyền sử dụng chức năng này. Vui lòng liên hệ người quản trị.</p><p class=\"assistance\"><a href=\"/\">← Quay về trang chủ</a></p>");
}
