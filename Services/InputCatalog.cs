using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TNM.Management.Services;

/// <summary>
/// Danh mục đầu vào V7. Các luồng PO, SO, gia hạn chỉ đọc ID và gọi hàm tra giá;
/// không ghi đè danh mục hay tạo bảng giá riêng tại luồng giao dịch.
/// SQLite writer được serialize bằng BEGIN IMMEDIATE để cấp SKU không trùng.
/// </summary>
public sealed class InputCatalog(IWebHostEnvironment env)
{
    private readonly string dbPath = Path.Combine(env.ContentRootPath, "App_Data", "tnm-demo.db");
    public record Choice(long Id, string Code, string Name);
    public record SupplierGroup(long Id, string Code, string Name, long Suppliers);
    public record Supplier(long Id, string Code, string Name, long? GroupId, string Group, string Phone, string TaxCode);
    public record Area(long Id, string Code, string Name, long Warehouses);
    public record Warehouse(long Id, string Code, string Name, long? AreaId, string Area, string Address);
    public record Product(long Id, string Code, string Name, long GroupId, string Group, bool Active);
    public record Customer(long Id, string Code, string Name, long? GroupId, string Group, string Phone, string Address, string SourceName = "");
    public record Installer(long Id, string Code, string Name, string Department, string Phone, string RateSummary);
    public record InstallerRate(long Id, long EmployeeId, string EmployeeCode, string EmployeeName, long ProductId, string Product, long InstallRate, string EffectiveFrom);
    public record PurchasePrice(long Id, long GroupId, string Group, long ProductId, string Product, long UnitCost, string EffectiveFrom);
    public record SalePrice(long Id, long GroupId, string Group, long ProductId, string Product, long Standard, long Installed, long Renewal, string EffectiveFrom);
    public record Campaign(long Id, long GroupId, string Group, string Name, int Discount, string From, string To, bool Active);
    public record PriceQuote(long GroupId, long ProductId, string ProductSku, long BasePrice, long FinalPrice, int DiscountPercent, long? CampaignId, string? CampaignName, string EffectiveDate);

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, ForeignKeys = true, Pooling = true, DefaultTimeout = 15 }.ToString());
        c.Open(); AuditRuntime.Attach(c);
        using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA busy_timeout=15000"; cmd.ExecuteNonQuery();
        return c;
    }
    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] p) { var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value); return cmd; }
    private static void Do(SqliteConnection c, string sql, params (string, object?)[] p) { using var cmd = Cmd(c, sql, p); cmd.ExecuteNonQuery(); }
    private static long Num(SqliteConnection c, string sql, params (string, object?)[] p) { using var cmd = Cmd(c, sql, p); return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L); }
    private static object? Cell(SqliteConnection c, string sql, params (string, object?)[] p) { using var cmd = Cmd(c, sql, p); return cmd.ExecuteScalar(); }
    private static string Txt(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static long? Opt(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static List<T> Read<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] p) { using var cmd = Cmd(c, sql, p); using var r = cmd.ExecuteReader(); var list = new List<T>(); while (r.Read()) list.Add(map(r)); return list; }
    private T Write<T>(long actor, Func<SqliteConnection, T> action) { using var scope = AuditRuntime.As(actor); return Write(action); }
    private T Write<T>(Func<SqliteConnection, T> action) { using var c = Open(); Do(c, "BEGIN IMMEDIATE"); try { var result = action(c); Do(c, "COMMIT"); return result; } catch { Do(c, "ROLLBACK"); throw; } }
    private static string Required(string text, string label, int max = 160) { text = (text ?? "").Trim(); if (text.Length == 0 || text.Length > max) throw new InvalidOperationException($"{label}: bắt buộc, tối đa {max} ký tự."); return text; }
    private static string Code(string value) { value = Required(value, "Mã", 32).ToUpperInvariant(); if (!Regex.IsMatch(value, @"^[A-Z0-9_-]+$")) throw new InvalidOperationException("Mã chỉ gồm A–Z, 0–9, dấu _ và -."); return value; }
    private static void Exists(SqliteConnection c, string table, long id) { if (id <= 0 || table is not ("supplier_groups" or "product_groups" or "customer_groups" or "warehouse_areas" or "suppliers" or "products" or "customers" or "warehouses" or "employees")) throw new InvalidOperationException("Danh mục không hợp lệ."); if (Num(c, $"SELECT COUNT(*) FROM {table} WHERE id=$id AND deleted_at IS NULL", ("$id", id)) == 0) throw new InvalidOperationException("Mục đã chọn không tồn tại."); }
    private static void Audit(SqliteConnection c, long actor, string operation, string entity, long id, string detail) { Do(c, "INSERT INTO audit_logs(actor_user_id,action,entity,entity_id,details) VALUES($a,$op,$e,$id,$d)", ("$a", actor), ("$op", operation), ("$e", entity), ("$id", id), ("$d", detail)); }

    public void Initialize()
    {
        Write(c => {
            Do(c, """
                CREATE TABLE IF NOT EXISTS v7_installer_product_rates(
                  id INTEGER PRIMARY KEY,
                  employee_id INTEGER NOT NULL REFERENCES employees(id),
                  product_id INTEGER NOT NULL REFERENCES products(id),
                  install_rate_vnd INTEGER NOT NULL DEFAULT 0 CHECK(install_rate_vnd>=0),
                  effective_from TEXT NOT NULL DEFAULT (date('now')),
                  UNIQUE(employee_id, product_id)
                );
                CREATE INDEX IF NOT EXISTS ix_installer_rates ON v7_installer_product_rates(employee_id, product_id);
                """);
            var codes = Read(c, "SELECT sku FROM products WHERE sku GLOB 'DX[0-9]*'", r => Txt(r, 0));
            var max = codes.Where(x => x.Length >= 7 && x.StartsWith("DX", StringComparison.Ordinal) && x[2..].All(char.IsDigit))
                .Select(x => long.TryParse(x[2..], out var n) ? n : 0).DefaultIfEmpty(0).Max();
            Do(c, "INSERT OR IGNORE INTO v7_sku_sequences(prefix,last_number) VALUES('DX',0)");
            Do(c, "UPDATE v7_sku_sequences SET last_number=MAX(last_number,$max) WHERE prefix='DX'", ("$max", max));

            var maxCust = GetMaxExistingCustomerNumber(c);
            Do(c, "INSERT OR IGNORE INTO v7_sku_sequences(prefix,last_number) VALUES('KH',9)");
            Do(c, "UPDATE v7_sku_sequences SET last_number=MAX(last_number,$max) WHERE prefix='KH'", ("$max", maxCust));
            return 0;
        });
    }

    private static long GetMaxExistingCustomerNumber(SqliteConnection c, SqliteTransaction? tx = null)
    {
        static long ScalarLong(SqliteConnection conn, SqliteTransaction? tran, string sql)
        {
            using var cmd = conn.CreateCommand(); cmd.Transaction = tran;
            cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
        var maxId = ScalarLong(c, tx, "SELECT COALESCE(MAX(id),0) FROM customers");
        var countAll = ScalarLong(c, tx, "SELECT COUNT(*) FROM customers");
        long maxFromCode = 0;
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT code FROM customers";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var code = r.IsDBNull(0) ? "" : r.GetString(0);
                var m = Regex.Match(code, @"(\d+)$");
                if (m.Success && long.TryParse(m.Groups[1].Value, out var parsed) && parsed > maxFromCode)
                {
                    maxFromCode = parsed;
                }
            }
        }
        // Mặc định bắt đầu từ 10 (last_number tối thiểu = 9) theo dữ liệu hiện có
        return Math.Max(9L, Math.Max(maxId, Math.Max(countAll, maxFromCode)));
    }

    /// <summary>Phải gọi trong transaction ghi SQLite; các lối tạo sản phẩm khác tái sử dụng để chống cấp trùng.</summary>
    public static string NextSku(SqliteConnection c, SqliteTransaction? tx = null)
    {
        static void Run(SqliteConnection connection, SqliteTransaction? transaction, string sql)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = sql; command.ExecuteNonQuery();
        }
        static object? ReadOne(SqliteConnection connection, SqliteTransaction? transaction, string sql)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = sql; return command.ExecuteScalar();
        }
        Run(c, tx, "INSERT OR IGNORE INTO v7_sku_sequences(prefix,last_number) VALUES('DX',0)");
        while (true)
        {
            Run(c, tx, "UPDATE v7_sku_sequences SET last_number=last_number+1 WHERE prefix='DX'");
            var n = Convert.ToInt64(ReadOne(c, tx, "SELECT last_number FROM v7_sku_sequences WHERE prefix='DX'") ?? 0L);
            var sku = "DX" + n.ToString("D5");
            using var command = c.CreateCommand(); command.Transaction = tx;
            command.CommandText = "SELECT COUNT(*) FROM products WHERE sku=$s";
            command.Parameters.AddWithValue("$s", sku);
            if (Convert.ToInt64(command.ExecuteScalar() ?? 0L) == 0) return sku;
        }
    }

    /// <summary>Phải gọi trong transaction ghi SQLite; tự động sinh Mã khách hàng theo thứ tự bắt đầu từ KH00010, KH00011...</summary>
    public static string NextCustomerCode(SqliteConnection c, SqliteTransaction? tx = null)
    {
        static void Run(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string, object?)[] p)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (k, v) in p) command.Parameters.AddWithValue(k, v ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        static long ReadLong(SqliteConnection connection, SqliteTransaction? transaction, string sql)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
        }
        var maxExisting = GetMaxExistingCustomerNumber(c, tx);
        Run(c, tx, "INSERT OR IGNORE INTO v7_sku_sequences(prefix,last_number) VALUES('KH',9)");
        Run(c, tx, "UPDATE v7_sku_sequences SET last_number=MAX(last_number,$max) WHERE prefix='KH'", ("$max", maxExisting));
        while (true)
        {
            Run(c, tx, "UPDATE v7_sku_sequences SET last_number=last_number+1 WHERE prefix='KH'");
            var n = ReadLong(c, tx, "SELECT last_number FROM v7_sku_sequences WHERE prefix='KH'");
            var code = "KH" + n.ToString("D5");
            using var command = c.CreateCommand(); command.Transaction = tx;
            command.CommandText = "SELECT COUNT(*) FROM customers WHERE code=$c";
            command.Parameters.AddWithValue("$c", code);
            if (Convert.ToInt64(command.ExecuteScalar() ?? 0L) == 0) return code;
        }
    }

    /// <summary>Xem trước mã khách hàng tiếp theo để hiển thị trên form Thêm mới.</summary>
    public string PeekNextCustomerCode()
    {
        using var c = Open();
        var seq = Num(c, "SELECT COALESCE((SELECT last_number FROM v7_sku_sequences WHERE prefix='KH'),9)");
        var maxExisting = GetMaxExistingCustomerNumber(c);
        var n = Math.Max(seq, maxExisting) + 1;
        while (Num(c, "SELECT COUNT(*) FROM customers WHERE code=$c", ("$c", "KH" + n.ToString("D5"))) > 0)
        {
            n++;
        }
        return "KH" + n.ToString("D5");
    }

    public List<Choice> SupplierGroupChoices() { using var c = Open(); return Read(c, "SELECT id,code,name FROM supplier_groups WHERE deleted_at IS NULL ORDER BY name", r => new Choice(r.GetInt64(0), Txt(r, 1), Txt(r, 2))); }
    public List<Choice> CustomerGroupChoices() { using var c = Open(); return Read(c, "SELECT id,code,name FROM customer_groups WHERE deleted_at IS NULL ORDER BY name", r => new Choice(r.GetInt64(0), Txt(r, 1), Txt(r, 2))); }
    public List<Choice> ProductGroupChoices() { using var c = Open(); return Read(c, "SELECT id,code,name FROM product_groups WHERE deleted_at IS NULL ORDER BY name", r => new Choice(r.GetInt64(0), Txt(r, 1), Txt(r, 2))); }
    public List<Choice> InstallerChoices() { using var c = Open(); return Read(c, "SELECT id,code,full_name FROM employees WHERE is_active=1 AND deleted_at IS NULL ORDER BY full_name", r => new Choice(r.GetInt64(0), Txt(r, 1), Txt(r, 2))); }
    public List<SupplierGroup> SupplierGroups() { using var c = Open(); return Read(c, "SELECT g.id,g.code,g.name,COUNT(s.id) FROM supplier_groups g LEFT JOIN suppliers s ON s.group_id=g.id AND s.deleted_at IS NULL WHERE g.deleted_at IS NULL GROUP BY g.id ORDER BY g.name", r => new SupplierGroup(r.GetInt64(0), Txt(r, 1), Txt(r, 2), r.GetInt64(3))); }
    public List<Supplier> Suppliers() { using var c = Open(); return Read(c, "SELECT s.id,s.code,s.name,s.group_id,COALESCE(g.name,'Chưa phân nhóm'),COALESCE(s.phone,''),COALESCE(s.tax_code,'') FROM suppliers s LEFT JOIN supplier_groups g ON g.id=s.group_id WHERE s.deleted_at IS NULL ORDER BY s.name", r => new Supplier(r.GetInt64(0), Txt(r, 1), Txt(r, 2), Opt(r, 3), Txt(r, 4), Txt(r, 5), Txt(r, 6))); }
    public List<Area> Areas() { using var c = Open(); return Read(c, "SELECT a.id,a.code,a.name,COUNT(w.id) FROM warehouse_areas a LEFT JOIN warehouses w ON w.area_id=a.id AND w.deleted_at IS NULL WHERE a.deleted_at IS NULL GROUP BY a.id ORDER BY a.name", r => new Area(r.GetInt64(0), Txt(r, 1), Txt(r, 2), r.GetInt64(3))); }
    public List<Warehouse> Warehouses() { using var c = Open(); return Read(c, "SELECT w.id,w.code,w.name,w.area_id,COALESCE(a.name,''),COALESCE(w.address,'') FROM warehouses w LEFT JOIN warehouse_areas a ON a.id=w.area_id WHERE w.deleted_at IS NULL ORDER BY a.name,w.name", r => new Warehouse(r.GetInt64(0), Txt(r, 1), Txt(r, 2), Opt(r, 3), Txt(r, 4), Txt(r, 5))); }
    public List<Product> Products() { using var c = Open(); return Read(c, "SELECT id,sku,name,group_id,group_name,is_active FROM v7_product_master WHERE is_active=1 ORDER BY id DESC", r => new Product(r.GetInt64(0), Txt(r, 1), Txt(r, 2), r.GetInt64(3), Txt(r, 4), r.GetInt64(5) == 1)); }
    public List<Customer> Customers() { using var c = Open(); return Read(c, "SELECT c.id,c.code,c.name,c.group_id,COALESCE(g.name,''),COALESCE(c.phone,''),COALESCE(c.address,''),COALESCE(c.source_name,'') FROM customers c LEFT JOIN customer_groups g ON g.id=c.group_id WHERE c.deleted_at IS NULL ORDER BY c.id ASC", r => new Customer(r.GetInt64(0), Txt(r, 1), Txt(r, 2), Opt(r, 3), Txt(r, 4), Txt(r, 5), Txt(r, 6), Txt(r, 7))); }
    public List<Installer> Installers()
    {
        using var c = Open();
        var rates = Read(c, "SELECT r.employee_id,p.sku,r.install_rate_vnd FROM v7_installer_product_rates r JOIN products p ON p.id=r.product_id WHERE r.deleted_at IS NULL AND p.deleted_at IS NULL ORDER BY p.sku",
            r => (EmpId: r.GetInt64(0), Sku: Txt(r, 1), Rate: r.GetInt64(2)));
        var rateMap = rates.GroupBy(x => x.EmpId).ToDictionary(g => g.Key, g => string.Join(" · ", g.Select(x => $"{x.Sku}: {x.Rate:N0} đ")));
        return Read(c, "SELECT id,code,full_name,department,COALESCE(phone,'') FROM employees WHERE deleted_at IS NULL ORDER BY full_name",
            r => {
                var id = r.GetInt64(0);
                var summary = rateMap.TryGetValue(id, out var s) && !string.IsNullOrWhiteSpace(s) ? s : "Chưa khai báo đơn giá SP";
                return new Installer(id, Txt(r, 1), Txt(r, 2), Txt(r, 3), Txt(r, 4), summary);
            });
    }
    public Dictionary<long, long> InstallerRatesForEmployee(long employeeId)
    {
        using var c = Open();
        return Read(c, "SELECT product_id,install_rate_vnd FROM v7_installer_product_rates WHERE employee_id=$e AND deleted_at IS NULL",
            r => (P: r.GetInt64(0), Rate: r.GetInt64(1)), ("$e", employeeId)).ToDictionary(x => x.P, x => x.Rate);
    }
    public List<InstallerRate> InstallerRates()
    {
        using var c = Open();
        return Read(c, @"SELECT x.id,x.employee_id,e.code,e.full_name,x.product_id,p.sku||' · '||p.name,x.install_rate_vnd,x.effective_from
            FROM v7_installer_product_rates x JOIN employees e ON e.id=x.employee_id JOIN products p ON p.id=x.product_id
            WHERE x.deleted_at IS NULL AND e.deleted_at IS NULL AND p.deleted_at IS NULL ORDER BY e.full_name,p.sku",
            r => new InstallerRate(r.GetInt64(0), r.GetInt64(1), Txt(r, 2), Txt(r, 3), r.GetInt64(4), Txt(r, 5), r.GetInt64(6), Txt(r, 7)));
    }
    public List<PurchasePrice> PurchasePrices() { using var c = Open(); return Read(c, "SELECT x.id,x.supplier_group_id,g.name,x.product_id,p.sku||' · '||p.name,x.unit_cost_vnd,x.effective_from FROM v7_supplier_group_prices x JOIN supplier_groups g ON g.id=x.supplier_group_id JOIN products p ON p.id=x.product_id WHERE x.deleted_at IS NULL ORDER BY x.id DESC", r => new PurchasePrice(r.GetInt64(0), r.GetInt64(1), Txt(r, 2), r.GetInt64(3), Txt(r, 4), r.GetInt64(5), Txt(r, 6))); }
    public List<SalePrice> SalePrices() { using var c = Open(); return Read(c, "SELECT x.id,x.group_id,g.name,x.product_id,p.sku||' · '||p.name,x.standard_price_vnd,x.installed_price_vnd,x.renewal_price_vnd,x.effective_from FROM v7_group_prices x JOIN customer_groups g ON g.id=x.group_id JOIN products p ON p.id=x.product_id WHERE x.deleted_at IS NULL ORDER BY x.id DESC", r => new SalePrice(r.GetInt64(0), r.GetInt64(1), Txt(r, 2), r.GetInt64(3), Txt(r, 4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), Txt(r, 8))); }
    public List<Campaign> Campaigns() { using var c = Open(); return Read(c, "SELECT x.id,x.group_id,g.name,x.name,x.discount_percent,x.start_date,x.end_date,x.is_active FROM v7_group_campaigns x JOIN customer_groups g ON g.id=x.group_id WHERE x.deleted_at IS NULL ORDER BY x.id DESC", r => new Campaign(r.GetInt64(0), r.GetInt64(1), Txt(r, 2), Txt(r, 3), r.GetInt32(4), Txt(r, 5), Txt(r, 6), r.GetInt64(7) == 1)); }

    public long SaveGroup(long actor, string type, long? id, string code, string name)
    {
        var table = type switch { "SUPPLIER" => "supplier_groups", "PRODUCT" => "product_groups", "CUSTOMER" => "customer_groups", "AREA" => "warehouse_areas", _ => throw new InvalidOperationException("Loại nhóm không hợp lệ.") };
        code = Code(code); name = Required(name, "Tên nhóm");
        return Write(actor, c => {
            long result;
            if (id.HasValue) { Exists(c, table, id.Value); Do(c, $"UPDATE {table} SET code=$code,name=$name WHERE id=$id", ("$code", code), ("$name", name), ("$id", id.Value)); result = id.Value; }
            else { Do(c, $"INSERT INTO {table}(code,name) VALUES($code,$name)", ("$code", code), ("$name", name)); result = Num(c, "SELECT last_insert_rowid()"); }
            Audit(c, actor, id.HasValue ? "UPDATE" : "CREATE", table.ToUpperInvariant(), result, code); return result;
        });
    }
    public void DeleteGroup(long actor, string type, long id)
    {
        var (table, child, foreign) = type switch { "SUPPLIER" => ("supplier_groups", "suppliers", "group_id"), "PRODUCT" => ("product_groups", "products", "group_id"), "CUSTOMER" => ("customer_groups", "customers", "group_id"), "AREA" => ("warehouse_areas", "warehouses", "area_id"), _ => throw new InvalidOperationException("Loại nhóm không hợp lệ.") };
        Write(actor, c => {
            Exists(c, table, id); if (Num(c, $"SELECT COUNT(*) FROM {child} WHERE {foreign}=$id AND deleted_at IS NULL", ("$id", id)) > 0) throw new InvalidOperationException("Nhóm đang có dữ liệu, không thể xóa.");
            Do(c, $"UPDATE {table} SET deleted_at=CURRENT_TIMESTAMP WHERE id=$id AND deleted_at IS NULL", ("$id", id)); Audit(c, actor, "DELETE", table.ToUpperInvariant(), id, ""); return 0;
        });
    }
    public long SaveSupplier(long actor, long? id, string code, string name, long groupId, string phone, string tax)
    {
        code = Code(code); name = Required(name, "Tên NCC"); return Write(actor, c => {
            Exists(c, "supplier_groups", groupId); long sid;
            if (id.HasValue) { Exists(c, "suppliers", id.Value); Do(c, "UPDATE suppliers SET code=$code,name=$name,group_id=$g,phone=$phone,tax_code=$tax WHERE id=$id", ("$code", code), ("$name", name), ("$g", groupId), ("$phone", phone.Trim()), ("$tax", tax.Trim()), ("$id", id.Value)); sid = id.Value; }
            else { Do(c, "INSERT INTO suppliers(code,name,group_id,phone,tax_code) VALUES($code,$name,$g,$phone,$tax)", ("$code", code), ("$name", name), ("$g", groupId), ("$phone", phone.Trim()), ("$tax", tax.Trim())); sid = Num(c, "SELECT last_insert_rowid()"); }
            Audit(c, actor, id.HasValue ? "UPDATE" : "CREATE", "SUPPLIER", sid, code); return sid;
        });
    }
    public long SaveWarehouse(long actor, long? id, string code, string name, long areaId, string address)
    {
        code = Code(code); name = Required(name, "Tên kho"); return Write(actor, c => {
            Exists(c, "warehouse_areas", areaId); long wid;
            if (id.HasValue) { Exists(c, "warehouses", id.Value); Do(c, "UPDATE warehouses SET code=$code,name=$name,address=$address,area_id=$area,province=$province WHERE id=$id", ("$code", code), ("$name", name), ("$address", address.Trim()), ("$area", areaId), ("$province", Cell(c, "SELECT name FROM warehouse_areas WHERE id=$id", ("$id", areaId))), ("$id", id.Value)); wid = id.Value; }
            else
            {
                Do(c, "INSERT INTO warehouses(code,name,address,area_id,province) VALUES($code,$name,$address,$area,$province)", ("$code", code), ("$name", name), ("$address", address.Trim()), ("$area", areaId), ("$province", Cell(c, "SELECT name FROM warehouse_areas WHERE id=$id", ("$id", areaId)))); wid = Num(c, "SELECT last_insert_rowid()");
                Do(c, "INSERT OR IGNORE INTO warehouse_locations(warehouse_id,code,name) VALUES($w,'MAIN','Mặc định')", ("$w", wid));
            }
            Audit(c, actor, id.HasValue ? "UPDATE" : "CREATE", "WAREHOUSE", wid, code); return wid;
        });
    }
    public long SaveCustomer(long actor, long? id, string? code, string name, long groupId, string phone, string address, string sourceName = "")
    {
        name = Required(name, "Tên khách hàng");
        return Write(actor, c => {
            Exists(c, "customer_groups", groupId);
            long cid;
            string finalCode;
            if (id.HasValue)
            {
                Exists(c, "customers", id.Value);
                var existingCode = Convert.ToString(Cell(c, "SELECT code FROM customers WHERE id=$id", ("$id", id.Value))) ?? "";
                finalCode = string.IsNullOrWhiteSpace(code) ? existingCode : Code(code);
                Do(c, "UPDATE customers SET code=$code,name=$name,group_id=$g,phone=$phone,address=$address,source_name=$src WHERE id=$id",
                    ("$code", finalCode), ("$name", name), ("$g", groupId), ("$phone", (phone ?? "").Trim()), ("$address", (address ?? "").Trim()), ("$src", (sourceName ?? "").Trim()), ("$id", id.Value));
                cid = id.Value;
            }
            else
            {
                finalCode = NextCustomerCode(c);
                Do(c, "INSERT INTO customers(code,name,group_id,phone,address,source_name) VALUES($code,$name,$g,$phone,$address,$src)",
                    ("$code", finalCode), ("$name", name), ("$g", groupId), ("$phone", (phone ?? "").Trim()), ("$address", (address ?? "").Trim()), ("$src", (sourceName ?? "").Trim()));
                cid = Num(c, "SELECT last_insert_rowid()");
            }
            Audit(c, actor, id.HasValue ? "UPDATE" : "CREATE", "CUSTOMER", cid, finalCode);
            return cid;
        });
    }
    public long SaveInstaller(long actor, long? id, string code, string name, string phone, string department = "Kỹ thuật", Dictionary<long, long>? productRates = null)
    {
        name = Required(name, "Họ tên nhân sự lắp đặt");
        department = string.IsNullOrWhiteSpace(department) ? "Kỹ thuật" : department.Trim();
        return Write(actor, c => {
            long empId;
            if (id.HasValue)
            {
                Exists(c, "employees", id.Value);
                code = Code(code);
                Do(c, "UPDATE employees SET code=$code,full_name=$name,department=$dept,phone=$phone WHERE id=$id",
                    ("$code", code), ("$name", name), ("$dept", department), ("$phone", (phone ?? "").Trim()), ("$id", id.Value));
                empId = id.Value;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    var nextNo = Num(c, "SELECT COALESCE(MAX(id),0)+1 FROM employees");
                    code = $"KT{nextNo:D3}";
                }
                else
                {
                    code = Code(code);
                }
                Do(c, "INSERT INTO employees(code,full_name,department,phone) VALUES($code,$name,$dept,$phone)",
                    ("$code", code), ("$name", name), ("$dept", department), ("$phone", (phone ?? "").Trim()));
                empId = Num(c, "SELECT last_insert_rowid()");
            }
            if (productRates is not null)
            {
                var today = DateTime.Today.ToString("yyyy-MM-dd");
                foreach (var (prodId, rate) in productRates)
                {
                    if (prodId <= 0 || rate < 0) continue;
                    Exists(c, "products", prodId);
                    Do(c, @"INSERT INTO v7_installer_product_rates(employee_id,product_id,install_rate_vnd,effective_from)
                        VALUES($e,$p,$r,$d)
                        ON CONFLICT(employee_id,product_id) DO UPDATE SET install_rate_vnd=excluded.install_rate_vnd,effective_from=excluded.effective_from,deleted_at=NULL,deleted_by=NULL",
                        ("$e", empId), ("$p", prodId), ("$r", rate), ("$d", today));
                }
            }
            Audit(c, actor, id.HasValue ? "UPDATE" : "CREATE", "INSTALLER", empId, $"{code} - {name}");
            return empId;
        });
    }
    public void SetInstallerRate(long actor, long employeeId, long productId, long rate, DateTime effective)
    {
        if (rate < 0) throw new InvalidOperationException("Đơn giá đi lắp không được âm.");
        var date = effective.ToString("yyyy-MM-dd");
        Write(actor, c => {
            Exists(c, "employees", employeeId); Exists(c, "products", productId);
            Do(c, @"INSERT INTO v7_installer_product_rates(employee_id,product_id,install_rate_vnd,effective_from)
                VALUES($e,$p,$r,$d)
                ON CONFLICT(employee_id,product_id) DO UPDATE SET install_rate_vnd=excluded.install_rate_vnd,effective_from=excluded.effective_from,deleted_at=NULL,deleted_by=NULL",
                ("$e", employeeId), ("$p", productId), ("$r", rate), ("$d", date));
            Audit(c, actor, "SET_INSTALLER_RATE", "INSTALLER_RATE", employeeId, $"product={productId}; rate={rate}"); return 0;
        });
    }
    public void UpdateInstallerRate(long actor, long id, long employeeId, long productId, long rate, DateTime effective)
    {
        if (rate < 0) throw new InvalidOperationException("Đơn giá đi lắp không được âm.");
        Write(actor, c => {
            Exists(c, "employees", employeeId); Exists(c, "products", productId);
            if (Num(c, "SELECT COUNT(*) FROM v7_installer_product_rates WHERE id=$id AND deleted_at IS NULL", ("$id", id)) == 0)
                throw new InvalidOperationException("Dòng đơn giá lắp đặt không tồn tại.");
            Do(c, "UPDATE v7_installer_product_rates SET employee_id=$e,product_id=$p,install_rate_vnd=$r,effective_from=$d WHERE id=$id",
                ("$e", employeeId), ("$p", productId), ("$r", rate), ("$d", effective.ToString("yyyy-MM-dd")), ("$id", id));
            Audit(c, actor, "UPDATE_INSTALLER_RATE", "INSTALLER_RATE", id, $"employee={employeeId};product={productId}"); return 0;
        });
    }
    public long QuoteInstallerRate(long employeeId, long productId)
    {
        using var c = Open();
        var val = Cell(c, "SELECT install_rate_vnd FROM v7_installer_product_rates WHERE employee_id=$e AND product_id=$p AND deleted_at IS NULL",
            ("$e", employeeId), ("$p", productId));
        if (val is not null) return Convert.ToInt64(val);
        return Num(c, "SELECT technical_fee_vnd FROM products WHERE id=$p", ("$p", productId));
    }
    public void DeleteEntity(long actor, string entity, long id)
    {
        var table = entity switch { "SUPPLIER" => "suppliers", "WAREHOUSE" => "warehouses", "CUSTOMER" => "customers", "INSTALLER" => "employees", _ => throw new InvalidOperationException("Loại danh mục không hợp lệ.") };
        try
        {
            Write(actor, c => {
                Exists(c, table, id);
                Do(c, $"UPDATE {table} SET deleted_at=CURRENT_TIMESTAMP WHERE id=$id AND deleted_at IS NULL", ("$id", id));
                Audit(c, actor, "DELETE", entity, id, "Xóa danh mục chưa phát sinh chứng từ"); return 0;
            });
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("Đối tượng đã phát sinh dữ liệu liên quan, không thể xóa. Vui lòng giữ lịch sử giao dịch.", ex);
        }
    }
    public void UpdateSupplierGroupPrice(long actor, long id, long groupId, long productId, long amount, DateTime effective)
    {
        if (amount < 0) throw new InvalidOperationException("Giá không được âm.");
        Write(actor, c => {
            Exists(c, "supplier_groups", groupId); Exists(c, "products", productId);
            if (Num(c, "SELECT COUNT(*) FROM v7_supplier_group_prices WHERE id=$id AND deleted_at IS NULL", ("$id", id)) == 0) throw new InvalidOperationException("Dòng giá nhập không tồn tại.");
            Do(c, "UPDATE v7_supplier_group_prices SET supplier_group_id=$g,product_id=$p,unit_cost_vnd=$v,effective_from=$d WHERE id=$id",
                ("$g", groupId), ("$p", productId), ("$v", amount), ("$d", effective.ToString("yyyy-MM-dd")), ("$id", id));
            Audit(c, actor, "UPDATE_PRICE", "SUPPLIER_GROUP_PRICE", id, $"group={groupId};product={productId}"); return 0;
        });
    }
    public void UpdateCustomerGroupPrice(long actor, long id, long groupId, long productId, long standard, long installed, long renewal, DateTime effective)
    {
        if (standard < 0 || installed < 0 || renewal < 0) throw new InvalidOperationException("Giá không được âm.");
        Write(actor, c => {
            Exists(c, "customer_groups", groupId); Exists(c, "products", productId);
            if (Num(c, "SELECT COUNT(*) FROM v7_group_prices WHERE id=$id AND deleted_at IS NULL", ("$id", id)) == 0) throw new InvalidOperationException("Dòng giá bán không tồn tại.");
            Do(c, "UPDATE v7_group_prices SET group_id=$g,product_id=$p,standard_price_vnd=$s,installed_price_vnd=$i,renewal_price_vnd=$r,effective_from=$d WHERE id=$id",
                ("$g", groupId), ("$p", productId), ("$s", standard), ("$i", installed), ("$r", renewal), ("$d", effective.ToString("yyyy-MM-dd")), ("$id", id));
            Audit(c, actor, "UPDATE_PRICE", "CUSTOMER_GROUP_PRICE", id, $"group={groupId};product={productId}"); return 0;
        });
    }
    public void DeletePrice(long actor, string kind, long id)
    {
        var table = kind switch { "BUY" => "v7_supplier_group_prices", "SALE" => "v7_group_prices", "INSTALLER_RATE" => "v7_installer_product_rates", _ => throw new InvalidOperationException("Loại bảng giá không hợp lệ.") };
        Write(actor, c => {
            if (Num(c, $"SELECT COUNT(*) FROM {table} WHERE id=$id AND deleted_at IS NULL", ("$id", id)) == 0) throw new InvalidOperationException("Bảng giá không tồn tại.");
            Do(c, $"UPDATE {table} SET deleted_at=CURRENT_TIMESTAMP WHERE id=$id AND deleted_at IS NULL", ("$id", id)); Audit(c, actor, "DELETE_PRICE", kind, id, "Bảng giá"); return 0;
        });
    }
    public long SaveCampaign(long actor, long? id, long groupId, string name, int discount, DateTime from, DateTime to)
    {
        name = Required(name, "Chương trình");
        if (discount < 0 || discount > 100 || from.Date > to.Date) throw new InvalidOperationException("Ưu đãi hoặc ngày hiệu lực không hợp lệ.");
        return Write(actor, c => {
            Exists(c, "customer_groups", groupId);
            if (id.HasValue)
            {
                if (Num(c, "SELECT COUNT(*) FROM v7_group_campaigns WHERE id=$id AND deleted_at IS NULL", ("$id", id.Value)) == 0) throw new InvalidOperationException("Chương trình không tồn tại.");
                Do(c, "UPDATE v7_group_campaigns SET group_id=$g,name=$n,discount_percent=$v,start_date=$f,end_date=$t WHERE id=$id",
                    ("$g", groupId), ("$n", name), ("$v", discount), ("$f", from.ToString("yyyy-MM-dd")), ("$t", to.ToString("yyyy-MM-dd")), ("$id", id.Value));
                Audit(c, actor, "UPDATE", "CAMPAIGN", id.Value, name); return id.Value;
            }
            Do(c, "INSERT INTO v7_group_campaigns(group_id,name,discount_percent,start_date,end_date) VALUES($g,$n,$v,$f,$t)",
                ("$g", groupId), ("$n", name), ("$v", discount), ("$f", from.ToString("yyyy-MM-dd")), ("$t", to.ToString("yyyy-MM-dd")));
            var created = Num(c, "SELECT last_insert_rowid()"); Audit(c, actor, "CREATE", "CAMPAIGN", created, name); return created;
        });
    }
    public void DeleteCampaign(long actor, long id)
    {
        Write(actor, c => {
            if (Num(c, "SELECT COUNT(*) FROM v7_group_campaigns WHERE id=$id AND deleted_at IS NULL", ("$id", id)) == 0) throw new InvalidOperationException("Chương trình không tồn tại.");
            Do(c, "UPDATE v7_group_campaigns SET deleted_at=CURRENT_TIMESTAMP WHERE id=$id AND deleted_at IS NULL", ("$id", id)); Audit(c, actor, "DELETE", "CAMPAIGN", id, ""); return 0;
        });
    }
    public void SetSupplierGroupPrice(long actor, long groupId, long productId, long amount, DateTime effective)
    {
        if (amount < 0) throw new InvalidOperationException("Giá không được âm."); var date = effective.ToString("yyyy-MM-dd"); Write(actor, c => {
            Exists(c, "supplier_groups", groupId); Exists(c, "products", productId);
            Do(c, "INSERT INTO v7_supplier_group_prices(supplier_group_id,product_id,unit_cost_vnd,effective_from) VALUES($g,$p,$v,$d) ON CONFLICT(supplier_group_id,product_id,effective_from) DO UPDATE SET unit_cost_vnd=excluded.unit_cost_vnd,deleted_at=NULL,deleted_by=NULL", ("$g", groupId), ("$p", productId), ("$v", amount), ("$d", date));
            Audit(c, actor, "SET_PRICE", "SUPPLIER_GROUP", groupId, $"product={productId}; from={date}"); return 0;
        });
    }
    public void SetCustomerGroupPrice(long actor, long groupId, long productId, long standard, long installed, long renewal, DateTime effective)
    {
        if (standard < 0 || installed < 0 || renewal < 0) throw new InvalidOperationException("Giá không được âm."); var date = effective.ToString("yyyy-MM-dd"); Write(actor, c => {
            Exists(c, "customer_groups", groupId); Exists(c, "products", productId);
            Do(c, "INSERT INTO v7_group_prices(group_id,product_id,standard_price_vnd,installed_price_vnd,renewal_price_vnd,effective_from) VALUES($g,$p,$s,$i,$r,$d) ON CONFLICT(group_id,product_id,effective_from) DO UPDATE SET standard_price_vnd=excluded.standard_price_vnd,installed_price_vnd=excluded.installed_price_vnd,renewal_price_vnd=excluded.renewal_price_vnd,deleted_at=NULL,deleted_by=NULL", ("$g", groupId), ("$p", productId), ("$s", standard), ("$i", installed), ("$r", renewal), ("$d", date));
            Audit(c, actor, "SET_PRICE", "CUSTOMER_GROUP", groupId, $"product={productId}; from={date}"); return 0;
        });
    }
    public long QuotePurchase(long supplierId, long productId, DateTime? at = null)
    {
        using var c = Open(); var day = (at ?? DateTime.Today).ToString("yyyy-MM-dd");
        var value = Cell(c, """
            SELECT gp.unit_cost_vnd FROM suppliers s
            JOIN v7_supplier_group_prices gp ON gp.supplier_group_id=s.group_id
            WHERE s.id=$s AND s.deleted_at IS NULL AND gp.deleted_at IS NULL AND gp.product_id=$p AND gp.effective_from<=$d
              AND (gp.effective_to IS NULL OR gp.effective_to>=$d)
            ORDER BY gp.effective_from DESC,gp.id DESC LIMIT 1
            """, ("$s", supplierId), ("$p", productId), ("$d", day));
        return value is null ? throw new InvalidOperationException("NCC chưa có giá nhập hiệu lực cho sản phẩm này.") : Convert.ToInt64(value);
    }
    public PriceQuote QuoteSale(long customerId, long productId, string priceType = "STANDARD", DateTime? at = null)
    {
        if (priceType is not ("STANDARD" or "INSTALLED" or "RENEWAL")) throw new InvalidOperationException("Loại giá không hợp lệ.");
        using var c = Open(); var day = (at ?? DateTime.Today).ToString("yyyy-MM-dd");
        using var q = Cmd(c, """
            SELECT c.group_id,p.sku,gp.standard_price_vnd,gp.installed_price_vnd,gp.renewal_price_vnd
            FROM customers c JOIN products p ON p.id=$p
            JOIN v7_group_prices gp ON gp.group_id=c.group_id AND gp.product_id=p.id
            WHERE c.id=$c AND c.is_active=1 AND c.deleted_at IS NULL AND p.is_active=1 AND p.deleted_at IS NULL AND gp.deleted_at IS NULL
              AND gp.effective_from<=$d AND (gp.effective_to IS NULL OR gp.effective_to>=$d)
            ORDER BY gp.effective_from DESC,gp.id DESC LIMIT 1
            """, ("$c", customerId), ("$p", productId), ("$d", day));
        long group, basePrice; string sku;
        using (var r = q.ExecuteReader()) { if (!r.Read()) throw new InvalidOperationException("Khách hàng chưa có bảng giá hiệu lực theo nhóm và sản phẩm."); group = r.GetInt64(0); sku = r.GetString(1); basePrice = priceType == "STANDARD" ? r.GetInt64(2) : priceType == "INSTALLED" ? r.GetInt64(3) : r.GetInt64(4); }
        using var cp = Cmd(c, """
            SELECT id,name,discount_percent FROM v7_group_campaigns
            WHERE group_id=$g AND is_active=1 AND deleted_at IS NULL AND start_date<=$d AND end_date>=$d
            ORDER BY discount_percent DESC,id ASC LIMIT 1
            """, ("$g", group), ("$d", day));
        using var cr = cp.ExecuteReader(); long? campaignId = null; string? campaignName = null; int discount = 0;
        if (cr.Read()) { campaignId = cr.GetInt64(0); campaignName = cr.GetString(1); discount = cr.GetInt32(2); }
        var final = (long)((decimal)basePrice * (100 - discount) / 100m);
        return new PriceQuote(group, productId, sku, basePrice, final, discount, campaignId, campaignName, day);
    }
}
