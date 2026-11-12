using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using TNM.Management.Models;

namespace TNM.Management.Services;

/// <summary>
/// Core database service for Dashboard, IMEI Inventory, Device Subscriptions/Renewals, Finance & Reports.
/// </summary>
public sealed class DemoDb
{
    public long ActorId { get; set; } = 0;
    private readonly string _path;
    private readonly string _contentRoot;

    public DemoDb(IWebHostEnvironment env)
    {
        _contentRoot = env.ContentRootPath;
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "tnm-local.db");
    }
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            ForeignKeys = true,
            DefaultTimeout = 8,
            Pooling = true
        }.ToString());
        c.Open(); AuditRuntime.Attach(c, () => ActorId);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout=8000;";
            cmd.ExecuteNonQuery();
        }
        return c;
    }
    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? tx, string sql,
        params (string name, object? value)[] parameters)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    private static long N(SqliteConnection c, SqliteTransaction? tx, string sql,
        params (string, object?)[] args)
    {
        using var cmd = Cmd(c, tx, sql, args);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
    }
    private static string S(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static long L(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);
    private static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader,T> map,
        params (string, object?)[] args)
    {
        using var cmd = Cmd(c, null, sql, args);
        using var reader = cmd.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(map(reader));
        return result;
    }
    private static List<T> QueryTx<T>(SqliteConnection c, SqliteTransaction tx, string sql, Func<SqliteDataReader,T> map,
        params (string, object?)[] args)
    {
        using var cmd = Cmd(c, tx, sql, args);
        using var reader = cmd.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(map(reader));
        return result;
    }
    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql,
        params (string, object?)[] args)
    {
        using var cmd = Cmd(c, tx, sql, args);
        cmd.ExecuteNonQuery();
    }
    private T Tx<T>(Func<SqliteConnection,SqliteTransaction,T> action)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        try { var result = action(c,tx); tx.Commit(); return result; }
        catch { tx.Rollback(); throw; }
    }
    private void Log(SqliteConnection c, SqliteTransaction tx, string action, string entity,
        long entityId, string message) => Exec(c, tx,
        "INSERT INTO audit_logs(actor_user_id,action,entity,entity_id,details) VALUES($actor,$a,$e,$id,$d)",
        ("$actor",ActorId > 0 ? ActorId : null),("$a",action),("$e",entity),("$id",entityId),("$d",message));

    public void Initialize()
    {
        using var c = Open();
        Exec(c, null, "PRAGMA journal_mode=WAL;");
        Exec(c, null, File.ReadAllText(Path.Combine(_contentRoot, "Data", "schema.sql")));
        Exec(c, null, "DROP TRIGGER IF EXISTS v7_product_sku_immutable;");
        if (N(c, null, "SELECT count(*) FROM app_users") == 0)
        {
            var demo = Environment.GetEnvironmentVariable("TNM_SEED_DEMO") == "1";
            Exec(c, null, File.ReadAllText(Path.Combine(_contentRoot, "Data", demo ? "seed.sql" : "seed_empty.sql")));
        }
        Exec(c, null, "INSERT OR IGNORE INTO warehouse_locations(warehouse_id,code,name) SELECT id,'MAIN','Vị trí chính' FROM warehouses");
        var custCols = Query(c, "PRAGMA table_info(customers)", r => S(r, 1));
        if (!custCols.Any(x => string.Equals(x, "representative_name", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE customers ADD COLUMN representative_name TEXT NOT NULL DEFAULT ''");
        if (!custCols.Any(x => string.Equals(x, "seller_id", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE customers ADD COLUMN seller_id INTEGER REFERENCES v7_sellers(id)");

        var renCols = Query(c, "PRAGMA table_info(renewals)", r => S(r, 1));
        if (!renCols.Any(x => string.Equals(x, "seller_id", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE renewals ADD COLUMN seller_id INTEGER REFERENCES v7_sellers(id)");
        if (!renCols.Any(x => string.Equals(x, "seller_name", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE renewals ADD COLUMN seller_name TEXT NOT NULL DEFAULT ''");
        if (!renCols.Any(x => string.Equals(x, "is_locked", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE renewals ADD COLUMN is_locked INTEGER NOT NULL DEFAULT 0");
        if (!renCols.Any(x => string.Equals(x, "locked_at", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE renewals ADD COLUMN locked_at TEXT");
        if (!renCols.Any(x => string.Equals(x, "locked_by", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE renewals ADD COLUMN locked_by INTEGER REFERENCES app_users(id)");

        var invCols = Query(c, "PRAGMA table_info(invoices)", r => S(r, 1));
        if (!invCols.Any(x => string.Equals(x, "is_locked", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE invoices ADD COLUMN is_locked INTEGER NOT NULL DEFAULT 0");
        if (!invCols.Any(x => string.Equals(x, "locked_at", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE invoices ADD COLUMN locked_at TEXT");
        if (!invCols.Any(x => string.Equals(x, "locked_by", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE invoices ADD COLUMN locked_by INTEGER REFERENCES app_users(id)");

        var actCols = Query(c, "PRAGMA table_info(activations)", r => S(r, 1));
        if (!actCols.Any(x => string.Equals(x, "device_type_label", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE activations ADD COLUMN device_type_label TEXT NOT NULL DEFAULT ''");
        if (!actCols.Any(x => string.Equals(x, "agency_name", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE activations ADD COLUMN agency_name TEXT NOT NULL DEFAULT ''");
        if (!actCols.Any(x => string.Equals(x, "owner_name", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE activations ADD COLUMN owner_name TEXT NOT NULL DEFAULT ''");
        if (!actCols.Any(x => string.Equals(x, "owner_phone", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE activations ADD COLUMN owner_phone TEXT NOT NULL DEFAULT ''");
        if (!actCols.Any(x => string.Equals(x, "last_renewed_at", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE activations ADD COLUMN last_renewed_at TEXT NOT NULL DEFAULT ''");
        if (!actCols.Any(x => string.Equals(x, "renewal_count", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE activations ADD COLUMN renewal_count INTEGER NOT NULL DEFAULT 0");

        var sgCols = Query(c, "PRAGMA table_info(supplier_groups)", r => S(r, 1));
        if (sgCols.Any(x => string.Equals(x, "deleted_at", StringComparison.OrdinalIgnoreCase)))
        {
            // Tự động đồng bộ Nhóm NCC -> Đơn vị NCC nếu nhóm đã khai báo bảng giá nhưng chưa tạo dòng trong suppliers
            var missingSupGroups = Query(c, @"SELECT sg.id, sg.code, sg.name FROM supplier_groups sg
                WHERE sg.deleted_at IS NULL AND sg.code <> 'DEFAULT'
                  AND NOT EXISTS (SELECT 1 FROM suppliers s WHERE s.group_id=sg.id AND s.deleted_at IS NULL)",
                r => (Id: L(r, 0), Code: S(r, 1), Name: S(r, 2)));
            foreach (var grp in missingSupGroups)
            {
                var nextNo = N(c, null, "SELECT COALESCE(MAX(id),0)+1 FROM suppliers");
                var supCode = $"NCC{nextNo:D5}";
                while (N(c, null, "SELECT COUNT(*) FROM suppliers WHERE code=$c", ("$c", supCode)) > 0)
                {
                    nextNo++;
                    supCode = $"NCC{nextNo:D5}";
                }
                Exec(c, null, "INSERT INTO suppliers(code,name,group_id,phone,tax_code) VALUES($c,$n,$g,'','')",
                    ("$c", supCode), ("$n", grp.Name), ("$g", grp.Id));
            }

            // Đồng bộ giá vốn & giá gia hạn từ bảng giá V7 về bảng products
            Exec(c, null, @"UPDATE products SET base_cost_vnd = COALESCE(
                (SELECT gp.unit_cost_vnd FROM v7_supplier_group_prices gp WHERE gp.product_id=products.id AND gp.deleted_at IS NULL AND gp.unit_cost_vnd>0 ORDER BY gp.effective_from DESC, gp.id DESC LIMIT 1),
                base_cost_vnd)
                WHERE base_cost_vnd = 0 AND EXISTS (SELECT 1 FROM v7_supplier_group_prices gp WHERE gp.product_id=products.id AND gp.deleted_at IS NULL AND gp.unit_cost_vnd>0)");
            Exec(c, null, @"UPDATE products SET renewal_fee_vnd = COALESCE(
                (SELECT gp.renewal_price_vnd FROM v7_group_prices gp WHERE gp.product_id=products.id AND gp.deleted_at IS NULL AND gp.renewal_price_vnd>0 ORDER BY gp.effective_from DESC, gp.id DESC LIMIT 1),
                renewal_fee_vnd)
                WHERE renewal_fee_vnd = 0 AND EXISTS (SELECT 1 FROM v7_group_prices gp WHERE gp.product_id=products.id AND gp.deleted_at IS NULL AND gp.renewal_price_vnd>0)");
        }
    }

    public DashboardVm Dashboard()
    {
        using var c = Open();
        long K(string sql) => N(c, null, sql);
        return new DashboardVm(
          K("SELECT COUNT(*) FROM products WHERE is_active=1 AND deleted_at IS NULL"),
          K("SELECT COUNT(*) FROM customers WHERE deleted_at IS NULL"),
          K("SELECT COUNT(*) FROM stock_units WHERE status='IN_STOCK' AND deleted_at IS NULL"),
          K("SELECT COUNT(*) FROM stock_units WHERE status='CONSIGNED' AND deleted_at IS NULL"),
          K("SELECT COUNT(*) FROM activations WHERE status='ACTIVE' AND deleted_at IS NULL AND date(expires_at)>=date('now')"),
          K("SELECT COUNT(*) FROM activations WHERE status='ACTIVE' AND deleted_at IS NULL AND date(expires_at) BETWEEN date('now') AND date('now','+30 days')"),
          K("SELECT COUNT(*) FROM v7_orders WHERE status IN ('PENDING','APPROVED','DISPATCHED','REPORTED') AND deleted_at IS NULL"),
          K("SELECT COALESCE(SUM(i.amount_vnd-COALESCE(p.paid,0)),0) FROM invoices i LEFT JOIN (SELECT invoice_id,SUM(allocated_vnd) paid FROM payment_allocations WHERE deleted_at IS NULL GROUP BY invoice_id) p ON p.invoice_id=i.id WHERE i.deleted_at IS NULL AND i.status!='VOID' AND i.invoice_type IN ('SALE','RENEWAL')"),
          K("SELECT COALESCE(SUM(amount_vnd),0) FROM invoices WHERE deleted_at IS NULL AND invoice_type IN ('SALE','RENEWAL') AND status!='VOID'"),
          K("SELECT COALESCE(SUM(pa.allocated_vnd),0) FROM payment_allocations pa JOIN payments p ON p.id=pa.payment_id WHERE p.deleted_at IS NULL AND pa.deleted_at IS NULL AND p.direction='IN'"),
          K("SELECT COALESCE(SUM(amount_vnd),0) FROM invoices WHERE deleted_at IS NULL AND invoice_type='RENEWAL' AND status!='VOID'")
        );
    }

    public List<ProductVm> Products()
    {
        using var c = Open();
        return Query(c, @"SELECT p.id,p.sku,p.name,g.name,
            COALESCE(NULLIF(p.base_cost_vnd,0),(SELECT gp.unit_cost_vnd FROM v7_supplier_group_prices gp WHERE gp.product_id=p.id AND gp.deleted_at IS NULL AND gp.unit_cost_vnd>0 ORDER BY gp.effective_from DESC,gp.id DESC LIMIT 1),0),
            COALESCE(NULLIF(p.renewal_fee_vnd,0),(SELECT gp.renewal_price_vnd FROM v7_group_prices gp WHERE gp.product_id=p.id AND gp.deleted_at IS NULL AND gp.renewal_price_vnd>0 ORDER BY gp.effective_from DESC,gp.id DESC LIMIT 1),0),
            COALESCE(NULLIF(p.install_fee_vnd,0),(SELECT CASE WHEN gp.installed_price_vnd>gp.standard_price_vnd AND gp.standard_price_vnd>0 THEN gp.installed_price_vnd-gp.standard_price_vnd ELSE gp.installed_price_vnd END FROM v7_group_prices gp WHERE gp.product_id=p.id AND gp.deleted_at IS NULL AND gp.installed_price_vnd>0 ORDER BY gp.effective_from DESC,gp.id DESC LIMIT 1),0),
            p.warranty_months,p.is_serialized,
            CASE
              WHEN COALESCE(m.product_kind,'GOODS') <> 'GOODS' THEN 0
              WHEN p.is_serialized = 1 THEN
                (SELECT COUNT(*) FROM stock_units u WHERE u.product_id=p.id AND u.status='IN_STOCK' AND u.deleted_at IS NULL)
                + COALESCE((SELECT SUM(qm.delta_qty) FROM quantity_movements qm WHERE qm.product_id=p.id), 0)
              ELSE
                COALESCE((SELECT SUM(qm.delta_qty) FROM quantity_movements qm WHERE qm.product_id=p.id), 0)
            END,
            COALESCE(m.product_kind,'GOODS')
            FROM products p JOIN product_groups g ON g.id=p.group_id
            LEFT JOIN product_catalog_metadata m ON m.product_id=p.id
            WHERE p.is_active=1 AND p.deleted_at IS NULL ORDER BY p.id DESC",
          r => new ProductVm(L(r,0),S(r,1),S(r,2),S(r,3),L(r,4),L(r,5),L(r,6),(int)L(r,7),L(r,8)==1,L(r,9),S(r,10)));
    }

    public List<ProductStockSummaryVm> ProductStockSummaries(long? warehouseId = null)
    {
        using var c = Open();
        var w = warehouseId is > 0 ? warehouseId.Value : 0;
        return Query(c, @"SELECT p.id, p.sku, p.name, p.is_serialized, COALESCE(m.product_kind,'GOODS'),
            CASE
              WHEN p.is_serialized = 1 THEN
                (SELECT COUNT(*) FROM stock_units u WHERE u.product_id=p.id AND u.status='IN_STOCK' AND u.deleted_at IS NULL AND ($w=0 OR u.warehouse_id=$w))
                + COALESCE((SELECT SUM(qm.delta_qty) FROM quantity_movements qm WHERE qm.product_id=p.id AND ($w=0 OR qm.warehouse_id=$w)), 0)
              ELSE
                COALESCE((SELECT SUM(qm.delta_qty) FROM quantity_movements qm WHERE qm.product_id=p.id AND ($w=0 OR qm.warehouse_id=$w)), 0)
            END AS in_stock_qty,
            (SELECT COUNT(*) FROM stock_units u WHERE u.product_id=p.id AND u.status IN ('CONSIGNED','TECHNICIAN') AND u.deleted_at IS NULL) AS consigned_tech_qty,
            (SELECT COUNT(*) FROM stock_units u WHERE u.product_id=p.id AND u.status='INSTALLED' AND u.deleted_at IS NULL)
              + MAX(0, COALESCE((SELECT ABS(SUM(qm.delta_qty)) FROM quantity_movements qm WHERE qm.product_id=p.id AND qm.movement_type='ISSUE' AND ($w=0 OR qm.warehouse_id=$w)), 0)
                     - COALESCE((SELECT SUM(qm2.delta_qty) FROM quantity_movements qm2 WHERE qm2.product_id=p.id AND qm2.ref_type='V7_ORDER_CANCEL' AND ($w=0 OR qm2.warehouse_id=$w)), 0)) AS installed_qty
            FROM products p
            LEFT JOIN product_catalog_metadata m ON m.product_id=p.id
            WHERE p.is_active=1 AND p.deleted_at IS NULL AND COALESCE(m.product_kind,'GOODS')='GOODS'
            ORDER BY p.sku",
            r => new ProductStockSummaryVm(L(r,0),S(r,1),S(r,2),L(r,3)==1,S(r,4),L(r,5),L(r,6),L(r,7)),
            ("$w", w));
    }

    public List<(long Id, string Name)> Warehouses()
    {
        using var c = Open();
        return Query(c, "SELECT id,name FROM warehouses WHERE deleted_at IS NULL ORDER BY id", r => (L(r,0),S(r,1)));
    }

    public List<(long Id, string Name)> Technicians()
    {
        using var c = Open();
        return Query(c, "SELECT id,full_name FROM employees WHERE is_active=1 AND deleted_at IS NULL ORDER BY id", r => (L(r,0),S(r,1)));
    }

    public List<(long Id, string Name)> Suppliers()
    {
        using var c = Open();
        return Query(c, "SELECT id,name FROM suppliers WHERE deleted_at IS NULL ORDER BY id", r => (L(r,0),S(r,1)));
    }

    public List<CustomerVm> Customers()
    {
        using var c = Open();
        return Query(c, @"SELECT c.id,c.code,c.name,COALESCE(g.name,'-'),
            COALESCE(s.code||' — '||s.full_name, NULLIF(c.source_name,''), ''),
            c.phone,c.address,COALESCE(c.representative_name,''),c.seller_id
            FROM customers c
            LEFT JOIN customer_groups g ON g.id=c.group_id
            LEFT JOIN v7_sellers s ON s.id=c.seller_id
            WHERE c.deleted_at IS NULL ORDER BY c.id DESC",
          r => new CustomerVm(L(r,0),S(r,1),S(r,2),S(r,3),S(r,4),S(r,5),S(r,6),S(r,7),r.IsDBNull(8) ? null : L(r,8)));
    }

    public List<SellerChoiceVm> SellerChoices()
    {
        using var c = Open();
        return Query(c, @"SELECT id, code, full_name, COALESCE(phone,''), COALESCE(department,'Kinh doanh / Sale'),
            COALESCE(order_commission_vnd,0), COALESCE(renewal_commission_vnd,0)
            FROM v7_sellers WHERE is_active=1 AND deleted_at IS NULL ORDER BY id ASC, full_name ASC",
            r => new SellerChoiceVm(L(r,0), S(r,1), S(r,2), S(r,3), S(r,4), L(r,5), L(r,6)));
    }

    public List<StockVm> Stock()
    {
        using var c = Open();
        return Query(c, @"SELECT u.id,u.imei,p.sku,p.name,u.status,
         CASE WHEN u.status='IN_STOCK' THEN COALESCE(w.name,'-')
              WHEN u.status IN ('CONSIGNED','INSTALLED','WARRANTY') THEN COALESCE(cw.name,c.name,'-')
              WHEN u.status='TECHNICIAN' THEN COALESCE(e.full_name,'-') ELSE 'NCC' END,
         u.updated_at,u.product_id,u.warehouse_id
         FROM stock_units u JOIN products p ON p.id=u.product_id
         LEFT JOIN warehouses w ON w.id=u.warehouse_id LEFT JOIN customers c ON c.id=u.customer_id
         LEFT JOIN warehouses cw ON cw.name=c.name AND cw.deleted_at IS NULL
         LEFT JOIN employees e ON e.id=u.employee_id
         WHERE u.deleted_at IS NULL
         ORDER BY CASE WHEN u.status='IN_STOCK' THEN 0 ELSE 1 END, u.id DESC",
          r => new StockVm(L(r,0),S(r,1),S(r,2),S(r,3),S(r,4),S(r,5),S(r,6),L(r,7),r.IsDBNull(8) ? null : L(r,8)));
    }

    public int Receive(long productId, long warehouseId, string multiline, long? supplierId = null, int quantityIfNoImei = 0)
    {
        var codes = (multiline ?? "").Replace("\r","").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Where(x => !string.IsNullOrWhiteSpace(x))
           .Distinct(StringComparer.OrdinalIgnoreCase)
           .ToList();
        if (codes.Count == 0 && quantityIfNoImei > 0 && quantityIfNoImei <= 500)
        {
            var stamp = DateTime.Now.ToString("yyMMddHHmmss");
            for (int i = 1; i <= quantityIfNoImei; i++)
                codes.Add($"KHO-{productId}-{stamp}-{i:D3}");
        }
        if (codes.Count == 0 || codes.Count > 500 || codes.Any(x => x.Length > 64))
            throw new InvalidOperationException("Nhập 1–500 IMEI (hoặc nhập Số lượng nhập 1–500), tối đa 64 ký tự / dòng.");
        return Tx((c,t) => {
            if (N(c,t,"SELECT COUNT(*) FROM products WHERE id=$id AND is_serialized=1 AND is_active=1 AND deleted_at IS NULL",("$id",productId)) == 0)
                throw new InvalidOperationException("Nhập kho theo IMEI chỉ hỗ trợ sản phẩm có serial đang kinh doanh.");
            var cost = N(c,t,@"SELECT COALESCE(
                (SELECT gp.unit_cost_vnd FROM v7_supplier_group_prices gp WHERE gp.product_id=$id AND gp.deleted_at IS NULL AND gp.effective_from<=date('now') ORDER BY gp.effective_from DESC,gp.id DESC LIMIT 1),
                NULLIF((SELECT base_cost_vnd FROM products WHERE id=$id), 0),
                0)",("$id",productId));
            var resolvedSup = supplierId.HasValue && supplierId.Value > 0
                ? supplierId.Value
                : N(c,t,@"SELECT COALESCE(
                    (SELECT s.id FROM suppliers s JOIN v7_supplier_group_prices gp ON gp.supplier_group_id=s.group_id WHERE gp.product_id=$id AND s.deleted_at IS NULL AND gp.deleted_at IS NULL ORDER BY gp.effective_from DESC,s.id LIMIT 1),
                    (SELECT id FROM suppliers WHERE deleted_at IS NULL ORDER BY id LIMIT 1),
                    0)",("$id",productId));
            var prodName = QueryTx(c,t,"SELECT sku||' · '||name FROM products WHERE id=$id", r => S(r,0), ("$id",productId)).FirstOrDefault() ?? $"SP #{productId}";
            var code = "NK-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            Exec(c,t,"INSERT INTO inventory_transactions(code,movement_type,warehouse_id,actor_user_id,notes) VALUES($n,'RECEIVE',$w,$actor,'Nhập kho IMEI')",
              ("$n",code),("$w",warehouseId),("$actor",ActorId));
            var transId = N(c,t,"SELECT last_insert_rowid()");
            int importedCount = 0;
            var skippedInstalled = new List<string>();
            foreach(var rawImei in codes)
            {
                var imei = rawImei.Trim();
                var existing = QueryTx(c,t,@"SELECT id, status, CASE WHEN deleted_at IS NULL THEN 0 ELSE 1 END
                    FROM stock_units WHERE LOWER(TRIM(imei))=LOWER($i) ORDER BY id DESC LIMIT 1",
                    r => (Id: L(r,0), Status: S(r,1), IsDeleted: L(r,2) == 1), ("$i", imei)).FirstOrDefault();

                long stockId;
                if (existing.Id > 0)
                {
                    if (!existing.IsDeleted && existing.Status is "INSTALLED" or "CONSIGNED" or "TECHNICIAN")
                    {
                        var hasActiveAct = N(c,t,"SELECT COUNT(*) FROM activations WHERE stock_unit_id=$sid AND deleted_at IS NULL", ("$sid", existing.Id)) > 0;
                        if (hasActiveAct)
                        {
                            skippedInstalled.Add(imei);
                            continue;
                        }
                    }
                    Exec(c,t,@"UPDATE stock_units
                        SET product_id=$p, imei=$i, status='IN_STOCK', warehouse_id=$w, supplier_id=$sup,
                            customer_id=NULL, employee_id=NULL, deleted_at=NULL,
                            updated_at=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                        WHERE id=$id",
                        ("$p",productId),("$i",imei),("$w",warehouseId),("$sup",resolvedSup > 0 ? resolvedSup : null),("$id",existing.Id));
                    stockId = existing.Id;
                }
                else
                {
                    Exec(c,t,"INSERT INTO stock_units(product_id,imei,status,warehouse_id,supplier_id) VALUES($p,$i,'IN_STOCK',$w,$sup)",
                        ("$p",productId),("$i",imei),("$w",warehouseId),("$sup",resolvedSup > 0 ? resolvedSup : null));
                    stockId = N(c,t,"SELECT last_insert_rowid()");
                }
                Exec(c,t,"INSERT INTO inventory_transaction_lines(transaction_id,stock_unit_id,product_id,unit_cost_snapshot_vnd) VALUES($t,$s,$p,$cost)",
                    ("$t",transId),("$s",stockId),("$p",productId),("$cost",cost));
                importedCount++;
            }
            if (importedCount == 0 && skippedInstalled.Count > 0)
            {
                throw new InvalidOperationException($"Tất cả {skippedInstalled.Count} mã IMEI đã tồn tại ở trạng thái Đã lắp đặt / Kích hoạt ({string.Join(", ", skippedInstalled.Take(5))}{(skippedInstalled.Count > 5 ? "..." : "")}).");
            }
            if (resolvedSup > 0 && cost * importedCount > 0)
            {
                var invNo = "NK-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                var totalCost = cost * importedCount;
                Exec(c,t,@"INSERT INTO invoices(invoice_no,invoice_type,supplier_id,due_at,amount_vnd,notes)
                    VALUES($no,'PURCHASE',$sup,date('now','+30 days'),$amt,$note)",
                    ("$no",invNo),("$sup",resolvedSup),("$amt",totalCost),("$note",$"Nhập lô {importedCount} IMEI ({code}): {prodName}"));
                var invId = N(c,t,"SELECT last_insert_rowid()");
                Exec(c,t,"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$d,$p,$q,$c)",
                    ("$i",invId),("$d",$"Nhập kho IMEI ({code}): {prodName}"),("$p",productId),("$q",importedCount),("$c",cost));
            }
            Log(c,t,"RECEIVE","INVENTORY_TX",transId,$"{importedCount} thiết bị / Product {productId}");
            return importedCount;
        });
    }

    public void MoveStock(long stockId, string mode, long targetId)
    {
        var transitions = new Dictionary<string,(string Required, string Next)>{
            ["CONSIGN_OUT"] = ("IN_STOCK","CONSIGNED"),
            ["CONSIGN_RETURN"] = ("CONSIGNED","IN_STOCK"),
            ["TECH_OUT"] = ("IN_STOCK","TECHNICIAN"),
            ["TECH_RETURN"] = ("TECHNICIAN","IN_STOCK")
        };
        if (!transitions.TryGetValue(mode, out var move)) throw new InvalidOperationException("Nghiệp vụ di chuyển chưa được hỗ trợ.");
        Tx((c,t) => {
            var found = N(c,t,"SELECT COUNT(*) FROM stock_units WHERE id=$id AND status=$status",("$id",stockId),("$status",move.Required));
            if (found != 1) throw new InvalidOperationException("Thiết bị không còn ở trạng thái hợp lệ để chuyển.");
            var prod = N(c,t,"SELECT product_id FROM stock_units WHERE id=$id",("$id",stockId));
            var cost = N(c,t,"SELECT base_cost_vnd FROM products WHERE id=$id",("$id",prod));
            var warehouseId = mode.EndsWith("RETURN") ? targetId : (long?)null;
            var customerId = mode == "CONSIGN_OUT" ? targetId : (long?)null;
            var employeeId = mode == "TECH_OUT" ? targetId : (long?)null;
            if (mode.EndsWith("RETURN") && N(c,t,"SELECT COUNT(*) FROM warehouses WHERE id=$id AND deleted_at IS NULL",("$id",targetId)) == 0)
                throw new InvalidOperationException("Không tìm thấy kho nhận.");
            if (mode == "CONSIGN_OUT" && N(c,t,"SELECT COUNT(*) FROM customers WHERE id=$id AND deleted_at IS NULL",("$id",targetId)) == 0)
                throw new InvalidOperationException("Không tìm thấy khách hàng.");
            if (mode == "TECH_OUT" && N(c,t,"SELECT COUNT(*) FROM employees WHERE id=$id AND deleted_at IS NULL",("$id",targetId)) == 0)
                throw new InvalidOperationException("Không tìm thấy nhân viên.");
            var code = "CK-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            Exec(c,t,@"INSERT INTO inventory_transactions(code,movement_type,warehouse_id,customer_id,employee_id,actor_user_id,notes)
              VALUES($code,$m,$w,$c,$e,$actor,'Điều chuyển IMEI')",
              ("$code",code),("$m",mode),("$w",warehouseId),("$c",customerId),("$e",employeeId),("$actor",ActorId));
            var movementId = N(c,t,"SELECT last_insert_rowid()");
            Exec(c,t,"INSERT INTO inventory_transaction_lines(transaction_id,stock_unit_id,product_id,unit_cost_snapshot_vnd) VALUES($t,$s,$p,$cost)",
              ("$t",movementId),("$s",stockId),("$p",prod),("$cost",cost));
            Exec(c,t,"UPDATE stock_units SET status=$st,warehouse_id=$w,customer_id=$c,employee_id=$e,updated_at=datetime('now') WHERE id=$id",
              ("$st",move.Next),("$w",warehouseId),("$c",customerId),("$e",employeeId),("$id",stockId));
            Log(c,t,mode,"STOCK_UNIT",stockId,$"{move.Required} → {move.Next}");
            return 0;
        });
    }

    public static event Action? OnDevicesChanged;
    public static void NotifyDevicesChanged() => OnDevicesChanged?.Invoke();

    public int Due10DevicesCount()
    {
        var today = DateTime.Today;
        using var c = Open();
        var dates = Query(c, @"SELECT a.expires_at
          FROM activations a
          JOIN stock_units u ON u.id=a.stock_unit_id
          JOIN products p ON p.id=a.product_id
          JOIN customers c ON c.id=a.customer_id
          WHERE a.deleted_at IS NULL AND COALESCE(a.status,'ACTIVE')='ACTIVE'",
          r => S(r, 0));
        var count = 0;
        foreach (var exp in dates)
        {
            if (DateTime.TryParse(exp, out var dt))
            {
                var days = (dt.Date - today).Days;
                if (days >= 0 && days <= 10)
                    count++;
            }
        }
        return count;
    }

    public List<DeviceVm> Devices()
    {
        using var c = Open();
        return Query(c, @"SELECT a.id,u.imei,p.sku,c.name,a.plate_no,a.activated_at,a.expires_at,c.id,p.id,
          COALESCE(a.vin_no,''),COALESCE(o.code,''),COALESCE(o.contact_name,''),
          COALESCE(o.seller_record_id, c.seller_id),
          COALESCE(sl.code||' — '||sl.full_name, NULLIF(o.sale_source,''), NULLIF(c.source_name,''), ''),
          COALESCE(NULLIF(a.device_type_label,''),
            CASE WHEN p.sku='SP000001' THEN 'XANHGPS'
                 WHEN p.sku IN ('DX00002','SP000019') THEN 'NASAGPS'
                 WHEN p.sku='SP000005' THEN 'TDCGPS'
                 WHEN p.sku='SP000012' THEN 'AICAM'
                 WHEN p.sku='SP000006' THEN 'mGPS'
                 ELSE p.sku END),
          COALESCE(NULLIF(a.agency_name,''), c.name),
          COALESCE(NULLIF(a.owner_name,''), o.contact_name, ''),
          COALESCE(a.owner_phone,''),
          COALESCE(a.status,'ACTIVE'),
          COALESCE(NULLIF(a.last_renewed_at,''), (SELECT MAX(r2.renewed_at) FROM renewals r2 WHERE r2.activation_id=a.id AND r2.deleted_at IS NULL), ''),
          MAX(COALESCE(a.renewal_count,0), (SELECT COUNT(*) FROM renewals r3 WHERE r3.activation_id=a.id AND r3.deleted_at IS NULL))
          FROM activations a JOIN stock_units u ON u.id=a.stock_unit_id
          JOIN products p ON p.id=a.product_id
          JOIN customers c ON c.id=a.customer_id
          LEFT JOIN v7_orders o ON o.id=a.v7_order_id
          LEFT JOIN v7_sellers sl ON sl.id=COALESCE(o.seller_record_id, c.seller_id)
          WHERE a.deleted_at IS NULL
          ORDER BY a.expires_at",
          r => new DeviceVm(
              L(r,0),S(r,1),S(r,2),S(r,3),S(r,4),S(r,5),S(r,6),L(r,7),L(r,8),
              S(r,9),S(r,10),S(r,11),r.IsDBNull(12) ? null : L(r,12),S(r,13),
              S(r,14),S(r,15),S(r,16),S(r,17),S(r,18),S(r,19),L(r,20)));
    }

    public void UpdateDeviceActivation(long activationId, string activatedAt, int durationMonths = 12, string? plateNo = null, string? vinNo = null, string? customExpiresAt = null, long? sellerId = null, string? status = null, string? ownerName = null, string? ownerPhone = null)
    {
        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy"];
        if (!DateTime.TryParseExact((activatedAt ?? "").Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var actDate))
            throw new InvalidOperationException("Ngày kích hoạt không hợp lệ (định dạng dd/MM/yyyy hoặc yyyy-MM-dd).");
        var months = durationMonths > 0 ? durationMonths : 12;
        var expDate = actDate.AddMonths(months);
        if (!string.IsNullOrWhiteSpace(customExpiresAt))
        {
            if (!DateTime.TryParseExact(customExpiresAt.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out expDate))
                throw new InvalidOperationException("Ngày hết hạn (Hạn gia hạn) không hợp lệ (định dạng dd/MM/yyyy hoặc yyyy-MM-dd).");
        }
        Tx((c,t) => {
            var cur = QueryTx(c,t,"SELECT id, customer_id, v7_order_id, expires_at FROM activations WHERE id=$id AND deleted_at IS NULL",
                r => (Id: L(r,0), CustId: L(r,1), OrderId: L(r,2), ExpiresAt: S(r,3)), ("$id",activationId)).SingleOrDefault();
            if (cur.Id == 0)
                throw new InvalidOperationException("Không tìm thấy thiết bị kích hoạt.");
            var actStr = actDate.ToString("yyyy-MM-dd");
            var expStr = expDate.ToString("yyyy-MM-dd");

            if (!string.Equals(cur.ExpiresAt, expStr, StringComparison.OrdinalIgnoreCase))
            {
                var lockedCount = N(c,t,@"SELECT COUNT(*) FROM renewals r
                    LEFT JOIN invoices i ON i.renewal_id=r.id AND i.deleted_at IS NULL
                    WHERE r.activation_id=$id AND r.deleted_at IS NULL AND (r.is_locked=1 OR COALESCE(i.is_locked,0)=1)",
                    ("$id", activationId));
                if (lockedCount > 0)
                    throw new InvalidOperationException("Thiết bị này có đợt gia hạn đã được Kế toán chốt số / khóa số, không thể cập nhật lại thời gian gia hạn.");
            }

            string? normStatus = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                normStatus = status.Trim().ToUpperInvariant() is "DISABLED" or "LOCKED" or "KHOÁ" or "KHÓA" ? "DISABLED" : "ACTIVE";
            }

            Exec(c,t,@"UPDATE activations SET activated_at=$act, expires_at=$exp,
                plate_no=COALESCE($plate, plate_no), vin_no=COALESCE($vin, vin_no),
                status=COALESCE($st, status),
                owner_name=COALESCE($owner, owner_name),
                owner_phone=COALESCE($phone, owner_phone),
                updated_at=datetime('now')
                WHERE id=$id",
                ("$act", actStr), ("$exp", expStr),
                ("$plate", plateNo is not null ? plateNo.Trim() : null),
                ("$vin", vinNo is not null ? vinNo.Trim() : null),
                ("$st", normStatus),
                ("$owner", ownerName is not null ? ownerName.Trim() : null),
                ("$phone", ownerPhone is not null ? ownerPhone.Trim() : null),
                ("$id", activationId));

            // Nếu thiết bị đã có đợt gia hạn gần nhất chưa chốt số, đồng bộ luôn hạn mới (new_expiry) của đợt gia hạn đó
            var latestRenewalId = N(c,t,@"SELECT COALESCE((SELECT r.id FROM renewals r
                LEFT JOIN invoices i ON i.renewal_id=r.id AND i.deleted_at IS NULL
                WHERE r.activation_id=$id AND r.deleted_at IS NULL AND r.is_locked=0 AND COALESCE(i.is_locked,0)=0
                ORDER BY r.renewed_at DESC, r.id DESC LIMIT 1), 0)", ("$id", activationId));
            if (latestRenewalId > 0 && !string.Equals(cur.ExpiresAt, expStr, StringComparison.OrdinalIgnoreCase))
            {
                Exec(c,t,"UPDATE renewals SET new_expiry=$exp WHERE id=$rid", ("$exp", expStr), ("$rid", latestRenewalId));
            }

            if (sellerId is > 0 && cur.CustId > 0)
            {
                var sellerLabel = QueryTx(c,t,"SELECT code||' — '||full_name FROM v7_sellers WHERE id=$id AND deleted_at IS NULL", r => S(r,0), ("$id", sellerId.Value)).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(sellerLabel))
                {
                    Exec(c,t,"UPDATE customers SET seller_id=$sid, source_name=$sname WHERE id=$cid",
                        ("$sid", sellerId.Value), ("$sname", sellerLabel), ("$cid", cur.CustId));
                }
            }

            Log(c,t,"UPDATE_ACTIVATION","ACTIVATION",activationId,$"Kích hoạt: {actStr} -> Hết hạn: {expStr}");
            return 0;
        });
        NotifyDevicesChanged();
    }

    public string ExportDevicesStandardCsv()
    {
        var list = Devices();
        var sb = new StringBuilder();
        sb.AppendLine("STT,Biển số xe/Số khung,Số EMEI Thiết bị,Tên Thiết bị,Tên Đại lý,Tên Khách hàng,SĐT Khách hàng,Trạng thái thiết bị,Ngày kích hoạt,Ngày hết hạn,Ngày gia hạn gần nhất (nếu có),Số lần gia hạn");
        static string Esc(string? s)
        {
            var v = (s ?? "").Trim();
            if (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }
        static string FmtDate(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return "";
            return DateTime.TryParse(d.Trim(), out var dt) ? dt.ToString("dd/MM/yyyy") : d.Trim();
        }
        for (int i = 0; i < list.Count; i++)
        {
            var x = list[i];
            var stLabel = string.Equals(x.Status, "DISABLED", StringComparison.OrdinalIgnoreCase) ? "Khoá" : "Hoạt động";
            sb.Append(i + 1).Append(',')
              .Append(Esc(x.Plate)).Append(',')
              .Append(Esc(x.Imei)).Append(',')
              .Append(Esc(x.DeviceTypeLabel)).Append(',')
              .Append(Esc(string.IsNullOrWhiteSpace(x.AgencyName) ? x.Customer : x.AgencyName)).Append(',')
              .Append(Esc(x.OwnerName)).Append(',')
              .Append(Esc(x.OwnerPhone)).Append(',')
              .Append(Esc(stLabel)).Append(',')
              .Append(Esc(FmtDate(x.ActivatedAt))).Append(',')
              .Append(Esc(FmtDate(x.ExpiresAt))).Append(',')
              .Append(Esc(FmtDate(x.LastRenewedAt))).Append(',')
              .Append(x.RenewalCount).AppendLine();
        }
        return sb.ToString();
    }

    public DeviceImportResultVm ImportDevicesFromStream(Stream stream, string fileName)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        var isXlsx = fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            || (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K');
        if (isXlsx)
        {
            var rows = ReadXlsxRows(bytes);
            return UpsertDeviceRows(rows);
        }
        else
        {
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            var rows = ReadCsvRows(text);
            return UpsertDeviceRows(rows);
        }
    }

    public DeviceImportResultVm ImportDevicesFromCsvText(string csvText)
    {
        var rows = ReadCsvRows((csvText ?? "").TrimStart('\uFEFF'));
        return UpsertDeviceRows(rows);
    }

    private static List<(string Val, bool IsString)[]> ReadXlsxRows(byte[] xlsxBytes)
    {
        using var ms = new MemoryStream(xlsxBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var sharedStrings = new List<string>();
        var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        if (ssEntry is not null)
        {
            using var ssStream = ssEntry.Open();
            var ssDoc = XDocument.Load(ssStream);
            foreach (var si in ssDoc.Descendants(ns + "si"))
            {
                var sb = new StringBuilder();
                foreach (var t in si.Descendants(ns + "t"))
                    sb.Append(t.Value);
                sharedStrings.Add(sb.ToString());
            }
        }
        var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml")
            ?? zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase));
        if (sheetEntry is null)
            throw new InvalidOperationException("Không tìm thấy sheet dữ liệu trong file Excel (.xlsx).");

        using var sheetStream = sheetEntry.Open();
        var sheetDoc = XDocument.Load(sheetStream);
        var result = new List<(string Val, bool IsString)[]>();
        foreach (var rowEl in sheetDoc.Descendants(ns + "row"))
        {
            var rowArr = new (string Val, bool IsString)[12];
            for (int i = 0; i < 12; i++) rowArr[i] = ("", true);
            foreach (var cEl in rowEl.Elements(ns + "c"))
            {
                var rAttr = cEl.Attribute("r")?.Value ?? "";
                int colIdx = 0;
                foreach (var ch in rAttr)
                {
                    if (char.IsLetter(ch))
                        colIdx = colIdx * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
                }
                if (colIdx < 1 || colIdx > 12) continue;
                var tAttr = cEl.Attribute("t")?.Value ?? "";
                var vEl = cEl.Element(ns + "v");
                var isEl = cEl.Element(ns + "is");
                string val = "";
                bool isStr = tAttr is "s" or "inlineStr" or "str";
                if (tAttr == "s" && vEl is not null && int.TryParse(vEl.Value, out var sIdx) && sIdx >= 0 && sIdx < sharedStrings.Count)
                    val = sharedStrings[sIdx];
                else if (tAttr == "inlineStr" && isEl is not null)
                    val = string.Concat(isEl.Descendants(ns + "t").Select(x => x.Value));
                else if (vEl is not null)
                    val = vEl.Value;
                rowArr[colIdx - 1] = (val.Trim(), isStr);
            }
            result.Add(rowArr);
        }
        return result;
    }

    private static List<(string Val, bool IsString)[]> ReadCsvRows(string text)
    {
        var result = new List<(string Val, bool IsString)[]>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var lines = new List<List<string>>();
        var curRow = new List<string>();
        var curField = new StringBuilder();
        bool inQuotes = false;
        char delimiter = ',';
        var firstLineEnd = text.IndexOfAny(['\r', '\n']);
        var firstLine = firstLineEnd > 0 ? text[..firstLineEnd] : text;
        if (firstLine.Count(c => c == '\t') >= 5 && firstLine.Count(c => c == ',') < 5)
            delimiter = '\t';

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { curField.Append('"'); i++; }
                    else inQuotes = false;
                }
                else curField.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == delimiter) { curRow.Add(curField.ToString().Trim()); curField.Clear(); }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    curRow.Add(curField.ToString().Trim());
                    curField.Clear();
                    if (curRow.Any(x => !string.IsNullOrWhiteSpace(x)))
                        lines.Add(curRow);
                    curRow = [];
                }
                else curField.Append(c);
            }
        }
        if (curField.Length > 0 || curRow.Count > 0)
        {
            curRow.Add(curField.ToString().Trim());
            if (curRow.Any(x => !string.IsNullOrWhiteSpace(x)))
                lines.Add(curRow);
        }

        foreach (var r in lines)
        {
            var arr = new (string Val, bool IsString)[12];
            for (int i = 0; i < 12; i++)
                arr[i] = (i < r.Count ? r[i] : "", true);
            result.Add(arr);
        }
        return result;
    }

    private static DateTime? ParseImportDate(string val, bool isString, string? refExpStr = null, bool refExpIsString = true)
    {
        val = (val ?? "").Trim();
        if (string.IsNullOrWhiteSpace(val) || val is "#REF!" or "NaN" or "None" or "-")
            return null;
        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa) && oa > 20000 && oa < 80000)
        {
            var dt = new DateTime(1899, 12, 30).AddDays(oa);
            if (!isString && !string.IsNullOrWhiteSpace(refExpStr) && refExpIsString && refExpStr.Contains('/'))
            {
                if (DateTime.TryParseExact(refExpStr.Trim(), ["dd/MM/yyyy", "d/M/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dExp))
                {
                    if (dt.Day != dExp.Day && dt.Month == dExp.Day && dt.Day == dExp.Month && dt.Month <= 12 && dt.Day <= 12)
                        dt = new DateTime(dt.Year, dExp.Month, dExp.Day);
                }
            }
            return dt.Date;
        }
        string[] formats = ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "d-M-yyyy", "MM/dd/yyyy", "yyyy/MM/dd"];
        var clean = val.Split(' ')[0].Trim();
        if (DateTime.TryParseExact(clean, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed.Date;
        if (DateTime.TryParse(clean, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            return parsed.Date;
        return null;
    }

    private DeviceImportResultVm UpsertDeviceRows(List<(string Val, bool IsString)[]> rows)
    {
        if (rows.Count == 0)
            throw new InvalidOperationException("File dữ liệu trống, không có dòng nào để import.");

        int updated = 0, inserted = 0, skipped = 0, totalProcessed = 0;

        Tx((c, t) =>
        {
            // Đảm bảo khách hàng 'Hợp tác xã 586' tồn tại trong danh mục customers
            var htx586Id = N(c, t, "SELECT COALESCE((SELECT id FROM customers WHERE UPPER(name) LIKE '%586%' AND deleted_at IS NULL LIMIT 1), 0)");
            if (htx586Id == 0)
            {
                var defGrpId = N(c, t, "SELECT COALESCE((SELECT group_id FROM customers WHERE deleted_at IS NULL LIMIT 1), 1)");
                var defSellerId = N(c, t, "SELECT COALESCE((SELECT id FROM v7_sellers WHERE deleted_at IS NULL ORDER BY id LIMIT 1), 1)");
                var nextCustNo = N(c, t, "SELECT COALESCE(MAX(id),0)+10 FROM customers");
                var custCode = $"KH{nextCustNo:D5}";
                while (N(c, t, "SELECT COUNT(*) FROM customers WHERE code=$c", ("$c", custCode)) > 0)
                {
                    nextCustNo++;
                    custCode = $"KH{nextCustNo:D5}";
                }
                Exec(c, t, @"INSERT INTO customers(group_id,code,name,source_type,source_name,phone,address,is_active,seller_id)
                    VALUES($g,$code,'Hợp tác xã 586','AGENCY','SL001 — Nguyễn Minh Anh','','',1,$sl)",
                    ("$g", defGrpId), ("$code", custCode), ("$sl", defSellerId > 0 ? defSellerId : null));
                htx586Id = N(c, t, "SELECT last_insert_rowid()");
            }

            var custList = QueryTx(c, t, "SELECT id, name FROM customers WHERE deleted_at IS NULL", r => (Id: L(r, 0), Name: S(r, 1)));
            long ResolveCustomerId(string agency)
            {
                var a = (agency ?? "").Trim();
                var au = a.ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(a))
                    return custList.FirstOrDefault(x => x.Name.Contains("Khách lẻ", StringComparison.OrdinalIgnoreCase)).Id;
                foreach (var cu in custList)
                {
                    if (string.Equals(cu.Name, a, StringComparison.OrdinalIgnoreCase))
                        return cu.Id;
                }
                if (au.Contains("TRƯỜNG NHẬT MINH") || au.Contains("TRƯỜNG NHẬT"))
                    return custList.FirstOrDefault(x => x.Name.Contains("Trường Nhật Minh", StringComparison.OrdinalIgnoreCase)).Id;
                if (au.Contains("NAM THẮNG") && (au.Contains("KIÊN GIANG") || au.Contains("KG")))
                    return custList.FirstOrDefault(x => x.Name.Contains("Nam Thắng KG", StringComparison.OrdinalIgnoreCase)).Id;
                if (au.Contains("NAM THẮNG") && au.Contains("CHÍNH HIỆP"))
                    return custList.FirstOrDefault(x => x.Name.Contains("Nam Thắng Chính Hiệp", StringComparison.OrdinalIgnoreCase)).Id;
                if (au.Contains("NAM THẮNG"))
                    return custList.FirstOrDefault(x => x.Name.Equals("HTX Vận tải Nam Thắng", StringComparison.OrdinalIgnoreCase)).Id;
                if (au.Contains("LÂM MINH") || au.Contains("HTXLAMMINH"))
                    return custList.FirstOrDefault(x => x.Name.Contains("Lâm Minh", StringComparison.OrdinalIgnoreCase)).Id;
                if (au.Contains("TÂN ĐẠI THÀNH"))
                    return custList.FirstOrDefault(x => x.Name.Contains("Tân Đại Thành", StringComparison.OrdinalIgnoreCase)).Id;
                if (au.Contains("NAM HI"))
                    return custList.FirstOrDefault(x => x.Name.Contains("Nam Hi", StringComparison.OrdinalIgnoreCase)).Id;
                if (au.Contains("586"))
                    return htx586Id;
                if (au.Contains("CHÍNH HIỆP"))
                    return custList.FirstOrDefault(x => x.Name.Contains("Chính Hiệp", StringComparison.OrdinalIgnoreCase) && !x.Name.Contains("Nam Thắng", StringComparison.OrdinalIgnoreCase)).Id;
                if (au.Contains("TÂN THÀNH ĐÔ"))
                    return custList.FirstOrDefault(x => x.Name.Contains("Tân Thành Đô", StringComparison.OrdinalIgnoreCase)).Id;
                if (au.Contains("NAM PHƯƠNG"))
                    return custList.FirstOrDefault(x => x.Name.Contains("Nam Phương", StringComparison.OrdinalIgnoreCase)).Id;
                if (au.Contains("ĐI XANH") || au.Contains("DI XANH") || au.Contains("GREENWAY"))
                    return custList.FirstOrDefault(x => x.Name.Contains("Đi Xanh", StringComparison.OrdinalIgnoreCase)).Id;
                if (au.Contains("HẢI VÂN") || au == "136")
                    return custList.FirstOrDefault(x => x.Name.Contains("Hải Vân", StringComparison.OrdinalIgnoreCase)).Id;
                if (au.Contains("ĐĂNG TIẾN") || au.Contains("SAO VIỆT"))
                    return custList.FirstOrDefault(x => x.Name.Contains("Đăng Tiến", StringComparison.OrdinalIgnoreCase)).Id;
                var fallback = custList.FirstOrDefault(x => x.Name.Contains("Khách lẻ", StringComparison.OrdinalIgnoreCase)).Id;
                return fallback > 0 ? fallback : custList[0].Id;
            }

            var prodBySku = QueryTx(c, t, "SELECT id, sku FROM products WHERE deleted_at IS NULL", r => (Id: L(r, 0), Sku: S(r, 1)))
                .ToDictionary(x => x.Sku.ToUpperInvariant(), x => x.Id);
            long ResolveProductId(string devName, long existingProdId = 0)
            {
                var d = (devName ?? "").Trim().ToUpperInvariant();
                if (d.Contains("XANH")) return prodBySku.GetValueOrDefault("SP000001", 4);
                if (d.Contains("NASA"))
                {
                    if (existingProdId is 1 or 2) return existingProdId;
                    return prodBySku.GetValueOrDefault("DX00002", 2);
                }
                if (d.Contains("AICAM") || d.Contains("CAM")) return prodBySku.GetValueOrDefault("SP000012", 16);
                if (d == "MGPS" || d.Contains("MGPS")) return prodBySku.GetValueOrDefault("SP000006", 5);
                if (d.Contains("TDC")) return prodBySku.GetValueOrDefault("SP000005", 6);
                return existingProdId > 0 ? existingProdId : prodBySku.GetValueOrDefault("SP000001", 4);
            }

            var existingUnits = QueryTx(c, t, "SELECT id, imei, product_id FROM stock_units",
                r => (Id: L(r, 0), Imei: S(r, 1), ProdId: L(r, 2)));
            var unitByNormImei = new Dictionary<string, (long Id, string RawImei, long ProdId)>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in existingUnits)
            {
                var norm = u.Imei.Trim().Replace(",", "").Replace(" ", "").ToUpperInvariant();
                if (!string.IsNullOrWhiteSpace(norm))
                    unitByNormImei[norm] = (u.Id, u.Imei, u.ProdId);
            }

            var existingActs = QueryTx(c, t, "SELECT id, stock_unit_id, vin_no FROM activations",
                r => (Id: L(r, 0), UnitId: L(r, 1), VinNo: S(r, 2)))
                .ToDictionary(x => x.UnitId, x => (x.Id, x.VinNo));

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var col0 = r[0].Val;
                var col2 = r[2].Val;
                // Skip header row
                if (col0.Equals("STT", StringComparison.OrdinalIgnoreCase) || col2.Contains("EMEI", StringComparison.OrdinalIgnoreCase) || col2.Contains("IMEI", StringComparison.OrdinalIgnoreCase))
                    continue;

                var normImei = col2.Trim().Replace(",", "").Replace(" ", "").ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(normImei))
                {
                    skipped++;
                    continue;
                }

                var plate = r[1].Val.Trim();
                var devLabel = r[3].Val.Trim();
                if (string.IsNullOrWhiteSpace(devLabel)) devLabel = "XANHGPS";
                var agency = r[4].Val.Trim();
                var owner = r[5].Val.Trim();
                var phone = r[6].Val.Trim();
                if (phone is "#REF!" or "NaN" or "None" or "-") phone = "";
                var rawStatus = r[7].Val.Trim();
                var dbStatus = rawStatus.ToUpperInvariant() is "KHOÁ" or "KHÓA" or "LOCKED" or "DISABLED" ? "DISABLED" : "ACTIVE";

                var dtAct = ParseImportDate(r[8].Val, r[8].IsString) ?? DateTime.Today;
                var dtExp = ParseImportDate(r[9].Val, r[9].IsString) ?? dtAct.AddYears(1);
                var dtRen = ParseImportDate(r[10].Val, r[10].IsString, r[9].Val, r[9].IsString);
                int renCnt = 0;
                if (!string.IsNullOrWhiteSpace(r[11].Val) && double.TryParse(r[11].Val, NumberStyles.Float, CultureInfo.InvariantCulture, out var rc))
                    renCnt = Math.Max(0, (int)Math.Round(rc));

                var actStr = dtAct.ToString("yyyy-MM-dd");
                var expStr = dtExp.ToString("yyyy-MM-dd");
                var renStr = dtRen?.ToString("yyyy-MM-dd") ?? "";

                var custId = ResolveCustomerId(agency);
                totalProcessed++;

                if (unitByNormImei.TryGetValue(normImei, out var existingUnit))
                {
                    var prodId = ResolveProductId(devLabel, existingUnit.ProdId);
                    Exec(c, t, @"UPDATE stock_units
                        SET product_id=$p, status='INSTALLED', warehouse_id=NULL, employee_id=NULL, customer_id=$c, deleted_at=NULL, updated_at=datetime('now')
                        WHERE id=$id",
                        ("$p", prodId), ("$c", custId), ("$id", existingUnit.Id));

                    if (existingActs.TryGetValue(existingUnit.Id, out var existingAct))
                    {
                        var vin = !string.IsNullOrWhiteSpace(owner) && !string.Equals(owner, plate, StringComparison.OrdinalIgnoreCase)
                            ? (string.IsNullOrWhiteSpace(phone) ? owner : $"{owner} · {phone}")
                            : existingAct.VinNo;
                        Exec(c, t, @"UPDATE activations
                            SET customer_id=$c, product_id=$p, plate_no=$plate, vin_no=$vin,
                                activated_at=$act, expires_at=$exp, status=$st,
                                device_type_label=$dtype, agency_name=$agency, owner_name=$owner, owner_phone=$phone,
                                last_renewed_at=$renAt, renewal_count=$renCnt,
                                deleted_at=NULL, updated_at=datetime('now')
                            WHERE id=$id",
                            ("$c", custId), ("$p", prodId), ("$plate", plate), ("$vin", vin),
                            ("$act", actStr), ("$exp", expStr), ("$st", dbStatus),
                            ("$dtype", devLabel), ("$agency", agency), ("$owner", owner), ("$phone", phone),
                            ("$renAt", renStr), ("$renCnt", renCnt),
                            ("$id", existingAct.Id));
                        updated++;
                    }
                    else
                    {
                        var vin = !string.IsNullOrWhiteSpace(owner) && !string.Equals(owner, plate, StringComparison.OrdinalIgnoreCase)
                            ? (string.IsNullOrWhiteSpace(phone) ? owner : $"{owner} · {phone}")
                            : normImei;
                        Exec(c, t, @"INSERT INTO activations(stock_unit_id,customer_id,product_id,plate_no,vin_no,activated_at,expires_at,status,device_type_label,agency_name,owner_name,owner_phone,last_renewed_at,renewal_count,created_at,updated_at)
                            VALUES($u,$c,$p,$plate,$vin,$act,$exp,$st,$dtype,$agency,$owner,$phone,$renAt,$renCnt,datetime('now'),datetime('now'))",
                            ("$u", existingUnit.Id), ("$c", custId), ("$p", prodId), ("$plate", plate), ("$vin", vin),
                            ("$act", actStr), ("$exp", expStr), ("$st", dbStatus),
                            ("$dtype", devLabel), ("$agency", agency), ("$owner", owner), ("$phone", phone),
                            ("$renAt", renStr), ("$renCnt", renCnt));
                        var newActId = N(c, t, "SELECT last_insert_rowid()");
                        existingActs[existingUnit.Id] = (newActId, vin);
                        updated++;
                    }
                }
                else
                {
                    var prodId = ResolveProductId(devLabel, 0);
                    Exec(c, t, @"INSERT INTO stock_units(product_id,imei,status,warehouse_id,customer_id,employee_id,created_at,updated_at)
                        VALUES($p,$imei,'INSTALLED',NULL,$c,NULL,datetime('now'),datetime('now'))",
                        ("$p", prodId), ("$imei", normImei), ("$c", custId));
                    var newUnitId = N(c, t, "SELECT last_insert_rowid()");
                    unitByNormImei[normImei] = (newUnitId, normImei, prodId);

                    var vin = !string.IsNullOrWhiteSpace(owner) && !string.Equals(owner, plate, StringComparison.OrdinalIgnoreCase)
                        ? (string.IsNullOrWhiteSpace(phone) ? owner : $"{owner} · {phone}")
                        : normImei;
                    Exec(c, t, @"INSERT INTO activations(stock_unit_id,customer_id,product_id,plate_no,vin_no,activated_at,expires_at,status,device_type_label,agency_name,owner_name,owner_phone,last_renewed_at,renewal_count,created_at,updated_at)
                        VALUES($u,$c,$p,$plate,$vin,$act,$exp,$st,$dtype,$agency,$owner,$phone,$renAt,$renCnt,datetime('now'),datetime('now'))",
                        ("$u", newUnitId), ("$c", custId), ("$p", prodId), ("$plate", plate), ("$vin", vin),
                        ("$act", actStr), ("$exp", expStr), ("$st", dbStatus),
                        ("$dtype", devLabel), ("$agency", agency), ("$owner", owner), ("$phone", phone),
                        ("$renAt", renStr), ("$renCnt", renCnt));
                    var newActId = N(c, t, "SELECT last_insert_rowid()");
                    existingActs[newUnitId] = (newActId, vin);
                    inserted++;
                }
            }

            Log(c, t, "IMPORT_DEVICES", "ACTIVATION", 0, $"Import {totalProcessed} dòng: Cập nhật trùng {updated} TB, Thêm mới {inserted} TB, Bỏ qua {skipped}");
            return 0;
        });

        NotifyDevicesChanged();
        var msg = $"Đã xử lý {totalProcessed:N0} dòng dữ liệu thiết bị: Cập nhật trùng {updated:N0} thiết bị theo danh sách mới, Thêm mới {inserted:N0} thiết bị" + (skipped > 0 ? $" (Bỏ qua {skipped} dòng trống)." : ".");
        return new DeviceImportResultVm(totalProcessed, updated, inserted, skipped, msg);
    }

    public List<RenewalVm> Renewals()
    {
        using var c = Open();
        return Query(c, @"SELECT r.id,u.imei,r.source_type,r.renewed_at,r.new_expiry,r.amount_vnd,r.payment_method,
          r.seller_id,
          COALESCE(sl.code||' — '||sl.full_name, NULLIF(r.seller_name,''), '—'),
          COALESCE(c.name,''),
          COALESCE(a.plate_no,''),
          r.activation_id,
          r.previous_expiry,
          CASE WHEN r.is_locked=1 OR COALESCE(inv.is_locked,0)=1 THEN 1 ELSE 0 END,
          COALESCE(r.locked_at, inv.locked_at, ''),
          COALESCE(inv.invoice_no, ''),
          inv.id
          FROM renewals r
          JOIN activations a ON a.id=r.activation_id
          JOIN stock_units u ON u.id=a.stock_unit_id
          LEFT JOIN customers c ON c.id=a.customer_id
          LEFT JOIN v7_sellers sl ON sl.id=r.seller_id
          LEFT JOIN invoices inv ON inv.id=(SELECT i2.id FROM invoices i2 WHERE i2.renewal_id=r.id AND i2.deleted_at IS NULL AND i2.status<>'VOID' ORDER BY i2.id DESC LIMIT 1)
          WHERE r.deleted_at IS NULL
          ORDER BY r.id DESC",
          r => new RenewalVm(
              L(r,0), S(r,1), S(r,2), S(r,3), S(r,4), L(r,5), S(r,6),
              r.IsDBNull(7) ? null : L(r,7), S(r,8), S(r,9), S(r,10),
              L(r,11), S(r,12), L(r,13) == 1, S(r,14), S(r,15), r.IsDBNull(16) ? null : L(r,16)));
    }

    public void Renew(long activationId, string source, string paymentMethod, long? sellerId = null, string? sellerName = null, long? customPriceVnd = null, string? customRenewedAt = null, string? customNewExpiry = null)
    {
        if (!(new[]{"TNM","NCC","HTX","B2C"}).Contains(source) || !(new[]{"CASH","DEBT"}).Contains(paymentMethod))
            throw new InvalidOperationException("Nguồn gia hạn / hình thức thanh toán không hợp lệ.");
        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy"];
        Tx((c,t) => {
            var device = QueryTx(c,t,@"SELECT a.expires_at,a.customer_id,a.product_id,p.renewal_fee_vnd,
                COALESCE(o.seller_record_id, c.seller_id, 0),
                COALESCE(sl.code||' — '||sl.full_name, NULLIF(o.sale_source,''), NULLIF(c.source_name,''), '')
             FROM activations a JOIN products p ON p.id=a.product_id
             JOIN customers c ON c.id=a.customer_id
             LEFT JOIN v7_orders o ON o.id=a.v7_order_id
             LEFT JOIN v7_sellers sl ON sl.id=COALESCE(o.seller_record_id, c.seller_id)
             WHERE a.id=$id AND a.status='ACTIVE' AND a.deleted_at IS NULL",
             r => (Expires:S(r,0),CustomerId:L(r,1),ProductId:L(r,2),DefaultPrice:L(r,3),FallbackSellerId:L(r,4),FallbackSellerName:S(r,5)),("$id",activationId)).SingleOrDefault();
            if(device.CustomerId == 0) throw new InvalidOperationException("Thiết bị chưa kích hoạt hoặc đã khóa.");

            long? resolvedSellerId = sellerId is > 0 ? sellerId.Value : (device.FallbackSellerId > 0 ? device.FallbackSellerId : null);
            string resolvedSellerName = (sellerName ?? "").Trim();
            if (resolvedSellerId.HasValue)
            {
                var foundLabel = QueryTx(c,t,"SELECT code||' — '||full_name FROM v7_sellers WHERE id=$id AND deleted_at IS NULL", r => S(r,0), ("$id",resolvedSellerId.Value)).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(foundLabel)) resolvedSellerName = foundLabel;
            }
            else if (!string.IsNullOrWhiteSpace(resolvedSellerName))
            {
                var matchedId = N(c,t,"SELECT COALESCE((SELECT id FROM v7_sellers WHERE deleted_at IS NULL AND (code=$s OR full_name=$s OR (code||' — '||full_name)=$s OR $s LIKE '%'||code||'%' OR $s LIKE '%'||full_name||'%') LIMIT 1), 0)", ("$s",resolvedSellerName));
                if (matchedId > 0)
                {
                    resolvedSellerId = matchedId;
                    var foundLabel = QueryTx(c,t,"SELECT code||' — '||full_name FROM v7_sellers WHERE id=$id", r => S(r,0), ("$id",matchedId)).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(foundLabel)) resolvedSellerName = foundLabel;
                }
            }
            if (string.IsNullOrWhiteSpace(resolvedSellerName) && !string.IsNullOrWhiteSpace(device.FallbackSellerName))
            {
                resolvedSellerName = device.FallbackSellerName;
            }

            // Lấy giá gia hạn cố định tại thời điểm phát sinh gia hạn (hoặc giá tùy chỉnh nếu Admin nhập)
            var price = customPriceVnd is >= 0 ? customPriceVnd.Value : N(c,t,@"SELECT renewal_price_vnd FROM v7_group_prices
               WHERE group_id=(SELECT group_id FROM customers WHERE id=$c) AND product_id=$p AND deleted_at IS NULL AND effective_from<=date('now')
                 AND (effective_to IS NULL OR effective_to>=date('now'))
               ORDER BY effective_from DESC,id DESC LIMIT 1",("$c",device.CustomerId),("$p",device.ProductId));
            if(customPriceVnd is null && price == 0) price = device.DefaultPrice;

            var renDate = DateTime.Today;
            if (!string.IsNullOrWhiteSpace(customRenewedAt) && DateTime.TryParseExact(customRenewedAt.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedRen))
                renDate = parsedRen.Date;

            var old = DateTime.ParseExact(device.Expires,"yyyy-MM-dd",CultureInfo.InvariantCulture);
            var next = (old.Date > renDate ? old.Date : renDate).AddYears(1);
            if (!string.IsNullOrWhiteSpace(customNewExpiry) && DateTime.TryParseExact(customNewExpiry.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedNext))
                next = parsedNext.Date;

            var renStr = renDate.ToString("yyyy-MM-dd");
            var nextStr = next.ToString("yyyy-MM-dd");

            Exec(c,t,@"INSERT INTO renewals(activation_id,seller_id,seller_name,source_type,renewed_at,previous_expiry,new_expiry,amount_vnd,payment_method)
               VALUES($a,$seller,$sellerName,$s,$renAt,$old,$next,$price,$pay)",
               ("$a",activationId),("$seller",resolvedSellerId),("$sellerName",resolvedSellerName),("$s",source),("$renAt",renStr),("$old",device.Expires),("$next",nextStr),("$price",price),("$pay",paymentMethod));
            var renewalId = N(c,t,"SELECT last_insert_rowid()");
            Exec(c,t,"UPDATE activations SET expires_at=$new WHERE id=$id",("$new",nextStr),("$id",activationId));
            var invoiceNo = "GH-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            var sellerNote = !string.IsNullOrWhiteSpace(resolvedSellerName) ? $"Gia hạn 12 tháng · NV bán hàng: {resolvedSellerName}" : "Gia hạn dịch vụ 12 tháng";
            Exec(c,t,@"INSERT INTO invoices(invoice_no,invoice_type,customer_id,renewal_id,issued_at,due_at,amount_vnd,notes)
               VALUES($no,'RENEWAL',$c,$r,$renAt,date($renAt,'+30 days'),$price,$notes)",
               ("$no",invoiceNo),("$c",device.CustomerId),("$r",renewalId),("$renAt",renStr),("$price",price),("$notes",sellerNote));
            var invoiceId = N(c,t,"SELECT last_insert_rowid()");
            Exec(c,t,"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$desc,$p,1,$price)",
               ("$i",invoiceId),("$desc",sellerNote),("$p",device.ProductId),("$price",price));
            if (paymentMethod == "CASH" && price > 0)
                InsertPayment(c,t,invoiceId,price,"CASH");
            SyncUnlockedSellerCommissionInvoices(c,t);
            Log(c,t,"RENEW","ACTIVATION",activationId,$"Mới: {nextStr}; {price} VND; {source}; Seller: {resolvedSellerName}");
            return 0;
        });
        NotifyDevicesChanged();
    }

    public void UpdateRenewal(long renewalId, long? sellerId, string? sellerName, string source, string renewedAt, string previousExpiry, string newExpiry, long amountVnd, string paymentMethod)
    {
        if (!(new[] { "TNM", "NCC", "HTX", "B2C" }).Contains(source) || !(new[] { "CASH", "DEBT" }).Contains(paymentMethod))
            throw new InvalidOperationException("Nguồn gia hạn / hình thức thanh toán không hợp lệ.");
        if (amountVnd < 0)
            throw new InvalidOperationException("Số tiền gia hạn không được âm.");
        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy"];
        if (!DateTime.TryParseExact((renewedAt ?? "").Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var renDate))
            throw new InvalidOperationException("Ngày gia hạn không hợp lệ.");
        if (!DateTime.TryParseExact((previousExpiry ?? "").Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var prevExpDate))
            throw new InvalidOperationException("Hạn cũ không hợp lệ.");
        if (!DateTime.TryParseExact((newExpiry ?? "").Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var newExpDate))
            throw new InvalidOperationException("Hạn mới không hợp lệ.");

        var renStr = renDate.ToString("yyyy-MM-dd");
        var prevExpStr = prevExpDate.ToString("yyyy-MM-dd");
        var newExpStr = newExpDate.ToString("yyyy-MM-dd");

        Tx((c, t) =>
        {
            var cur = QueryTx(c, t, @"SELECT r.id, r.activation_id, r.is_locked, a.customer_id, a.product_id
                FROM renewals r JOIN activations a ON a.id=r.activation_id
                WHERE r.id=$id AND r.deleted_at IS NULL",
                r => (Id: L(r, 0), ActId: L(r, 1), IsLocked: L(r, 2) == 1, CustId: L(r, 3), ProdId: L(r, 4)),
                ("$id", renewalId)).SingleOrDefault();
            if (cur.Id == 0)
                throw new InvalidOperationException("Không tìm thấy lịch sử gia hạn hoặc bản ghi đã bị xóa.");

            var lockedInvCount = N(c, t, "SELECT COUNT(*) FROM invoices WHERE renewal_id=$id AND deleted_at IS NULL AND is_locked=1", ("$id", renewalId));
            if (cur.IsLocked || lockedInvCount > 0)
                throw new InvalidOperationException("Đợt gia hạn này đã được Kế toán chốt số / khóa số, không thể chỉnh sửa.");

            long? resolvedSellerId = sellerId is > 0 ? sellerId.Value : null;
            string resolvedSellerName = (sellerName ?? "").Trim();
            if (resolvedSellerId.HasValue)
            {
                var foundLabel = QueryTx(c, t, "SELECT code||' — '||full_name FROM v7_sellers WHERE id=$id AND deleted_at IS NULL", r => S(r, 0), ("$id", resolvedSellerId.Value)).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(foundLabel)) resolvedSellerName = foundLabel;
            }
            else if (!string.IsNullOrWhiteSpace(resolvedSellerName))
            {
                var matchedId = N(c, t, "SELECT COALESCE((SELECT id FROM v7_sellers WHERE deleted_at IS NULL AND (code=$s OR full_name=$s OR (code||' — '||full_name)=$s OR $s LIKE '%'||code||'%' OR $s LIKE '%'||full_name||'%') LIMIT 1), 0)", ("$s", resolvedSellerName));
                if (matchedId > 0)
                {
                    resolvedSellerId = matchedId;
                    var foundLabel = QueryTx(c, t, "SELECT code||' — '||full_name FROM v7_sellers WHERE id=$id", r => S(r, 0), ("$id", matchedId)).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(foundLabel)) resolvedSellerName = foundLabel;
                }
            }

            Exec(c, t, @"UPDATE renewals
                SET seller_id=$seller, seller_name=$sellerName, source_type=$src,
                    renewed_at=$renAt, previous_expiry=$prevExp, new_expiry=$newExp,
                    amount_vnd=$amt, payment_method=$pay
                WHERE id=$id",
                ("$seller", resolvedSellerId), ("$sellerName", resolvedSellerName), ("$src", source),
                ("$renAt", renStr), ("$prevExp", prevExpStr), ("$newExp", newExpStr),
                ("$amt", amountVnd), ("$pay", paymentMethod), ("$id", renewalId));

            // Cập nhật lại thời gian gia hạn (expires_at) của thiết bị theo đợt gia hạn mới nhất còn hiệu lực
            var latestExp = QueryTx(c, t, @"SELECT new_expiry FROM renewals
                WHERE activation_id=$act AND deleted_at IS NULL
                ORDER BY renewed_at DESC, id DESC LIMIT 1",
                r => S(r, 0), ("$act", cur.ActId)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(latestExp))
            {
                Exec(c, t, "UPDATE activations SET expires_at=$exp WHERE id=$act", ("$exp", latestExp), ("$act", cur.ActId));
            }

            // Đồng bộ Chứng từ Kế toán (invoices, invoice_lines, payments, payment_allocations)
            var sellerNote = !string.IsNullOrWhiteSpace(resolvedSellerName) ? $"Gia hạn 12 tháng · NV bán hàng: {resolvedSellerName}" : "Gia hạn dịch vụ 12 tháng";
            var invId = N(c, t, "SELECT COALESCE((SELECT id FROM invoices WHERE renewal_id=$r AND invoice_type='RENEWAL' AND deleted_at IS NULL ORDER BY id DESC LIMIT 1), 0)", ("$r", renewalId));
            if (invId > 0)
            {
                Exec(c, t, @"UPDATE invoices SET customer_id=$c, issued_at=$renAt, due_at=date($renAt,'+30 days'), amount_vnd=$amt, notes=$notes
                    WHERE id=$inv",
                    ("$c", cur.CustId), ("$renAt", renStr), ("$amt", amountVnd), ("$notes", sellerNote), ("$inv", invId));
                Exec(c, t, @"UPDATE invoice_lines SET description=$desc, product_id=$p, unit_price_vnd=$amt
                    WHERE invoice_id=$inv AND deleted_at IS NULL",
                    ("$desc", sellerNote), ("$p", cur.ProdId), ("$amt", amountVnd), ("$inv", invId));

                var allocs = QueryTx(c, t, "SELECT id, payment_id, allocated_vnd FROM payment_allocations WHERE invoice_id=$inv AND deleted_at IS NULL",
                    r => (AllocId: L(r, 0), PayId: L(r, 1), Amt: L(r, 2)), ("$inv", invId));

                if (paymentMethod == "DEBT" || amountVnd == 0)
                {
                    foreach (var al in allocs)
                    {
                        Exec(c, t, "UPDATE payment_allocations SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=$id", ("$id", al.AllocId));
                        if (N(c, t, "SELECT COUNT(*) FROM payment_allocations WHERE payment_id=$p AND deleted_at IS NULL", ("$p", al.PayId)) == 0)
                            Exec(c, t, "UPDATE payments SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=$p AND deleted_at IS NULL", ("$p", al.PayId));
                    }
                    Exec(c, t, "UPDATE invoices SET status=$st WHERE id=$inv", ("$st", amountVnd == 0 ? "PAID" : "OPEN"), ("$inv", invId));
                }
                else // CASH && amountVnd > 0
                {
                    if (allocs.Count == 1)
                    {
                        Exec(c, t, "UPDATE payment_allocations SET allocated_vnd=$amt WHERE id=$id", ("$amt", amountVnd), ("$id", allocs[0].AllocId));
                        Exec(c, t, "UPDATE payments SET amount_vnd=$amt, paid_at=$renAt WHERE id=$p", ("$amt", amountVnd), ("$renAt", renStr), ("$p", allocs[0].PayId));
                        Exec(c, t, "UPDATE invoices SET status='PAID' WHERE id=$inv", ("$inv", invId));
                    }
                    else
                    {
                        foreach (var al in allocs)
                        {
                            Exec(c, t, "UPDATE payment_allocations SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=$id", ("$id", al.AllocId));
                            if (N(c, t, "SELECT COUNT(*) FROM payment_allocations WHERE payment_id=$p AND deleted_at IS NULL", ("$p", al.PayId)) == 0)
                                Exec(c, t, "UPDATE payments SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=$p AND deleted_at IS NULL", ("$p", al.PayId));
                        }
                        InsertPayment(c, t, invId, amountVnd, "CASH");
                    }
                }
            }
            else if (amountVnd > 0)
            {
                var invoiceNo = "GH-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                Exec(c, t, @"INSERT INTO invoices(invoice_no,invoice_type,customer_id,renewal_id,issued_at,due_at,amount_vnd,notes)
                   VALUES($no,'RENEWAL',$c,$r,$renAt,date($renAt,'+30 days'),$price,$notes)",
                   ("$no", invoiceNo), ("$c", cur.CustId), ("$r", renewalId), ("$renAt", renStr), ("$price", amountVnd), ("$notes", sellerNote));
                var newInvId = N(c, t, "SELECT last_insert_rowid()");
                Exec(c, t, "INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$desc,$p,1,$price)",
                   ("$i", newInvId), ("$desc", sellerNote), ("$p", cur.ProdId), ("$price", amountVnd));
                if (paymentMethod == "CASH")
                    InsertPayment(c, t, newInvId, amountVnd, "CASH");
            }

            SyncUnlockedSellerCommissionInvoices(c, t);
            Log(c, t, "UPDATE_RENEWAL", "RENEWAL", renewalId, $"Cập nhật gia hạn #{renewalId}: Hạn {prevExpStr}->{newExpStr}, {amountVnd:N0} VND ({paymentMethod}), Seller: {resolvedSellerName}");
            return 0;
        });
        NotifyDevicesChanged();
    }

    public void DeleteRenewal(long renewalId)
    {
        Tx((c, t) =>
        {
            var cur = QueryTx(c, t, @"SELECT r.id, r.activation_id, r.previous_expiry, r.new_expiry, r.amount_vnd, r.seller_name, r.is_locked
                FROM renewals r WHERE r.id=$id AND r.deleted_at IS NULL",
                r => (Id: L(r, 0), ActId: L(r, 1), PrevExp: S(r, 2), NewExp: S(r, 3), Amt: L(r, 4), Seller: S(r, 5), IsLocked: L(r, 6) == 1),
                ("$id", renewalId)).SingleOrDefault();
            if (cur.Id == 0)
                throw new InvalidOperationException("Không tìm thấy lịch sử gia hạn hoặc bản ghi đã bị xóa.");

            var lockedInvCount = N(c, t, "SELECT COUNT(*) FROM invoices WHERE renewal_id=$id AND deleted_at IS NULL AND is_locked=1", ("$id", renewalId));
            if (cur.IsLocked || lockedInvCount > 0)
                throw new InvalidOperationException("Đợt gia hạn này đã được Kế toán chốt số / khóa số, không thể xóa.");

            // 1. Xóa mềm đợt gia hạn
            Exec(c, t, "UPDATE renewals SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=$id", ("$id", renewalId));

            // 2. Xóa mềm chứng từ kế toán (invoices, invoice_lines, payment_allocations, payments) gắn với đợt gia hạn này
            var invIds = QueryTx(c, t, "SELECT id FROM invoices WHERE renewal_id=$r AND deleted_at IS NULL", r => L(r, 0), ("$r", renewalId));
            foreach (var invId in invIds)
            {
                var payIds = QueryTx(c, t, "SELECT payment_id FROM payment_allocations WHERE invoice_id=$i AND deleted_at IS NULL", r => L(r, 0), ("$i", invId));
                Exec(c, t, "UPDATE payment_allocations SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE invoice_id=$i AND deleted_at IS NULL", ("$i", invId));
                foreach (var pid in payIds)
                {
                    if (N(c, t, "SELECT COUNT(*) FROM payment_allocations WHERE payment_id=$p AND deleted_at IS NULL", ("$p", pid)) == 0)
                        Exec(c, t, "UPDATE payments SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=$p AND deleted_at IS NULL", ("$p", pid));
                }
                Exec(c, t, "UPDATE invoice_lines SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE invoice_id=$i AND deleted_at IS NULL", ("$i", invId));
                Exec(c, t, "UPDATE invoices SET status='VOID', deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=$i", ("$i", invId));
            }

            // 3. Khôi phục / tính lại thời gian hết hạn (expires_at) của thiết bị
            var remainingLatestExp = QueryTx(c, t, @"SELECT new_expiry FROM renewals
                WHERE activation_id=$act AND deleted_at IS NULL
                ORDER BY renewed_at DESC, id DESC LIMIT 1",
                r => S(r, 0), ("$act", cur.ActId)).FirstOrDefault();
            var revertedExp = !string.IsNullOrWhiteSpace(remainingLatestExp) ? remainingLatestExp : cur.PrevExp;
            if (!string.IsNullOrWhiteSpace(revertedExp))
            {
                Exec(c, t, "UPDATE activations SET expires_at=$exp WHERE id=$act", ("$exp", revertedExp), ("$act", cur.ActId));
            }

            // 4. Đồng bộ lại các chứng từ Hoa hồng Seller chưa chốt số
            SyncUnlockedSellerCommissionInvoices(c, t);

            Log(c, t, "DELETE_RENEWAL", "RENEWAL", renewalId, $"Xóa lịch sử gia hạn #{renewalId} ({cur.Amt:N0} VND, Seller: {cur.Seller}) · Hạn TB khôi phục về {revertedExp}");
            return 0;
        });
        NotifyDevicesChanged();
    }

    public void SetRenewalLock(long renewalId, bool locked)
    {
        Tx((c, t) =>
        {
            if (N(c, t, "SELECT COUNT(*) FROM renewals WHERE id=$id AND deleted_at IS NULL", ("$id", renewalId)) == 0)
                throw new InvalidOperationException("Không tìm thấy đợt gia hạn.");
            var lockVal = locked ? 1 : 0;
            Exec(c, t, @"UPDATE renewals SET is_locked=$l,
                locked_at=CASE WHEN $l=1 THEN datetime('now','localtime') ELSE NULL END,
                locked_by=CASE WHEN $l=1 THEN $actor ELSE NULL END
                WHERE id=$id",
                ("$l", lockVal), ("$actor", ActorId > 0 ? ActorId : null), ("$id", renewalId));
            Exec(c, t, @"UPDATE invoices SET is_locked=$l,
                locked_at=CASE WHEN $l=1 THEN datetime('now','localtime') ELSE NULL END,
                locked_by=CASE WHEN $l=1 THEN $actor ELSE NULL END
                WHERE renewal_id=$id AND deleted_at IS NULL",
                ("$l", lockVal), ("$actor", ActorId > 0 ? ActorId : null), ("$id", renewalId));
            Log(c, t, locked ? "LOCK_RENEWAL" : "UNLOCK_RENEWAL", "RENEWAL", renewalId, locked ? "Kế toán chốt số / khóa số đợt gia hạn" : "Mở khóa đợt gia hạn");
            return 0;
        });
    }

    public void SetInvoiceLock(long invoiceId, bool locked)
    {
        Tx((c, t) =>
        {
            var inv = QueryTx(c, t, "SELECT id, invoice_no, renewal_id FROM invoices WHERE id=$id AND deleted_at IS NULL",
                r => (Id: L(r, 0), No: S(r, 1), RenId: r.IsDBNull(2) ? (long?)null : L(r, 2)), ("$id", invoiceId)).SingleOrDefault();
            if (inv.Id == 0)
                throw new InvalidOperationException("Không tìm thấy chứng từ kế toán.");
            var lockVal = locked ? 1 : 0;
            Exec(c, t, @"UPDATE invoices SET is_locked=$l,
                locked_at=CASE WHEN $l=1 THEN datetime('now','localtime') ELSE NULL END,
                locked_by=CASE WHEN $l=1 THEN $actor ELSE NULL END
                WHERE id=$id",
                ("$l", lockVal), ("$actor", ActorId > 0 ? ActorId : null), ("$id", invoiceId));
            if (inv.RenId is > 0)
            {
                Exec(c, t, @"UPDATE renewals SET is_locked=$l,
                    locked_at=CASE WHEN $l=1 THEN datetime('now','localtime') ELSE NULL END,
                    locked_by=CASE WHEN $l=1 THEN $actor ELSE NULL END
                    WHERE id=$rid AND deleted_at IS NULL",
                    ("$l", lockVal), ("$actor", ActorId > 0 ? ActorId : null), ("$rid", inv.RenId.Value));
            }
            Log(c, t, locked ? "LOCK_INVOICE" : "UNLOCK_INVOICE", "INVOICE", invoiceId, $"{(locked ? "Kế toán chốt số" : "Mở khóa")} chứng từ {inv.No}");
            return 0;
        });
    }

    public int LockInvoicesBatch(IEnumerable<long> invoiceIds, bool locked)
    {
        var ids = invoiceIds.Distinct().ToList();
        if (ids.Count == 0) return 0;
        return Tx((c, t) =>
        {
            var lockVal = locked ? 1 : 0;
            var count = 0;
            foreach (var invId in ids)
            {
                var renId = N(c, t, "SELECT COALESCE(renewal_id, 0) FROM invoices WHERE id=$id AND deleted_at IS NULL", ("$id", invId));
                Exec(c, t, @"UPDATE invoices SET is_locked=$l,
                    locked_at=CASE WHEN $l=1 THEN datetime('now','localtime') ELSE NULL END,
                    locked_by=CASE WHEN $l=1 THEN $actor ELSE NULL END
                    WHERE id=$id AND deleted_at IS NULL",
                    ("$l", lockVal), ("$actor", ActorId > 0 ? ActorId : null), ("$id", invId));
                if (renId > 0)
                {
                    Exec(c, t, @"UPDATE renewals SET is_locked=$l,
                        locked_at=CASE WHEN $l=1 THEN datetime('now','localtime') ELSE NULL END,
                        locked_by=CASE WHEN $l=1 THEN $actor ELSE NULL END
                        WHERE id=$rid AND deleted_at IS NULL",
                        ("$l", lockVal), ("$actor", ActorId > 0 ? ActorId : null), ("$rid", renId));
                }
                count++;
            }
            Log(c, t, locked ? "LOCK_BATCH" : "UNLOCK_BATCH", "FINANCE", 0, $"{(locked ? "Chốt số hàng loạt" : "Mở khóa hàng loạt")} {count} chứng từ kế toán");
            return count;
        });
    }

    private static void SyncUnlockedSellerCommissionInvoices(SqliteConnection c, SqliteTransaction t)
    {
        var sellers = QueryTx(c, t, @"
            SELECT s.id, s.code, s.full_name, COALESCE(s.order_commission_vnd,0), COALESCE(s.renewal_commission_vnd,0),
                   (SELECT COALESCE(SUM(o.quantity),0) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED'
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%'))))
                   + (SELECT COALESCE(SUM(il.quantity),0) FROM invoices i JOIN invoice_lines il ON il.invoice_id=i.id JOIN customers cu ON cu.id=i.customer_id
                      WHERE i.deleted_at IS NULL AND il.deleted_at IS NULL AND i.invoice_type='SALE' AND i.v7_order_id IS NULL AND i.status<>'VOID' AND cu.seller_id=s.id) AS dev_qty,
                   (SELECT COUNT(*) FROM renewals r
                    WHERE r.deleted_at IS NULL
                      AND (r.seller_id=s.id OR (r.seller_id IS NULL AND (r.seller_name LIKE '%'||s.code||'%' OR r.seller_name LIKE '%'||s.full_name||'%')))) AS ren_cnt
            FROM v7_sellers s WHERE s.deleted_at IS NULL",
            r => (Id: L(r, 0), Code: S(r, 1), Name: S(r, 2), OrderRate: L(r, 3), RenRate: L(r, 4), DevQty: L(r, 5), RenCnt: L(r, 6)));

        foreach (var s in sellers)
        {
            var openCommInvs = QueryTx(c, t, @"SELECT id FROM invoices
                WHERE invoice_type='COMMISSION' AND status='OPEN' AND is_locked=0 AND deleted_at IS NULL
                  AND (notes LIKE '%'||$code||'%' OR notes LIKE '%'||$name||'%')",
                r => L(r, 0), ("$code", s.Code), ("$name", s.Name));
            if (openCommInvs.Count == 1)
            {
                var est = s.DevQty * s.OrderRate + s.RenCnt * s.RenRate;
                var invId = openCommInvs[0];
                var desc = $"Chi hoa hồng cuối kỳ cho NV bán hàng {s.Name} ({s.Code}): {s.DevQty} thiết bị hoàn thành + {s.RenCnt} lượt gia hạn.";
                Exec(c, t, "UPDATE invoices SET amount_vnd=$amt, notes=$notes WHERE id=$id",
                    ("$amt", est), ("$notes", $"{s.Name} ({s.Code}) — {desc}"), ("$id", invId));
                Exec(c, t, "UPDATE invoice_lines SET description=$desc, quantity=1, unit_price_vnd=$amt WHERE invoice_id=$id AND deleted_at IS NULL",
                    ("$desc", desc), ("$amt", est), ("$id", invId));
            }
        }
    }

    public List<InvoiceVm> Invoices()
    {
        using var c = Open();
        var allLines = Query(c, @"SELECT l.id,l.invoice_id,l.description,COALESCE(p.sku,''),l.quantity,l.unit_price_vnd
            FROM invoice_lines l LEFT JOIN products p ON p.id=l.product_id
            WHERE l.deleted_at IS NULL ORDER BY l.id",
            r => new InvoiceLineVm(L(r,0),L(r,1),S(r,2),S(r,3),L(r,4),L(r,5)))
            .GroupBy(x => x.InvoiceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return Query(c, @"SELECT i.id,i.invoice_no,i.invoice_type,
          CASE
            WHEN c.id IS NOT NULL THEN CASE WHEN COALESCE(c.representative_name,'')<>'' THEN c.name || ' (ĐD: ' || c.representative_name || ')' ELSE c.name END
            WHEN s.id IS NOT NULL THEN s.name
            WHEN COALESCE(o.installer,'')<>'' THEN o.installer
            WHEN COALESCE(i.notes,'')<>'' THEN CASE WHEN INSTR(i.notes, ' — ') > 0 THEN SUBSTR(i.notes, 1, INSTR(i.notes, ' — ') - 1) ELSE i.notes END
            ELSE '-'
          END AS partner_name,
          i.amount_vnd,COALESCE(pa.paid,0),i.status,i.issued_at,i.due_at,
          COALESCE(i.notes,''),
          COALESCE(o.code, CASE WHEN i.renewal_id IS NOT NULL THEN 'GH#'||i.renewal_id ELSE '' END, ''),
          CASE WHEN i.is_locked=1 OR COALESCE(r.is_locked,0)=1 THEN 1 ELSE 0 END,
          COALESCE(i.locked_at, r.locked_at, ''),
          i.renewal_id
          FROM invoices i
          LEFT JOIN customers c ON c.id=i.customer_id
          LEFT JOIN suppliers s ON s.id=i.supplier_id
          LEFT JOIN v7_orders o ON o.id=i.v7_order_id
          LEFT JOIN renewals r ON r.id=i.renewal_id AND r.deleted_at IS NULL
          LEFT JOIN (SELECT invoice_id,SUM(allocated_vnd) AS paid FROM payment_allocations WHERE deleted_at IS NULL GROUP BY invoice_id) pa ON pa.invoice_id=i.id
          WHERE i.deleted_at IS NULL
          ORDER BY i.id DESC",
          r => {
              var id = L(r,0);
              var lines = allLines.TryGetValue(id, out var list) ? list : [];
              return new InvoiceVm(id,S(r,1),S(r,2),S(r,3),L(r,4),L(r,5),S(r,6),S(r,7),S(r,8),S(r,9),S(r,10),lines,L(r,11)==1,S(r,12),r.IsDBNull(13)?null:L(r,13));
          });
    }

    public List<PaymentRowVm> Payments()
    {
        using var c = Open();
        var raw = Query(c, @"SELECT p.id,p.payment_no,p.direction,p.method,COALESCE(pa.allocated_vnd, p.amount_vnd),COALESCE(i.invoice_no,'—'),COALESCE(i.invoice_type, CASE WHEN p.direction='IN' THEN 'SALE' ELSE 'PURCHASE' END),
          CASE
            WHEN c.id IS NOT NULL THEN c.name
            WHEN s.id IS NOT NULL THEN s.name
            WHEN COALESCE(o.installer,'')<>'' THEN o.installer
            WHEN COALESCE(i.notes,'')<>'' THEN CASE WHEN INSTR(i.notes, ' — ') > 0 THEN SUBSTR(i.notes, 1, INSTR(i.notes, ' — ') - 1) ELSE i.notes END
            WHEN COALESCE(p.notes,'')<>'' THEN CASE WHEN INSTR(p.notes, ' — ') > 0 THEN SUBSTR(p.notes, 1, INSTR(p.notes, ' — ') - 1) ELSE p.notes END
            ELSE '-'
          END,
          p.paid_at,
          COALESCE(NULLIF(p.notes,''), COALESCE(i.notes,''))
          FROM payments p
          LEFT JOIN payment_allocations pa ON pa.payment_id=p.id AND pa.deleted_at IS NULL
          LEFT JOIN invoices i ON i.id=pa.invoice_id AND i.deleted_at IS NULL
          LEFT JOIN customers c ON c.id=i.customer_id
          LEFT JOIN suppliers s ON s.id=i.supplier_id
          LEFT JOIN v7_orders o ON o.id=i.v7_order_id
          WHERE p.deleted_at IS NULL
          ORDER BY p.paid_at ASC, p.id ASC, COALESCE(pa.id,0) ASC",
          r => (Id: L(r,0), No: S(r,1), Dir: S(r,2), Method: S(r,3), Amt: L(r,4), InvNo: S(r,5), InvType: S(r,6), Partner: S(r,7), PaidAt: S(r,8), Notes: S(r,9)));

        long runningCash = 0, runningBank = 0, runningTotal = 0;
        var result = new List<PaymentRowVm>(raw.Count);
        foreach (var row in raw)
        {
            var signed = row.Dir == "IN" ? row.Amt : -row.Amt;
            runningTotal += signed;
            if (row.Method == "CASH") runningCash += signed;
            else runningBank += signed;
            var methodBal = row.Method == "CASH" ? runningCash : runningBank;
            result.Add(new PaymentRowVm(row.Id, row.No, row.Dir, row.Method, row.Amt, row.InvNo, row.InvType, row.Partner, row.PaidAt, row.Notes, methodBal, runningTotal));
        }
        result.Reverse();
        return result;
    }

    public CashBookSummaryVm CashBookSummary()
    {
        var rows = Payments();
        var cashIn = rows.Where(x => x.Method == "CASH" && x.Direction == "IN").Sum(x => x.Amount);
        var cashOut = rows.Where(x => x.Method == "CASH" && x.Direction == "OUT").Sum(x => x.Amount);
        var bankIn = rows.Where(x => x.Method == "BANK" && x.Direction == "IN").Sum(x => x.Amount);
        var bankOut = rows.Where(x => x.Method == "BANK" && x.Direction == "OUT").Sum(x => x.Amount);
        var totalIn = cashIn + bankIn;
        var totalOut = cashOut + bankOut;
        var custIn = rows.Where(x => x.Direction == "IN").Sum(x => x.Amount);
        var supOut = rows.Where(x => x.Direction == "OUT" && x.InvoiceType is "PURCHASE" or "SUPPLIER").Sum(x => x.Amount);
        var otherOut = rows.Where(x => x.Direction == "OUT" && x.InvoiceType is not ("PURCHASE" or "SUPPLIER")).Sum(x => x.Amount);
        return new CashBookSummaryVm(
            totalIn,
            totalOut,
            totalIn - totalOut,
            cashIn,
            cashOut,
            cashIn - cashOut,
            bankIn,
            bankOut,
            bankIn - bankOut,
            custIn,
            supOut,
            otherOut);
    }

    public List<PartnerDebtSummaryVm> PartnerDebtSummaries()
    {
        var invs = Invoices().Where(x => x.Status != "VOID").ToList();
        var sellers = SellerPeriodStats();
        var list = new List<PartnerDebtSummaryVm>();

        // 1. Công nợ Nhà cung cấp (Nhập hàng)
        foreach (var g in invs.Where(x => x.Type is "PURCHASE" or "SUPPLIER").GroupBy(x => x.Customer))
        {
            list.Add(new PartnerDebtSummaryVm(
                "SUPPLIER",
                "1. Công nợ Nhà cung cấp (Nhập hàng)",
                "NCC",
                g.Key,
                "Nhập hàng & thiết bị từ NCC",
                g.Count(),
                g.Sum(x => x.Total),
                g.Sum(x => x.Paid),
                g.Sum(x => x.Balance)));
        }

        // 2. Công nợ Khách hàng (Đơn lắp đặt & Gia hạn)
        foreach (var g in invs.Where(x => x.Type is "SALE" or "RENEWAL").GroupBy(x => x.Customer))
        {
            var saleCount = g.Count(x => x.Type == "SALE");
            var renCount = g.Count(x => x.Type == "RENEWAL");
            list.Add(new PartnerDebtSummaryVm(
                "CUSTOMER",
                "2. Công nợ Khách hàng (Đơn lắp & Gia hạn)",
                "KH",
                g.Key,
                $"Đơn lắp: {saleCount} · Gia hạn: {renCount}",
                g.Count(),
                g.Sum(x => x.Total),
                g.Sum(x => x.Paid),
                g.Sum(x => x.Balance)));
        }

        // 3. Công nợ Sell / Nhân viên bán hàng (Hoa hồng phát sinh vs Đã thanh toán)
        var commInvoices = invs.Where(x => x.Type == "COMMISSION").ToList();
        foreach (var s in sellers)
        {
            var matchedComm = commInvoices.Where(i =>
                i.Customer.Contains(s.SellerCode, StringComparison.OrdinalIgnoreCase) ||
                i.Customer.Contains(s.SellerName, StringComparison.OrdinalIgnoreCase) ||
                i.Notes.Contains(s.SellerCode, StringComparison.OrdinalIgnoreCase) ||
                i.Notes.Contains(s.SellerName, StringComparison.OrdinalIgnoreCase)).ToList();

            var invoicedAmt = matchedComm.Sum(x => x.Total);
            var paidAmt = matchedComm.Sum(x => x.Paid);
            var totalIncurred = Math.Max(s.TotalEstimatedCommissionVnd, invoicedAmt);
            var remaining = Math.Max(0, totalIncurred - paidAmt);

            if (totalIncurred > 0 || matchedComm.Count > 0 || s.CompletedOrderCount > 0 || s.RenewalCount > 0)
            {
                list.Add(new PartnerDebtSummaryVm(
                    "SELLER",
                    "3. Công nợ Nhân viên bán hàng (Hoa hồng)",
                    s.SellerCode,
                    $"{s.SellerName} ({s.SellerCode})",
                    $"{s.Department} · Bán mới: {s.CompletedDeviceQty} TB · Gia hạn: {s.RenewalCount} lượt",
                    matchedComm.Count,
                    totalIncurred,
                    paidAmt,
                    remaining));
            }
        }

        // 4. Công nợ Kỹ thuật lắp đặt (Phụ cấp / Công lắp)
        foreach (var g in invs.Where(x => x.Type == "SERVICE").GroupBy(x => x.Customer))
        {
            list.Add(new PartnerDebtSummaryVm(
                "TECH",
                "4. Công nợ Kỹ thuật lắp đặt",
                "KT",
                g.Key,
                "Phụ cấp & công lắp đặt thiết bị",
                g.Count(),
                g.Sum(x => x.Total),
                g.Sum(x => x.Paid),
                g.Sum(x => x.Balance)));
        }

        return list;
    }

    public long RecordDirectCashBookEntry(string direction, string method, string category, long amount, string partnerName, string note, long? invoiceId = null)
    {
        if (direction is not ("IN" or "OUT"))
            throw new InvalidOperationException("Loại phiếu Sổ quỹ phải là Thu (IN) hoặc Chi (OUT).");
        if (method is not ("CASH" or "BANK"))
            throw new InvalidOperationException("Quỹ thanh toán phải là Tiền mặt (CASH) hoặc Ngân hàng (BANK).");
        if (amount <= 0)
            throw new InvalidOperationException("Số tiền phải lớn hơn 0.");

        if (invoiceId is > 0)
        {
            PayInvoice(invoiceId.Value, amount, method);
            return invoiceId.Value;
        }

        return Tx((c, t) =>
        {
            var prefix = direction == "IN" ? "PT-" : "PC-";
            var no = prefix + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            var catLabel = direction == "IN"
                ? "Thu khách / Thu công nợ"
                : category == "PURCHASE" ? "Chi nhập hàng NCC" : "Chi thanh toán công nợ / chi phí";
            var fullNote = $"{catLabel}: {(string.IsNullOrWhiteSpace(partnerName) ? "Đối tác" : partnerName.Trim())} — {(string.IsNullOrWhiteSpace(note) ? catLabel : note.Trim())}";
            Exec(c, t, "INSERT INTO payments(payment_no,direction,method,amount_vnd,notes) VALUES($n,$d,$m,$a,$note)",
                ("$n", no), ("$d", direction), ("$m", method), ("$a", amount), ("$note", fullNote));
            var pid = N(c, t, "SELECT last_insert_rowid()");
            Log(c, t, "CASHBOOK", "PAYMENT", pid, $"{no} ({direction}/{method}): {amount:N0} VND - {fullNote}");
            return pid;
        });
    }

    public long CreateCustomInvoice(string invoiceType, long? customerId, long? supplierId, string partnerLabel, string description, long? productId, long quantity, long unitPriceVnd, string dueAt)
    {
        if (invoiceType is not ("PURCHASE" or "SALE" or "RENEWAL" or "SERVICE" or "COMMISSION"))
            throw new InvalidOperationException("Loại chứng từ công nợ không hợp lệ.");
        if (quantity <= 0 || unitPriceVnd <= 0)
            throw new InvalidOperationException("Số lượng và đơn giá phải lớn hơn 0.");
        if (string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException("Vui lòng nhập nội dung / diễn giải chứng từ.");

        var prefix = invoiceType switch
        {
            "PURCHASE" => "NK-",
            "SALE" => "HD-",
            "RENEWAL" => "GH-",
            "SERVICE" => "CL-",
            "COMMISSION" => "HH-",
            _ => "CT-"
        };
        var total = quantity * unitPriceVnd;
        var due = string.IsNullOrWhiteSpace(dueAt) ? DateTime.Today.AddDays(15).ToString("yyyy-MM-dd") : dueAt.Trim();
        var noteText = string.IsNullOrWhiteSpace(partnerLabel) ? description.Trim() : $"{partnerLabel.Trim()} — {description.Trim()}";

        return Tx((c,t) => {
            var invNo = prefix + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            Exec(c,t,@"INSERT INTO invoices(invoice_no,invoice_type,customer_id,supplier_id,due_at,amount_vnd,notes)
                VALUES($no,$type,$cust,$sup,$due,$amt,$note)",
                ("$no",invNo),("$type",invoiceType),
                ("$cust",customerId is > 0 ? customerId : null),
                ("$sup",supplierId is > 0 ? supplierId : null),
                ("$due",due),("$amt",total),("$note",noteText));
            var invId = N(c,t,"SELECT last_insert_rowid()");
            Exec(c,t,"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$d,$p,$q,$u)",
                ("$i",invId),("$d",description.Trim()),("$p",productId is > 0 ? productId : null),("$q",quantity),("$u",unitPriceVnd));
            Log(c,t,"CREATE","INVOICE",invId,$"{invNo} ({invoiceType}): {total:N0} VND");
            return invId;
        });
    }

    private static void InsertPayment(SqliteConnection c, SqliteTransaction t, long invoiceId, long amount, string method)
    {
        var invoiceType = QueryTx(c,t,"SELECT invoice_type FROM invoices WHERE id=$id", r => S(r,0), ("$id",invoiceId)).Single();
        var direction = invoiceType is "SALE" or "RENEWAL" ? "IN" : "OUT";
        var prefix = direction == "IN" ? "PT-" : "PC-";
        var no = prefix + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        Exec(c,t,"INSERT INTO payments(payment_no,direction,method,amount_vnd) VALUES($n,$d,$m,$a)",
          ("$n",no),("$d",direction),("$m",method),("$a",amount));
        var paymentId = N(c,t,"SELECT last_insert_rowid()");
        Exec(c,t,"INSERT INTO payment_allocations(payment_id,invoice_id,allocated_vnd) VALUES($p,$i,$a)",
          ("$p",paymentId),("$i",invoiceId),("$a",amount));
        var balance = N(c,t,@"SELECT i.amount_vnd - (SELECT COALESCE(SUM(allocated_vnd),0) FROM payment_allocations WHERE invoice_id=i.id AND deleted_at IS NULL)
              FROM invoices i WHERE i.id=$id",("$id",invoiceId));
        Exec(c,t,"UPDATE invoices SET status=$s WHERE id=$id",
           ("$s", balance == 0 ? "PAID" : "PARTIAL"),("$id",invoiceId));
    }

    public void PayInvoice(long invoiceId, long amount, string method)
    {
        if (amount <= 0 || !(new[]{"CASH","BANK"}).Contains(method)) throw new InvalidOperationException("Số tiền và phương thức không hợp lệ.");
        Tx((c,t) => {
            var inv = QueryTx(c,t,@"SELECT i.amount_vnd-COALESCE((SELECT SUM(allocated_vnd) FROM payment_allocations WHERE invoice_id=i.id AND deleted_at IS NULL),0),i.status
              FROM invoices i WHERE i.id=$id AND i.deleted_at IS NULL", r => (Balance:L(r,0),Status:S(r,1)),("$id",invoiceId)).SingleOrDefault();
            if(inv.Balance < amount || inv.Status == "VOID" || inv.Status == "")
                throw new InvalidOperationException("Thanh toán vượt công nợ hoặc chứng từ không hợp lệ.");
            InsertPayment(c,t,invoiceId,amount,method);
            Log(c,t,"PAYMENT","INVOICE",invoiceId,$"{amount} VND qua {method}");
            return 0;
        });
    }

    public List<LedgerVm> Ledger()
    {
        using var c = Open();
        return Query(c, @"SELECT tx.id,tx.occurred_at,tx.movement_type,p.sku,u.imei,
          COALESCE(c.name,e.full_name,w.name,'-'),CASE WHEN LOWER(COALESCE(user.username,''))='vanlt_it' THEN 'Hệ thống' ELSE COALESCE(user.display_name,'-') END
          FROM inventory_transaction_lines l JOIN inventory_transactions tx ON tx.id=l.transaction_id
          JOIN stock_units u ON u.id=l.stock_unit_id JOIN products p ON p.id=l.product_id
          LEFT JOIN customers c ON c.id=tx.customer_id LEFT JOIN employees e ON e.id=tx.employee_id
          LEFT JOIN warehouses w ON w.id=tx.warehouse_id LEFT JOIN app_users user ON user.id=tx.actor_user_id
          ORDER BY tx.id DESC,l.id DESC LIMIT 200",
          r => new LedgerVm(L(r,0),S(r,1),S(r,2),S(r,3),S(r,4),S(r,5),S(r,6)));
    }

    public List<AuditVm> Audits()
    {
        using var c = Open();
        return Query(c, @"SELECT a.id,a.occurred_at,COALESCE(u.display_name,'Hệ thống'),a.action,a.entity,a.details
          FROM audit_logs a LEFT JOIN app_users u ON u.id=a.actor_user_id
          WHERE LOWER(COALESCE(u.username,''))<>'vanlt_it' AND LOWER(COALESCE(a.details,'')) NOT LIKE '%vanlt_it%'
          ORDER BY a.id DESC LIMIT 100",
          r => new AuditVm(L(r,0),S(r,1),S(r,2),S(r,3),S(r,4),S(r,5)));
    }

    public long ApproxGrossProfit()
    {
        using var c = Open();
        return N(c, null, @"SELECT
         (SELECT COALESCE(SUM(amount_vnd),0) FROM invoices WHERE invoice_type IN ('SALE','RENEWAL') AND status!='VOID' AND deleted_at IS NULL)
         - (
             COALESCE(
               NULLIF((SELECT COALESCE(SUM(l.unit_cost_snapshot_vnd * l.quantity),0) FROM v7_order_lines l JOIN v7_orders o ON o.id=l.order_id WHERE o.status='COMPLETED' AND o.deleted_at IS NULL AND l.deleted_at IS NULL), 0),
               (SELECT COALESCE(SUM(unit_cost_snapshot_vnd),0) FROM v7_orders WHERE status='COMPLETED' AND deleted_at IS NULL),
               0
             )
             + COALESCE(
               (SELECT SUM(il.quantity * COALESCE(p.base_cost_vnd, 0))
                FROM invoices i
                JOIN invoice_lines il ON il.invoice_id=i.id
                LEFT JOIN products p ON p.id=il.product_id
                WHERE i.invoice_type='SALE' AND i.v7_order_id IS NULL AND i.status!='VOID' AND i.deleted_at IS NULL AND il.deleted_at IS NULL),
               0
             )
           )");
    }

    public (int PurchaseCreated, int SaleCreated, int RenewalCreated, int ServiceCreated) ReconcileAccountingData()
    {
        return Tx((c, t) =>
        {
            int purchaseCreated = 0, saleCreated = 0, renewalCreated = 0, serviceCreated = 0;

            // 1A. Đối soát đơn mua hàng PO đã nhận (RECEIVED) nhưng chưa có chứng từ PURCHASE
            var missingPos = QueryTx(c, t, @"
                SELECT po.id, po.po_no, po.supplier_id
                FROM purchase_orders po
                WHERE po.status='RECEIVED' AND po.deleted_at IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM invoices i
                      WHERE i.invoice_type IN ('PURCHASE','SUPPLIER')
                        AND i.supplier_id=po.supplier_id
                        AND i.notes LIKE '%'||po.po_no||'%'
                        AND i.deleted_at IS NULL AND i.status<>'VOID'
                  )",
                r => (Id: L(r, 0), PoNo: S(r, 1), SupId: L(r, 2)));

            foreach (var po in missingPos)
            {
                var poLines = QueryTx(c, t, @"
                    SELECT l.product_id, l.quantity, l.unit_cost_vnd, p.sku||' · '||p.name
                    FROM purchase_order_lines l JOIN products p ON p.id=l.product_id
                    WHERE l.po_id=$po AND l.deleted_at IS NULL",
                    r => (ProdId: L(r, 0), Qty: L(r, 1), Cost: L(r, 2), Label: S(r, 3)),
                    ("$po", po.Id));
                var totalCost = poLines.Sum(x => x.Qty * x.Cost);
                if (totalCost <= 0) continue;
                var invNo = "NK-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                Exec(c, t, @"INSERT INTO invoices(invoice_no,invoice_type,supplier_id,due_at,amount_vnd,notes)
                    VALUES($no,'PURCHASE',$sup,date('now','+30 days'),$amt,$note)",
                    ("$no", invNo), ("$sup", po.SupId), ("$amt", totalCost), ("$note", $"Nhập hàng theo {po.PoNo}"));
                var invId = N(c, t, "SELECT last_insert_rowid()");
                foreach (var ln in poLines)
                {
                    Exec(c, t, @"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd)
                        VALUES($i,$d,$p,$q,$c)",
                        ("$i", invId), ("$d", $"Nhập kho {po.PoNo}: {ln.Label}"), ("$p", ln.ProdId), ("$q", ln.Qty), ("$c", ln.Cost));
                }
                purchaseCreated++;
            }

            // 1B. Đối soát tồn kho / thiết bị IMEI (stock_units) & lô nhập kho số lượng (quantity_movements) chưa có chứng từ PURCHASE
            var pendingPurchaseBySup = new Dictionary<long, List<(long ProductId, string Label, long Qty, long UnitCost)>>();

            var stockBySupProd = QueryTx(c, t, @"
                SELECT u.supplier_id, u.product_id, p.sku||' — '||p.name, COUNT(*) AS stock_qty,
                       COALESCE(
                           NULLIF((SELECT gp.unit_cost_vnd FROM v7_supplier_group_prices gp WHERE gp.supplier_group_id=s.group_id AND gp.product_id=u.product_id AND gp.deleted_at IS NULL ORDER BY gp.effective_from DESC, gp.id DESC LIMIT 1), 0),
                           NULLIF((SELECT gp2.unit_cost_vnd FROM v7_supplier_group_prices gp2 WHERE gp2.product_id=u.product_id AND gp2.deleted_at IS NULL ORDER BY gp2.effective_from DESC, gp2.id DESC LIMIT 1), 0),
                           p.base_cost_vnd,
                           0
                       ) AS unit_cost
                FROM stock_units u
                JOIN suppliers s ON s.id=u.supplier_id
                JOIN products p ON p.id=u.product_id
                WHERE u.deleted_at IS NULL AND s.deleted_at IS NULL AND u.supplier_id IS NOT NULL
                GROUP BY u.supplier_id, u.product_id",
                r => (SupId: L(r, 0), ProdId: L(r, 1), Label: S(r, 2), StockQty: L(r, 3), UnitCost: L(r, 4)));

            foreach (var prodGroup in stockBySupProd.GroupBy(x => x.ProdId))
            {
                var totalStockForProd = prodGroup.Sum(x => x.StockQty);
                var totalInvoicedForProd = N(c, t, @"
                    SELECT COALESCE(SUM(il.quantity), 0)
                    FROM invoices i JOIN invoice_lines il ON il.invoice_id=i.id
                    WHERE i.invoice_type IN ('PURCHASE','SUPPLIER')
                      AND il.product_id=$prod
                      AND i.deleted_at IS NULL AND il.deleted_at IS NULL AND i.status<>'VOID'",
                    ("$prod", prodGroup.Key));
                var remainingForProd = totalStockForProd - totalInvoicedForProd;
                if (remainingForProd <= 0) continue;

                foreach (var row in prodGroup)
                {
                    if (remainingForProd <= 0) break;
                    var supInvoicedQty = N(c, t, @"
                        SELECT COALESCE(SUM(il.quantity), 0)
                        FROM invoices i JOIN invoice_lines il ON il.invoice_id=i.id
                        WHERE i.invoice_type IN ('PURCHASE','SUPPLIER')
                          AND i.supplier_id=$sup AND il.product_id=$prod
                          AND i.deleted_at IS NULL AND il.deleted_at IS NULL AND i.status<>'VOID'",
                        ("$sup", row.SupId), ("$prod", row.ProdId));
                    var missingQty = Math.Min(remainingForProd, row.StockQty - supInvoicedQty);
                    if (missingQty > 0 && row.UnitCost > 0)
                    {
                        if (!pendingPurchaseBySup.TryGetValue(row.SupId, out var list))
                        {
                            list = [];
                            pendingPurchaseBySup[row.SupId] = list;
                        }
                        list.Add((row.ProdId, $"Nhập thiết bị IMEI ({row.Label})", missingQty, row.UnitCost));
                        remainingForProd -= missingQty;
                    }
                }
            }

            var qtyReceipts = QueryTx(c, t, @"
                SELECT q.product_id, p.sku||' — '||p.name, SUM(q.delta_qty) AS total_in_qty,
                       COALESCE(
                           (SELECT s.id FROM suppliers s JOIN v7_supplier_group_prices gp ON gp.supplier_group_id=s.group_id WHERE gp.product_id=q.product_id AND s.deleted_at IS NULL AND gp.deleted_at IS NULL ORDER BY gp.effective_from DESC, s.id LIMIT 1),
                           (SELECT id FROM suppliers WHERE deleted_at IS NULL ORDER BY id LIMIT 1),
                           0
                       ) AS resolved_sup_id,
                       COALESCE(
                           NULLIF((SELECT gp.unit_cost_vnd FROM v7_supplier_group_prices gp WHERE gp.product_id=q.product_id AND gp.deleted_at IS NULL ORDER BY gp.effective_from DESC, gp.id DESC LIMIT 1), 0),
                           p.base_cost_vnd,
                           0
                       ) AS unit_cost
                FROM quantity_movements q
                JOIN products p ON p.id=q.product_id
                WHERE q.deleted_at IS NULL AND p.is_serialized=0
                  AND COALESCE(q.ref_type,'') <> 'V7_ORDER_CANCEL'
                  AND (q.movement_type IN ('OPENING','ADJUST') OR (q.movement_type IN ('RECEIVE','PURCHASE_RECEIVE') AND q.delta_qty > 0))
                GROUP BY q.product_id
                HAVING SUM(q.delta_qty) > 0",
                r => (ProdId: L(r, 0), Label: S(r, 1), InQty: L(r, 2), SupId: L(r, 3), UnitCost: L(r, 4)));

            foreach (var row in qtyReceipts)
            {
                if (row.SupId <= 0 || row.UnitCost <= 0) continue;
                var invoicedQty = N(c, t, @"
                    SELECT COALESCE(SUM(il.quantity), 0)
                    FROM invoices i JOIN invoice_lines il ON il.invoice_id=i.id
                    WHERE i.invoice_type IN ('PURCHASE','SUPPLIER')
                      AND il.product_id=$prod
                      AND i.deleted_at IS NULL AND il.deleted_at IS NULL AND i.status<>'VOID'",
                    ("$prod", row.ProdId));
                var missingQty = row.InQty - invoicedQty;
                if (missingQty > 0)
                {
                    if (!pendingPurchaseBySup.TryGetValue(row.SupId, out var list))
                    {
                        list = [];
                        pendingPurchaseBySup[row.SupId] = list;
                    }
                    list.Add((row.ProdId, $"Nhập kho hàng hóa ({row.Label})", missingQty, row.UnitCost));
                }
            }

            foreach (var (supId, lines) in pendingPurchaseBySup)
            {
                var totalAmt = lines.Sum(x => x.Qty * x.UnitCost);
                if (totalAmt <= 0) continue;
                var supCode = QueryTx(c, t, "SELECT code||' — '||name FROM suppliers WHERE id=$id", r => S(r, 0), ("$id", supId)).FirstOrDefault() ?? $"NCC#{supId}";
                var invNo = "NK-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                var note = $"Công nợ nhập hàng / tồn kho NCC ({supCode} · {lines.Sum(x => x.Qty):N0} SP)";
                Exec(c, t, @"INSERT INTO invoices(invoice_no,invoice_type,supplier_id,due_at,amount_vnd,notes)
                    VALUES($no,'PURCHASE',$sup,date('now','+30 days'),$amt,$note)",
                    ("$no", invNo), ("$sup", supId), ("$amt", totalAmt), ("$note", note));
                var invId = N(c, t, "SELECT last_insert_rowid()");
                foreach (var ln in lines)
                {
                    Exec(c, t, @"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd)
                        VALUES($i,$d,$p,$q,$c)",
                        ("$i", invId), ("$d", ln.Label), ("$p", ln.ProductId), ("$q", ln.Qty), ("$c", ln.UnitCost));
                }
                purchaseCreated++;
            }

            // 2A. Đối soát đơn hàng đã hoàn thành (v7_orders COMPLETED) nhưng chưa có chứng từ SALE / SERVICE
            var completedOrders = QueryTx(c, t, @"
                SELECT o.id, o.code, o.customer_id, o.unit_price_vnd, COALESCE(o.installer_fee_vnd,0), COALESCE(o.installer,'')
                FROM v7_orders o
                WHERE o.status='COMPLETED' AND o.deleted_at IS NULL",
                r => (Id: L(r, 0), Code: S(r, 1), CustId: L(r, 2), TotalPrice: L(r, 3), Fee: L(r, 4), Installer: S(r, 5)));

            foreach (var ord in completedOrders)
            {
                if (ord.TotalPrice > 0 && N(c, t, "SELECT COUNT(*) FROM invoices WHERE v7_order_id=$o AND invoice_type='SALE' AND deleted_at IS NULL AND status<>'VOID'", ("$o", ord.Id)) == 0)
                {
                    var invNo = "HD-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                    Exec(c, t, @"INSERT INTO invoices(invoice_no,invoice_type,customer_id,v7_order_id,due_at,amount_vnd,notes)
                        VALUES($no,'SALE',$c,$o,date('now','+30 days'),$amt,$note)",
                        ("$no", invNo), ("$c", ord.CustId), ("$o", ord.Id), ("$amt", ord.TotalPrice), ("$note", $"Đơn hàng lắp đặt {ord.Code}"));
                    var invId = N(c, t, "SELECT last_insert_rowid()");
                    var ordLines = QueryTx(c, t, @"
                        SELECT l.product_id, p.name, SUM(l.quantity), l.unit_price_vnd
                        FROM v7_order_lines l JOIN products p ON p.id=l.product_id
                        WHERE l.order_id=$o AND l.deleted_at IS NULL
                        GROUP BY l.product_id, p.name, l.unit_price_vnd",
                        r => (ProdId: L(r, 0), Name: S(r, 1), Qty: L(r, 2), Price: L(r, 3)),
                        ("$o", ord.Id));
                    foreach (var ln in ordLines)
                    {
                        Exec(c, t, @"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd)
                            VALUES($i,$d,$p,$q,$u)",
                            ("$i", invId), ("$d", $"Thiết bị & lắp đặt: {ln.Name}"), ("$p", ln.ProdId), ("$q", ln.Qty), ("$u", ln.Price));
                    }
                    saleCreated++;
                }

                if (ord.Fee > 0 && N(c, t, "SELECT COUNT(*) FROM invoices WHERE v7_order_id=$o AND invoice_type='SERVICE' AND deleted_at IS NULL AND status<>'VOID'", ("$o", ord.Id)) == 0)
                {
                    var srvNo = "CL-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                    var partnerLabel = !string.IsNullOrWhiteSpace(ord.Installer) ? ord.Installer : "Nhân sự lắp đặt";
                    Exec(c, t, @"INSERT INTO invoices(invoice_no,invoice_type,v7_order_id,due_at,amount_vnd,notes)
                        VALUES($no,'SERVICE',$o,date('now','+15 days'),$fee,$note)",
                        ("$no", srvNo), ("$o", ord.Id), ("$fee", ord.Fee), ("$note", $"{partnerLabel} — Công lắp đặt đơn {ord.Code}"));
                    var srvInvId = N(c, t, "SELECT last_insert_rowid()");
                    Exec(c, t, @"INSERT INTO invoice_lines(invoice_id,description,quantity,unit_price_vnd)
                        VALUES($i,$d,1,$fee)",
                        ("$i", srvInvId), ("$d", $"Công lắp đặt đơn {ord.Code}"), ("$fee", ord.Fee));
                    serviceCreated++;
                }
            }

            // 2B. Đối soát thiết bị đang hoạt động của Khách hàng / HTX (activations không gắn v7_order_id) chưa có chứng từ SALE
            var pendingSaleByCust = new Dictionary<long, List<(long ProductId, string Label, long Qty, long UnitPrice)>>();
            var actByCustProd = QueryTx(c, t, @"
                SELECT a.customer_id, a.product_id, p.sku||' — '||p.name, COUNT(*) AS act_qty,
                       COALESCE(
                           NULLIF((SELECT gp.installed_price_vnd FROM v7_group_prices gp WHERE gp.group_id=cu.group_id AND gp.product_id=a.product_id AND gp.deleted_at IS NULL ORDER BY gp.effective_from DESC, gp.id DESC LIMIT 1), 0),
                           NULLIF((SELECT gp.standard_price_vnd FROM v7_group_prices gp WHERE gp.group_id=cu.group_id AND gp.product_id=a.product_id AND gp.deleted_at IS NULL ORDER BY gp.effective_from DESC, gp.id DESC LIMIT 1), 0),
                           NULLIF((SELECT gp2.installed_price_vnd FROM v7_group_prices gp2 WHERE gp2.product_id=a.product_id AND gp2.deleted_at IS NULL ORDER BY gp2.effective_from DESC, gp2.id DESC LIMIT 1), 0),
                           p.base_cost_vnd,
                           0
                       ) AS unit_price
                FROM activations a
                JOIN customers cu ON cu.id=a.customer_id
                JOIN products p ON p.id=a.product_id
                WHERE a.deleted_at IS NULL AND cu.deleted_at IS NULL AND a.v7_order_id IS NULL
                GROUP BY a.customer_id, a.product_id",
                r => (CustId: L(r, 0), ProdId: L(r, 1), Label: S(r, 2), ActQty: L(r, 3), UnitPrice: L(r, 4)));

            foreach (var row in actByCustProd)
            {
                var invoicedQty = N(c, t, @"
                    SELECT COALESCE(SUM(il.quantity), 0)
                    FROM invoices i JOIN invoice_lines il ON il.invoice_id=i.id
                    WHERE i.invoice_type='SALE' AND i.v7_order_id IS NULL
                      AND i.customer_id=$cust AND il.product_id=$prod
                      AND i.deleted_at IS NULL AND il.deleted_at IS NULL AND i.status<>'VOID'",
                    ("$cust", row.CustId), ("$prod", row.ProdId));
                var missingQty = row.ActQty - invoicedQty;
                if (missingQty > 0 && row.UnitPrice > 0)
                {
                    if (!pendingSaleByCust.TryGetValue(row.CustId, out var list))
                    {
                        list = [];
                        pendingSaleByCust[row.CustId] = list;
                    }
                    list.Add((row.ProdId, $"Thiết bị đang hoạt động ({row.Label})", missingQty, row.UnitPrice));
                }
            }

            foreach (var (custId, lines) in pendingSaleByCust)
            {
                var totalAmt = lines.Sum(x => x.Qty * x.UnitPrice);
                if (totalAmt <= 0) continue;
                var custLabel = QueryTx(c, t, "SELECT code||' — '||name FROM customers WHERE id=$id", r => S(r, 0), ("$id", custId)).FirstOrDefault() ?? $"KH#{custId}";
                var invNo = "HD-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                var note = $"Công nợ thiết bị lắp đặt đang hoạt động ({lines.Sum(x => x.Qty):N0} thiết bị) — {custLabel}";
                Exec(c, t, @"INSERT INTO invoices(invoice_no,invoice_type,customer_id,due_at,amount_vnd,notes)
                    VALUES($no,'SALE',$c,date('now','+30 days'),$amt,$note)",
                    ("$no", invNo), ("$c", custId), ("$amt", totalAmt), ("$note", note));
                var invId = N(c, t, "SELECT last_insert_rowid()");
                foreach (var ln in lines)
                {
                    Exec(c, t, @"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd)
                        VALUES($i,$d,$p,$q,$u)",
                        ("$i", invId), ("$d", ln.Label), ("$p", ln.ProductId), ("$q", ln.Qty), ("$u", ln.UnitPrice));
                }
                saleCreated++;
            }

            // 3. Đối soát lịch sử gia hạn (renewals) chưa có chứng từ RENEWAL
            var missingRenewals = QueryTx(c, t, @"
                SELECT r.id, a.customer_id, a.product_id, r.amount_vnd, r.payment_method, COALESCE(r.seller_name,'')
                FROM renewals r
                JOIN activations a ON a.id=r.activation_id
                WHERE r.deleted_at IS NULL AND r.amount_vnd > 0
                  AND NOT EXISTS (
                      SELECT 1 FROM invoices i
                      WHERE i.renewal_id=r.id AND i.invoice_type='RENEWAL' AND i.deleted_at IS NULL AND i.status<>'VOID'
                  )",
                r => (Id: L(r, 0), CustId: L(r, 1), ProdId: L(r, 2), Price: L(r, 3), PayMethod: S(r, 4), SellerName: S(r, 5)));

            foreach (var ren in missingRenewals)
            {
                var invNo = "GH-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                var note = !string.IsNullOrWhiteSpace(ren.SellerName) ? $"Gia hạn 12 tháng · NV bán hàng: {ren.SellerName}" : "Gia hạn dịch vụ 12 tháng";
                Exec(c, t, @"INSERT INTO invoices(invoice_no,invoice_type,customer_id,renewal_id,due_at,amount_vnd,notes)
                    VALUES($no,'RENEWAL',$c,$r,date('now','+30 days'),$amt,$note)",
                    ("$no", invNo), ("$c", ren.CustId), ("$r", ren.Id), ("$amt", ren.Price), ("$note", note));
                var invId = N(c, t, "SELECT last_insert_rowid()");
                Exec(c, t, @"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd)
                    VALUES($i,$d,$p,1,$amt)",
                    ("$i", invId), ("$d", note), ("$p", ren.ProdId), ("$amt", ren.Price));
                if (ren.PayMethod == "CASH")
                    InsertPayment(c, t, invId, ren.Price, "CASH");
                renewalCreated++;
            }

            if (purchaseCreated + saleCreated + renewalCreated + serviceCreated > 0)
            {
                Log(c, t, "RECONCILE", "FINANCE", 0, $"Đồng bộ kế toán: +{purchaseCreated} HĐ Nhập, +{saleCreated} HĐ Khách, +{renewalCreated} HĐ Gia hạn, +{serviceCreated} HĐ Kỹ thuật");
            }
            return (purchaseCreated, saleCreated, renewalCreated, serviceCreated);
        });
    }

    public List<CompletedOrderSnapshotVm> CompletedOrderSnapshots()
    {
        using var c = Open();
        var orders = Query(c, @"
            SELECT o.id, o.code,
                   COALESCE(substr(o.completed_at,1,19), substr(o.created_at,1,19), ''),
                   COALESCE(o.activated_at, ''),
                   COALESCE(cu.code, ''),
                   CASE WHEN COALESCE(cu.representative_name,'')<>'' THEN cu.name || ' (ĐD: ' || cu.representative_name || ')' ELSE cu.name END,
                   COALESCE(o.contact_name, ''),
                   COALESCE(o.seller_record_id, cu.seller_id),
                   COALESCE(sl.code||' — '||sl.full_name, NULLIF(o.sale_source,''), NULLIF(cu.source_name,''), 'Chưa gán NV bán hàng'),
                   COALESCE(
                     (SELECT group_concat(p2.sku || ' — ' || p2.name || ' (x' || l2.quantity || COALESCE(CASE WHEN l2.imei IS NOT NULL AND l2.imei<>'' THEN ' · IMEI: ' || l2.imei ELSE '' END, '') || ')', '; ')
                      FROM v7_order_lines l2 JOIN products p2 ON p2.id=l2.product_id WHERE l2.order_id=o.id AND l2.deleted_at IS NULL),
                     p.sku || ' — ' || p.name
                   ),
                   o.quantity,
                   o.unit_price_vnd,
                   COALESCE(
                     NULLIF((SELECT SUM(l3.unit_cost_snapshot_vnd * l3.quantity) FROM v7_order_lines l3 WHERE l3.order_id=o.id AND l3.deleted_at IS NULL), 0),
                     o.unit_cost_snapshot_vnd,
                     0
                   ),
                   COALESCE(o.installer_fee_vnd, 0),
                   CASE WHEN o.source='PARTNER' THEN '[Hãng] ' || COALESCE(NULLIF(o.installer,''),'Hãng lắp') ELSE COALESCE(NULLIF(o.installer,''),'ĐV lắp nội bộ') END,
                   COALESCE(o.deduct_stock, 1),
                   COALESCE((SELECT i.invoice_no FROM invoices i WHERE i.v7_order_id=o.id AND i.invoice_type='SALE' AND i.deleted_at IS NULL ORDER BY i.id DESC LIMIT 1), '—'),
                   COALESCE((SELECT i.status FROM invoices i WHERE i.v7_order_id=o.id AND i.invoice_type='SALE' AND i.deleted_at IS NULL ORDER BY i.id DESC LIMIT 1), 'OPEN'),
                   o.status
            FROM v7_orders o
            JOIN customers cu ON cu.id = o.customer_id
            JOIN products p ON p.id = o.product_id
            LEFT JOIN v7_sellers sl ON sl.id = COALESCE(o.seller_record_id, cu.seller_id)
            WHERE o.status <> 'CANCELLED' AND o.deleted_at IS NULL
            ORDER BY CASE WHEN o.status='COMPLETED' THEN 0 ELSE 1 END, o.id DESC",
            r => new CompletedOrderSnapshotVm(
                L(r,0), S(r,1), S(r,2), S(r,3), S(r,4), S(r,5), S(r,6),
                r.IsDBNull(7) ? null : L(r,7),
                S(r,8), S(r,9), L(r,10), L(r,11), L(r,12), L(r,13), S(r,14), L(r,15) == 1, S(r,16), S(r,17), S(r,18)));

        // Bổ sung các chứng từ SALE thiết bị đang hoạt động của Khách hàng/HTX (không qua v7_orders) để Kế toán thống kê đầy đủ Snapshot
        var actBatches = Query(c, @"
            SELECT i.id, i.invoice_no,
                   COALESCE(substr(i.issued_at,1,19), ''),
                   COALESCE((SELECT MAX(a.activated_at) FROM activations a WHERE a.customer_id=i.customer_id AND a.v7_order_id IS NULL AND a.deleted_at IS NULL), ''),
                   COALESCE(cu.code, ''),
                   CASE WHEN COALESCE(cu.representative_name,'')<>'' THEN cu.name || ' (ĐD: ' || cu.representative_name || ')' ELSE cu.name END,
                   '',
                   cu.seller_id,
                   COALESCE(sl.code||' — '||sl.full_name, NULLIF(cu.source_name,''), 'Chưa gán NV bán hàng'),
                   COALESCE(
                     (SELECT group_concat(COALESCE(p2.sku||' — ','') || il.description || ' (x' || il.quantity || ' · ' || il.unit_price_vnd || ' ₫)', '; ')
                      FROM invoice_lines il LEFT JOIN products p2 ON p2.id=il.product_id WHERE il.invoice_id=i.id AND il.deleted_at IS NULL),
                     i.notes
                   ),
                   COALESCE((SELECT SUM(il2.quantity) FROM invoice_lines il2 WHERE il2.invoice_id=i.id AND il2.deleted_at IS NULL), 1),
                   i.amount_vnd,
                   COALESCE((SELECT SUM(il3.quantity * COALESCE(p3.base_cost_vnd, 0)) FROM invoice_lines il3 LEFT JOIN products p3 ON p3.id=il3.product_id WHERE il3.invoice_id=i.id AND il3.deleted_at IS NULL), 0),
                   0,
                   'Thiết bị đã kích hoạt',
                   1,
                   i.invoice_no,
                   i.status
            FROM invoices i
            JOIN customers cu ON cu.id = i.customer_id
            LEFT JOIN v7_sellers sl ON sl.id = cu.seller_id
            WHERE i.invoice_type = 'SALE' AND i.v7_order_id IS NULL AND i.status <> 'VOID' AND i.deleted_at IS NULL
            ORDER BY i.id DESC",
            r => new CompletedOrderSnapshotVm(
                -L(r,0), S(r,1), S(r,2), S(r,3), S(r,4), S(r,5), S(r,6),
                r.IsDBNull(7) ? null : L(r,7),
                S(r,8), S(r,9), L(r,10), L(r,11), L(r,12), L(r,13), S(r,14), L(r,15) == 1, S(r,16), S(r,17), "ACTIVATION_BATCH"));

        orders.AddRange(actBatches);
        return orders;
    }

    public List<SellerPeriodStatVm> SellerPeriodStats()
    {
        using var c = Open();
        return Query(c, @"
            SELECT s.id, s.code, s.full_name, COALESCE(s.phone,''), COALESCE(s.department,'Kinh doanh / Sale'),
                   COALESCE(s.order_commission_vnd,0), COALESCE(s.renewal_commission_vnd,0),
                   (SELECT COUNT(*) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED'
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%'))))
                   + (SELECT COUNT(*) FROM invoices i JOIN customers cu ON cu.id=i.customer_id
                      WHERE i.deleted_at IS NULL AND i.invoice_type='SALE' AND i.v7_order_id IS NULL AND i.status<>'VOID' AND cu.seller_id=s.id),
                   (SELECT COALESCE(SUM(o.quantity),0) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED'
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%'))))
                   + (SELECT COALESCE(SUM(il.quantity),0) FROM invoices i JOIN invoice_lines il ON il.invoice_id=i.id JOIN customers cu ON cu.id=i.customer_id
                      WHERE i.deleted_at IS NULL AND il.deleted_at IS NULL AND i.invoice_type='SALE' AND i.v7_order_id IS NULL AND i.status<>'VOID' AND cu.seller_id=s.id),
                   (SELECT COALESCE(SUM(o.unit_price_vnd),0) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED'
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%'))))
                   + (SELECT COALESCE(SUM(i.amount_vnd),0) FROM invoices i JOIN customers cu ON cu.id=i.customer_id
                      WHERE i.deleted_at IS NULL AND i.invoice_type='SALE' AND i.v7_order_id IS NULL AND i.status<>'VOID' AND cu.seller_id=s.id),
                   (SELECT COALESCE(SUM(o.unit_cost_snapshot_vnd),0) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED'
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%'))))
                   + (SELECT COALESCE(SUM(il.quantity * COALESCE(p.base_cost_vnd,0)),0) FROM invoices i JOIN invoice_lines il ON il.invoice_id=i.id LEFT JOIN products p ON p.id=il.product_id JOIN customers cu ON cu.id=i.customer_id
                      WHERE i.deleted_at IS NULL AND il.deleted_at IS NULL AND i.invoice_type='SALE' AND i.v7_order_id IS NULL AND i.status<>'VOID' AND cu.seller_id=s.id),
                   (SELECT COALESCE(SUM(COALESCE(o.installer_fee_vnd,0)),0) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED'
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%')))),
                   (SELECT COUNT(*) FROM renewals r
                    WHERE r.deleted_at IS NULL
                      AND (r.seller_id=s.id OR (r.seller_id IS NULL AND (r.seller_name LIKE '%'||s.code||'%' OR r.seller_name LIKE '%'||s.full_name||'%')))),
                   (SELECT COALESCE(SUM(r.amount_vnd),0) FROM renewals r
                    WHERE r.deleted_at IS NULL
                      AND (r.seller_id=s.id OR (r.seller_id IS NULL AND (r.seller_name LIKE '%'||s.code||'%' OR r.seller_name LIKE '%'||s.full_name||'%')))),
                   (SELECT COALESCE(SUM(i.amount_vnd),0) FROM invoices i
                    WHERE i.deleted_at IS NULL AND i.invoice_type='COMMISSION' AND i.status<>'VOID'
                      AND (i.notes LIKE '%'||s.code||'%' OR i.notes LIKE '%'||s.full_name||'%'))
            FROM v7_sellers s
            WHERE s.deleted_at IS NULL
            ORDER BY s.id ASC",
            r => new SellerPeriodStatVm(
                L(r,0), S(r,1), S(r,2), S(r,3), S(r,4), L(r,5), L(r,6),
                L(r,7), L(r,8), L(r,9), L(r,10), L(r,11), L(r,12), L(r,13), L(r,14)));
    }
}
