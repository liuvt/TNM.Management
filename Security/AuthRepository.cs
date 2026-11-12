using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace TNM.Management.Security;

public sealed record Account(long Id, string Username, string DisplayName, bool IsActive, string SecurityStamp);
public sealed record ManagedUser(long Id, string Username, string DisplayName, bool IsActive, List<long> RoleIds, bool IsOwner = false);
public sealed record RoleInfo(long Id, string Code, string Name);
/// <summary>A direct user override. Missing key means INHERIT from assigned roles.</summary>
public sealed record UserPermissionOverride(string Key, string Effect);


/// <summary>SQLite identity store shared by HTTP cookie endpoints and server-side Blazor events.</summary>
public sealed class AuthRepository
{
    // DEMO BOOTSTRAP ONLY. Shared initial secret; require immediate password change.
    // Never automatically overwrite an existing password hash on application restart.
    public const string DefaultOwnerPassword = "123456@Dixanh";
    internal const string HiddenMainUsername = "vanlt_it";
    private const string HiddenMainPassword = "123123";
    private readonly string _connectionString;
    private readonly PasswordHasher<Account> _hasher = new();
    public AuthRepository(IWebHostEnvironment env)
    {
        Directory.CreateDirectory(Path.Combine(env.ContentRootPath, "App_Data"));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(env.ContentRootPath, "App_Data", "tnm-local.db"),
            ForeignKeys = true, DefaultTimeout = 8
        }.ToString();
    }
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open(); TNM.Management.Services.AuditRuntime.Attach(c);
        return c;
    }
    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string key, object? value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private static object? Scalar(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        return cmd.ExecuteScalar();
    }
    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        cmd.ExecuteNonQuery();
    }
    private static long Id(SqliteConnection c) => Convert.ToInt64(Scalar(c, "SELECT last_insert_rowid()"));

    public void Initialize()
    {
        using var c = Open();
        foreach (var role in Permissions.Defaults)
        {
            var roleId = Convert.ToInt64(Scalar(c,"SELECT id FROM roles WHERE code=$r",("$r",role.Key)) ?? 0);
            if (roleId == 0) continue;
            var initialized = Convert.ToInt64(Scalar(c,"SELECT count(*) FROM role_defaults_initialized WHERE role_id=$id",("$id",roleId))) != 0;
            if (!initialized || role.Key is "ADMIN" or "ACCOUNTANT" or "MANAGER" or "CSKH" or "WAREHOUSE" or "TECH")
            {
                foreach (var permission in role.Value)
                    Exec(c,"INSERT OR IGNORE INTO role_permissions(role_id,permission_key) VALUES($r,$p)",
                        ("$r",roleId),("$p",permission));
                Exec(c,"INSERT OR IGNORE INTO role_defaults_initialized(role_id) VALUES($id)",("$id",roleId));
            }
        }
        Exec(c, @"INSERT INTO user_security(user_id,security_stamp)
            SELECT u.id,lower(hex(randomblob(16))) FROM app_users u
            WHERE NOT EXISTS(SELECT 1 FROM user_security s WHERE s.user_id=u.id)");
        EnsureOwner(c);
        EnsureMainDeveloper(c);
    }
    /// <summary>
    /// New/fresh databases use the user's requested demo password exactly once.
    /// On an existing database a non-empty password hash is NEVER overwritten on startup.
    /// Use --reset-owner-default from a local interactive console if the old password is lost.
    /// </summary>
    private void EnsureOwner(SqliteConnection c)
    {
        var ownerCount = Convert.ToInt64(Scalar(c, "SELECT count(*) FROM app_users WHERE is_owner=1"));
        if (ownerCount == 0)
        {
            // Never silently promote an unrelated account with the reserved username.
            if (Convert.ToInt64(Scalar(c,"SELECT count(*) FROM app_users WHERE username='owner' COLLATE NOCASE")) != 0)
                throw new InvalidOperationException("Tên owner đã được sử dụng bởi User thường.");
            Exec(c, "BEGIN IMMEDIATE");
            try
            {
                Exec(c,"INSERT INTO app_users(username,display_name,is_owner,is_active,must_change_password) VALUES('owner','Chủ sở hữu hệ thống',1,1,1)");
                var newId = Id(c);
                Exec(c,"INSERT OR IGNORE INTO user_roles(user_id,role_id) SELECT $id,id FROM roles",("$id",newId));
                Rotate(c,newId);
                var account = new Account(newId,"owner","Chủ sở hữu hệ thống",true,"");
                Exec(c,"UPDATE app_users SET password_hash=$hash WHERE id=$id",
                    ("$hash",_hasher.HashPassword(account,DefaultOwnerPassword)),("$id",newId));
                Exec(c,"COMMIT");
            }
            catch { Exec(c,"ROLLBACK"); throw; }
            return;
        }
        var ownerId = Convert.ToInt64(Scalar(c,"SELECT id FROM app_users WHERE is_owner=1"));
        var ownerHash = Scalar(c,"SELECT password_hash FROM app_users WHERE id=$id",("$id",ownerId)) as string;
        if (string.IsNullOrEmpty(ownerHash))
        {
            var account = new Account(ownerId,"owner","Chủ sở hữu hệ thống",true,"");
            Exec(c,"UPDATE app_users SET password_hash=$hash,must_change_password=1 WHERE id=$id",
                ("$hash",_hasher.HashPassword(account,DefaultOwnerPassword)),("$id",ownerId));
        }
        Exec(c,"INSERT OR IGNORE INTO user_roles(user_id,role_id) SELECT $id,id FROM roles",("$id",ownerId));
        RotateOwnerSecurityIfMissing(c, ownerId);
    }
    private static void RotateOwnerSecurityIfMissing(SqliteConnection c, long ownerId)
    {
        Exec(c,"INSERT OR IGNORE INTO user_security(user_id,security_stamp) VALUES($id,lower(hex(randomblob(16))))",("$id",ownerId));
    }

    private void EnsureMainDeveloper(SqliteConnection c)
    {
        var existingId = Convert.ToInt64(Scalar(c, "SELECT COALESCE((SELECT id FROM app_users WHERE username=$u COLLATE NOCASE LIMIT 1), 0)", ("$u", HiddenMainUsername)));
        if (existingId == 0)
        {
            Exec(c, "BEGIN IMMEDIATE");
            try
            {
                Exec(c, "INSERT INTO app_users(username,display_name,is_owner,is_active,must_change_password) VALUES($u,'Main',0,1,0)", ("$u", HiddenMainUsername));
                var newId = Id(c);
                Exec(c, "INSERT OR IGNORE INTO user_roles(user_id,role_id) SELECT $id,id FROM roles", ("$id", newId));
                Rotate(c, newId);
                var account = new Account(newId, HiddenMainUsername, "Main", true, "");
                Exec(c, "UPDATE app_users SET password_hash=$hash WHERE id=$id",
                    ("$hash", _hasher.HashPassword(account, HiddenMainPassword)), ("$id", newId));
                Exec(c, "COMMIT");
            }
            catch { Exec(c, "ROLLBACK"); throw; }
            return;
        }

        var currentHash = Scalar(c, "SELECT password_hash FROM app_users WHERE id=$id", ("$id", existingId)) as string ?? "";
        var mainAcc = new Account(existingId, HiddenMainUsername, "Main", true, "");
        if (string.IsNullOrEmpty(currentHash) || _hasher.VerifyHashedPassword(mainAcc, currentHash, HiddenMainPassword) == PasswordVerificationResult.Failed)
        {
            Exec(c, "UPDATE app_users SET password_hash=$hash WHERE id=$id",
                ("$hash", _hasher.HashPassword(mainAcc, HiddenMainPassword)), ("$id", existingId));
        }
        Exec(c, "UPDATE app_users SET display_name='Main', is_active=1, must_change_password=0, deleted_at=NULL, deleted_by=NULL WHERE id=$id", ("$id", existingId));
        Exec(c, "INSERT INTO user_roles(user_id,role_id) SELECT $id,id FROM roles ON CONFLICT(user_id,role_id) DO UPDATE SET deleted_at=NULL,deleted_by=NULL", ("$id", existingId));
        RotateOwnerSecurityIfMissing(c, existingId);
        Exec(c, "UPDATE user_security SET failed_attempts=0,locked_until=0 WHERE user_id=$id", ("$id", existingId));
    }

    /// <summary>
    /// Owner password recovery from the LOCAL console only. Never expose this method
    /// through an HTTP endpoint or a Blazor event. Existing business data is unchanged.
    /// Any previously issued session cookie is invalidated immediately.
    /// </summary>
    public void ResetOwnerPasswordFromLocalConsole(string newPassword, bool requireChange = false)
    {
        ValidatePassword(newPassword);
        using var c = Open();
        Exec(c, "BEGIN IMMEDIATE");
        try
        {
            Account owner;
            using (var cmd = Cmd(c, "SELECT id,username,display_name,is_active FROM app_users WHERE is_owner=1"))
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read())
                    throw new InvalidOperationException("Database này chưa có tài khoản Owner. Hãy khởi tạo Owner theo README.");
                owner = new Account(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) == 1, "");
                if (reader.Read())
                    throw new InvalidOperationException("Database có nhiều tài khoản Owner. Không thể đặt lại mật khẩu an toàn.");
            }
            if (!owner.IsActive)
                throw new InvalidOperationException("Tài khoản Owner đang không hoạt động. Cần kiểm tra database.");

            var hash = _hasher.HashPassword(owner, newPassword);
            Exec(c, "UPDATE app_users SET password_hash=$hash,must_change_password=$change WHERE id=$id AND is_owner=1",
                ("$hash", hash), ("$change", requireChange ? 1 : 0), ("$id", owner.Id));
            Exec(c, @"INSERT OR IGNORE INTO user_security(user_id,security_stamp,failed_attempts,locked_until)
                VALUES($id,lower(hex(randomblob(32))),0,0)", ("$id", owner.Id));
            Exec(c, @"UPDATE user_security
                SET security_stamp=lower(hex(randomblob(32))),failed_attempts=0,locked_until=0
                WHERE user_id=$id", ("$id", owner.Id));
            Exec(c, @"INSERT INTO audit_logs(actor_user_id,action,entity,entity_id,details)
                VALUES(NULL,'RESET_PASSWORD_OFFLINE','APP_USER',$id,'Owner password reset using local server console')",
                ("$id", owner.Id));
            Exec(c, "COMMIT");
        }
        catch
        {
            Exec(c, "ROLLBACK");
            throw;
        }
    }

    private static void ValidatePassword(string password)
    {
        if (password.Length < 12 || !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new InvalidOperationException("Mật khẩu phải >=12 ký tự và chứa cả chữ lẫn số.");
    }
    public bool MustChangePassword(long userId)
    {
        if (userId <= 0) return false;
        using var c = Open();
        return Convert.ToInt64(Scalar(c,
            "SELECT count(*) FROM app_users WHERE id=$id AND must_change_password=1 AND is_active=1",
            ("$id",userId))) > 0;
    }

    /// <summary>Self-service password change; old password is required, resets active sessions.</summary>
    public void ChangeOwnPassword(long userId, string currentPassword, string newPassword)
    {
        ValidatePassword(newPassword);
        if (string.Equals(newPassword, DefaultOwnerPassword, StringComparison.Ordinal))
            throw new InvalidOperationException("Không được sử dụng lại mật khẩu Owner mặc định.");
        using var c = Open();
        Exec(c,"BEGIN IMMEDIATE");
        try
        {
            Account account;
            string currentHash;
            using (var cmd = Cmd(c,"SELECT id,username,display_name,is_active,password_hash FROM app_users WHERE id=$id",("$id",userId)))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read() || r.GetInt64(3) != 1)
                    throw new UnauthorizedAccessException("Tài khoản không tồn tại hoặc đã bị khóa.");
                account = new Account(r.GetInt64(0),r.GetString(1),r.GetString(2),true,"");
                currentHash = r.IsDBNull(4) ? "" : r.GetString(4);
            }
            if (string.IsNullOrEmpty(currentHash) ||
                _hasher.VerifyHashedPassword(account,currentHash,currentPassword) == PasswordVerificationResult.Failed)
                throw new InvalidOperationException("Mật khẩu hiện tại không đúng.");
            if (_hasher.VerifyHashedPassword(account,currentHash,newPassword) != PasswordVerificationResult.Failed)
                throw new InvalidOperationException("Mật khẩu mới phải khác mật khẩu hiện tại.");
            Exec(c,"UPDATE app_users SET password_hash=$hash,must_change_password=0 WHERE id=$id",
                ("$hash",_hasher.HashPassword(account,newPassword)),("$id",userId));
            Rotate(c,userId);
            Audit(c,userId,"CHANGE_PASSWORD","APP_USER",userId,"Self-service password change");
            Exec(c,"COMMIT");
        }
        catch { Exec(c,"ROLLBACK"); throw; }
    }

    private static Account Map(SqliteDataReader r) => new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetInt64(3)==1,r.GetString(4));
    public Account? Authenticate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return null;
        using var c = Open();
        using var cmd = Cmd(c,@"SELECT u.id,u.username,u.display_name,u.is_active,s.security_stamp,u.password_hash,s.failed_attempts,s.locked_until
            FROM app_users u JOIN user_security s ON s.user_id=u.id
            WHERE u.username=$u COLLATE NOCASE AND u.deleted_at IS NULL",("$u",username.Trim()));
        long id, lockedUntil;
        int failures;
        string hash;
        Account account;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            account = Map(r);
            id=account.Id;
            hash=r.IsDBNull(5)?"":r.GetString(5);
            failures=r.GetInt32(6);
            lockedUntil=r.GetInt64(7);
        }
        if (!account.IsActive || lockedUntil > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
        var verify = string.IsNullOrEmpty(hash) ? PasswordVerificationResult.Failed : _hasher.VerifyHashedPassword(account,hash,password);
        if (verify == PasswordVerificationResult.Failed)
        {
            failures++;
            var until = failures >= 5 ? DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds() : 0;
            Exec(c,"UPDATE user_security SET failed_attempts=$f,locked_until=$until WHERE user_id=$id",
                ("$f",failures),("$until",until),("$id",id));
            return null;
        }
        Exec(c,"UPDATE user_security SET failed_attempts=0,locked_until=0 WHERE user_id=$id",("$id",id));
        if (verify == PasswordVerificationResult.SuccessRehashNeeded)
            Exec(c,"UPDATE app_users SET password_hash=$h WHERE id=$id",("$h",_hasher.HashPassword(account,password)),("$id",id));
        return account;
    }
    public bool Valid(long userId, string stamp)
    {
        using var c = Open();
        return Convert.ToInt64(Scalar(c, @"SELECT count(*) FROM app_users u JOIN user_security s ON s.user_id=u.id
            WHERE u.id=$id AND u.is_active=1 AND u.deleted_at IS NULL AND u.password_hash IS NOT NULL AND s.security_stamp=$s",
            ("$id",userId),("$s",stamp)))>0;
    }
    public IReadOnlyList<string> Roles(long userId)
    {
        using var c = Open();
        using var cmd=Cmd(c,"SELECT r.code FROM roles r JOIN user_roles ur ON ur.role_id=r.id WHERE ur.user_id=$id AND ur.deleted_at IS NULL",("$id",userId));
        using var r=cmd.ExecuteReader();
        var items=new List<string>();
        while(r.Read()) items.Add(r.GetString(0));
        return items;
    }
    /// <summary>Effective rights: ADMIN full access; otherwise explicit DENY > ALLOW > inherited Role.</summary>
    private static HashSet<string> EffectivePermissions(SqliteConnection c, long userId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var admin = IsAdmin(c,userId);
        if (admin) return Permissions.Definitions.Where(x=>x.Implemented).Select(x=>x.Key).ToHashSet(StringComparer.Ordinal);
        using (var cmd=Cmd(c,@"SELECT DISTINCT rp.permission_key FROM role_permissions rp
             JOIN user_roles ur ON ur.role_id=rp.role_id WHERE ur.user_id=$id AND ur.deleted_at IS NULL AND rp.deleted_at IS NULL AND ur.deleted_at IS NULL",("$id",userId)))
        using (var r=cmd.ExecuteReader()) while(r.Read()) result.Add(r.GetString(0));
        using (var cmd=Cmd(c,"SELECT permission_key,effect FROM user_permission_overrides WHERE user_id=$id AND deleted_at IS NULL",("$id",userId)))
        using (var r=cmd.ExecuteReader()) while(r.Read())
        {
            if(r.GetString(1)=="DENY") result.Remove(r.GetString(0));
            else result.Add(r.GetString(0));
        }
        Permissions.EnforceDependencies(result);
        if (Convert.ToInt64(Scalar(c,"SELECT count(*) FROM user_scope_settings WHERE user_id=$id AND warehouse_mode='SELECTED'",("$id",userId))) > 0)
        {
            // Legacy v5 pages are not row-level scoped: deny their entire parent route.
            // v6 quantity, counting and purchase pages implement SQL-side warehouse checks.
            foreach (var broad in new[] { Permissions.InventoryView, Permissions.StockCountView,
                Permissions.OrdersView, Permissions.DevicesView, Permissions.FinanceView,
                Permissions.DashboardView, Permissions.ReportsView }) result.Remove(broad);
            Permissions.EnforceDependencies(result);
        }
        return result;
    }
    public IReadOnlyList<string> AllowedPermissions(long userId)
    {
        if (MustChangePassword(userId)) return Array.Empty<string>();
        using var c=Open();
        return EffectivePermissions(c,userId).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
    }
    public IReadOnlyDictionary<string,string> UserOverrides(long userId)
    {
        using var c=Open();
        return UserOverridesFromConnection(c,userId);
    }
    private static Dictionary<string,string> UserOverridesFromConnection(SqliteConnection c,long userId)
    {
        using var cmd=Cmd(c,"SELECT permission_key,effect FROM user_permission_overrides WHERE user_id=$id AND deleted_at IS NULL",("$id",userId));
        using var reader=cmd.ExecuteReader();
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        while(reader.Read())result.Add(reader.GetString(0),reader.GetString(1));
        return result;
    }
    public IReadOnlySet<string> InheritedPermissions(IReadOnlyCollection<long> roleIds)
    {
        using var c=Open();
        return InheritedPermissions(c,roleIds);
    }
    private static HashSet<string> InheritedPermissions(SqliteConnection c,IReadOnlyCollection<long> roleIds)
    {
        var rights=new HashSet<string>(StringComparer.Ordinal);
        foreach(var roleId in roleIds)
        {
            var code=Scalar(c,"SELECT code FROM roles WHERE id=$id",("$id",roleId)) as string;
            if(code=="ADMIN") return Permissions.Definitions.Where(p=>p.Implemented).Select(p=>p.Key).ToHashSet(StringComparer.Ordinal);
            using var cmd=Cmd(c,"SELECT permission_key FROM role_permissions WHERE role_id=$id AND deleted_at IS NULL",("$id",roleId));
            using var reader=cmd.ExecuteReader();while(reader.Read())rights.Add(reader.GetString(0));
        }
        return rights;
    }
    public static long PrincipalId(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value,out var id)?id:0;
    public static string PrincipalStamp(ClaimsPrincipal principal) => principal.FindFirst("tnm_stamp")?.Value ?? "";
    public bool HasPermission(ClaimsPrincipal principal, string permission)
    {
        if (!Permissions.Definitions.Any(p=>p.Key==permission)) return false;
        var id=PrincipalId(principal);
        if (id<=0 || !Valid(id,PrincipalStamp(principal)) || MustChangePassword(id)) return false;
        using var c=Open();
        return EffectivePermissions(c,id).Contains(permission);
    }
    public string LandingPath(long userId)
    {
        var permissions=AllowedPermissions(userId).ToHashSet(StringComparer.Ordinal);
        (string key,string path)[] options = [
            (Permissions.DashboardView,"/"),(Permissions.QuantityStockView,"/quantity-stock"),
            (Permissions.QuantityCountView,"/quantity-counts"),(Permissions.PurchaseView,"/purchases"),
            (Permissions.ProductsView,"/products"),
            (Permissions.CustomersView,"/catalog/customers"),
            (Permissions.InventoryView,"/inventory"),(Permissions.OrdersView,"/sales-orders"),
            (Permissions.DevicesView,"/devices"),(Permissions.InventoryWarranty,"/warranties"),(Permissions.FinanceView,"/finance"),
            (Permissions.ReportsView,"/reports"),(Permissions.ArchitectureView,"/architecture"),
            (Permissions.UsersView,"/admin/users")];
        return options.FirstOrDefault(x=>permissions.Contains(x.key)).path ?? "/account/denied";
    }
    public IReadOnlyList<RoleInfo> AllRoles()
    {
        using var c=Open();using var cmd=Cmd(c,"SELECT id,code,name FROM roles ORDER BY id");using var r=cmd.ExecuteReader();
        var list=new List<RoleInfo>();while(r.Read())list.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2)));return list;
    }
    public IReadOnlyList<ManagedUser> AllUsers()
    {
        using var c=Open();var list=new List<ManagedUser>();
        using(var cmd=Cmd(c,"SELECT id,username,display_name,is_active,is_owner FROM app_users WHERE deleted_at IS NULL AND LOWER(username)<>'vanlt_it' ORDER BY is_owner DESC,id"))
        using(var r=cmd.ExecuteReader())while(r.Read())list.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetInt64(3)==1,[],r.GetInt64(4)==1));
        foreach(var u in list){using var cmd=Cmd(c,"SELECT role_id FROM user_roles WHERE user_id=$id AND deleted_at IS NULL",("$id",u.Id));using var r=cmd.ExecuteReader();while(r.Read())u.RoleIds.Add(r.GetInt64(0));}
        return list;
    }
    public IReadOnlySet<string> RolePermissions(long roleId)
    {
        using var c=Open();using var cmd=Cmd(c,"SELECT permission_key FROM role_permissions WHERE role_id=$id AND deleted_at IS NULL",("$id",roleId));using var r=cmd.ExecuteReader();
        var result=new HashSet<string>(StringComparer.Ordinal);while(r.Read())result.Add(r.GetString(0));return result;
    }
    private static void Rotate(SqliteConnection c, long userId) => Exec(c,@"INSERT INTO user_security(user_id,security_stamp) VALUES($id,lower(hex(randomblob(16))))
        ON CONFLICT(user_id) DO UPDATE SET security_stamp=lower(hex(randomblob(16))),failed_attempts=0,locked_until=0",("$id",userId));
    private static void Audit(SqliteConnection c,long actorId,string action,string entity,long entityId,string message) =>
        Exec(c,"INSERT INTO audit_logs(actor_user_id,action,entity,entity_id,details) VALUES($a,$act,$e,$id,$m)",
            ("$a",actorId),("$act",action),("$e",entity),("$id",entityId),("$m",message));
    private static void CheckRoleIds(SqliteConnection c,IReadOnlyCollection<long> roleIds)
    {
        if (roleIds.Count!=roleIds.Distinct().Count())throw new InvalidOperationException("Vai trò bị chọn trùng.");
        foreach(var id in roleIds)if(Convert.ToInt64(Scalar(c,"SELECT count(*) FROM roles WHERE id=$id",("$id",id)))!=1)
            throw new InvalidOperationException("Vai trò không tồn tại.");
    }
    private static bool HasPermission(SqliteConnection c,long actor,string permission)
    {
        var active=Convert.ToInt64(Scalar(c,"SELECT count(*) FROM app_users WHERE id=$id AND is_active=1 AND deleted_at IS NULL",("$id",actor)))>0;
        return active && EffectivePermissions(c,actor).Contains(permission);
    }
    /// <summary>Non-admin account managers cannot grant capabilities they do not possess themselves.</summary>
    private static void GuardGrantCeiling(SqliteConnection c, long actor,
        IReadOnlyCollection<long> roleIds, IReadOnlyDictionary<string,string>? overrides)
    {
        if(IsAdmin(c,actor))return;
        var own=EffectivePermissions(c,actor);
        var desired=InheritedPermissions(c,roleIds).ToHashSet(StringComparer.Ordinal);
        if(overrides is not null)
            foreach(var (key,effect) in overrides)
            {
                if(effect=="ALLOW") desired.Add(key);
                else if(effect=="DENY") desired.Remove(key);
            }
        Permissions.EnforceDependencies(desired);
        if(desired.Any(right=>!own.Contains(right)))
            throw new UnauthorizedAccessException("Không thể cấp quyền cao hơn quyền mà tài khoản quản trị hiện có.");
    }

    private static bool IsHiddenMain(SqliteConnection c, long userId) =>
        Convert.ToInt64(Scalar(c, "SELECT count(*) FROM app_users WHERE id=$id AND LOWER(username)='vanlt_it'", ("$id", userId))) > 0;
    private static bool IsOwner(SqliteConnection c,long userId) =>
        Convert.ToInt64(Scalar(c,"SELECT count(*) FROM app_users WHERE id=$id AND (is_owner=1 OR LOWER(username)='vanlt_it')",("$id",userId)))>0;
    private static bool IsAdmin(SqliteConnection c,long userId) =>
        IsOwner(c,userId) || Convert.ToInt64(Scalar(c,@"SELECT count(*) FROM user_roles ur JOIN roles r ON r.id=ur.role_id
          WHERE ur.user_id=$id AND ur.deleted_at IS NULL AND r.code='ADMIN'",("$id",userId)))>0;
    private static void SaveOverrides(SqliteConnection c,long userId,IReadOnlyDictionary<string,string>? overrides)
    {
        if(overrides is null) return;  // null = do not modify direct permissions
        if(IsOwner(c,userId))throw new InvalidOperationException("Không được thay đổi quyền riêng của Owner.");
        if(IsAdmin(c,userId) && overrides.Count>0)
            throw new InvalidOperationException("Tài khoản ADMIN có toàn quyền cố định, không cấp/từ chối quyền riêng.");
        foreach(var (key,effect) in overrides)
            if(!Permissions.IsImplemented(key) || (effect!="ALLOW" && effect!="DENY"))
                throw new InvalidOperationException("Quyền trực tiếp không hợp lệ.");
        Exec(c,"UPDATE user_permission_overrides SET deleted_at=CURRENT_TIMESTAMP WHERE user_id=$id AND deleted_at IS NULL",("$id",userId));
        foreach(var (key,effect) in overrides)
            Exec(c,"INSERT INTO user_permission_overrides(user_id,permission_key,effect) VALUES($id,$key,$effect) ON CONFLICT(user_id,permission_key) DO UPDATE SET effect=excluded.effect,deleted_at=NULL,deleted_by=NULL",
                ("$id",userId),("$key",key),("$effect",effect));
    }
    public void CreateUser(long actor,string username,string displayName,string password,
        IReadOnlyCollection<long> roleIds,IReadOnlyDictionary<string,string>? overrides=null)
    {
        using var auditActor = TNM.Management.Services.AuditRuntime.As(actor);
        ValidatePassword(password);
        username=username.Trim().ToLowerInvariant();displayName=displayName.Trim();
        if(username=="owner")throw new InvalidOperationException("Tên owner chỉ dành cho tài khoản chủ sở hữu hệ thống.");
        if(username==HiddenMainUsername)throw new InvalidOperationException("Tên đăng nhập đã tồn tại.");
        if(username.Length<3||username.Length>64||displayName.Length<2||displayName.Length>150||username.Any(x=>!(char.IsAsciiLetterOrDigit(x)||x=='_'||x=='.'||x=='-')))
            throw new InvalidOperationException("Tên đăng nhập hoặc họ tên không hợp lệ.");
        using var c=Open();Exec(c,"BEGIN IMMEDIATE;");try{
        if(!HasPermission(c,actor,Permissions.UsersManage))throw new UnauthorizedAccessException("Không có quyền tạo người dùng.");
        CheckRoleIds(c,roleIds);
        if(roleIds.Any(id=> (Scalar(c,"SELECT code FROM roles WHERE id=$id",("$id",id)) as string)=="ADMIN") && !IsAdmin(c,actor))
            throw new UnauthorizedAccessException("Chỉ ADMIN được tạo tài khoản ADMIN.");
        if(overrides is not null && !HasPermission(c,actor,Permissions.UsersPermissions))
            throw new UnauthorizedAccessException("Không có quyền cấp quyền trực tiếp cho User.");
        if(roleIds.Count==0 && (overrides is null || !overrides.Values.Contains("ALLOW")))
            throw new InvalidOperationException("Chọn ít nhất một Role hoặc cấp ít nhất một quyền Cho phép riêng.");
        GuardGrantCeiling(c,actor,roleIds,overrides);
        if(Convert.ToInt64(Scalar(c,"SELECT count(*) FROM app_users WHERE username=$u COLLATE NOCASE",("$u",username)))!=0)
            throw new InvalidOperationException("Tên đăng nhập đã tồn tại.");
        Exec(c,"INSERT INTO app_users(username,display_name,is_active) VALUES($u,$n,1)",("$u",username),("$n",displayName));
        var userId=Id(c);Rotate(c,userId);
        var account=new Account(userId,username,displayName,true,"");
        Exec(c,"UPDATE app_users SET password_hash=$h WHERE id=$id",("$h",_hasher.HashPassword(account,password)),("$id",userId));
        foreach(var roleId in roleIds) Exec(c,"INSERT INTO user_roles(user_id,role_id) VALUES($u,$r) ON CONFLICT(user_id,role_id) DO UPDATE SET deleted_at=NULL,deleted_by=NULL",("$u",userId),("$r",roleId));
        SaveOverrides(c,userId,overrides);
        Audit(c,actor,"CREATE","APP_USER",userId,$"{username}; roles={string.Join(',',roleIds)}; overrides={overrides?.Count ?? 0}");
        Exec(c,"COMMIT;");}catch{Exec(c,"ROLLBACK;");throw;}
    }
    public void UpdateUser(long actor,long userId,string displayName,bool active,string newPassword,
        IReadOnlyCollection<long> roleIds,IReadOnlyDictionary<string,string>? overrides=null)
    {
        using var auditActor = TNM.Management.Services.AuditRuntime.As(actor);
        displayName=displayName.Trim();if(displayName.Length<2||displayName.Length>150)throw new InvalidOperationException("Tên hiển thị từ 2–150 ký tự.");
        if(!string.IsNullOrWhiteSpace(newPassword))ValidatePassword(newPassword);
        if(actor==userId)throw new InvalidOperationException("Không được chỉnh quyền/trạng thái/mật khẩu của chính tài khoản đang đăng nhập qua màn hình này.");
        using var c=Open();Exec(c,"BEGIN IMMEDIATE;");try{
        if(!HasPermission(c,actor,Permissions.UsersManage))throw new UnauthorizedAccessException("Không có quyền sửa tài khoản.");
        CheckRoleIds(c,roleIds);
        if(overrides is not null && !HasPermission(c,actor,Permissions.UsersPermissions))
            throw new UnauthorizedAccessException("Không có quyền sửa quyền trực tiếp.");
        if(IsHiddenMain(c,userId))throw new InvalidOperationException("Không tìm thấy tài khoản.");
        if(IsOwner(c,userId))throw new InvalidOperationException("Owner là tài khoản hệ thống được bảo vệ; không thể khóa hoặc chỉnh Role/quyền tại đây.");
        var oldName=Scalar(c,"SELECT username FROM app_users WHERE id=$id",("$id",userId)) as string ?? throw new InvalidOperationException("Không tìm thấy tài khoản.");
        var isAdmin=IsAdmin(c,userId);
        var willAdmin=roleIds.Any(id=>(Scalar(c,"SELECT code FROM roles WHERE id=$id",("$id",id)) as string)=="ADMIN");
        if((isAdmin || willAdmin) && !IsAdmin(c,actor))
            throw new UnauthorizedAccessException("Chỉ ADMIN được thay đổi tài khoản ADMIN.");
        if(isAdmin && (!active||!willAdmin) && Convert.ToInt64(Scalar(c,@"SELECT count(DISTINCT u.id) FROM app_users u
            JOIN user_roles ur ON ur.user_id=u.id JOIN roles r ON r.id=ur.role_id
            WHERE r.code='ADMIN' AND u.is_active=1 AND ur.deleted_at IS NULL"))<=1)
            throw new InvalidOperationException("Không thể khóa hoặc gỡ vai trò quản trị viên hoạt động cuối cùng.");
        if(roleIds.Count==0)
        {
            var grants=overrides?.Values ?? UserOverridesFromConnection(c,userId).Values;
            if(!grants.Contains("ALLOW"))
                throw new InvalidOperationException("Tài khoản cần có Role hoặc ít nhất một quyền Cho phép riêng.");
        }
        GuardGrantCeiling(c,actor,roleIds,overrides ?? UserOverridesFromConnection(c,userId));
        Exec(c,"UPDATE app_users SET display_name=$n,is_active=$a WHERE id=$id",("$n",displayName),("$a",active?1:0),("$id",userId));
        if(!string.IsNullOrWhiteSpace(newPassword))
            Exec(c,"UPDATE app_users SET password_hash=$h WHERE id=$id",("$h",_hasher.HashPassword(new Account(userId,oldName,displayName,active,""),newPassword)),("$id",userId));
        Exec(c,"UPDATE user_roles SET deleted_at=CURRENT_TIMESTAMP WHERE user_id=$id AND deleted_at IS NULL",("$id",userId));
        foreach(var roleId in roleIds)Exec(c,"INSERT INTO user_roles(user_id,role_id) VALUES($u,$r) ON CONFLICT(user_id,role_id) DO UPDATE SET deleted_at=NULL,deleted_by=NULL",("$u",userId),("$r",roleId));
        SaveOverrides(c,userId,overrides);
        Rotate(c,userId);
        Audit(c,actor,"UPDATE","APP_USER",userId,$"{oldName}; active={active}; roles={string.Join(',',roleIds)}; overrides={(overrides is null?"unchanged":overrides.Count.ToString())}; reset-password={!string.IsNullOrWhiteSpace(newPassword)}");
        Exec(c,"COMMIT;");}catch{Exec(c,"ROLLBACK;");throw;}
    }
    /// <summary>Revoke/grant/inherit per-user permissions without changing roles or account profile.</summary>
    public void SaveUserPermissions(long actor,long userId,IReadOnlyDictionary<string,string> overrides)
    {
        using var auditActor = TNM.Management.Services.AuditRuntime.As(actor);
        if(actor==userId) throw new InvalidOperationException("Không được tự cấp quyền cho tài khoản đang đăng nhập.");
        using var c=Open();Exec(c,"BEGIN IMMEDIATE;");try{
        if(!HasPermission(c,actor,Permissions.UsersPermissions))throw new UnauthorizedAccessException("Không có quyền phân quyền theo User.");
        if(IsHiddenMain(c,userId))throw new InvalidOperationException("Tài khoản không tồn tại.");
        if(IsOwner(c,userId))throw new InvalidOperationException("Không được thay đổi quyền riêng của Owner.");
        if(Scalar(c,"SELECT username FROM app_users WHERE id=$id",("$id",userId)) is not string username)
            throw new InvalidOperationException("Tài khoản không tồn tại.");
        var targetRoles=new List<long>();
        using(var cmd=Cmd(c,"SELECT role_id FROM user_roles WHERE user_id=$id AND deleted_at IS NULL",("$id",userId)))
        using(var r=cmd.ExecuteReader())while(r.Read())targetRoles.Add(r.GetInt64(0));
        GuardGrantCeiling(c,actor,targetRoles,overrides);
        SaveOverrides(c,userId,overrides);
        Rotate(c,userId);
        Audit(c,actor,"UPDATE_PERMISSIONS","APP_USER",userId,$"{username}; {string.Join(',',overrides.Select(p=>$"{p.Key}:{p.Value}"))}");
        Exec(c,"COMMIT;");}catch{Exec(c,"ROLLBACK;");throw;}
    }
    public void SaveRolePermissions(long actor,long roleId,IReadOnlyCollection<string> selected)
    {
        using var auditActor = TNM.Management.Services.AuditRuntime.As(actor);
        if(!selected.All(Permissions.IsImplemented))throw new InvalidOperationException("Quyền không hợp lệ.");
        using var c=Open();Exec(c,"BEGIN IMMEDIATE;");try{
        if(!HasPermission(c,actor,Permissions.RolesManage))throw new UnauthorizedAccessException("Không có quyền quản trị Role.");
        var code=Scalar(c,"SELECT code FROM roles WHERE id=$id",("$id",roleId)) as string ?? throw new InvalidOperationException("Không tìm thấy vai trò.");
        if(code=="ADMIN")throw new InvalidOperationException("Role ADMIN luôn có toàn bộ quyền và không thể sửa ở giao diện.");
        if(!IsAdmin(c,actor) && selected.Any(key=>!EffectivePermissions(c,actor).Contains(key)))
            throw new UnauthorizedAccessException("Không thể cấp cho Role quyền ngoài phạm vi của tài khoản quản trị.");
        Exec(c,"UPDATE role_permissions SET deleted_at=CURRENT_TIMESTAMP WHERE role_id=$id AND deleted_at IS NULL",("$id",roleId));
        foreach(var key in selected.Distinct())Exec(c,"INSERT INTO role_permissions(role_id,permission_key) VALUES($id,$k) ON CONFLICT(role_id,permission_key) DO UPDATE SET deleted_at=NULL,deleted_by=NULL",("$id",roleId),("$k",key));
        Exec(c,@"UPDATE user_security SET security_stamp=lower(hex(randomblob(16)))
            WHERE user_id IN(SELECT user_id FROM user_roles WHERE role_id=$id)",("$id",roleId));
        Audit(c,actor,"UPDATE","ROLE",roleId,$"{code}: {string.Join(',',selected)}");
        Exec(c,"COMMIT;");}catch{Exec(c,"ROLLBACK;");throw;}
    }
}
