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
    List<V7OrderLine> Lines,
    string CustomerCode = "",
    string CustomerGroup = "",
    string RepresentativeName = "",
    string ContactName = "",
    long DurationMonths = 12,
    bool DeductStock = true,
    string ActivatedAt = "",
    long? WarehouseId = null,
    string Notes = "",
    bool Cod = false,
    long? SellerRecordId = null,
    long CostSnapshot = 0)
{
    public string CustomerB2BDisplay => string.IsNullOrWhiteSpace(RepresentativeName) ? Customer : $"{Customer} (ĐD: {RepresentativeName})";
    public string DurationDisplay => DurationMonths == 12 ? "1 năm" : (DurationMonths > 0 && DurationMonths % 12 == 0) ? $"{DurationMonths / 12} năm" : $"{DurationMonths} tháng";
    public string InstallerTypeDisplay => Source == "PARTNER" ? "Hãng lắp đặt" : "ĐV lắp đặt";
    public string StockEffectDisplay => DeductStock ? "Trừ tồn kho" : "Không trừ tồn kho (Hãng)";
    public long GrossProfitSnapshot => Price - CostSnapshot - InstallerFee;
    public bool IsImmutable => Status == "COMPLETED";
}

public sealed class V7Orders(IWebHostEnvironment env)
{
    private readonly string path = Path.Combine(env.ContentRootPath, "App_Data", "tnm-local.db");
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
        Run(c, """
            CREATE TABLE IF NOT EXISTS v7_sellers(
              id INTEGER PRIMARY KEY,
              code TEXT NOT NULL UNIQUE,
              full_name TEXT NOT NULL,
              phone TEXT NOT NULL DEFAULT '',
              department TEXT NOT NULL DEFAULT 'Kinh doanh / Sale',
              order_commission_vnd INTEGER NOT NULL DEFAULT 0 CHECK(order_commission_vnd>=0),
              renewal_commission_vnd INTEGER NOT NULL DEFAULT 0 CHECK(renewal_commission_vnd>=0),
              notes TEXT NOT NULL DEFAULT '',
              is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1))
            );
            """);

        using var q = Cmd(c, "PRAGMA table_info(v7_orders)");
        using var r = q.ExecuteReader();
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) cols.Add(r.GetString(1));
        r.Close();
        if (!cols.Contains("installer_employee_id")) Run(c, "ALTER TABLE v7_orders ADD COLUMN installer_employee_id INTEGER REFERENCES employees(id)");
        if (!cols.Contains("installer_fee_vnd")) Run(c, "ALTER TABLE v7_orders ADD COLUMN installer_fee_vnd INTEGER NOT NULL DEFAULT 0");
        if (!cols.Contains("contact_name")) Run(c, "ALTER TABLE v7_orders ADD COLUMN contact_name TEXT NOT NULL DEFAULT ''");
        if (!cols.Contains("customer_phone")) Run(c, "ALTER TABLE v7_orders ADD COLUMN customer_phone TEXT NOT NULL DEFAULT ''");
        if (!cols.Contains("sale_source")) Run(c, "ALTER TABLE v7_orders ADD COLUMN sale_source TEXT NOT NULL DEFAULT ''");
        if (!cols.Contains("duration_months")) Run(c, "ALTER TABLE v7_orders ADD COLUMN duration_months INTEGER NOT NULL DEFAULT 12");
        if (!cols.Contains("deduct_stock")) Run(c, "ALTER TABLE v7_orders ADD COLUMN deduct_stock INTEGER NOT NULL DEFAULT 1");
        if (!cols.Contains("seller_record_id")) Run(c, "ALTER TABLE v7_orders ADD COLUMN seller_record_id INTEGER REFERENCES v7_sellers(id)");

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

        // Trigger khóa vĩnh viễn đơn hàng đã hoàn thành (COMPLETED) & giá snapshot cố định
        Run(c, """
            DROP TRIGGER IF EXISTS trg_v7_orders_lock_completed_update;
            CREATE TRIGGER trg_v7_orders_lock_completed_update
            BEFORE UPDATE OF code, customer_id, seller_id, seller_record_id, product_id, quantity, unit_price_vnd, unit_cost_snapshot_vnd, source, status, warehouse_id, installer, installer_employee_id, installer_fee_vnd, contact_name, customer_phone, sale_source, duration_months, deduct_stock, collect_on_delivery, install_address, install_at, plate_no, vin_no, notes, imei, activated_at, completed_at ON v7_orders
            WHEN OLD.status = 'COMPLETED'
            BEGIN
              SELECT RAISE(ABORT,'COMPLETED_ORDER_IMMUTABLE: Đơn hàng đã hoàn thành và khóa snapshot giá cố định, không thể thay đổi.');
            END;

            DROP TRIGGER IF EXISTS trg_v7_order_lines_lock_completed_update;
            CREATE TRIGGER trg_v7_order_lines_lock_completed_update
            BEFORE UPDATE OF order_id, product_id, quantity, unit_price_vnd, unit_cost_snapshot_vnd, installer_fee_vnd, imei, plate_no, vin_no ON v7_order_lines
            WHEN (SELECT status FROM v7_orders WHERE id = OLD.order_id) = 'COMPLETED'
            BEGIN
              SELECT RAISE(ABORT,'COMPLETED_ORDER_LINE_IMMUTABLE: Dòng sản phẩm của đơn hàng đã hoàn thành và khóa snapshot giá cố định, không thể thay đổi.');
            END;

            DROP TRIGGER IF EXISTS trg_v7_order_lines_lock_completed_insert;
            CREATE TRIGGER trg_v7_order_lines_lock_completed_insert
            BEFORE INSERT ON v7_order_lines
            WHEN (SELECT status FROM v7_orders WHERE id = NEW.order_id) = 'COMPLETED'
            BEGIN
              SELECT RAISE(ABORT,'COMPLETED_ORDER_LINE_IMMUTABLE: Đơn hàng đã hoàn thành, không thể thêm dòng sản phẩm mới.');
            END;
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
        var cost = Scalar(c, @"SELECT COALESCE(
            (SELECT gp.unit_cost_vnd FROM v7_supplier_group_prices gp WHERE gp.product_id=$p AND gp.deleted_at IS NULL AND gp.unit_cost_vnd>0 AND gp.effective_from<=date('now') ORDER BY gp.effective_from DESC,gp.id DESC LIMIT 1),
            NULLIF((SELECT base_cost_vnd FROM products WHERE id=$p), 0),
            0)", ("$p", productId));
        var basePrice = Scalar(c, @"SELECT CASE
            WHEN installed_price_vnd>0 THEN installed_price_vnd
            WHEN standard_price_vnd>0 THEN standard_price_vnd
            ELSE renewal_price_vnd END
            FROM v7_group_prices
            WHERE group_id=(SELECT group_id FROM customers WHERE id=$c) AND product_id=$p
              AND deleted_at IS NULL AND effective_from<=date('now') AND (effective_to IS NULL OR effective_to>=date('now'))
            ORDER BY effective_from DESC,id DESC LIMIT 1", ("$c", customerId), ("$p", productId));
        if (basePrice <= 0)
        {
            basePrice = Scalar(c, @"SELECT CASE
                WHEN installed_price_vnd>0 THEN installed_price_vnd
                WHEN standard_price_vnd>0 THEN standard_price_vnd
                ELSE renewal_price_vnd END
                FROM v7_group_prices
                WHERE product_id=$p AND deleted_at IS NULL
                  AND (installed_price_vnd>0 OR standard_price_vnd>0 OR renewal_price_vnd>0)
                ORDER BY effective_from DESC,id DESC LIMIT 1", ("$p", productId));
        }
        if (basePrice <= 0)
        {
            basePrice = Scalar(c, @"SELECT COALESCE(MAX(install_rate_vnd),0) FROM v7_installer_product_rates WHERE product_id=$p AND deleted_at IS NULL", ("$p", productId));
        }
        if (basePrice <= 0)
            basePrice = cost + Scalar(c, "SELECT install_fee_vnd FROM products WHERE id=$p", ("$p", productId));
        var discount = Scalar(c, @"SELECT COALESCE(MAX(discount_percent),0) FROM v7_group_campaigns
            WHERE group_id=(SELECT group_id FROM customers WHERE id=$c) AND is_active=1 AND deleted_at IS NULL
              AND start_date<=date('now') AND end_date>=date('now')", ("$c", customerId));
        var finalPrice = (long)((decimal)basePrice * (100 - discount) / 100m);
        return (finalPrice, cost);
    }

    public long PreviewUnitPrice(long customerId, long productId)
    {
        if (customerId <= 0 || productId <= 0) return 0;
        using var c = Open();
        return QuoteOrderPrice(c, customerId, productId).Price;
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
            COALESCE(sl.code||' — '||sl.full_name, NULLIF(o.sale_source,''), NULLIF(c.source_name,''), u.display_name, ''),
            o.product_id,p.name,o.status,o.source,o.imei,w.name,o.installer,
            o.installer_employee_id,COALESCE(o.installer_fee_vnd,0),
            COALESCE(o.install_address,''),COALESCE(o.install_at,''),COALESCE(o.plate_no,''),o.unit_price_vnd,
            COALESCE(c.code,''),COALESCE(cg.name,'-'),COALESCE(c.representative_name,''),
            COALESCE(o.contact_name,''),COALESCE(o.duration_months,12),COALESCE(o.deduct_stock,1),
            COALESCE(o.activated_at,''),o.warehouse_id,COALESCE(o.notes,''),COALESCE(o.collect_on_delivery,0),
            COALESCE(o.seller_record_id, c.seller_id),COALESCE(o.unit_cost_snapshot_vnd,0)
            FROM v7_orders o JOIN customers c ON c.id=o.customer_id JOIN products p ON p.id=o.product_id
            LEFT JOIN customer_groups cg ON cg.id=c.group_id
            LEFT JOIN warehouses w ON w.id=o.warehouse_id
            LEFT JOIN app_users u ON u.id=o.seller_id
            LEFT JOIN v7_sellers sl ON sl.id=COALESCE(o.seller_record_id, c.seller_id)
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
                FormatInstallDateTime(r.GetString(15)),
                r.GetString(16),
                r.GetInt64(17),
                lines,
                r.GetString(18),
                r.GetString(19),
                r.GetString(20),
                r.GetString(21),
                r.GetInt64(22),
                r.GetInt64(23) == 1,
                r.GetString(24),
                r.IsDBNull(25) ? null : r.GetInt64(25),
                r.GetString(26),
                r.GetInt64(27) == 1,
                r.IsDBNull(28) ? null : r.GetInt64(28),
                r.GetInt64(29)));
        }
        return list;
    }

    public static string FormatInstallDateTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = raw.Trim();
        string[] formats = [
            "dd/MM/yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm",
            "d/M/yyyy HH:mm:ss",
            "d/M/yyyy HH:mm",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm"
        ];
        if (DateTime.TryParseExact(text, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
            return dt.ToString("dd/MM/yyyy HH:mm:ss");
        if (DateTime.TryParse(text, System.Globalization.CultureInfo.GetCultureInfo("vi-VN"), System.Globalization.DateTimeStyles.None, out var dtVi))
            return dtVi.ToString("dd/MM/yyyy HH:mm:ss");
        return text;
    }

    private static (long? SellerRecordId, string SaleSource) ResolveOrderSeller(SqliteConnection c, long customerId, long actor, long? sellerRecordId, string? saleSource)
    {
        long? resolvedId = sellerRecordId is > 0 ? sellerRecordId.Value : null;
        string src = (saleSource ?? "").Trim();

        if (resolvedId.HasValue)
        {
            var label = Str(c, "SELECT code || ' — ' || full_name FROM v7_sellers WHERE id=$id AND deleted_at IS NULL", ("$id", resolvedId.Value));
            if (string.IsNullOrWhiteSpace(label))
                throw new InvalidOperationException("Nhân viên bán hàng đã chọn không tồn tại.");
            src = label;
            return (resolvedId, src);
        }

        if (!string.IsNullOrWhiteSpace(src))
        {
            var matched = Scalar(c, "SELECT COALESCE((SELECT id FROM v7_sellers WHERE deleted_at IS NULL AND (code=$s OR full_name=$s OR (code || ' — ' || full_name)=$s OR $s LIKE '%' || code || '%' OR $s LIKE '%' || full_name || '%') LIMIT 1), 0)", ("$s", src));
            if (matched > 0)
            {
                resolvedId = matched;
                var label = Str(c, "SELECT code || ' — ' || full_name FROM v7_sellers WHERE id=$id", ("$id", matched));
                if (!string.IsNullOrWhiteSpace(label)) src = label;
                return (resolvedId, src);
            }
        }

        var custSeller = Scalar(c, "SELECT COALESCE(seller_id, 0) FROM customers WHERE id=$id", ("$id", customerId));
        if (custSeller > 0)
        {
            var label = Str(c, "SELECT code || ' — ' || full_name FROM v7_sellers WHERE id=$id AND deleted_at IS NULL", ("$id", custSeller));
            if (!string.IsNullOrWhiteSpace(label))
                return (custSeller, string.IsNullOrWhiteSpace(src) ? label : src);
        }

        if (string.IsNullOrWhiteSpace(src))
        {
            src = Str(c, "SELECT COALESCE(NULLIF(source_name,''),(SELECT display_name FROM app_users WHERE id=$u),'') FROM customers WHERE id=$id", ("$id", customerId), ("$u", actor));
        }
        return (resolvedId, src);
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
        string? saleSource = null,
        string? contactName = null,
        long durationMonths = 12,
        long? warehouseId = null,
        bool partner = false,
        string? installer = null,
        long? installerEmployeeId = null,
        bool deductStock = true,
        long? sellerRecordId = null)
    {
        var validItems = (items ?? []).Where(x => x.ProductId > 0 && x.Quantity > 0).ToList();
        if (customer < 1 || validItems.Count == 0)
            throw new InvalidOperationException("Chọn khách hàng và ít nhất 1 sản phẩm cho đơn hàng.");
        if (!deductStock && !partner)
            throw new InvalidOperationException("Chỉ cho phép chọn 'Không trừ tồn kho' khi Đơn vị lắp đặt là Hãng lắp đặt.");
        if (!deductStock && string.IsNullOrWhiteSpace(installer) && !(installerEmployeeId is > 0))
            throw new InvalidOperationException("Khi chọn 'Không trừ tồn kho', bắt buộc phải nhập/chọn tên Hãng lắp đặt.");

        return Tx(actor, c => {
            Role(c, actor, "SELLER", "CSKH", "MANAGER", "ADMIN", "ACCOUNTANT", "WAREHOUSE");
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

            var contact = (contactName ?? "").Trim();
            var phone = !string.IsNullOrWhiteSpace(customerPhone) ? customerPhone.Trim() : Str(c, "SELECT COALESCE(phone,'') FROM customers WHERE id=$id", ("$id", customer));
            var (resolvedSellerId, src) = ResolveOrderSeller(c, customer, actor, sellerRecordId, saleSource);
            var addr = !string.IsNullOrWhiteSpace(address) ? address.Trim() : Str(c, "SELECT COALESCE(address,'') FROM customers WHERE id=$id", ("$id", customer));
            var when = !string.IsNullOrWhiteSpace(installAt) ? FormatInstallDateTime(installAt) : DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
            var dur = durationMonths > 0 ? durationMonths : 12;

            Run(c, @"INSERT INTO v7_orders(code,customer_id,seller_id,seller_record_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd,contact_name,customer_phone,sale_source,duration_months,deduct_stock,source,install_address,install_at,notes,collect_on_delivery)
                VALUES(lower(hex(randomblob(16))),$c,$s,$sellerRec,$p,$qty,$price,$cost,$contact,$phone,$src,$dur,$deduct,$source,$a,$at,$n,$cod)",
                ("$c", customer), ("$s", actor), ("$sellerRec", resolvedSellerId), ("$p", primaryProduct), ("$qty", totalQty), ("$price", totalPrice), ("$cost", totalCost),
                ("$contact", contact), ("$phone", phone), ("$src", src), ("$dur", dur), ("$deduct", deductStock ? 1 : 0),
                ("$source", partner ? "PARTNER" : "INTERNAL"),
                ("$a", addr), ("$at", when), ("$n", notes ?? ""), ("$cod", cod ? 1 : 0));
            var id = Scalar(c, "SELECT last_insert_rowid()");
            Run(c, "UPDATE v7_orders SET code=$code WHERE id=$id", ("$code", $"SO7-{id:000000}"), ("$id", id));

            foreach (var line in expandedLines)
            {
                Run(c, @"INSERT INTO v7_order_lines(order_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd)
                    VALUES($o,$p,$q,$price,$cost)",
                    ("$o", id), ("$p", line.ProductId), ("$q", line.Qty), ("$price", line.UnitPrice), ("$cost", line.UnitCost));
            }

            Event(c, id, actor, "CREATED");

            // Nếu CSKH đã chọn Kho hoặc Đơn vị / Hãng lắp đặt ngay khi tạo đơn thì ghi nhận luôn (nhưng chưa trừ tồn kho cho đến khi Hoàn thành)
            if (warehouseId is > 0 && Scalar(c, "SELECT COUNT(*) FROM warehouses WHERE id=$w AND deleted_at IS NULL", ("$w", warehouseId.Value)) > 0)
            {
                Run(c, "UPDATE v7_orders SET status='APPROVED',warehouse_id=$w WHERE id=$id", ("$w", warehouseId.Value), ("$id", id));
                Event(c, id, actor, "APPROVED");
            }
            if ((installerEmployeeId is > 0) || !string.IsNullOrWhiteSpace(installer))
            {
                ApplyDispatchInternal(c, actor, id, partner, installer ?? "", installerEmployeeId, deductStock, warehouseId);
            }

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
            Role(c, actor, "ADMIN", "MANAGER", "WAREHOUSE", "ACCOUNTANT", "CSKH");
            var st = Status(c, id);
            if (st is "COMPLETED" or "CANCELLED") throw new InvalidOperationException("Đơn đã hoàn tất hoặc đã hủy.");
            if (Scalar(c, "SELECT COUNT(*) FROM warehouses WHERE id=$w AND deleted_at IS NULL", ("$w", warehouse)) == 0)
                throw new InvalidOperationException("Kho không tồn tại.");
            var nextSt = st == "PENDING" ? "APPROVED" : st;
            Run(c, "UPDATE v7_orders SET status=$st,warehouse_id=$w WHERE id=$id", ("$st", nextSt), ("$w", warehouse), ("$id", id));
            Event(c, id, actor, "APPROVED");
            return 0;
        });
    }

    private static void ApplyDispatchInternal(SqliteConnection c, long actor, long id, bool partner, string installer, long? installerEmployeeId, bool? deductStock, long? warehouseId)
    {
        var curSt = Status(c, id);
        if (curSt is "COMPLETED" or "CANCELLED") throw new InvalidOperationException("Đơn đã hoàn tất hoặc đã hủy.");

        bool effectiveDeduct = deductStock ?? !partner;
        if (!effectiveDeduct && !partner)
            throw new InvalidOperationException("Chỉ cho phép chọn 'Không trừ tồn kho' khi Đơn vị lắp đặt là Hãng lắp đặt.");

        if (warehouseId is > 0 && Scalar(c, "SELECT COUNT(*) FROM warehouses WHERE id=$w AND deleted_at IS NULL", ("$w", warehouseId.Value)) > 0)
        {
            Run(c, "UPDATE v7_orders SET warehouse_id=$w WHERE id=$id", ("$w", warehouseId.Value), ("$id", id));
        }

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
            throw new InvalidOperationException(partner
                ? "Bắt buộc phải chọn hoặc nhập tên Hãng lắp đặt."
                : "Cần chọn nhân sự lắp đặt hoặc nhập tên Đơn vị lắp đặt.");
        }

        var nextSt = curSt == "REPORTED" ? "REPORTED" : "DISPATCHED";
        Run(c, "UPDATE v7_orders SET status=$st,source=$s,installer=$i,installer_employee_id=$emp,installer_fee_vnd=$fee,deduct_stock=$deduct WHERE id=$id",
            ("$st", nextSt), ("$s", partner ? "PARTNER" : "INTERNAL"), ("$i", installerName), ("$emp", empId), ("$fee", totalFee), ("$deduct", effectiveDeduct ? 1 : 0), ("$id", id));
        Event(c, id, actor, "DISPATCHED");
    }

    public void Dispatch(long actor, long id, bool partner, string installer, long? installerEmployeeId = null, bool? deductStock = null, long? warehouseId = null)
    {
        Tx(actor, c => {
            Role(c, actor, "ADMIN", "MANAGER", "CSKH", "WAREHOUSE", "ACCOUNTANT");
            ApplyDispatchInternal(c, actor, id, partner, installer, installerEmployeeId, deductStock, warehouseId);
            return 0;
        });
    }

    public void UpdateOrderInfo(
        long actor,
        long id,
        string contactName,
        string customerPhone,
        string installAddress,
        string installAt,
        long durationMonths,
        string notes,
        bool cod,
        long? warehouseId,
        bool partner,
        string installer,
        long? installerEmployeeId,
        bool deductStock,
        long? sellerRecordId = null,
        string? saleSource = null)
    {
        Tx(actor, c => {
            Role(c, actor, "ADMIN", "MANAGER", "CSKH", "WAREHOUSE", "ACCOUNTANT", "SELLER");
            var st = Status(c, id);
            if (st is "COMPLETED" or "CANCELLED") throw new InvalidOperationException("Đơn hàng đã hoàn thành hoặc đã hủy, không thể thay đổi.");
            if (!deductStock && !partner)
                throw new InvalidOperationException("Chỉ cho phép chọn 'Không trừ tồn kho' khi Đơn vị lắp đặt là Hãng lắp đặt.");
            if (!deductStock && string.IsNullOrWhiteSpace(installer) && !(installerEmployeeId is > 0))
                throw new InvalidOperationException("Khi chọn 'Không trừ tồn kho', bắt buộc phải có tên Hãng lắp đặt.");

            var custId = Scalar(c, "SELECT customer_id FROM v7_orders WHERE id=$id", ("$id", id));
            var (resolvedSellerId, resolvedSrc) = ResolveOrderSeller(c, custId, actor, sellerRecordId, saleSource);

            Run(c, @"UPDATE v7_orders SET
                contact_name=$contact,
                customer_phone=$phone,
                install_address=$addr,
                install_at=$at,
                duration_months=$dur,
                notes=$notes,
                collect_on_delivery=$cod,
                deduct_stock=$deduct,
                source=$source,
                seller_record_id=COALESCE($sellerRec, seller_record_id),
                sale_source=CASE WHEN $src<>'' THEN $src ELSE sale_source END
                WHERE id=$id",
                ("$contact", (contactName ?? "").Trim()),
                ("$phone", (customerPhone ?? "").Trim()),
                ("$addr", (installAddress ?? "").Trim()),
                ("$at", FormatInstallDateTime(installAt)),
                ("$dur", durationMonths > 0 ? durationMonths : 12),
                ("$notes", (notes ?? "").Trim()),
                ("$cod", cod ? 1 : 0),
                ("$deduct", deductStock ? 1 : 0),
                ("$source", partner ? "PARTNER" : "INTERNAL"),
                ("$sellerRec", resolvedSellerId),
                ("$src", resolvedSrc),
                ("$id", id));

            if (warehouseId is > 0 && Scalar(c, "SELECT COUNT(*) FROM warehouses WHERE id=$w AND deleted_at IS NULL", ("$w", warehouseId.Value)) > 0)
            {
                var nextSt = st == "PENDING" ? "APPROVED" : st;
                Run(c, "UPDATE v7_orders SET status=$st,warehouse_id=$w WHERE id=$id", ("$st", nextSt), ("$w", warehouseId.Value), ("$id", id));
            }
            if ((installerEmployeeId is > 0) || !string.IsNullOrWhiteSpace(installer))
            {
                ApplyDispatchInternal(c, actor, id, partner, installer, installerEmployeeId, deductStock, warehouseId);
            }

            Event(c, id, actor, "UPDATED_INFO");
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
            Role(c, actor, "ADMIN", "MANAGER", "CSKH", "TECH", "INSTALLER", "WAREHOUSE", "ACCOUNTANT");
            if (Status(c, id) is not ("DISPATCHED" or "REPORTED")) throw new InvalidOperationException("Đơn chưa được điều phối.");

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

    public void Complete(long actor, long id, string? activatedDate = null)
    {
        Tx(actor, c => {
            Role(c, actor, "ADMIN", "MANAGER", "CSKH", "WAREHOUSE", "ACCOUNTANT");
            var curStatus = Status(c, id);
            if (curStatus is not ("REPORTED" or "DISPATCHED"))
                throw new InvalidOperationException("Đơn chưa điều phối hoặc chưa báo cáo lắp đặt.");

            using var q = Cmd(c, @"SELECT customer_id,warehouse_id,source,unit_price_vnd,COALESCE(installer_fee_vnd,0),
                COALESCE(installer,''),COALESCE(code,''),COALESCE(duration_months,12),COALESCE(deduct_stock,1),COALESCE(activated_at,'')
                FROM v7_orders WHERE id=$id", ("$id", id));
            long customer, warehouse, totalPrice, totalInstallerFee, durationMonths;
            bool deductStock;
            string source, installerName, orderCode, existingActivatedAt;
            using (var r = q.ExecuteReader())
            {
                if (!r.Read()) throw new InvalidOperationException("Không thấy đơn.");
                customer = r.GetInt64(0);
                warehouse = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                source = r.GetString(2);
                totalPrice = r.GetInt64(3);
                totalInstallerFee = r.GetInt64(4);
                installerName = r.GetString(5);
                orderCode = r.GetString(6);
                durationMonths = r.GetInt64(7) > 0 ? r.GetInt64(7) : 12;
                deductStock = r.GetInt64(8) == 1 && source != "PARTNER" ? true : (r.GetInt64(8) == 1);
                existingActivatedAt = r.GetString(9);
            }

            if (!deductStock)
            {
                if (source != "PARTNER" || string.IsNullOrWhiteSpace(installerName))
                    throw new InvalidOperationException("Đơn hàng chọn 'Không trừ tồn kho' bắt buộc phải có Đơn vị lắp đặt là Hãng lắp đặt.");
            }

            DateTime actDate = DateTime.Today;
            var rawAct = !string.IsNullOrWhiteSpace(activatedDate) ? activatedDate.Trim() : existingActivatedAt.Trim();
            if (!string.IsNullOrWhiteSpace(rawAct))
            {
                string[] actFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "yyyy-MM-dd HH:mm:ss", "dd/MM/yyyy HH:mm:ss"];
                if (DateTime.TryParseExact(rawAct, actFormats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedAct))
                    actDate = parsedAct.Date;
                else if (DateTime.TryParse(rawAct, System.Globalization.CultureInfo.GetCultureInfo("vi-VN"), System.Globalization.DateTimeStyles.None, out var parsedVi))
                    actDate = parsedVi.Date;
            }
            var actDateStr = actDate.ToString("yyyy-MM-dd");
            var expDateStr = actDate.AddMonths((int)durationMonths).ToString("yyyy-MM-dd");

            using var lq = Cmd(c, @"SELECT l.id,l.product_id,p.name,p.is_serialized,l.quantity,l.unit_price_vnd,l.unit_cost_snapshot_vnd,
                COALESCE(l.imei,''),COALESCE(l.plate_no,''),COALESCE(l.vin_no,''),COALESCE(l.installer_fee_vnd,0),COALESCE(m.product_kind,'GOODS')
                FROM v7_order_lines l JOIN products p ON p.id=l.product_id
                LEFT JOIN product_catalog_metadata m ON m.product_id=p.id
                WHERE l.order_id=$o AND l.deleted_at IS NULL ORDER BY l.id", ("$o", id));
            var lines = new List<(long LineId, long ProductId, string ProductName, bool IsSerialized, long Qty, long UnitPrice, long UnitCost, string Imei, string PlateNo, string VinNo, long InstallerFee, string ProductKind)>();
            using (var lr = lq.ExecuteReader())
            {
                while (lr.Read())
                    lines.Add((lr.GetInt64(0), lr.GetInt64(1), lr.GetString(2), lr.GetInt64(3) == 1, lr.GetInt64(4), lr.GetInt64(5), lr.GetInt64(6), lr.GetString(7), lr.GetString(8), lr.GetString(9), lr.GetInt64(10), lr.GetString(11)));
            }

            if (curStatus == "DISPATCHED" && lines.Any(x => x.IsSerialized && string.IsNullOrWhiteSpace(x.Imei)))
                throw new InvalidOperationException("Đơn chưa báo cáo mã IMEI lắp đặt.");

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
                    if (deductStock)
                    {
                        long txWarehouse = warehouse;
                        using var find = Cmd(c, @"SELECT id, COALESCE(warehouse_id, $w) FROM stock_units
                            WHERE imei=$imei AND product_id=$p AND deleted_at IS NULL
                              AND status IN ('IN_STOCK','CONSIGNED','TECHNICIAN')
                            ORDER BY CASE WHEN warehouse_id=$w THEN 0 ELSE 1 END, id ASC LIMIT 1",
                            ("$imei", ln.Imei), ("$p", ln.ProductId), ("$w", warehouse));
                        using var fr = find.ExecuteReader();
                        if (fr.Read())
                        {
                            stockUnitId = fr.GetInt64(0);
                            if (txWarehouse <= 0 && !fr.IsDBNull(1)) txWarehouse = fr.GetInt64(1);
                            fr.Close();
                        }
                        else
                        {
                            fr.Close();
                            if (Scalar(c, "SELECT COUNT(*) FROM stock_units WHERE imei=$imei AND deleted_at IS NULL", ("$imei", ln.Imei)) > 0)
                                throw new InvalidOperationException($"IMEI '{ln.Imei}' đã tồn tại và không còn ở trạng thái tồn kho khả dụng.");

                            // Nếu có bản ghi stock_units đã bị xóa mềm (deleted_at IS NOT NULL) trùng IMEI, giải phóng hậu tố IMEI cũ để không vướng UNIQUE
                            Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id WHERE imei=$imei AND deleted_at IS NOT NULL", ("$imei", ln.Imei));

                            // ĐV lắp đặt gửi mã IMEI thực tế ngoài hiện trường về -> lấy 1 thiết bị IN_STOCK của cùng sản phẩm trong kho để gán IMEI & trừ tồn kho
                            using var findSlot = Cmd(c, @"SELECT id, COALESCE(warehouse_id, $w) FROM stock_units
                                WHERE product_id=$p AND status='IN_STOCK' AND deleted_at IS NULL
                                ORDER BY CASE WHEN warehouse_id=$w THEN 0 ELSE 1 END,
                                         CASE WHEN imei LIKE 'KHO-%' THEN 0 ELSE 1 END,
                                         id ASC LIMIT 1",
                                ("$p", ln.ProductId), ("$w", warehouse));
                            using var sr = findSlot.ExecuteReader();
                            if (sr.Read())
                            {
                                stockUnitId = sr.GetInt64(0);
                                if (txWarehouse <= 0 && !sr.IsDBNull(1)) txWarehouse = sr.GetInt64(1);
                                sr.Close();
                                Run(c, "UPDATE stock_units SET imei=$imei WHERE id=$id", ("$imei", ln.Imei), ("$id", stockUnitId));
                            }
                            else
                            {
                                sr.Close();
                                throw new InvalidOperationException($"Sản phẩm '{ln.ProductName}' hiện đã hết tồn kho (hoặc IMEI '{ln.Imei}' không có trong kho). Vui lòng nhập kho trước khi hoàn tất đơn có trừ tồn kho, hoặc chọn Hãng lắp đặt (Không trừ tồn kho).");
                            }
                        }

                        if (!saleOutTxId.HasValue)
                        {
                            var txCode = "XK-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
                            Run(c, "INSERT INTO inventory_transactions(code,movement_type,warehouse_id,customer_id,actor_user_id,notes) VALUES($code,'SALE_OUT',$w,$c,$actor,'Xuất lắp đặt đơn V7')",
                                ("$code", txCode), ("$w", txWarehouse > 0 ? txWarehouse : null), ("$c", customer), ("$actor", actor));
                            saleOutTxId = Scalar(c, "SELECT last_insert_rowid()");
                        }

                        Run(c, "INSERT INTO inventory_transaction_lines(transaction_id,stock_unit_id,product_id,unit_cost_snapshot_vnd) VALUES($t,$s,$p,$cost)",
                            ("$t", saleOutTxId.Value), ("$s", stockUnitId), ("$p", ln.ProductId), ("$cost", ln.UnitCost));

                        Run(c, "UPDATE stock_units SET status='INSTALLED',warehouse_id=NULL,customer_id=$c,employee_id=NULL,updated_at=CURRENT_TIMESTAMP WHERE id=$id AND status IN ('IN_STOCK','CONSIGNED','TECHNICIAN')",
                            ("$c", customer), ("$id", stockUnitId));
                        if (Scalar(c, "SELECT changes()") != 1) throw new InvalidOperationException($"Không thể trừ kho IMEI '{ln.Imei}'.");
                        Run(c, "INSERT INTO v7_stock_effects(order_id,stock_unit_id) VALUES($o,$s)", ("$o", id), ("$s", stockUnitId));
                    }
                    else
                    {
                        using var find = Cmd(c, "SELECT id FROM stock_units WHERE imei=$imei AND deleted_at IS NULL", ("$imei", ln.Imei));
                        var existing = find.ExecuteScalar();
                        if (existing is not null)
                            throw new InvalidOperationException($"IMEI '{ln.Imei}' đã tồn tại trong danh sách thiết bị.");
                        Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id WHERE imei=$imei AND deleted_at IS NOT NULL", ("$imei", ln.Imei));
                        Run(c, "INSERT INTO stock_units(product_id,imei,status,customer_id) VALUES($p,$imei,'INSTALLED',$c)",
                            ("$p", ln.ProductId), ("$imei", ln.Imei), ("$c", customer));
                        stockUnitId = Scalar(c, "SELECT last_insert_rowid()");
                    }

                    // Tạo thuê bao kích hoạt (activations) theo Ngày kích hoạt và Thời hạn của đơn
                    Run(c, @"INSERT INTO activations(v7_order_id,stock_unit_id,customer_id,product_id,plate_no,vin_no,activated_at,expires_at)
                        VALUES($o,$s,$c,$p,$plate,$vin,$act,$exp)",
                        ("$o", id), ("$s", stockUnitId), ("$c", customer), ("$p", ln.ProductId), ("$plate", ln.PlateNo), ("$vin", ln.VinNo), ("$act", actDateStr), ("$exp", expDateStr));
                }
                else if (deductStock && ln.ProductKind == "GOODS")
                {
                    // Sản phẩm hàng hóa quản lý theo số lượng: trừ tồn kho tại kho đã chọn (hoặc kho đang có tồn)
                    long targetWh = warehouse;
                    long locId = 0;
                    if (targetWh > 0)
                    {
                        locId = Scalar(c, @"SELECT l.id FROM warehouse_locations l
                            WHERE l.warehouse_id=$w AND l.is_active=1 AND l.deleted_at IS NULL
                            ORDER BY CASE WHEN (SELECT COALESCE(SUM(delta_qty),0) FROM quantity_movements q WHERE q.product_id=$p AND q.warehouse_id=$w AND q.location_id=l.id) >= $q THEN 0 ELSE 1 END,
                                     CASE WHEN l.code='MAIN' THEN 0 ELSE 1 END, l.id LIMIT 1",
                            ("$w", targetWh), ("$p", ln.ProductId), ("$q", ln.Qty));
                    }
                    var curQty = (targetWh > 0 && locId > 0)
                        ? Scalar(c, "SELECT COALESCE(SUM(delta_qty),0) FROM quantity_movements WHERE product_id=$p AND warehouse_id=$w AND location_id=$l",
                            ("$p", ln.ProductId), ("$w", targetWh), ("$l", locId))
                        : 0;
                    if (curQty < ln.Qty)
                    {
                        // Tìm vị trí kho bất kỳ còn đủ tồn khả dụng của sản phẩm này
                        using var findLoc = Cmd(c, @"SELECT warehouse_id, location_id, COALESCE(SUM(delta_qty),0) AS bal
                            FROM quantity_movements WHERE product_id=$p
                            GROUP BY warehouse_id, location_id
                            HAVING bal >= $q
                            ORDER BY CASE WHEN warehouse_id=$w THEN 0 ELSE 1 END, bal DESC LIMIT 1",
                            ("$p", ln.ProductId), ("$q", ln.Qty), ("$w", warehouse));
                        using var lrLoc = findLoc.ExecuteReader();
                        if (lrLoc.Read())
                        {
                            targetWh = lrLoc.GetInt64(0);
                            locId = lrLoc.GetInt64(1);
                            curQty = lrLoc.GetInt64(2);
                        }
                    }
                    if (locId > 0 && curQty >= ln.Qty)
                    {
                        Run(c, @"INSERT INTO quantity_movements(document_no,product_id,warehouse_id,location_id,delta_qty,movement_type,ref_type,ref_id,note,actor_user_id)
                            VALUES('Q-'||lower(hex(randomblob(8))),$p,$w,$l,$d,'ISSUE','V7_ORDER',$o,'Xuất theo đơn hàng SO',$u)",
                            ("$p", ln.ProductId), ("$w", targetWh), ("$l", locId), ("$d", -ln.Qty), ("$o", id), ("$u", actor));
                    }
                    else
                    {
                        throw new InvalidOperationException($"Sản phẩm '{ln.ProductName}' không đủ số lượng tồn kho để xuất (Tồn: {curQty}, Cần xuất: {ln.Qty}).");
                    }
                }
            }

            // Tự động tạo hóa đơn công nợ SALE gồm đầy đủ các dòng sản phẩm
            var invoiceNo = "HD-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            Run(c, @"INSERT INTO invoices(invoice_no,invoice_type,customer_id,v7_order_id,due_at,amount_vnd,notes)
                VALUES($no,'SALE',$c,$o,date('now','+30 days'),$price,$notes)",
                ("$no", invoiceNo), ("$c", customer), ("$o", id), ("$price", totalPrice), ("$notes", $"Đơn hàng lắp đặt {orderCode}"));
            var invoiceId = Scalar(c, "SELECT last_insert_rowid()");
            foreach (var grp in lines.GroupBy(x => (x.ProductId, x.ProductName, x.UnitPrice)))
            {
                Run(c, "INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$desc,$p,$q,$price)",
                    ("$i", invoiceId), ("$desc", $"Thiết bị & lắp đặt: {grp.Key.ProductName}"), ("$p", grp.Key.ProductId), ("$q", grp.Sum(x => x.Qty)), ("$price", grp.Key.UnitPrice));
            }

            // Tự động tạo chứng từ công nợ chi phí công kỹ thuật lắp đặt (SERVICE) nếu đơn có công lắp > 0
            if (totalInstallerFee > 0)
            {
                var srvNo = "CL-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                var partnerLabel = !string.IsNullOrWhiteSpace(installerName) ? installerName : "Nhân sự lắp đặt";
                Run(c, @"INSERT INTO invoices(invoice_no,invoice_type,v7_order_id,due_at,amount_vnd,notes)
                    VALUES($no,'SERVICE',$o,date('now','+15 days'),$fee,$notes)",
                    ("$no", srvNo), ("$o", id), ("$fee", totalInstallerFee), ("$notes", $"{partnerLabel} — Công lắp đặt đơn {orderCode}"));
                var srvInvId = Scalar(c, "SELECT last_insert_rowid()");
                foreach (var grp in lines.Where(x => x.InstallerFee > 0).GroupBy(x => (x.ProductId, x.ProductName, x.InstallerFee)))
                {
                    Run(c, "INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$desc,$p,$q,$price)",
                        ("$i", srvInvId), ("$desc", $"Công lắp đặt ({orderCode}): {grp.Key.ProductName}"), ("$p", grp.Key.ProductId), ("$q", grp.Sum(x => x.Qty)), ("$price", grp.Key.InstallerFee));
                }
            }

            Run(c, "UPDATE v7_orders SET status='COMPLETED',activated_at=$act,completed_at=CURRENT_TIMESTAMP WHERE id=$id", ("$act", actDateStr), ("$id", id));
            Event(c, id, actor, "COMPLETED");
            return 0;
        });
    }

    public void Cancel(long actor, long id)
    {
        Tx(actor, c => {
            Role(c, actor, "ADMIN", "MANAGER", "CSKH", "WAREHOUSE", "ACCOUNTANT", "SELLER");
            if (Status(c, id) == "COMPLETED") throw new InvalidOperationException("Không được hủy đơn đã hoàn tất.");
            Run(c, "UPDATE v7_orders SET status='CANCELLED' WHERE id=$id AND status<>'CANCELLED'", ("$id", id));
            Event(c, id, actor, "CANCELLED");
            return 0;
        });
    }
}
