using System.Globalization;
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
        ("$actor",ActorId),("$a",action),("$e",entity),("$id",entityId),("$d",message));

    public void Initialize()
    {
        using var c = Open();
        Exec(c, null, "PRAGMA journal_mode=WAL;");
        Exec(c, null, File.ReadAllText(Path.Combine(_contentRoot, "Data", "schema.sql")));
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
          K("SELECT COUNT(*) FROM stock_units WHERE status='IN_STOCK'"),
          K("SELECT COUNT(*) FROM stock_units WHERE status='CONSIGNED'"),
          K("SELECT COUNT(*) FROM activations WHERE status='ACTIVE' AND date(expires_at)>=date('now')"),
          K("SELECT COUNT(*) FROM activations WHERE status='ACTIVE' AND date(expires_at) BETWEEN date('now') AND date('now','+30 days')"),
          K("SELECT COUNT(*) FROM v7_orders WHERE status IN ('PENDING','APPROVED','DISPATCHED','REPORTED') AND deleted_at IS NULL"),
          K("SELECT COALESCE(SUM(i.amount_vnd-COALESCE(p.paid,0)),0) FROM invoices i LEFT JOIN (SELECT invoice_id,SUM(allocated_vnd) paid FROM payment_allocations GROUP BY invoice_id) p ON p.invoice_id=i.id WHERE i.status!='VOID' AND i.invoice_type IN ('SALE','RENEWAL')"),
          K("SELECT COALESCE(SUM(amount_vnd),0) FROM invoices WHERE invoice_type IN ('SALE','RENEWAL') AND status!='VOID'"),
          K("SELECT COALESCE(SUM(pa.allocated_vnd),0) FROM payment_allocations pa JOIN payments p ON p.id=pa.payment_id WHERE p.direction='IN'"),
          K("SELECT COALESCE(SUM(amount_vnd),0) FROM invoices WHERE invoice_type='RENEWAL' AND status!='VOID'")
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
              + COALESCE((SELECT ABS(SUM(qm.delta_qty)) FROM quantity_movements qm WHERE qm.product_id=p.id AND qm.movement_type='ISSUE' AND ($w=0 OR qm.warehouse_id=$w)), 0) AS installed_qty
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
           .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (codes.Count == 0 && quantityIfNoImei > 0 && quantityIfNoImei <= 500)
        {
            var stamp = DateTime.Now.ToString("yyMMddHHmmss");
            for (int i = 1; i <= quantityIfNoImei; i++)
                codes.Add($"KHO-{productId}-{stamp}-{i:D3}");
        }
        if (codes.Count == 0 || codes.Count > 500 || codes.Any(x => x.Length > 64) || codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != codes.Count)
            throw new InvalidOperationException("Nhập 1–500 IMEI khác nhau (hoặc nhập Số lượng nhập 1–500), tối đa 64 ký tự / dòng.");
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
            foreach(var imei in codes)
            {
                Exec(c,t,"INSERT INTO stock_units(product_id,imei,status,warehouse_id,supplier_id) VALUES($p,$i,'IN_STOCK',$w,$sup)",
                    ("$p",productId),("$i",imei),("$w",warehouseId),("$sup",resolvedSup > 0 ? resolvedSup : null));
                var stockId = N(c,t,"SELECT last_insert_rowid()");
                Exec(c,t,"INSERT INTO inventory_transaction_lines(transaction_id,stock_unit_id,product_id,unit_cost_snapshot_vnd) VALUES($t,$s,$p,$cost)",
                    ("$t",transId),("$s",stockId),("$p",productId),("$cost",cost));
            }
            if (resolvedSup > 0 && cost * codes.Count > 0)
            {
                var invNo = "NK-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                var totalCost = cost * codes.Count;
                Exec(c,t,@"INSERT INTO invoices(invoice_no,invoice_type,supplier_id,due_at,amount_vnd,notes)
                    VALUES($no,'PURCHASE',$sup,date('now','+30 days'),$amt,$note)",
                    ("$no",invNo),("$sup",resolvedSup),("$amt",totalCost),("$note",$"Nhập lô {codes.Count} IMEI ({code}): {prodName}"));
                var invId = N(c,t,"SELECT last_insert_rowid()");
                Exec(c,t,"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$d,$p,$q,$c)",
                    ("$i",invId),("$d",$"Nhập kho IMEI ({code}): {prodName}"),("$p",productId),("$q",codes.Count),("$c",cost));
            }
            Log(c,t,"RECEIVE","INVENTORY_TX",transId,$"{codes.Count} thiết bị / Product {productId}");
            return codes.Count;
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

    public List<DeviceVm> Devices()
    {
        using var c = Open();
        return Query(c, @"SELECT a.id,u.imei,p.sku,c.name,a.plate_no,a.activated_at,a.expires_at,c.id,p.id,
          COALESCE(a.vin_no,''),COALESCE(o.code,''),COALESCE(o.contact_name,''),
          COALESCE(o.seller_record_id, c.seller_id),
          COALESCE(sl.code||' — '||sl.full_name, NULLIF(o.sale_source,''), NULLIF(c.source_name,''), '')
          FROM activations a JOIN stock_units u ON u.id=a.stock_unit_id
          JOIN products p ON p.id=a.product_id
          JOIN customers c ON c.id=a.customer_id
          LEFT JOIN v7_orders o ON o.id=a.v7_order_id
          LEFT JOIN v7_sellers sl ON sl.id=COALESCE(o.seller_record_id, c.seller_id)
          WHERE a.deleted_at IS NULL
          ORDER BY a.expires_at",
          r => new DeviceVm(L(r,0),S(r,1),S(r,2),S(r,3),S(r,4),S(r,5),S(r,6),L(r,7),L(r,8),S(r,9),S(r,10),S(r,11),r.IsDBNull(12) ? null : L(r,12),S(r,13)));
    }

    public void UpdateDeviceActivation(long activationId, string activatedAt, int durationMonths = 12, string? plateNo = null, string? vinNo = null)
    {
        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy"];
        if (!DateTime.TryParseExact((activatedAt ?? "").Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var actDate))
            throw new InvalidOperationException("Ngày kích hoạt không hợp lệ (định dạng dd/MM/yyyy hoặc yyyy-MM-dd).");
        var months = durationMonths > 0 ? durationMonths : 12;
        var expDate = actDate.AddMonths(months);
        Tx((c,t) => {
            if (N(c,t,"SELECT COUNT(*) FROM activations WHERE id=$id AND deleted_at IS NULL",("$id",activationId)) == 0)
                throw new InvalidOperationException("Không tìm thấy thiết bị kích hoạt.");
            var actStr = actDate.ToString("yyyy-MM-dd");
            var expStr = expDate.ToString("yyyy-MM-dd");
            Exec(c,t,@"UPDATE activations SET activated_at=$act, expires_at=$exp,
                plate_no=COALESCE($plate, plate_no), vin_no=COALESCE($vin, vin_no)
                WHERE id=$id",
                ("$act", actStr), ("$exp", expStr),
                ("$plate", plateNo is not null ? plateNo.Trim() : null),
                ("$vin", vinNo is not null ? vinNo.Trim() : null),
                ("$id", activationId));
            Log(c,t,"UPDATE_ACTIVATION","ACTIVATION",activationId,$"Kích hoạt: {actStr} -> Hết hạn: {expStr}");
            return 0;
        });
    }

    public List<RenewalVm> Renewals()
    {
        using var c = Open();
        return Query(c, @"SELECT r.id,u.imei,r.source_type,r.renewed_at,r.new_expiry,r.amount_vnd,r.payment_method,
          r.seller_id,
          COALESCE(sl.code||' — '||sl.full_name, NULLIF(r.seller_name,''), '—'),
          COALESCE(c.name,''),
          COALESCE(a.plate_no,'')
          FROM renewals r
          JOIN activations a ON a.id=r.activation_id
          JOIN stock_units u ON u.id=a.stock_unit_id
          LEFT JOIN customers c ON c.id=a.customer_id
          LEFT JOIN v7_sellers sl ON sl.id=r.seller_id
          WHERE r.deleted_at IS NULL
          ORDER BY r.id DESC",
          r => new RenewalVm(L(r,0),S(r,1),S(r,2),S(r,3),S(r,4),L(r,5),S(r,6),r.IsDBNull(7) ? null : L(r,7),S(r,8),S(r,9),S(r,10)));
    }

    public void Renew(long activationId, string source, string paymentMethod, long? sellerId = null, string? sellerName = null)
    {
        if (!(new[]{"TNM","NCC","HTX","B2C"}).Contains(source) || !(new[]{"CASH","DEBT"}).Contains(paymentMethod))
            throw new InvalidOperationException("Nguồn gia hạn / hình thức thanh toán không hợp lệ.");
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

            // Lấy giá gia hạn cố định snapshot tại thời điểm phát sinh gia hạn
            var price = N(c,t,@"SELECT renewal_price_vnd FROM v7_group_prices
               WHERE group_id=(SELECT group_id FROM customers WHERE id=$c) AND product_id=$p AND deleted_at IS NULL AND effective_from<=date('now')
                 AND (effective_to IS NULL OR effective_to>=date('now'))
               ORDER BY effective_from DESC,id DESC LIMIT 1",("$c",device.CustomerId),("$p",device.ProductId));
            if(price == 0) price = device.DefaultPrice;
            var old = DateTime.ParseExact(device.Expires,"yyyy-MM-dd",CultureInfo.InvariantCulture);
            var next = (old.Date > DateTime.Today ? old.Date : DateTime.Today).AddYears(1);
            Exec(c,t,@"INSERT INTO renewals(activation_id,seller_id,seller_name,source_type,renewed_at,previous_expiry,new_expiry,amount_vnd,payment_method)
               VALUES($a,$seller,$sellerName,$s,date('now'),$old,$next,$price,$pay)",
               ("$a",activationId),("$seller",resolvedSellerId),("$sellerName",resolvedSellerName),("$s",source),("$old",device.Expires),("$next",next.ToString("yyyy-MM-dd")),("$price",price),("$pay",paymentMethod));
            var renewalId = N(c,t,"SELECT last_insert_rowid()");
            Exec(c,t,"UPDATE activations SET expires_at=$new WHERE id=$id",("$new",next.ToString("yyyy-MM-dd")),("$id",activationId));
            var invoiceNo = "GH-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            var sellerNote = !string.IsNullOrWhiteSpace(resolvedSellerName) ? $"Gia hạn 12 tháng · Seller: {resolvedSellerName}" : "Gia hạn dịch vụ 12 tháng";
            Exec(c,t,@"INSERT INTO invoices(invoice_no,invoice_type,customer_id,renewal_id,due_at,amount_vnd,notes)
               VALUES($no,'RENEWAL',$c,$r,date('now','+30 days'),$price,$notes)",
               ("$no",invoiceNo),("$c",device.CustomerId),("$r",renewalId),("$price",price),("$notes",sellerNote));
            var invoiceId = N(c,t,"SELECT last_insert_rowid()");
            Exec(c,t,"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$desc,$p,1,$price)",
               ("$i",invoiceId),("$desc",sellerNote),("$p",device.ProductId),("$price",price));
            if (paymentMethod == "CASH" && price > 0)
                InsertPayment(c,t,invoiceId,price,"CASH");
            Log(c,t,"RENEW","ACTIVATION",activationId,$"Mới: {next:yyyy-MM-dd}; {price} VND; {source}; Seller: {resolvedSellerName}");
            return 0;
        });
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
            WHEN COALESCE(i.notes,'')<>'' THEN i.notes
            ELSE '-'
          END AS partner_name,
          i.amount_vnd,COALESCE(pa.paid,0),i.status,i.issued_at,i.due_at,
          COALESCE(i.notes,''),
          COALESCE(o.code, CASE WHEN i.renewal_id IS NOT NULL THEN 'GH#'||i.renewal_id ELSE '' END, '')
          FROM invoices i
          LEFT JOIN customers c ON c.id=i.customer_id
          LEFT JOIN suppliers s ON s.id=i.supplier_id
          LEFT JOIN v7_orders o ON o.id=i.v7_order_id
          LEFT JOIN (SELECT invoice_id,SUM(allocated_vnd) AS paid FROM payment_allocations WHERE deleted_at IS NULL GROUP BY invoice_id) pa ON pa.invoice_id=i.id
          WHERE i.deleted_at IS NULL
          ORDER BY i.id DESC",
          r => {
              var id = L(r,0);
              var lines = allLines.TryGetValue(id, out var list) ? list : [];
              return new InvoiceVm(id,S(r,1),S(r,2),S(r,3),L(r,4),L(r,5),S(r,6),S(r,7),S(r,8),S(r,9),S(r,10),lines);
          });
    }

    public List<PaymentRowVm> Payments()
    {
        using var c = Open();
        return Query(c, @"SELECT p.id,p.payment_no,p.direction,p.method,pa.allocated_vnd,i.invoice_no,i.invoice_type,
          CASE
            WHEN c.id IS NOT NULL THEN c.name
            WHEN s.id IS NOT NULL THEN s.name
            WHEN COALESCE(o.installer,'')<>'' THEN o.installer
            WHEN COALESCE(i.notes,'')<>'' THEN i.notes
            ELSE '-'
          END,
          p.paid_at
          FROM payment_allocations pa
          JOIN payments p ON p.id=pa.payment_id
          JOIN invoices i ON i.id=pa.invoice_id
          LEFT JOIN customers c ON c.id=i.customer_id
          LEFT JOIN suppliers s ON s.id=i.supplier_id
          LEFT JOIN v7_orders o ON o.id=i.v7_order_id
          WHERE pa.deleted_at IS NULL AND p.deleted_at IS NULL AND i.deleted_at IS NULL
          ORDER BY p.id DESC, pa.id DESC LIMIT 300",
          r => new PaymentRowVm(L(r,0),S(r,1),S(r,2),S(r,3),L(r,4),S(r,5),S(r,6),S(r,7),S(r,8)));
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
        var balance = N(c,t,@"SELECT i.amount_vnd - (SELECT COALESCE(SUM(allocated_vnd),0) FROM payment_allocations WHERE invoice_id=i.id)
              FROM invoices i WHERE i.id=$id",("$id",invoiceId));
        Exec(c,t,"UPDATE invoices SET status=$s WHERE id=$id",
           ("$s", balance == 0 ? "PAID" : "PARTIAL"),("$id",invoiceId));
    }

    public void PayInvoice(long invoiceId, long amount, string method)
    {
        if (amount <= 0 || !(new[]{"CASH","BANK"}).Contains(method)) throw new InvalidOperationException("Số tiền và phương thức không hợp lệ.");
        Tx((c,t) => {
            var inv = QueryTx(c,t,@"SELECT i.amount_vnd-COALESCE((SELECT SUM(allocated_vnd) FROM payment_allocations WHERE invoice_id=i.id),0),i.status
              FROM invoices i WHERE i.id=$id", r => (Balance:L(r,0),Status:S(r,1)),("$id",invoiceId)).SingleOrDefault();
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
          COALESCE(c.name,e.full_name,w.name,'-'),COALESCE(user.display_name,'-')
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
          FROM audit_logs a LEFT JOIN app_users u ON u.id=a.actor_user_id ORDER BY a.id DESC LIMIT 100",
          r => new AuditVm(L(r,0),S(r,1),S(r,2),S(r,3),S(r,4),S(r,5)));
    }

    public long ApproxGrossProfit()
    {
        using var c = Open();
        return N(c, null, @"SELECT
         (SELECT COALESCE(SUM(amount_vnd),0) FROM invoices WHERE invoice_type IN ('SALE','RENEWAL') AND status!='VOID' AND deleted_at IS NULL)
         - COALESCE(
             NULLIF((SELECT COALESCE(SUM(l.unit_cost_snapshot_vnd * l.quantity),0) FROM v7_order_lines l JOIN v7_orders o ON o.id=l.order_id WHERE o.status='COMPLETED' AND o.deleted_at IS NULL AND l.deleted_at IS NULL), 0),
             (SELECT COALESCE(SUM(unit_cost_snapshot_vnd),0) FROM v7_orders WHERE status='COMPLETED' AND deleted_at IS NULL)
           )");
    }

    public List<CompletedOrderSnapshotVm> CompletedOrderSnapshots()
    {
        using var c = Open();
        return Query(c, @"
            SELECT o.id, o.code,
                   COALESCE(substr(o.completed_at,1,19), substr(o.created_at,1,19), ''),
                   COALESCE(o.activated_at, ''),
                   COALESCE(cu.code, ''),
                   CASE WHEN COALESCE(cu.representative_name,'')<>'' THEN cu.name || ' (ĐD: ' || cu.representative_name || ')' ELSE cu.name END,
                   COALESCE(o.contact_name, ''),
                   COALESCE(o.seller_record_id, cu.seller_id),
                   COALESCE(sl.code||' — '||sl.full_name, NULLIF(o.sale_source,''), NULLIF(cu.source_name,''), 'Chưa gán Seller'),
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
                   COALESCE((SELECT i.status FROM invoices i WHERE i.v7_order_id=o.id AND i.invoice_type='SALE' AND i.deleted_at IS NULL ORDER BY i.id DESC LIMIT 1), 'OPEN')
            FROM v7_orders o
            JOIN customers cu ON cu.id = o.customer_id
            JOIN products p ON p.id = o.product_id
            LEFT JOIN v7_sellers sl ON sl.id = COALESCE(o.seller_record_id, cu.seller_id)
            WHERE o.status = 'COMPLETED' AND o.deleted_at IS NULL
            ORDER BY o.id DESC",
            r => new CompletedOrderSnapshotVm(
                L(r,0), S(r,1), S(r,2), S(r,3), S(r,4), S(r,5), S(r,6),
                r.IsDBNull(7) ? null : L(r,7),
                S(r,8), S(r,9), L(r,10), L(r,11), L(r,12), L(r,13), S(r,14), L(r,15) == 1, S(r,16), S(r,17)));
    }

    public List<SellerPeriodStatVm> SellerPeriodStats()
    {
        using var c = Open();
        return Query(c, @"
            SELECT s.id, s.code, s.full_name, COALESCE(s.phone,''), COALESCE(s.department,'Kinh doanh / Sale'),
                   COALESCE(s.order_commission_vnd,0), COALESCE(s.renewal_commission_vnd,0),
                   (SELECT COUNT(*) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED'
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%')))),
                   (SELECT COALESCE(SUM(o.quantity),0) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED'
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%')))),
                   (SELECT COALESCE(SUM(o.unit_price_vnd),0) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED'
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%')))),
                   (SELECT COALESCE(SUM(o.unit_cost_snapshot_vnd),0) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED'
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%')))),
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
