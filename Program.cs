using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using TNM.Management.Components;
using TNM.Management.Security;
using TNM.Management.Services;

// Offline-only recovery: --reset-owner-password prompts twice without echo.
// --reset-owner-default restores the publicly documented demo secret after
// interactive RESET confirmation. Never expose recovery through HTTP.
var ownerReset = args.Length == 1 && string.Equals(args[0], "--reset-owner-password", StringComparison.OrdinalIgnoreCase);
var ownerResetDefault = args.Length == 1 && string.Equals(args[0], "--reset-owner-default", StringComparison.OrdinalIgnoreCase);
var runFlowTests = args.Length == 1 && string.Equals(args[0], "--test-flows", StringComparison.OrdinalIgnoreCase);
if (runFlowTests)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    var report = FlowTester.RunAll(Directory.GetCurrentDirectory());
    Console.WriteLine($"=== KẾT QUẢ KIỂM THỬ TOÀN BỘ LUỒNG (TNM.Management V7) ===");
    foreach (var step in report.Steps)
        Console.WriteLine($"[{(step.Passed ? "PASS" : "FAIL")}] #{step.Number:00} [{step.Category}] {step.Name} ({step.DurationMs}ms)\n       -> {step.Detail}");
    Console.WriteLine($"----------------------------------------------------------");
    Console.WriteLine($"Tổng cộng: {report.Passed}/{report.Total} PASS · {report.Failed} FAIL · Thời gian: {report.TotalDurationMs}ms");
    Environment.ExitCode = report.AllPassed ? 0 : 1;
    return;
}
var offlineRecovery = ownerReset || ownerResetDefault;
if (!offlineRecovery && args.Any(x => x.StartsWith("--reset-owner-", StringComparison.OrdinalIgnoreCase)))
    throw new ArgumentException("Chỉ sử dụng: dotnet run -- --reset-owner-password hoặc --reset-owner-default");
var builder = WebApplication.CreateBuilder(offlineRecovery ? Array.Empty<string>() : args);
// Persist cookie-encryption keys across service restarts, outside public wwwroot.
var keyDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keyring");
Directory.CreateDirectory(keyDirectory);
builder.Services.AddDataProtection()
    .SetApplicationName("TNM.Management.V6")
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
// Nginx runs on this host and forwards only via 127.0.0.1. Loopback proxy
// addresses are trusted by default; do not trust arbitrary external proxies.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
});
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
{
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromHours(4);
    options.DisconnectedCircuitMaxRetained = 500;
    options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(2);
});
builder.Services.Configure<Microsoft.AspNetCore.SignalR.HubOptions>(hubOptions =>
{
    hubOptions.ClientTimeoutInterval = TimeSpan.FromHours(2);
    hubOptions.KeepAliveInterval = TimeSpan.FromSeconds(15);
    hubOptions.HandshakeTimeout = TimeSpan.FromMinutes(1);
});
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<DemoDb>();
builder.Services.AddScoped<V6Ops>();
builder.Services.AddScoped<V7Orders>();
builder.Services.AddScoped<InputCatalog>();
builder.Services.AddScoped<ProductMaster>();
builder.Services.AddScoped<AuditTrail>();
builder.Services.AddSingleton<AuthRepository>();
builder.Services.AddScoped<PermissionGate>();
builder.Services.AddSingleton<IAuthorizationHandler,LivePermissionHandler>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/account/login";
        options.AccessDeniedPath = "/account/denied";
        options.Cookie.Name = "TNM.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Configuration["TNM_REQUIRE_SECURE_COOKIE"] == "1"
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = async context =>
        {
            var id = AuthRepository.PrincipalId(context.Principal!);
            var stamp = AuthRepository.PrincipalStamp(context.Principal!);
            var auth = context.HttpContext.RequestServices.GetRequiredService<AuthRepository>();
            if (!auth.Valid(id, stamp))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    foreach (var def in Permissions.Definitions)
        options.AddPolicy(def.Key, policy => { policy.RequireAuthenticatedUser(); policy.AddRequirements(new LivePermissionRequirement(def.Key)); });
});
var app = builder.Build();
if (offlineRecovery)
{
    // The reset command is for an EXISTING SQLite database. Do not accidentally
    // create an empty DB or invoke first-run seeding as a side effect.
    var existingDb = Path.Combine(app.Environment.ContentRootPath, "App_Data", "tnm-local.db");
    if (!File.Exists(existingDb))
    {
        Console.Error.WriteLine("Không thấy App_Data/tnm-local.db. Hãy chạy tại đúng thư mục dự án có database cũ.");
        return;
    }
    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine("Đặt lại mật khẩu chỉ hỗ trợ PowerShell/terminal tương tác trực tiếp trên máy chủ.");
        return;
    }
}
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<DemoDb>().Initialize();
    scope.ServiceProvider.GetRequiredService<V7Orders>().Initialize();
    scope.ServiceProvider.GetRequiredService<InputCatalog>().Initialize();
    scope.ServiceProvider.GetRequiredService<ProductMaster>().Initialize();
    var repository = scope.ServiceProvider.GetRequiredService<AuthRepository>();
    repository.Initialize();
    scope.ServiceProvider.GetRequiredService<AuditTrail>().Initialize();
    if (offlineRecovery)
    {
        if (ownerResetDefault)
        {
            Console.Write("Đặt lại Owner về mật khẩu mặc định công khai (chỉ dùng khôi phục tại máy chủ)? Gõ RESET để xác nhận: ");
            if (Console.ReadLine() != "RESET")
            {
                Console.WriteLine("Đã hủy, database không bị thay đổi.");
                return;
            }
            repository.ResetOwnerPasswordFromLocalConsole(AuthRepository.DefaultOwnerPassword,requireChange:true);
            Console.WriteLine("Đã reset Owner về mật khẩu khởi tạo. BẮT BUỘC đổi mật khẩu sau đăng nhập.");
        }
        else
        {
            var first = ReadPasswordNoEcho("Mật khẩu OWNER mới (>=12 ký tự, có chữ và số): ");
            var confirm = ReadPasswordNoEcho("Nhập lại mật khẩu OWNER mới: ");
            if (!string.Equals(first, confirm, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Hai mật khẩu không khớp. Database không bị thay đổi.");
                return;
            }
            repository.ResetOwnerPasswordFromLocalConsole(first);
            Console.WriteLine("Đã cập nhật mật khẩu Owner và thu hồi các phiên đăng nhập cũ. Database nghiệp vụ được giữ nguyên.");
        }
    }
}
if (offlineRecovery) return;
// Must run before HSTS, auth and cookie generation: SSL terminates at Nginx.
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Error"); app.UseHsts(); }
// HTTP localhost demo: do not redirect without an actual HTTPS listener.
// For production/LAN, terminate HTTPS at a trusted reverse proxy and set TNM_ENABLE_HTTPS_REDIRECT=1.
if (builder.Configuration["TNM_ENABLE_HTTPS_REDIRECT"] == "1") app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthentication();
// Require immediate password change for accounts using the initial password.
// Do not block Blazor static/framework resources needed by the change-password page.
app.Use(async (http, next) =>
{
    if (http.User.Identity?.IsAuthenticated == true)
    {
        var path = http.Request.Path;
        var allowed = path.StartsWithSegments("/account/change-password") ||
            path.StartsWithSegments("/account/logout") ||
            path.StartsWithSegments("/_framework") ||
            path.StartsWithSegments("/_blazor") ||
            path.StartsWithSegments("/_content") ||
            path.StartsWithSegments("/css");
        if (!allowed)
        {
            var accounts = http.RequestServices.GetRequiredService<AuthRepository>();
            if (accounts.MustChangePassword(AuthRepository.PrincipalId(http.User)))
            {
                if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
                    http.Response.Redirect("/account/change-password");
                else http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
    }
    await next();
});
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/account/login", (HttpContext http, IAntiforgery xsrf, string? returnUrl, string? error, string? success) =>
{
    if (http.User.Identity?.IsAuthenticated == true) return (IResult)Results.Redirect(AuthPages.LocalUrl(returnUrl));
    var token = xsrf.GetAndStoreTokens(http).RequestToken ?? "";
    return Results.Content(AuthPages.Login(token, returnUrl, error == "1", success == "changed"),"text/html; charset=utf-8");
}).AllowAnonymous();
app.MapPost("/account/login", async (HttpContext http, IAntiforgery xsrf, AuthRepository repository, AuditTrail trail) =>
{
    try { await xsrf.ValidateRequestAsync(http); }
    catch (AntiforgeryValidationException) { return (IResult)Results.BadRequest("Invalid antiforgery token"); }
    var form=await http.Request.ReadFormAsync();
    var username=form["username"].ToString();
    var password=form["password"].ToString();
    var destination=AuthPages.LocalUrl(form["returnUrl"].ToString());
    var account=repository.Authenticate(username,password);
    if (account is null)
    {
        trail.RecordAction(null,"LOGIN_FAILED","/account/login");
        return Results.Redirect("/account/login?error=1&returnUrl="+Uri.EscapeDataString(destination));
    }
    trail.RecordAction(account.Id,"LOGIN","/account/login");
    if(destination == "/" && !repository.AllowedPermissions(account.Id).Contains(Permissions.DashboardView))
        destination=repository.LandingPath(account.Id);
    var claims=new List<Claim>
    {
        new(ClaimTypes.NameIdentifier,account.Id.ToString()),
        new(ClaimTypes.Name,account.DisplayName),
        new("tnm_username",account.Username),
        new("tnm_stamp",account.SecurityStamp)
    };
    // Avoid oversized authentication cookies. LivePermissionHandler reads effective rights
    // from SQLite on every authorization check; do not serialize roles/permissions into claims.
    var principal=new ClaimsPrincipal(new ClaimsIdentity(claims,CookieAuthenticationDefaults.AuthenticationScheme));
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,principal,
        new AuthenticationProperties { IsPersistent=false });
    if (repository.MustChangePassword(account.Id)) return Results.Redirect("/account/change-password");
    return Results.Redirect(destination);
}).AllowAnonymous();
app.MapGet("/account/change-password", (HttpContext http, IAntiforgery xsrf, AuthRepository repository, string? error) =>
{
    var id = AuthRepository.PrincipalId(http.User);
    var message = error switch
    {
        "old" => "Mật khẩu hiện tại không đúng hoặc mật khẩu mới không hợp lệ.",
        "mismatch" => "Hai mật khẩu mới không khớp.",
        _ => ""
    };
    return Results.Content(AuthPages.ChangePassword(xsrf.GetAndStoreTokens(http).RequestToken ?? "",
        http.User.FindFirst("tnm_username")?.Value ?? http.User.Identity?.Name,
        message,repository.MustChangePassword(id)),"text/html; charset=utf-8");
}).RequireAuthorization();
app.MapPost("/account/change-password", async (HttpContext http, IAntiforgery xsrf, AuthRepository repository) =>
{
    try { await xsrf.ValidateRequestAsync(http); }
    catch (AntiforgeryValidationException) { return (IResult)Results.BadRequest("Invalid antiforgery token"); }
    var form = await http.Request.ReadFormAsync();
    var current = form["currentPassword"].ToString();
    var newPassword = form["newPassword"].ToString();
    var confirmation = form["confirmPassword"].ToString();
    if (!string.Equals(newPassword,confirmation,StringComparison.Ordinal))
        return Results.Redirect("/account/change-password?error=mismatch");
    try { repository.ChangeOwnPassword(AuthRepository.PrincipalId(http.User),current,newPassword); }
    catch (InvalidOperationException) { return Results.Redirect("/account/change-password?error=old"); }
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/account/login?success=changed");
}).RequireAuthorization();
app.MapGet("/account/logout", (HttpContext http, IAntiforgery xsrf) =>
    Results.Content(AuthPages.Logout(xsrf.GetAndStoreTokens(http).RequestToken ?? ""),"text/html; charset=utf-8"))
    .RequireAuthorization();
app.MapPost("/account/logout", async (HttpContext http, IAntiforgery xsrf, AuditTrail trail) =>
{
    try { await xsrf.ValidateRequestAsync(http); }
    catch (AntiforgeryValidationException) { return (IResult)Results.BadRequest("Invalid antiforgery token"); }
    trail.RecordAction(AuthRepository.PrincipalId(http.User),"LOGOUT","/account/logout");
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/account/login");
}).RequireAuthorization();
app.MapGet("/account/denied", () => Results.Content(AuthPages.Denied(),"text/html; charset=utf-8")).AllowAnonymous();

app.MapGet("/export/inventory.csv", (DemoDb db, V6Ops ops, HttpContext http) =>
    ops.IsWarehouseRestricted(AuthRepository.PrincipalId(http.User)) ? (IResult)Results.Forbid()
    : Results.File(CsvExports.Inventory(db), "text/csv; charset=utf-8", "tnm-inventory.csv"))
    .RequireAuthorization(Permissions.InventoryLedgerExport,Permissions.InventoryView);
app.MapGet("/export/invoices.csv", (DemoDb db, AuthRepository repository, HttpContext http, string? type) =>
{
    var types = new List<string>();
    if (repository.HasPermission(http.User,Permissions.FinanceCustomerView)) types.Add("SALE");
    if (repository.HasPermission(http.User,Permissions.FinanceRenewView)) types.Add("RENEWAL");
    if (repository.HasPermission(http.User,Permissions.FinanceSupplierView)) {types.Add("PURCHASE");types.Add("SUPPLIER");}
    if (repository.HasPermission(http.User,Permissions.FinanceOtherView))
    {
        types.Add("SERVICE");
        types.Add("COMMISSION");
        types.AddRange(db.Invoices().Select(x=>x.Type).Where(x=>x is not ("SALE" or "RENEWAL" or "PURCHASE" or "SUPPLIER")).Distinct());
    }
    var suffix = string.IsNullOrWhiteSpace(type) || type == "ALL" ? "all" : type.ToLowerInvariant();
    return Results.File(CsvExports.Invoices(db,types,type), "text/csv; charset=utf-8", $"tnm-cong-no-{suffix}.csv");
}).RequireAuthorization(Permissions.FinanceExport,Permissions.FinanceView);
app.MapGet("/export/product-catalog.csv", (ProductMaster master,V6Ops ops,HttpContext http) =>
    ops.IsWarehouseRestricted(AuthRepository.PrincipalId(http.User)) ? (IResult)Results.Forbid()
    : Results.File(CsvExports.ProductCatalog(master),"text/csv; charset=utf-8","tnm-danh-muc-san-pham.csv"))
    .RequireAuthorization(Permissions.ProductsExport,Permissions.ProductsView);
app.MapGet("/export/quantity.csv", (V6Ops ops,HttpContext http) => Results.File(
    CsvExports.QuantityLedger(ops,AuthRepository.PrincipalId(http.User)),
    "text/csv; charset=utf-8","tnm-so-kho-so-luong.csv"))
    .RequireAuthorization(Permissions.QuantityStockView);
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

// Console.ReadKey(..., intercept:true) prevents passwords appearing in the
// terminal, PowerShell command history, application logs or process arguments.
static string ReadPasswordNoEcho(string prompt)
{
    Console.Write(prompt);
    var characters = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return new string(characters.ToArray());
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (characters.Count > 0) characters.RemoveAt(characters.Count - 1);
            continue;
        }
        if (!char.IsControl(key.KeyChar)) characters.Add(key.KeyChar);
    }
}
