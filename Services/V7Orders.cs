using Microsoft.Data.Sqlite;

namespace TNM.Management.Services;

public record V7OrderLineDraft(long ProductId, long Quantity);
public record V7OrderLineReport(long LineId, string Imei, string PlateNo = "", string VinNo = "");
public record V7OrderLine(
    long Id,
    long OrderId,
    long ProductId,
    string ProductSku,
    string ProductName,
    bool IsSerialized,
    long Quantity,
    long UnitPrice,
    long UnitCost,
    long InstallerFee,
    string? Imei,
    string PlateNo,
    string VinNo);

public record V7Order(
    long Id,
    string Code,
    string Customer,
    string CustomerPhone,
    string SaleSource,
    long ProductId,
    string Product,
    string Status,
    string Source,
    string? Imei,
    string? Warehouse,
    string? Installer,
    long? InstallerEmployeeId,
    long InstallerFee,
    string InstallAddress,
    string InstallAt,
    string PlateNo,
    long Price,
    List<V7OrderLine> Lines);

public sealed class V7Orders(IWebHostEnvironment env)
{
    private readonly string path = Path.Combine(env.ContentRootPath, "App_Data", "tnm-demo.db");
    private SqliteConnection Open() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, DefaultTimeout = 15 }.ToString()); c.Open(); AuditRuntime.Attach(c); return c; }
    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] p) { var q = c.CreateCommand(); q.CommandText = sql; foreach (var (k, v) in p) q.Parameters.AddWithValue(k, v ?? DBNull.Value); return q; }
    private static long Scalar(SqliteConnection c, string sql, params (string, object?)[] p) { using var q = Cmd(c, sql, p); return Convert.ToInt64(q.ExecuteScalar() ?? 0); }
    private static string Str(SqliteConnection c, string sql, params (string, object?)[] p) { using var q = Cmd(c, sql, p); return q.ExecuteScalar()?.ToString() ?? ""; }
    private static void Run(SqliteConnection c, string sql, params (string, object?)[] p) { using var q = Cmd(c, sql, p); q.ExecuteNonQuery(); }
    private T Tx<T>(long actor, Func<SqliteConnection, T> action) { using var scope = AuditRuntime.As(actor); return Tx(action); }
    private T Tx<T>(Func<SqliteConnection, T> action) { using var c = Open(); Run(c, "BEGIN IMMEDIATE"); try { var r = action(c); Run(c, "COMMIT"); return r; } catch { Run(c, "ROLLBACK"); throw; } }

    public void Initialize()
    {
        using var c = Open();
        using var q = Cmd(c, "PRAGMA table_info(v7_orders)");
        using var r = q.ExecuteReader();
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) cols.Add(r.GetString(1));
        r.Close();
        if (!cols.Contains("installer_employee_id")) Run(c, "ALTER TABLE v7_orders ADD COLUMN installer_employee_id INTEGER REFERENCES employees(id)");
        if (!cols.Contains("installer_fee_vnd")) Run(c, "ALTER TABLE v7_orders ADD COLUMN installer_fee_vnd INTEGER NOT NULL DEFAULT 0");
        if (!cols.Contains("customer_phone")) Run(c, "ALTER TABLE v7_orders ADD COLUMN customer_phone TEXT NOT NULL DEFAULT ''");
        if (!cols.Contains("sale_source")) Run(c, "ALTER TABLE v7_orders ADD COLUMN sale_source TEXT NOT NULL DEFAULT ''");

        Run(c, """
            CREATE TABLE IF NOT EXISTS v7_order_lines(
              id INTEGER PRIMARY KEY,
              order_id INTEGER NOT NULL REFERENCES v7_orders(id) ON DELETE CASCADE,
              product_id INTEGER NOT NULL REFERENCES products(id),
              quantity INTEGER NOT NULL DEFAULT 1 CHECK(quantity>0),
              unit_price_vnd INTEGER NOT NULL DEFAULT 0 CHECK(unit_price_vnd>=0),
              unit_cost_snapshot_vnd INTEGER NOT NULL DEFAULT 0 CHECK(unit_cost_snapshot_vnd>=0),
              installer_fee_vnd INTEGER NOT NULL DEFAULT 0 CHECK(installer_fee_vnd>=0),
              imei TEXT,
              plate_no TEXT NOT NULL DEFAULT '',
              vin_no TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_v7_order_lines_order ON v7_order_lines(order_id);
            """);

        Run(c, """
            INSERT INTO v7_order_lines(order_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd,installer_fee_vnd,imei,plate_no,vin_no)
            SELECT o.id,o.product_id,o.quantity,o.unit_price_vnd,o.unit_cost_snapshot_vnd,COALESCE(o.installer_fee_vnd,0),o.imei,COALESCE(o.plate_no,''),COALESCE(o.vin_no,'')
            FROM v7_orders o
            WHERE NOT EXISTS (SELECT 1 FROM v7_order_lines l WHERE l.order_id=o.id);
            """);
    }

    private static void Role(SqliteConnection c, long actor, params string[] roles)
    {
        if (Scalar(c, "SELECT COUNT(*) FROM app_users WHERE id=$u AND is_active=1 AND is_owner=1", ("$u", actor)) > 0) return;
        foreach (var role in roles)
            if (Scalar(c, "SELECT COUNT(*) FROM user_roles ur JOIN roles r ON r.id=ur.role_id WHERE ur.user_id=$u AND ur.deleted_at IS NULL AND r.code=$r", ("$u", actor), ("$r", role)) > 0) return;
        throw new UnauthorizedAccessException("Tài khoản không có vai trò phù hợp.");
    }

    private static void Event(SqliteConnection c, long id, long actor, string name) =>
        Run(c, "INSERT INTO v7_order_events(order_id,actor_id,event) VALUES($o,$a,$e)", ("$o", id), ("$a", actor), ("$e", name));

    private static (long Price, long Cost) QuoteOrderPrice(SqliteConnection c, long customerId, long productId)
    {
        var cost = Scalar(c, "SELECT base_cost_vnd FROM products WHERE id=$p", ("$p", productId));
        var basePrice = Scalar(c, @"SELECT CASE WHEN installed_price_vnd>0 THEN installed_price_vnd ELSE standard_price_vnd END FROM v7_group_prices
            WHERE group_id=(SELECT group_id FROM customers WHERE id=$c) AND product_id=$p
              AND deleted_at IS NULL AND effective_from<=date('now') AND (effective_to IS NULL OR effective_to>=date('now'))
            ORDER BY effective_from DESC,id DESC LIMIT 1", ("$c", customerId), ("$p", productId));
        if (basePrice <= 0)
            basePrice = cost + Scalar(c, "SELECT install_fee_vnd FROM products WHERE id=$p", ("$p", productId));
        var discount = Scalar(c, @"SELECT COALESCE(MAX(discount_percent),0) FROM v7_group_campaigns
            WHERE group_id=(SELECT group_id FROM customers WHERE id=$c) AND is_active=1 AND deleted_at IS NULL
              AND start_date<=date('now') AND end_date>=date('now')", ("$c", customerId));
        var finalPrice = (long)((decimal)basePrice * (100 - discount) / 100m);
        return (finalPrice, cost);
    }

    public List<V7Order> List()
    {
        using var c = Open();
        var linesByOrder = new Dictionary<long, List<V7OrderLine>>();
        using (var lq = Cmd(c, @"SELECT l.id,l.order_id,l.product_id,p.sku,p.name,p.is_serialized,l.quantity,l.unit_price_vnd,l.unit_cost_snapshot_vnd,COALESCE(l.installer_fee_vnd,0),l.imei,COALESCE(l.plate_no,''),COALESCE(l.vin_no,'')
            FROM v7_order_lines l JOIN products p ON p.id=l.product_id
            WHERE l.deleted_at IS NULL ORDER BY l.order_id DESC, l.id ASC"))
        using (var lr = lq.ExecuteReader())
        {
            while (lr.Read())
            {
                var line = new V7OrderLine(
                    lr.GetInt64(0),
                    lr.GetInt64(1),
                    lr.GetInt64(2),
                    lr.GetString(3),
                    lr.GetString(4),
                    lr.GetInt64(5) == 1,
                    lr.GetInt64(6),
                    lr.GetInt64(7),
                    lr.GetInt64(8),
                    lr.GetInt64(9),
                    lr.IsDBNull(10) ? null : lr.GetString(10),
                    lr.GetString(11),
                    lr.GetString(12));
                if (!linesByOrder.TryGetValue(line.OrderId, out var bucket))
                {
                    bucket = [];
                    linesByOrder[line.OrderId] = bucket;
                }
                bucket.Add(line);
            }
        }

        using var q = Cmd(c, @"SELECT o.id,o.code,c.name,
            COALESCE(NULLIF(o.customer_phone,''),c.phone,''),
            COALESCE(NULLIF(o.sale_source,''),NULLIF(c.source_name,''),u.display_name,''),
            o.product_id,p.name,o.status,o.source,o.imei,w.name,o.installer,
            o.installer_employee_id,COALESCE(o.installer_fee_vnd,0),
            COALESCE(o.install_address,''),COALESCE(o.install_at,''),COALESCE(o.plate_no,''),o.unit_price_vnd
            FROM v7_orders o JOIN customers c ON c.id=o.customer_id JOIN products p ON p.id=o.product_id
            LEFT JOIN warehouses w ON w.id=o.warehouse_id
            LEFT JOIN app_users u ON u.id=o.seller_id
            WHERE o.deleted_at IS NULL ORDER BY o.id DESC LIMIT 250");
        using var r = q.ExecuteReader();
        var list = new List<V7Order>();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            var lines = linesByOrder.TryGetValue(id, out var l) ? l : [];
            var prodSummary = lines.Count <= 1
                ? (lines.Count == 1 ? $"{lines[0].ProductName} (x{lines[0].Quantity})" : r.GetString(6))
                : string.Join(", ", lines.GroupBy(x => x.ProductName).Select(g => $"{g.Key} (x{g.Sum(i => i.Quantity)})"));
            list.Add(new(
                id,
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.GetInt64(5),
                prodSummary,
                r.GetString(7),
                r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetString(11),
                r.IsDBNull(12) ? null : r.GetInt64(12),
                r.GetInt64(13),
                r.GetString(14),
                r.GetString(15),
                r.GetString(16),
                r.GetInt64(17),
                lines));
        }
        return list;
    }

    public long Create(long actor, long customer, long product, string address, string notes, bool cod, string? customerPhone = null, string? installAt = null, string? saleSource = null) =>
        CreateMulti(actor, customer, [new V7OrderLineDraft(product, 1)], address, notes, cod, customerPhone, installAt, saleSource);

    public long CreateMulti(
        long actor,
        long customer,
        IReadOnlyList<V7OrderLineDraft> items,
        string address,
        string notes,
        bool cod,
        string? customerPhone = null,
        string? installAt = null,
        string? saleSource = null)
    {
        var validItems = (items ?? []).Where(x => x.ProductId > 0 && x.Quantity > 0).ToList();
        if (customer < 1 || validItems.Count == 0)
            throw new InvalidOperationException("Chọn khách hàng và ít nhất 1 sản phẩm cho đơn hàng.");

        return Tx(actor, c => {
            Role(c, actor, "SELLER", "CSKH", "MANAGER", "ADMIN");
            if (Scalar(c, "SELECT COUNT(*) FROM customers WHERE id=$id AND is_active=1 AND deleted_at IS NULL", ("$id", customer)) == 0)
                throw new InvalidOperationException("Khách hàng không hợp lệ.");

            var expandedLines = new List<(long ProductId, long Qty, long UnitPrice, long UnitCost)>();
            foreach (var item in validItems)
            {
                if (item.Quantity > 100) throw new InvalidOperationException("Số lượng mỗi dòng tối đa 100.");
                if (Scalar(c, "SELECT COUNT(*) FROM products WHERE id=$id AND is_active=1 AND deleted_at IS NULL", ("$id", item.ProductId)) == 0)
                    throw new InvalidOperationException("Sản phẩm không hợp lệ.");
                var isSerialized = Scalar(c, "SELECT is_serialized FROM products WHERE id=$id", ("$id", item.ProductId)) == 1;
                var (unitPrice, unitCost) = QuoteOrderPrice(c, customer, item.ProductId);

                if (isSerialized)
                {
                    // Tách mỗi thiết bị IMEI thành 1 dòng riêng để báo IMEI / Biển số / VIN cho từng xe
                    for (long i = 0; i < item.Quantity; i++)
                        expandedLines.Add((item.ProductId, 1, unitPrice, unitCost));
                }
                else
                {
                    expandedLines.Add((item.ProductId, item.Quantity, unitPrice, unitCost));
                }
            }

            var totalPrice = expandedLines.Sum(x => x.UnitPrice * x.Qty);
            var totalCost = expandedLines.Sum(x => x.UnitCost * x.Qty);
            var totalQty = expandedLines.Sum(x => x.Qty);
            var primaryProduct = expandedLines[0].ProductId;

            var phone = !string.IsNullOrWhiteSpace(customerPhone) ? customerPhone.Trim() : Str(c, "SELECT COALESCE(phone,'') FROM customers WHERE id=$id", ("$id", customer));
            var src = !string.IsNullOrWhiteSpace(saleSource) ? saleSource.Trim() : Str(c, "SELECT COALESCE(NULLIF(source_name,''),(SELECT display_name FROM app_users WHERE id=$u),'') FROM customers WHERE id=$id", ("$id", customer), ("$u", actor));
            var addr = !string.IsNullOrWhiteSpace(address) ? address.Trim() : Str(c, "SELECT COALESCE(address,'') FROM customers WHERE id=$id", ("$id", customer));
            var when = !string.IsNullOrWhiteSpace(installAt) ? installAt.Trim() : DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            Run(c, @"INSERT INTO v7_orders(code,customer_id,seller_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd,customer_phone,sale_source,install_address,install_at,notes,collect_on_delivery)
                VALUES(lower(hex(randomblob(16))),$c,$s,$p,$qty,$price,$cost,$phone,$src,$a,$at,$n,$cod)",
                ("$c", customer), ("$s", actor), ("$p", primaryProduct), ("$qty", totalQty), ("$price", totalPrice), ("$cost", totalCost),
                ("$phone", phone), ("$src", src), ("$a", addr), ("$at", when), ("$n", notes ?? ""), ("$cod", cod ? 1 : 0));
            var id = Scalar(c, "SELECT last_insert_rowid()");
            Run(c, "UPDATE v7_orders SET code=$code WHERE id=$id", ("$code", $"SO7-{id:000000}"), ("$id", id));

            foreach (var line in expandedLines)
            {
                Run(c, @"INSERT INTO v7_order_lines(order_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd)
                    VALUES($o,$p,$q,$price,$cost)",
                    ("$o", id), ("$p", line.ProductId), ("$q", line.Qty), ("$price", line.UnitPrice), ("$cost", line.UnitCost));
            }

            Event(c, id, actor, "CREATED");
            return id;
        });
    }

    private static string Status(SqliteConnection c, long id)
    {
        using var q = Cmd(c, "SELECT status FROM v7_orders WHERE id=$id AND deleted_at IS NULL", ("$id", id));
        return q.ExecuteScalar()?.ToString() ?? throw new InvalidOperationException("Không thấy đơn.");
    }

    public void Approve(long actor, long id, long warehouse)
    {
        Tx(actor, c => {
            Role(c, actor, "ADMIN", "MANAGER");
            if (Status(c, id) != "PENDING") throw new InvalidOperationException("Đơn không ở trạng thái chờ duyệt.");
            if (Scalar(c, "SELECT COUNT(*) FROM warehouses WHERE id=$w AND deleted_at IS NULL", ("$w", warehouse)) == 0)
                throw new InvalidOperationException("Kho không tồn tại.");
            Run(c, "UPDATE v7_orders SET status='APPROVED',warehouse_id=$w WHERE id=$id", ("$w", warehouse), ("$id", id));
            Event(c, id, actor, "APPROVED");
            return 0;
        });
    }

    public void Dispatch(long actor, long id, bool partner, string installer, long? installerEmployeeId = null)
    {
        Tx(actor, c => {
            Role(c, actor, "ADMIN", "MANAGER", "CSKH");
            if (Status(c, id) != "APPROVED") throw new InvalidOperationException("Đơn chưa được duyệt.");
            long? empId = installerEmployeeId is > 0 ? installerEmployeeId : null;
            string installerName = (installer ?? "").Trim();
            long totalFee = 0;

            if (empId.HasValue)
            {
                var empName = Str(c, "SELECT full_name FROM employees WHERE id=$id AND deleted_at IS NULL", ("$id", empId.Value));
                if (string.IsNullOrWhiteSpace(empName)) throw new InvalidOperationException("Nhân sự lắp đặt không tồn tại.");
                if (string.IsNullOrWhiteSpace(installerName)) installerName = empName;

                using var lq = Cmd(c, "SELECT id,product_id,quantity FROM v7_order_lines WHERE order_id=$o AND deleted_at IS NULL", ("$o", id));
                using var lr = lq.ExecuteReader();
                var orderLines = new List<(long LineId, long ProductId, long Qty)>();
                while (lr.Read()) orderLines.Add((lr.GetInt64(0), lr.GetInt64(1), lr.GetInt64(2)));
                lr.Close();

                foreach (var ln in orderLines)
                {
                    using var rq = Cmd(c, "SELECT install_rate_vnd FROM v7_installer_product_rates WHERE employee_id=$e AND product_id=$p AND deleted_at IS NULL", ("$e", empId.Value), ("$p", ln.ProductId));
                    var rateVal = rq.ExecuteScalar();
                    var unitFee = rateVal is not null ? Convert.ToInt64(rateVal) : Scalar(c, "SELECT technical_fee_vnd FROM products WHERE id=$p", ("$p", ln.ProductId));
                    Run(c, "UPDATE v7_order_lines SET installer_fee_vnd=$fee WHERE id=$lid", ("$fee", unitFee), ("$lid", ln.LineId));
                    totalFee += unitFee * ln.Qty;
                }
            }
            else if (string.IsNullOrWhiteSpace(installerName))
            {
                throw new InvalidOperationException("Cần chọn nhân sự lắp đặt hoặc nhập đơn vị lắp đặt.");
            }

            Run(c, "UPDATE v7_orders SET status='DISPATCHED',source=$s,installer=$i,installer_employee_id=$emp,installer_fee_vnd=$fee WHERE id=$id",
                ("$s", partner ? "PARTNER" : "INTERNAL"), ("$i", installerName), ("$emp", empId), ("$fee", totalFee), ("$id", id));
            Event(c, id, actor, "DISPATCHED");
            return 0;
        });
    }

    public void Report(long actor, long id, string imei, string plateNo = "", string vinNo = "")
    {
        using var c = Open();
        using var q = Cmd(c, @"SELECT l.id FROM v7_order_lines l JOIN products p ON p.id=l.product_id
            WHERE l.order_id=$o AND l.deleted_at IS NULL AND p.is_serialized=1 ORDER BY l.id", ("$o", id));
        using var r = q.ExecuteReader();
        var lineIds = new List<long>();
        while (r.Read()) lineIds.Add(r.GetInt64(0));
        r.Close();

        var imeis = (imei ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var plates = (plateNo ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var vins = (vinNo ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var reports = new List<V7OrderLineReport>();
        for (int i = 0; i < lineIds.Count; i++)
        {
            var m = i < imeis.Length ? imeis[i] : "";
            var pl = i < plates.Length ? plates[i] : (plates.Length == 1 ? plates[0] : "");
            var vn = i < vins.Length ? vins[i] : (vins.Length == 1 ? vins[0] : "");
            reports.Add(new V7OrderLineReport(lineIds[i], m, pl, vn));
        }
        ReportLines(actor, id, reports);
    }

    public void ReportLines(long actor, long id, IReadOnlyList<V7OrderLineReport> reports)
    {
        Tx(actor, c => {
            Role(c, actor, "ADMIN", "MANAGER", "CSKH", "TECH", "INSTALLER");
            if (Status(c, id) != "DISPATCHED") throw new InvalidOperationException("Đơn chưa được điều phối.");

            using var q = Cmd(c, @"SELECT l.id,p.is_serialized,p.name FROM v7_order_lines l JOIN products p ON p.id=l.product_id
                WHERE l.order_id=$o AND l.deleted_at IS NULL ORDER BY l.id", ("$o", id));
            using var r = q.ExecuteReader();
            var dbLines = new List<(long LineId, bool IsSerialized, string ProductName)>();
            while (r.Read()) dbLines.Add((r.GetInt64(0), r.GetInt64(1) == 1, r.GetString(2)));
            r.Close();

            var reportMap = (reports ?? []).ToDictionary(x => x.LineId, x => x);
            var seenImeis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allImeis = new List<string>();
            var allPlates = new List<string>();
            var allVins = new List<string>();

            foreach (var ln in dbLines)
            {
                if (!ln.IsSerialized) continue;
                if (!reportMap.TryGetValue(ln.LineId, out var rep) || string.IsNullOrWhiteSpace(rep.Imei))
                    throw new InvalidOperationException($"Thiếu mã IMEI cho sản phẩm '{ln.ProductName}'.");
                var cleanImei = rep.Imei.Trim();
                if (!seenImeis.Add(cleanImei))
                    throw new InvalidOperationException($"Mã IMEI '{cleanImei}' bị trùng lặp trong cùng đơn hàng.");
                var cleanPlate = (rep.PlateNo ?? "").Trim();
                var cleanVin = (rep.VinNo ?? "").Trim();

                Run(c, "UPDATE v7_order_lines SET imei=$imei,plate_no=$plate,vin_no=$vin WHERE id=$lid",
                    ("$imei", cleanImei), ("$plate", cleanPlate), ("$vin", cleanVin), ("$lid", ln.LineId));

                allImeis.Add(cleanImei);
                if (!string.IsNullOrWhiteSpace(cleanPlate)) allPlates.Add(cleanPlate);
                if (!string.IsNullOrWhiteSpace(cleanVin)) allVins.Add(cleanVin);
            }

            Run(c, "UPDATE v7_orders SET status='REPORTED',imei=$imei,plate_no=$plate,vin_no=$vin WHERE id=$id",
                ("$imei", allImeis.Count > 0 ? string.Join(", ", allImeis) : "KHÔNG-IMEI"),
                ("$plate", string.Join(", ", allPlates.Distinct())),
                ("$vin", string.Join(", ", allVins.Distinct())),
                ("$id", id));
            Event(c, id, actor, "REPORTED");
            return 0;
        });
    }

    public void Complete(long actor, long id)
    {
        Tx(actor, c => {
            Role(c, actor, "ADMIN", "MANAGER", "CSKH");
            if (Status(c, id) != "REPORTED") throw new InvalidOperationException("Đơn chưa báo cáo lắp đặt.");

            using var q = Cmd(c, "SELECT customer_id,warehouse_id,source,unit_price_vnd FROM v7_orders WHERE id=$id", ("$id", id));
            long customer, warehouse, totalPrice;
            string source;
            using (var r = q.ExecuteReader())
            {
                if (!r.Read()) throw new InvalidOperationException("Không thấy đơn.");
                customer = r.GetInt64(0);
                warehouse = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                source = r.GetString(2);
                totalPrice = r.GetInt64(3);
            }

            using var lq = Cmd(c, @"SELECT l.id,l.product_id,p.name,p.is_serialized,l.quantity,l.unit_price_vnd,l.unit_cost_snapshot_vnd,COALESCE(l.imei,''),COALESCE(l.plate_no,''),COALESCE(l.vin_no,'')
                FROM v7_order_lines l JOIN products p ON p.id=l.product_id
                WHERE l.order_id=$o AND l.deleted_at IS NULL ORDER BY l.id", ("$o", id));
            var lines = new List<(long LineId, long ProductId, string ProductName, bool IsSerialized, long Qty, long UnitPrice, long UnitCost, string Imei, string PlateNo, string VinNo)>();
            using (var lr = lq.ExecuteReader())
            {
                while (lr.Read())
                    lines.Add((lr.GetInt64(0), lr.GetInt64(1), lr.GetString(2), lr.GetInt64(3) == 1, lr.GetInt64(4), lr.GetInt64(5), lr.GetInt64(6), lr.GetString(7), lr.GetString(8), lr.GetString(9)));
            }

            long? saleOutTxId = null;
            foreach (var ln in lines)
            {
                if (ln.IsSerialized)
                {
                    if (string.IsNullOrWhiteSpace(ln.Imei))
                        throw new InvalidOperationException($"Thiếu mã IMEI ở sản phẩm '{ln.ProductName}'.");
                    if (Scalar(c, "SELECT COUNT(*) FROM activations a JOIN stock_units s ON s.id=a.stock_unit_id WHERE s.imei=$imei AND a.deleted_at IS NULL", ("$imei", ln.Imei)) > 0)
                        throw new InvalidOperationException($"IMEI '{ln.Imei}' đã được kích hoạt.");

                    long stockUnitId;
                    if (source == "INTERNAL")
                    {
                        using var find = Cmd(c, "SELECT id FROM stock_units WHERE imei=$imei AND product_id=$p AND warehouse_id=$w AND status='IN_STOCK'",
                            ("$imei", ln.Imei), ("$p", ln.ProductId), ("$w", warehouse));
                        var stock = find.ExecuteScalar();
                        if (stock is null) throw new InvalidOperationException($"IMEI '{ln.Imei}' ({ln.ProductName}) không có trong kho đã duyệt hoặc đã xuất.");
                        stockUnitId = Convert.ToInt64(stock);

                        if (!saleOutTxId.HasValue)
                        {
                            var txCode = "XK-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
                            Run(c, "INSERT INTO inventory_transactions(code,movement_type,warehouse_id,customer_id,actor_user_id,notes) VALUES($code,'SALE_OUT',$w,$c,$actor,'Xuất lắp đặt đơn V7')",
                                ("$code", txCode), ("$w", warehouse), ("$c", customer), ("$actor", actor));
                            saleOutTxId = Scalar(c, "SELECT last_insert_rowid()");
                        }

                        Run(c, "INSERT INTO inventory_transaction_lines(transaction_id,stock_unit_id,product_id,unit_cost_snapshot_vnd) VALUES($t,$s,$p,$cost)",
                            ("$t", saleOutTxId.Value), ("$s", stockUnitId), ("$p", ln.ProductId), ("$cost", ln.UnitCost));

                        Run(c, "UPDATE stock_units SET status='INSTALLED',warehouse_id=NULL,customer_id=$c,employee_id=NULL,updated_at=CURRENT_TIMESTAMP WHERE id=$id AND status='IN_STOCK'",
                            ("$c", customer), ("$id", stockUnitId));
                        if (Scalar(c, "SELECT changes()") != 1) throw new InvalidOperationException($"Không thể trừ kho IMEI '{ln.Imei}'.");
                        Run(c, "INSERT INTO v7_stock_effects(order_id,stock_unit_id) VALUES($o,$s)", ("$o", id), ("$s", stockUnitId));
                    }
                    else
                    {
                        using var find = Cmd(c, "SELECT id FROM stock_units WHERE imei=$imei", ("$imei", ln.Imei));
                        var existing = find.ExecuteScalar();
                        if (existing is not null)
                            throw new InvalidOperationException($"IMEI '{ln.Imei}' đã tồn tại trong danh sách thiết bị.");
                        Run(c, "INSERT INTO stock_units(product_id,imei,status,customer_id) VALUES($p,$imei,'INSTALLED',$c)",
                            ("$p", ln.ProductId), ("$imei", ln.Imei), ("$c", customer));
                        stockUnitId = Scalar(c, "SELECT last_insert_rowid()");
                    }

                    // Tạo thuê bao kích hoạt (activations) hạn 1 năm cho từng thiết bị IMEI
                    Run(c, @"INSERT INTO activations(v7_order_id,stock_unit_id,customer_id,product_id,plate_no,vin_no,activated_at,expires_at)
                        VALUES($o,$s,$c,$p,$plate,$vin,date('now'),date('now','+1 year'))",
                        ("$o", id), ("$s", stockUnitId), ("$c", customer), ("$p", ln.ProductId), ("$plate", ln.PlateNo), ("$vin", ln.VinNo));
                }
                else if (source == "INTERNAL" && warehouse > 0)
                {
                    // Sản phẩm quản lý theo số lượng: trừ tồn tại vị trí MAIN nếu có tồn khả dụng
                    var locId = Scalar(c, "SELECT id FROM warehouse_locations WHERE warehouse_id=$w AND is_active=1 AND deleted_at IS NULL ORDER BY CASE WHEN code='MAIN' THEN 0 ELSE 1 END, id LIMIT 1", ("$w", warehouse));
                    if (locId > 0)
                    {
                        var curQty = Scalar(c, "SELECT COALESCE(SUM(delta_qty),0) FROM quantity_movements WHERE product_id=$p AND warehouse_id=$w AND location_id=$l",
                            ("$p", ln.ProductId), ("$w", warehouse), ("$l", locId));
                        if (curQty >= ln.Qty)
                        {
                            Run(c, @"INSERT INTO quantity_movements(document_no,product_id,warehouse_id,location_id,delta_qty,movement_type,ref_type,ref_id,note,actor_user_id)
                                VALUES('Q-'||lower(hex(randomblob(8))),$p,$w,$l,$d,'ISSUE','V7_ORDER',$o,'Xuất theo đơn hàng SO',$u)",
                                ("$p", ln.ProductId), ("$w", warehouse), ("$l", locId), ("$d", -ln.Qty), ("$o", id), ("$u", actor));
                        }
                    }
                }
            }

            // Tự động tạo hóa đơn công nợ SALE gồm đầy đủ các dòng sản phẩm
            var invoiceNo = "HD-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            Run(c, @"INSERT INTO invoices(invoice_no,invoice_type,customer_id,v7_order_id,due_at,amount_vnd)
                VALUES($no,'SALE',$c,$o,date('now','+30 days'),$price)",
                ("$no", invoiceNo), ("$c", customer), ("$o", id), ("$price", totalPrice));
            var invoiceId = Scalar(c, "SELECT last_insert_rowid()");
            foreach (var grp in lines.GroupBy(x => (x.ProductId, x.ProductName, x.UnitPrice)))
            {
                Run(c, "INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$desc,$p,$q,$price)",
                    ("$i", invoiceId), ("$desc", $"Thiết bị & lắp đặt: {grp.Key.ProductName}"), ("$p", grp.Key.ProductId), ("$q", grp.Sum(x => x.Qty)), ("$price", grp.Key.UnitPrice));
            }

            Run(c, "UPDATE v7_orders SET status='COMPLETED',activated_at=CURRENT_TIMESTAMP,completed_at=CURRENT_TIMESTAMP WHERE id=$id", ("$id", id));
            Event(c, id, actor, "COMPLETED");
            return 0;
        });
    }

    public void Cancel(long actor, long id)
    {
        Tx(actor, c => {
            Role(c, actor, "ADMIN", "MANAGER", "CSKH");
            if (Status(c, id) == "COMPLETED") throw new InvalidOperationException("Không được hủy đơn đã hoàn tất.");
            Run(c, "UPDATE v7_orders SET status='CANCELLED' WHERE id=$id AND status<>'CANCELLED'", ("$id", id));
            Event(c, id, actor, "CANCELLED");
            return 0;
        });
    }
}
