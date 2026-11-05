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
        return Query(c, "SELECT p.id,p.sku,p.name,g.name,p.base_cost_vnd,p.renewal_fee_vnd,p.install_fee_vnd,p.warranty_months,p.is_serialized FROM products p JOIN product_groups g ON g.id=p.group_id WHERE p.is_active=1 AND p.deleted_at IS NULL ORDER BY p.id DESC",
          r => new ProductVm(L(r,0),S(r,1),S(r,2),S(r,3),L(r,4),L(r,5),L(r,6),(int)L(r,7),L(r,8)==1));
    }

    public List<(long Id, string Name)> Warehouses()
    {
        using var c = Open();
        return Query(c, "SELECT id,name FROM warehouses WHERE deleted_at IS NULL ORDER BY id", r => (L(r,0),S(r,1)));
    }

    public List<(long Id, string Name)> Technicians()
    {
        using var c = Open();
        return Query(c, "SELECT id,full_name FROM employees WHERE department='Kỹ thuật' AND is_active=1 AND deleted_at IS NULL", r => (L(r,0),S(r,1)));
    }

    public List<CustomerVm> Customers()
    {
        using var c = Open();
        return Query(c, "SELECT c.id,c.code,c.name,COALESCE(g.name,'-'),c.source_name,c.phone,c.address FROM customers c LEFT JOIN customer_groups g ON g.id=c.group_id WHERE c.deleted_at IS NULL ORDER BY c.id DESC",
          r => new CustomerVm(L(r,0),S(r,1),S(r,2),S(r,3),S(r,4),S(r,5),S(r,6)));
    }

    public List<StockVm> Stock()
    {
        using var c = Open();
        return Query(c, @"SELECT u.id,u.imei,p.sku,p.name,u.status,
         CASE WHEN u.status='IN_STOCK' THEN COALESCE(w.name,'-')
              WHEN u.status IN ('CONSIGNED','INSTALLED','WARRANTY') THEN COALESCE(cw.name,c.name,'-')
              WHEN u.status='TECHNICIAN' THEN COALESCE(e.full_name,'-') ELSE 'NCC' END,
         u.updated_at,u.product_id
         FROM stock_units u JOIN products p ON p.id=u.product_id
         LEFT JOIN warehouses w ON w.id=u.warehouse_id LEFT JOIN customers c ON c.id=u.customer_id
         LEFT JOIN warehouses cw ON cw.name=c.name AND cw.deleted_at IS NULL
         LEFT JOIN employees e ON e.id=u.employee_id ORDER BY u.id DESC",
          r => new StockVm(L(r,0),S(r,1),S(r,2),S(r,3),S(r,4),S(r,5),S(r,6),L(r,7)));
    }

    public int Receive(long productId, long warehouseId, string multiline)
    {
        var codes = multiline.Replace("\r","").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (codes.Count == 0 || codes.Count > 500 || codes.Any(x => x.Length > 64) || codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != codes.Count)
            throw new InvalidOperationException("Nhập 1–500 IMEI khác nhau, tối đa 64 ký tự / dòng.");
        return Tx((c,t) => {
            if (N(c,t,"SELECT COUNT(*) FROM products WHERE id=$id AND is_serialized=1 AND is_active=1 AND deleted_at IS NULL",("$id",productId)) == 0)
                throw new InvalidOperationException("Nhập kho theo IMEI chỉ hỗ trợ sản phẩm có serial đang kinh doanh.");
            var cost = N(c,t,"SELECT base_cost_vnd FROM products WHERE id=$id",("$id",productId));
            var code = "NK-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            Exec(c,t,"INSERT INTO inventory_transactions(code,movement_type,warehouse_id,actor_user_id,notes) VALUES($n,'RECEIVE',$w,$actor,'Nhập kho IMEI')",
              ("$n",code),("$w",warehouseId),("$actor",ActorId));
            var transId = N(c,t,"SELECT last_insert_rowid()");
            foreach(var imei in codes)
            {
                Exec(c,t,"INSERT INTO stock_units(product_id,imei,status,warehouse_id) VALUES($p,$i,'IN_STOCK',$w)",
                    ("$p",productId),("$i",imei),("$w",warehouseId));
                var stockId = N(c,t,"SELECT last_insert_rowid()");
                Exec(c,t,"INSERT INTO inventory_transaction_lines(transaction_id,stock_unit_id,product_id,unit_cost_snapshot_vnd) VALUES($t,$s,$p,$cost)",
                    ("$t",transId),("$s",stockId),("$p",productId),("$cost",cost));
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
        return Query(c, @"SELECT a.id,u.imei,p.sku,c.name,a.plate_no,a.activated_at,a.expires_at,c.id,p.id
          FROM activations a JOIN stock_units u ON u.id=a.stock_unit_id
          JOIN products p ON p.id=a.product_id
          JOIN customers c ON c.id=a.customer_id
          WHERE a.deleted_at IS NULL
          ORDER BY a.expires_at", r => new DeviceVm(L(r,0),S(r,1),S(r,2),S(r,3),S(r,4),S(r,5),S(r,6),L(r,7),L(r,8)));
    }

    public List<RenewalVm> Renewals()
    {
        using var c = Open();
        return Query(c, @"SELECT r.id,u.imei,r.source_type,r.renewed_at,r.new_expiry,r.amount_vnd,r.payment_method
          FROM renewals r JOIN activations a ON a.id=r.activation_id JOIN stock_units u ON u.id=a.stock_unit_id
          WHERE r.deleted_at IS NULL
          ORDER BY r.id DESC", r => new RenewalVm(L(r,0),S(r,1),S(r,2),S(r,3),S(r,4),L(r,5),S(r,6)));
    }

    public void Renew(long activationId, string source, string paymentMethod)
    {
        if (!(new[]{"TNM","NCC","HTX","B2C"}).Contains(source) || !(new[]{"CASH","DEBT"}).Contains(paymentMethod))
            throw new InvalidOperationException("Nguồn gia hạn / hình thức thanh toán không hợp lệ.");
        Tx((c,t) => {
            var device = QueryTx(c,t,@"SELECT a.expires_at,a.customer_id,a.product_id,p.renewal_fee_vnd
             FROM activations a JOIN products p ON p.id=a.product_id
             WHERE a.id=$id AND a.status='ACTIVE' AND a.deleted_at IS NULL",
             r => (Expires:S(r,0),CustomerId:L(r,1),ProductId:L(r,2),DefaultPrice:L(r,3)),("$id",activationId)).SingleOrDefault();
            if(device.CustomerId == 0) throw new InvalidOperationException("Thiết bị chưa kích hoạt hoặc đã khóa.");
            var price = N(c,t,@"SELECT renewal_price_vnd FROM v7_group_prices
               WHERE group_id=(SELECT group_id FROM customers WHERE id=$c) AND product_id=$p AND deleted_at IS NULL AND effective_from<=date('now')
                 AND (effective_to IS NULL OR effective_to>=date('now'))
               ORDER BY effective_from DESC,id DESC LIMIT 1",("$c",device.CustomerId),("$p",device.ProductId));
            if(price == 0) price = device.DefaultPrice;
            var old = DateTime.ParseExact(device.Expires,"yyyy-MM-dd",CultureInfo.InvariantCulture);
            var next = (old.Date > DateTime.Today ? old.Date : DateTime.Today).AddYears(1);
            Exec(c,t,@"INSERT INTO renewals(activation_id,source_type,renewed_at,previous_expiry,new_expiry,amount_vnd,payment_method)
               VALUES($a,$s,date('now'),$old,$next,$price,$pay)",
               ("$a",activationId),("$s",source),("$old",device.Expires),("$next",next.ToString("yyyy-MM-dd")),("$price",price),("$pay",paymentMethod));
            var renewalId = N(c,t,"SELECT last_insert_rowid()");
            Exec(c,t,"UPDATE activations SET expires_at=$new WHERE id=$id",("$new",next.ToString("yyyy-MM-dd")),("$id",activationId));
            var invoiceNo = "GH-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            Exec(c,t,@"INSERT INTO invoices(invoice_no,invoice_type,customer_id,renewal_id,due_at,amount_vnd)
               VALUES($no,'RENEWAL',$c,$r,date('now','+30 days'),$price)",
               ("$no",invoiceNo),("$c",device.CustomerId),("$r",renewalId),("$price",price));
            var invoiceId = N(c,t,"SELECT last_insert_rowid()");
            Exec(c,t,"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,'Gia hạn dịch vụ 12 tháng',$p,1,$price)",
               ("$i",invoiceId),("$p",device.ProductId),("$price",price));
            if (paymentMethod == "CASH" && price > 0)
                InsertPayment(c,t,invoiceId,price,"CASH");
            Log(c,t,"RENEW","ACTIVATION",activationId,$"Mới: {next:yyyy-MM-dd}; {price} VND; {source}");
            return 0;
        });
    }

    public List<InvoiceVm> Invoices()
    {
        using var c = Open();
        return Query(c, @"SELECT i.id,i.invoice_no,i.invoice_type,COALESCE(c.name,s.name,'-'),i.amount_vnd,
          COALESCE(pa.paid,0),i.status,i.issued_at,i.due_at
          FROM invoices i LEFT JOIN customers c ON c.id=i.customer_id
          LEFT JOIN suppliers s ON s.id=i.supplier_id
          LEFT JOIN (SELECT invoice_id,SUM(allocated_vnd) AS paid FROM payment_allocations GROUP BY invoice_id) pa ON pa.invoice_id=i.id
          WHERE i.deleted_at IS NULL
          ORDER BY i.id DESC",
          r => new InvoiceVm(L(r,0),S(r,1),S(r,2),S(r,3),L(r,4),L(r,5),S(r,6),S(r,7),S(r,8)));
    }

    private static void InsertPayment(SqliteConnection c, SqliteTransaction t, long invoiceId, long amount, string method)
    {
        var invoiceType = QueryTx(c,t,"SELECT invoice_type FROM invoices WHERE id=$id", r => S(r,0), ("$id",invoiceId)).Single();
        var direction = invoiceType is "SALE" or "RENEWAL" ? "IN" : "OUT";
        var no = "PT-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
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
}
