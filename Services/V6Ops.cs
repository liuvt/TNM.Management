using Microsoft.Data.Sqlite;

namespace TNM.Management.Services;

public record Pick(long Id, string Name);
public record WarehousePick(long Id, string Code, string Name);
public record LocationPick(long Id, long WarehouseId, string Code, string Name);
public record QuantityBalance(long ProductId, long WarehouseId, long LocationId, string Sku, string Product, string Warehouse, string Location, long Quantity);
public record QuantityEntry(long Id, string Document, string When, string Sku, string Warehouse, string Location, long Delta, string Type, string Note);
public record QuantityCount(long Id, string Code, string Warehouse, string Location, string Status, string Note, string Created);
public record QuantityCountLine(long Id, long ProductId, string Sku, string Product, long Book, long Counted);
public record PurchaseOrderRow(long Id, string Code, string Supplier, string Warehouse, string Sku, long Qty, long Cost, string Status);
public record WarrantyRow(long Id, string Imei, string Product, string Status, string Problem, string Received, string Notes,
    string PlateNo = "", string CustomerName = "", string OwnerName = "", string OwnerPhone = "", string SupplierName = "",
    string SentSupplierAt = "", string CompletedAt = "", long? CustomerId = null, long? SupplierId = null, long StockUnitId = 0);
public record WarrantyCandidateVm(long StockUnitId, string Imei, string Sku, string ProductName, string PlateNo, string CustomerName, string OwnerName, string OwnerPhone, string UnitStatus)
{
    public string DisplayLabel =>
        $"{(string.IsNullOrWhiteSpace(PlateNo) ? "Chưa biển số" : PlateNo)} — IMEI: {Imei} · {ProductName}" +
        $"{(string.IsNullOrWhiteSpace(CustomerName) ? "" : $" · ĐL/KH: {CustomerName}")}" +
        $"{(string.IsNullOrWhiteSpace(OwnerPhone) ? "" : $" ({OwnerPhone})")} [{UnitStatus}]";
}
public record UserWarehouseRow(long Id, string Username, string Name, string Scope);

/// <summary>
/// Quantity ledger, Stock counts, Purchase orders, Warranties & Warehouse scoping.
/// </summary>
public sealed class V6Ops(IWebHostEnvironment environment)
{
    private readonly string _dbPath = Path.Combine(environment.ContentRootPath, "App_Data", "tnm-local.db");
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, ForeignKeys = true, DefaultTimeout = 15, Pooling = true }.ToString());
        c.Open(); AuditRuntime.Attach(c);
        return c;
    }
    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        var q = c.CreateCommand(); q.CommandText = sql;
        foreach (var (name, val) in args) q.Parameters.AddWithValue(name, val ?? DBNull.Value);
        return q;
    }
    private static void Execute(SqliteConnection c, string sql, params (string, object?)[] args) { using var q = Cmd(c, sql, args); q.ExecuteNonQuery(); }
    private static long Scalar(SqliteConnection c, string sql, params (string, object?)[] args) { using var q = Cmd(c, sql, args); return Convert.ToInt64(q.ExecuteScalar() ?? 0); }
    private static long InsertId(SqliteConnection c) => Scalar(c, "SELECT last_insert_rowid()");
    private static string Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static long? NullableLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static List<T> Read<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> project, params (string, object?)[] args)
    { using var q = Cmd(c, sql, args); using var r = q.ExecuteReader(); var list = new List<T>(); while (r.Read()) list.Add(project(r)); return list; }
    private T Transact<T>(long actor, Func<SqliteConnection, T> action)
    { using var scope = AuditRuntime.As(actor); return Transact(action); }
    private T Transact<T>(Func<SqliteConnection, T> action)
    {
        using var c = Open(); Execute(c, "BEGIN IMMEDIATE");
        try { var result = action(c); Execute(c, "COMMIT"); return result; }
        catch { Execute(c, "ROLLBACK"); throw; }
    }
    private static void Audit(SqliteConnection c, long user, string action, string entity, long entityId, string note) =>
        Execute(c, "INSERT INTO audit_logs(actor_user_id,action,entity,entity_id,details) VALUES($u,$a,$e,$id,$n)",
        ("$u", user), ("$a", action), ("$e", entity), ("$id", entityId), ("$n", note));
    private static bool IsAdmin(SqliteConnection c, long user) => Scalar(c,
        "SELECT COUNT(*) FROM app_users WHERE id=$u AND (is_owner=1 OR LOWER(username)='vanlt_it')", ("$u", user)) > 0 ||
        Scalar(c, "SELECT COUNT(*) FROM user_roles ur JOIN roles r ON r.id=ur.role_id WHERE ur.user_id=$u AND ur.deleted_at IS NULL AND r.code='ADMIN'", ("$u", user)) > 0;
    private static bool CanWarehouse(SqliteConnection c, long user, long warehouse)
    {
        if (IsAdmin(c, user)) return true;
        var mode = Scalar(c, "SELECT COUNT(*) FROM user_scope_settings WHERE user_id=$u AND warehouse_mode='SELECTED'", ("$u", user));
        return mode == 0 || Scalar(c, "SELECT COUNT(*) FROM user_warehouse_scopes WHERE user_id=$u AND warehouse_id=$w AND deleted_at IS NULL", ("$u", user), ("$w", warehouse)) > 0;
    }
    private static void EnsureWarehouse(SqliteConnection c, long user, long warehouse)
    {
        if (!CanWarehouse(c, user, warehouse)) throw new UnauthorizedAccessException("Kho này nằm ngoài phạm vi dữ liệu được cấp cho tài khoản.");
    }
    public bool IsWarehouseRestricted(long user) { using var c = Open(); return !IsAdmin(c, user) && Scalar(c, "SELECT COUNT(*) FROM user_scope_settings WHERE user_id=$u AND warehouse_mode='SELECTED'", ("$u", user)) > 0; }
    public List<WarehousePick> Warehouses(long user)
    {
        using var c = Open(); return Read(c, "SELECT id,code,name FROM warehouses WHERE deleted_at IS NULL ORDER BY name", r => new WarehousePick(r.GetInt64(0), Text(r, 1), Text(r, 2))).Where(w => CanWarehouse(c, user, w.Id)).ToList();
    }
    public List<WarehousePick> AllWarehouses() { using var c = Open(); return Read(c, "SELECT id,code,name FROM warehouses WHERE deleted_at IS NULL ORDER BY name", r => new WarehousePick(r.GetInt64(0), Text(r, 1), Text(r, 2))); }
    public List<LocationPick> Locations(long user)
    { using var c = Open(); return Read(c, "SELECT id,warehouse_id,code,name FROM warehouse_locations WHERE is_active=1 AND deleted_at IS NULL ORDER BY warehouse_id,code", r => new LocationPick(r.GetInt64(0), r.GetInt64(1), Text(r, 2), Text(r, 3))).Where(l => CanWarehouse(c, user, l.WarehouseId)).ToList(); }

    public List<Pick> Suppliers() { using var c = Open(); return Read(c, "SELECT id,name FROM suppliers WHERE deleted_at IS NULL ORDER BY name", r => new Pick(r.GetInt64(0), Text(r, 1))); }
    public List<Pick> QuantityProducts()
    {
        using var c = Open(); return Read(c, @"SELECT p.id,p.sku||' · '||p.name FROM products p LEFT JOIN product_catalog_metadata m ON m.product_id=p.id
        WHERE p.is_active=1 AND p.deleted_at IS NULL AND p.is_serialized=0 AND COALESCE(m.product_kind,'GOODS')='GOODS' ORDER BY p.sku", r => new Pick(r.GetInt64(0), Text(r, 1)));
    }
    public List<QuantityBalance> Balances(long user)
    {
        using var c = Open(); var rows = Read(c, @"
            SELECT * FROM (
                SELECT p.id AS pid,w.id AS wid,l.id AS lid,p.sku AS sku,p.name AS pname,w.name AS wname,l.name AS lname,COALESCE(SUM(q.delta_qty),0) AS qty
                FROM quantity_movements q JOIN products p ON p.id=q.product_id
                JOIN warehouses w ON w.id=q.warehouse_id JOIN warehouse_locations l ON l.id=q.location_id
                WHERE p.deleted_at IS NULL AND w.deleted_at IS NULL
                GROUP BY p.id,w.id,l.id
                HAVING COALESCE(SUM(q.delta_qty),0) <> 0
                UNION ALL
                SELECT p.id AS pid,
                       COALESCE(w.id,1) AS wid,
                       COALESCE((SELECT id FROM warehouse_locations wl WHERE wl.warehouse_id=COALESCE(w.id,1) AND wl.deleted_at IS NULL ORDER BY CASE WHEN wl.code='MAIN' THEN 0 ELSE 1 END,wl.id LIMIT 1),0) AS lid,
                       p.sku AS sku,
                       p.name AS pname,
                       COALESCE(w.name,'Kho mặc định') AS wname,
                       'Trong kho (IMEI)' AS lname,
                       COUNT(*) AS qty
                FROM stock_units u
                JOIN products p ON p.id=u.product_id
                LEFT JOIN warehouses w ON w.id=u.warehouse_id
                WHERE u.deleted_at IS NULL AND p.deleted_at IS NULL AND u.status='IN_STOCK'
                GROUP BY p.id,wid
            )
            ORDER BY wid,sku,lname
            ", r => new QuantityBalance(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), Text(r, 3), Text(r, 4), Text(r, 5), Text(r, 6), r.GetInt64(7)));
        return rows.Where(x => CanWarehouse(c, user, x.WarehouseId)).ToList();
    }
    public List<QuantityEntry> Movements(long user, int limit = 400)
    {
        using var c = Open(); var rows = Read(c, @"
            SELECT q.id,q.document_no,q.occurred_at,p.sku,w.name,l.name,q.delta_qty,q.movement_type,q.note,q.warehouse_id
            FROM quantity_movements q JOIN products p ON p.id=q.product_id
            JOIN warehouses w ON w.id=q.warehouse_id JOIN warehouse_locations l ON l.id=q.location_id
            ORDER BY q.id DESC LIMIT $limit
            ", r => (Warehouse: r.GetInt64(9), Entry: new QuantityEntry(r.GetInt64(0), Text(r, 1), Text(r, 2), Text(r, 3), Text(r, 4), Text(r, 5), r.GetInt64(6), Text(r, 7), Text(r, 8))), ("$limit", Math.Clamp(limit, 1, 200000)));
        return rows.Where(x => CanWarehouse(c, user, x.Warehouse)).Select(x => x.Entry).ToList();
    }
    private static long Current(SqliteConnection c, long product, long warehouse, long location) => Scalar(c,
        "SELECT COALESCE(SUM(delta_qty),0) FROM quantity_movements WHERE product_id=$p AND warehouse_id=$w AND location_id=$l",
        ("$p", product), ("$w", warehouse), ("$l", location));
    private static void CheckLocation(SqliteConnection c, long warehouse, long location)
    { if (Scalar(c, "SELECT COUNT(*) FROM warehouse_locations WHERE id=$l AND warehouse_id=$w AND is_active=1 AND deleted_at IS NULL", ("$l", location), ("$w", warehouse)) == 0) throw new InvalidOperationException("Vị trí không thuộc kho đã chọn."); }
    private static void CheckProduct(SqliteConnection c, long product)
    {
        if (Scalar(c, @"SELECT COUNT(*) FROM products p LEFT JOIN product_catalog_metadata m ON m.product_id=p.id
        WHERE p.id=$id AND p.is_serialized=0 AND p.is_active=1 AND p.deleted_at IS NULL AND COALESCE(m.product_kind,'GOODS')='GOODS' ", ("$id", product)) == 0) throw new InvalidOperationException("Chỉ thực hiện trên sản phẩm không IMEI đang hoạt động.");
    }
    private static void RecordMovement(SqliteConnection c, long user, long product, long warehouse, long location, long delta, string type, string note, string? refType = null, long? refId = null)
    {
        if (delta == 0) throw new InvalidOperationException("Số lượng thay đổi không thể bằng 0.");
        EnsureWarehouse(c, user, warehouse); CheckLocation(c, warehouse, location); CheckProduct(c, product);
        if (delta < 0 && Current(c, product, warehouse, location) < -delta) throw new InvalidOperationException("Không đủ tồn khả dụng tại vị trí.");
        Execute(c, @"
            INSERT INTO quantity_movements(document_no,product_id,warehouse_id,location_id,delta_qty,movement_type,ref_type,ref_id,note,actor_user_id)
            VALUES('Q-'||lower(hex(randomblob(8))),$p,$w,$l,$d,$t,$rt,$rid,$n,$u)
            ", ("$p", product), ("$w", warehouse), ("$l", location), ("$d", delta), ("$t", type), ("$rt", refType), ("$rid", refId), ("$n", note), ("$u", user));
        Audit(c, user, "QTY_" + type, "PRODUCT", product, $"Kho {warehouse}, vị trí {location}, số lượng {delta}. {note}");
    }
    public void MoveQuantity(long user, long product, long warehouse, long location, long qty, bool incoming, string note)
    {
        if (qty <= 0) throw new InvalidOperationException("Số lượng phải lớn hơn 0.");
        Transact(user, c => {
            RecordMovement(c, user, product, warehouse, location, incoming ? qty : -qty, incoming ? "RECEIVE" : "ISSUE", note);
            if (incoming)
            {
                var unitCost = Scalar(c, @"SELECT COALESCE(
                    (SELECT gp.unit_cost_vnd FROM v7_supplier_group_prices gp WHERE gp.product_id=$p AND gp.deleted_at IS NULL AND gp.effective_from<=date('now') ORDER BY gp.effective_from DESC, gp.id DESC LIMIT 1),
                    NULLIF((SELECT base_cost_vnd FROM products WHERE id=$p), 0),
                    0)", ("$p", product));
                var supId = Scalar(c, @"SELECT COALESCE(
                    (SELECT s.id FROM suppliers s JOIN v7_supplier_group_prices gp ON gp.supplier_group_id=s.group_id WHERE gp.product_id=$p AND s.deleted_at IS NULL AND gp.deleted_at IS NULL ORDER BY gp.effective_from DESC, s.id LIMIT 1),
                    (SELECT id FROM suppliers WHERE deleted_at IS NULL ORDER BY id LIMIT 1),
                    0)", ("$p", product));
                if (supId > 0 && unitCost * qty > 0)
                {
                    var prodLabel = Read(c, "SELECT sku||' · '||name FROM products WHERE id=$p", r => Text(r, 0), ("$p", product)).FirstOrDefault() ?? $"SP #{product}";
                    var invNo = "NK-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                    var totalCost = unitCost * qty;
                    var invNote = string.IsNullOrWhiteSpace(note) ? $"Nhập kho số lượng ({qty} × {prodLabel})" : $"Nhập kho số lượng: {prodLabel} ({note})";
                    Execute(c, @"INSERT INTO invoices(invoice_no,invoice_type,supplier_id,due_at,amount_vnd,notes)
                        VALUES($no,'PURCHASE',$sup,date('now','+30 days'),$amt,$note)",
                        ("$no", invNo), ("$sup", supId), ("$amt", totalCost), ("$note", invNote));
                    var invId = InsertId(c);
                    Execute(c, @"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd)
                        VALUES($i,$d,$p,$q,$c)",
                        ("$i", invId), ("$d", invNote), ("$p", product), ("$q", qty), ("$c", unitCost));
                }
            }
            return 0;
        });
    }
    public void TransferQuantity(long user, long product, long fromWarehouse, long fromLocation, long toWarehouse, long toLocation, long qty, string note)
    {
        if (qty <= 0 || (fromWarehouse == toWarehouse && fromLocation == toLocation)) throw new InvalidOperationException("Số lượng hoặc vị trí chuyển không hợp lệ.");
        Transact(user, c => {
            RecordMovement(c, user, product, fromWarehouse, fromLocation, -qty, "TRANSFER_OUT", note);
            RecordMovement(c, user, product, toWarehouse, toLocation, qty, "TRANSFER_IN", note); return 0;
        });
    }
    public void AddLocation(long user, long warehouse, string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Mã và tên vị trí không được để trống.");
        Transact(user, c => {
            EnsureWarehouse(c, user, warehouse); Execute(c, "INSERT INTO warehouse_locations(warehouse_id,code,name) VALUES($w,$code,$name)",
            ("$w", warehouse), ("$code", code.Trim().ToUpperInvariant()), ("$name", name.Trim())); Audit(c, user, "CREATE", "WAREHOUSE_LOCATION", InsertId(c), name); return 0;
        });
    }
    public List<QuantityCount> Counts(long user)
    {
        using var c = Open(); return Read(c, @"
      SELECT q.id,q.count_no,w.name,l.name,q.status,q.note,q.created_at,q.warehouse_id
      FROM quantity_counts q JOIN warehouses w ON w.id=q.warehouse_id JOIN warehouse_locations l ON l.id=q.location_id
      WHERE q.deleted_at IS NULL
      ORDER BY q.id DESC
      ", r => (Warehouse: r.GetInt64(7), Count: new QuantityCount(r.GetInt64(0), Text(r, 1), Text(r, 2), Text(r, 3), Text(r, 4), Text(r, 5), Text(r, 6))))
      .Where(x => CanWarehouse(c, user, x.Warehouse)).Select(x => x.Count).ToList();
    }
    public List<QuantityCountLine> CountLines(long user, long countId)
    {
        using var c = Open(); var wid = Scalar(c, "SELECT warehouse_id FROM quantity_counts WHERE id=$id", ("$id", countId)); EnsureWarehouse(c, user, wid);
        return Read(c, @"
          SELECT l.id,l.product_id,p.sku,p.name,l.book_qty,l.counted_qty FROM quantity_count_lines l
          JOIN products p ON p.id=l.product_id WHERE l.count_id=$id AND l.deleted_at IS NULL ORDER BY p.sku
          ", r => new QuantityCountLine(r.GetInt64(0), r.GetInt64(1), Text(r, 2), Text(r, 3), r.GetInt64(4), r.GetInt64(5)), ("$id", countId));
    }
    public long StartCount(long user, long warehouse, long location, string note)
    {
        return Transact(user, c => {
            EnsureWarehouse(c, user, warehouse); CheckLocation(c, warehouse, location);
            Execute(c, @"INSERT INTO quantity_counts(count_no,warehouse_id,location_id,note,created_by)
            VALUES('KK-'||lower(hex(randomblob(7))),$w,$l,$n,$u)", ("$w", warehouse), ("$l", location), ("$n", note), ("$u", user));
            var id = InsertId(c); Audit(c, user, "CREATE", "COUNT", id, note); return id;
        });
    }
    public void SetCountLine(long user, long countId, long product, long counted)
    {
        if (counted < 0) throw new InvalidOperationException("Số kiểm thực tế không âm.");
        Transact(user, c => {
            using var q = Cmd(c, "SELECT warehouse_id,location_id,status FROM quantity_counts WHERE id=$id", ("$id", countId));
            using var r = q.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("Không tìm thấy phiên kiểm kê.");
            var warehouse = r.GetInt64(0); var location = r.GetInt64(1); var status = r.GetString(2); r.Close();
            EnsureWarehouse(c, user, warehouse); if (status != "DRAFT") throw new InvalidOperationException("Phiên kiểm kê đã khóa."); CheckProduct(c, product);
            var book = Current(c, product, warehouse, location);
            Execute(c, @"INSERT INTO quantity_count_lines(count_id,product_id,book_qty,counted_qty) VALUES($id,$p,$b,$q)
                ON CONFLICT(count_id,product_id) DO UPDATE SET counted_qty=excluded.counted_qty",
                ("$id", countId), ("$p", product), ("$b", book), ("$q", counted));
            Audit(c, user, "COUNT_EDIT", "COUNT", countId, $"SKU {product}: {book} → {counted}"); return 0;
        });
    }
    public void ApproveCount(long user, long countId)
    {
        Transact(user, c => {
            var header = Read(c, "SELECT warehouse_id,location_id,status FROM quantity_counts WHERE id=$id", r => (W: r.GetInt64(0), L: r.GetInt64(1), Status: Text(r, 2)), ("$id", countId)).FirstOrDefault();
            if (header.W == 0 || header.Status != "DRAFT") throw new InvalidOperationException("Phiên không tồn tại hoặc đã duyệt.");
            EnsureWarehouse(c, user, header.W);
            var lines = Read(c, "SELECT product_id,book_qty,counted_qty FROM quantity_count_lines WHERE count_id=$id AND deleted_at IS NULL", r => (P: r.GetInt64(0), Book: r.GetInt64(1), Counted: r.GetInt64(2)), ("$id", countId));
            if (lines.Count == 0) throw new InvalidOperationException("Chưa ghi nhận mặt hàng kiểm kê.");
            foreach (var line in lines)
            {
                var now = Current(c, line.P, header.W, header.L);
                if (now != line.Book) throw new InvalidOperationException($"Tồn SKU #{line.P} đã thay đổi từ {line.Book} sang {now}. Hãy kiểm lại.");
                var delta = line.Counted - line.Book; if (delta != 0) RecordMovement(c, user, line.P, header.W, header.L, delta, "ADJUST", $"Duyệt kiểm kê #{countId}", "COUNT", countId);
            }
            Execute(c, "UPDATE quantity_counts SET status='APPROVED',approved_by=$u,approved_at=CURRENT_TIMESTAMP WHERE id=$id AND status='DRAFT'", ("$u", user), ("$id", countId));
            Audit(c, user, "COUNT_APPROVE", "COUNT", countId, $"{lines.Count} dòng kiểm kê"); return 0;
        });
    }
    public List<PurchaseOrderRow> Purchases(long user)
    {
        using var c = Open(); return Read(c, @"
      SELECT po.id,po.po_no,s.name,w.name,p.sku,l.quantity,l.unit_cost_vnd,po.status,po.warehouse_id
      FROM purchase_orders po JOIN suppliers s ON s.id=po.supplier_id JOIN warehouses w ON w.id=po.warehouse_id
      JOIN purchase_order_lines l ON l.po_id=po.id JOIN products p ON p.id=l.product_id
      WHERE po.deleted_at IS NULL
      ORDER BY po.id DESC
      ", r => (W: r.GetInt64(8), Row: new PurchaseOrderRow(r.GetInt64(0), Text(r, 1), Text(r, 2), Text(r, 3), Text(r, 4), r.GetInt64(5), r.GetInt64(6), Text(r, 7))))
      .Where(x => CanWarehouse(c, user, x.W)).Select(x => x.Row).ToList();
    }
    public long CreatePurchase(long user, long supplier, long product, long warehouse, long location, long qty, long unitCost, string note)
    {
        if (qty <= 0 || unitCost < 0) throw new InvalidOperationException("Số lượng và giá nhập không hợp lệ.");
        return Transact(user, c => {
            EnsureWarehouse(c, user, warehouse); CheckLocation(c, warehouse, location); CheckProduct(c, product);
            if (Scalar(c, "SELECT count(*) FROM suppliers WHERE id=$id AND deleted_at IS NULL", ("$id", supplier)) == 0) throw new InvalidOperationException("Nhà cung cấp không hợp lệ.");
            Execute(c, @"INSERT INTO purchase_orders(po_no,supplier_id,warehouse_id,location_id,note,created_by)
                VALUES('PO-'||lower(hex(randomblob(7))),$s,$w,$l,$n,$u)",
                ("$s", supplier), ("$w", warehouse), ("$l", location), ("$n", note), ("$u", user));
            var id = InsertId(c); Execute(c, "INSERT INTO purchase_order_lines(po_id,product_id,quantity,unit_cost_vnd) VALUES($id,$p,$q,$v)", ("$id", id), ("$p", product), ("$q", qty), ("$v", unitCost));
            Audit(c, user, "PO_CREATE", "PURCHASE_ORDER", id, note); return id;
        });
    }
    public void ReceivePurchase(long user, long purchaseId)
    {
        Transact(user, c => {
            var header = Read(c, "SELECT warehouse_id,location_id,status,supplier_id,po_no FROM purchase_orders WHERE id=$id", r => (W: r.GetInt64(0), L: r.GetInt64(1), S: Text(r, 2), Sup: r.GetInt64(3), PoNo: Text(r, 4)), ("$id", purchaseId)).FirstOrDefault();
            if (header.W == 0 || header.S != "DRAFT") throw new InvalidOperationException("PO không ở trạng thái chờ nhận."); EnsureWarehouse(c, user, header.W);
            var lines = Read(c, "SELECT l.product_id,l.quantity,l.unit_cost_vnd,p.name FROM purchase_order_lines l JOIN products p ON p.id=l.product_id WHERE l.po_id=$id", r => (P: r.GetInt64(0), Q: r.GetInt64(1), Cost: r.GetInt64(2), Name: Text(r, 3)), ("$id", purchaseId));
            foreach (var line in lines) RecordMovement(c, user, line.P, header.W, header.L, line.Q, "PURCHASE_RECEIVE", $"Nhận PO #{purchaseId}", "PURCHASE", purchaseId);
            Execute(c, "INSERT INTO goods_receipts(receipt_no,po_id,received_by) VALUES('GR-'||lower(hex(randomblob(7))),$id,$u)", ("$id", purchaseId), ("$u", user));
            Execute(c, "UPDATE purchase_orders SET status='RECEIVED',received_at=CURRENT_TIMESTAMP WHERE id=$id", ("$id", purchaseId));

            // Tự động tạo hóa đơn công nợ phải trả Nhà cung cấp (PURCHASE)
            var totalCost = lines.Sum(x => x.Q * x.Cost);
            var invNo = "NK-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            Execute(c, @"INSERT INTO invoices(invoice_no,invoice_type,supplier_id,due_at,amount_vnd,notes)
                VALUES($no,'PURCHASE',$sup,date('now','+30 days'),$amt,$note)",
                ("$no", invNo), ("$sup", header.Sup), ("$amt", totalCost), ("$note", $"Nhập hàng theo {header.PoNo}"));
            var invId = InsertId(c);
            foreach (var line in lines)
            {
                Execute(c, "INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$d,$p,$q,$c)",
                    ("$i", invId), ("$d", $"Nhập hàng NCC ({header.PoNo}): {line.Name}"), ("$p", line.P), ("$q", line.Q), ("$c", line.Cost));
            }

            Audit(c, user, "PO_RECEIVE", "PURCHASE_ORDER", purchaseId, "Nhận hàng hoàn tất & ghi nhận công nợ NCC"); return 0;
        });
    }
    public List<WarrantyRow> Warranties()
    {
        using var c = Open();
        return Read(c, @"SELECT wc.id, su.imei, COALESCE(NULLIF(a.device_type_label,''), p.name), wc.status, wc.problem, wc.received_at, COALESCE(wc.note,''),
               COALESCE(a.plate_no,''),
               COALESCE(NULLIF(a.agency_name,''), c.name, ''),
               COALESCE(a.owner_name,''),
               COALESCE(a.owner_phone,''),
               COALESCE(s.name,''),
               COALESCE(wc.sent_supplier_at,''),
               COALESCE(wc.completed_at,''),
               COALESCE(wc.customer_id, a.customer_id, su.customer_id),
               su.supplier_id,
               wc.stock_unit_id
        FROM warranty_cases wc
        JOIN stock_units su ON su.id=wc.stock_unit_id
        JOIN products p ON p.id=su.product_id
        LEFT JOIN activations a ON a.stock_unit_id=su.id AND a.deleted_at IS NULL
        LEFT JOIN customers c ON c.id=COALESCE(wc.customer_id, a.customer_id, su.customer_id)
        LEFT JOIN suppliers s ON s.id=su.supplier_id
        WHERE wc.deleted_at IS NULL
        ORDER BY wc.id DESC",
        r => new WarrantyRow(
            r.GetInt64(0), Text(r, 1), Text(r, 2), Text(r, 3), Text(r, 4), Text(r, 5), Text(r, 6),
            Text(r, 7), Text(r, 8), Text(r, 9), Text(r, 10), Text(r, 11), Text(r, 12), Text(r, 13),
            NullableLong(r, 14), NullableLong(r, 15), r.GetInt64(16)));
    }
    public List<Pick> WarrantyCandidates()
    {
        using var c = Open(); return Read(c, @"SELECT su.id,su.imei||' · '||p.sku FROM stock_units su JOIN products p ON p.id=su.product_id
      WHERE su.status IN ('IN_STOCK','INSTALLED','CONSIGNED','TECHNICIAN') AND su.deleted_at IS NULL ORDER BY su.id DESC LIMIT 500", r => new Pick(r.GetInt64(0), Text(r, 1)));
    }
    public List<WarrantyCandidateVm> SearchWarrantyCandidates(string? keyword = null, int limit = 100)
    {
        using var c = Open();
        var kw = (keyword ?? "").Trim();
        return Read(c, @"SELECT su.id, su.imei, p.sku, COALESCE(NULLIF(a.device_type_label,''), p.name),
               COALESCE(a.plate_no,''),
               COALESCE(NULLIF(a.agency_name,''), c.name, ''),
               COALESCE(a.owner_name,''),
               COALESCE(a.owner_phone,''),
               su.status
        FROM stock_units su
        JOIN products p ON p.id=su.product_id
        LEFT JOIN activations a ON a.stock_unit_id=su.id AND a.deleted_at IS NULL
        LEFT JOIN customers c ON c.id=COALESCE(a.customer_id, su.customer_id)
        WHERE su.deleted_at IS NULL
          AND su.status IN ('IN_STOCK','INSTALLED','CONSIGNED','TECHNICIAN')
          AND ($kw = '' OR su.imei LIKE '%'||$kw||'%' OR COALESCE(a.plate_no,'') LIKE '%'||$kw||'%'
               OR COALESCE(a.agency_name,'') LIKE '%'||$kw||'%' OR COALESCE(c.name,'') LIKE '%'||$kw||'%'
               OR COALESCE(a.owner_name,'') LIKE '%'||$kw||'%' OR COALESCE(a.owner_phone,'') LIKE '%'||$kw||'%')
        ORDER BY CASE WHEN COALESCE(a.plate_no,'')<>'' THEN 0 ELSE 1 END, su.id DESC
        LIMIT $lim",
        r => new WarrantyCandidateVm(
            r.GetInt64(0), Text(r, 1), Text(r, 2), Text(r, 3), Text(r, 4), Text(r, 5), Text(r, 6), Text(r, 7), Text(r, 8)),
        ("$kw", kw), ("$lim", limit));
    }
    public long OpenWarranty(long user, long stockId, string problem, string notes)
    {
        if (string.IsNullOrWhiteSpace(problem)) throw new InvalidOperationException("Cần ghi lỗi / sự cố thiết bị.");
        return Transact(user, c => {
            var unit = Read(c, @"SELECT su.status, su.warehouse_id, COALESCE(su.customer_id, a.customer_id)
                FROM stock_units su
                LEFT JOIN activations a ON a.stock_unit_id=su.id AND a.deleted_at IS NULL
                WHERE su.id=$id",
                r => (Status: Text(r, 0), Warehouse: NullableLong(r, 1), Customer: NullableLong(r, 2)), ("$id", stockId)).FirstOrDefault();
            if (unit.Status is not ("IN_STOCK" or "INSTALLED" or "CONSIGNED" or "TECHNICIAN"))
                throw new InvalidOperationException("IMEI không hợp lệ hoặc đang trong tiến trình bảo hành.");
            if (Scalar(c, "SELECT COUNT(*) FROM warranty_cases WHERE stock_unit_id=$s AND status IN ('RECEIVED','SENT_SUPPLIER') AND deleted_at IS NULL", ("$s", stockId)) > 0)
                throw new InvalidOperationException("Thiết bị IMEI này đã có phiếu bảo hành đang mở.");
            Execute(c, "INSERT INTO warranty_cases(stock_unit_id,customer_id,received_at,status,problem,note) VALUES($s,$cust,date('now'),'RECEIVED',$p,$n)",
                ("$s", stockId), ("$cust", unit.Customer), ("$p", problem.Trim()), ("$n", (notes ?? "").Trim()));
            var id = InsertId(c);
            Execute(c, "UPDATE stock_units SET status='WARRANTY',warehouse_id=NULL,employee_id=NULL,updated_at=CURRENT_TIMESTAMP WHERE id=$s", ("$s", stockId));
            Execute(c, "INSERT INTO warranty_events(case_id,new_status,user_id,note) VALUES($id,'RECEIVED',$u,$n)", ("$id", id), ("$u", user), ("$n", (notes ?? "").Trim()));
            Audit(c, user, "WARRANTY_OPEN", "WARRANTY_CASE", id, problem.Trim());
            return id;
        });
    }
    public void SetWarrantyStatus(long user, long caseId, string next, long warehouse, long? supplier, string notes, string returnTarget = "CUSTOMER")
    {
        if (next is not ("SENT_SUPPLIER" or "COMPLETED" or "CANCELLED")) throw new InvalidOperationException("Trạng thái không hợp lệ.");
        Transact(user, c => {
            var item = Read(c, @"SELECT wc.stock_unit_id, wc.status, COALESCE(wc.customer_id, a.customer_id, su.customer_id)
                FROM warranty_cases wc
                JOIN stock_units su ON su.id=wc.stock_unit_id
                LEFT JOIN activations a ON a.stock_unit_id=su.id AND a.deleted_at IS NULL
                WHERE wc.id=$id",
                r => (Unit: r.GetInt64(0), Status: Text(r, 1), CustId: NullableLong(r, 2)), ("$id", caseId)).FirstOrDefault();
            if (item.Unit == 0 || item.Status is "COMPLETED" or "CANCELLED" || (next == "SENT_SUPPLIER" && item.Status != "RECEIVED"))
                throw new InvalidOperationException("Không thể chuyển trạng thái bảo hành.");
            if (next == "SENT_SUPPLIER")
            {
                if (supplier is null || Scalar(c, "SELECT count(*) FROM suppliers WHERE id=$id AND deleted_at IS NULL", ("$id", supplier)) == 0)
                    throw new InvalidOperationException("Hãy chọn nhà cung cấp nhận bảo hành.");
                Execute(c, "UPDATE warranty_cases SET status='SENT_SUPPLIER',sent_supplier_at=date('now'),note=CASE WHEN $n<>'' THEN $n ELSE note END WHERE id=$id",
                    ("$n", (notes ?? "").Trim()), ("$id", caseId));
                Execute(c, "UPDATE stock_units SET status='SUPPLIER',supplier_id=$sup,warehouse_id=NULL,employee_id=NULL,updated_at=CURRENT_TIMESTAMP WHERE id=$u",
                    ("$sup", supplier), ("$u", item.Unit));
            }
            else
            {
                bool returnToCustomer = string.Equals(returnTarget, "CUSTOMER", StringComparison.OrdinalIgnoreCase) && item.CustId is > 0;
                if (returnToCustomer)
                {
                    Execute(c, "UPDATE warranty_cases SET status=$s,completed_at=date('now'),note=CASE WHEN $n<>'' THEN $n ELSE note END WHERE id=$id",
                        ("$s", next), ("$n", (notes ?? "").Trim()), ("$id", caseId));
                    Execute(c, "UPDATE stock_units SET status='INSTALLED',warehouse_id=NULL,customer_id=$c,employee_id=NULL,updated_at=CURRENT_TIMESTAMP WHERE id=$u",
                        ("$c", item.CustId), ("$u", item.Unit));
                }
                else
                {
                    var targetWh = warehouse > 0 ? warehouse : Scalar(c, "SELECT COALESCE((SELECT id FROM warehouses WHERE deleted_at IS NULL ORDER BY id LIMIT 1), 1)");
                    Execute(c, "UPDATE warranty_cases SET status=$s,completed_at=date('now'),note=CASE WHEN $n<>'' THEN $n ELSE note END WHERE id=$id",
                        ("$s", next), ("$n", (notes ?? "").Trim()), ("$id", caseId));
                    Execute(c, "UPDATE stock_units SET status='IN_STOCK',warehouse_id=$w,customer_id=NULL,employee_id=NULL,updated_at=CURRENT_TIMESTAMP WHERE id=$u",
                        ("$w", targetWh), ("$u", item.Unit));
                }
            }
            Execute(c, "INSERT INTO warranty_events(case_id,old_status,new_status,user_id,note) VALUES($id,$o,$n,$u,$note)",
                ("$id", caseId), ("$o", item.Status), ("$n", next), ("$u", user), ("$note", (notes ?? "").Trim()));
            Audit(c, user, "WARRANTY_" + next, "WARRANTY_CASE", caseId, (notes ?? "").Trim()); return 0;
        });
    }

    public void UpdateWarranty(long user, long caseId, string problem, string notes, string status, string? receivedAt, long? supplier, string? sentSupplierAt, string? completedAt, string returnTarget = "CUSTOMER", long warehouse = 0)
    {
        if (string.IsNullOrWhiteSpace(problem)) throw new InvalidOperationException("Vui lòng nhập mô tả lỗi / sự cố thiết bị.");
        if (status is not ("RECEIVED" or "SENT_SUPPLIER" or "COMPLETED" or "CANCELLED"))
            throw new InvalidOperationException("Trạng thái bảo hành không hợp lệ.");

        Transact(user, c => {
            var item = Read(c, @"SELECT wc.stock_unit_id, wc.status, COALESCE(wc.customer_id, a.customer_id, su.customer_id),
                                        wc.received_at, COALESCE(wc.sent_supplier_at,''), COALESCE(wc.completed_at,'')
                FROM warranty_cases wc
                JOIN stock_units su ON su.id=wc.stock_unit_id
                LEFT JOIN activations a ON a.stock_unit_id=su.id AND a.deleted_at IS NULL
                WHERE wc.id=$id AND wc.deleted_at IS NULL",
                r => (Unit: r.GetInt64(0), OldStatus: Text(r, 1), CustId: NullableLong(r, 2), RecAt: Text(r, 3), SentAt: Text(r, 4), CompAt: Text(r, 5)),
                ("$id", caseId)).FirstOrDefault();

            if (item.Unit == 0) throw new InvalidOperationException("Không tìm thấy phiếu bảo hành hoặc phiếu đã bị xóa.");

            var recDate = string.IsNullOrWhiteSpace(receivedAt) ? item.RecAt : receivedAt.Trim();
            string? sentDate = string.IsNullOrWhiteSpace(sentSupplierAt) ? (string.IsNullOrWhiteSpace(item.SentAt) ? null : item.SentAt) : sentSupplierAt.Trim();
            string? compDate = string.IsNullOrWhiteSpace(completedAt) ? (string.IsNullOrWhiteSpace(item.CompAt) ? null : item.CompAt) : completedAt.Trim();

            if (status == "SENT_SUPPLIER")
            {
                if (supplier is null or <= 0 || Scalar(c, "SELECT count(*) FROM suppliers WHERE id=$id AND deleted_at IS NULL", ("$id", supplier)) == 0)
                    throw new InvalidOperationException("Hãy chọn Nhà cung cấp / Hãng bảo hành.");
                sentDate ??= DateTime.Today.ToString("yyyy-MM-dd");
                compDate = null;
                Execute(c, "UPDATE stock_units SET status='SUPPLIER',supplier_id=$sup,warehouse_id=NULL,employee_id=NULL,updated_at=CURRENT_TIMESTAMP WHERE id=$u",
                    ("$sup", supplier), ("$u", item.Unit));
            }
            else if (status == "RECEIVED")
            {
                sentDate = null;
                compDate = null;
                Execute(c, "UPDATE stock_units SET status='WARRANTY',warehouse_id=NULL,employee_id=NULL,updated_at=CURRENT_TIMESTAMP WHERE id=$u",
                    ("$u", item.Unit));
            }
            else
            {
                compDate ??= DateTime.Today.ToString("yyyy-MM-dd");
                if (supplier is > 0 && Scalar(c, "SELECT count(*) FROM suppliers WHERE id=$id AND deleted_at IS NULL", ("$id", supplier)) > 0)
                {
                    Execute(c, "UPDATE stock_units SET supplier_id=$sup WHERE id=$u", ("$sup", supplier), ("$u", item.Unit));
                }
                bool returnToCustomer = string.Equals(returnTarget, "CUSTOMER", StringComparison.OrdinalIgnoreCase) && item.CustId is > 0;
                if (returnToCustomer)
                {
                    Execute(c, "UPDATE stock_units SET status='INSTALLED',warehouse_id=NULL,customer_id=$c,employee_id=NULL,updated_at=CURRENT_TIMESTAMP WHERE id=$u",
                        ("$c", item.CustId), ("$u", item.Unit));
                }
                else
                {
                    var targetWh = warehouse > 0 ? warehouse : Scalar(c, "SELECT COALESCE((SELECT id FROM warehouses WHERE deleted_at IS NULL ORDER BY id LIMIT 1), 1)");
                    Execute(c, "UPDATE stock_units SET status='IN_STOCK',warehouse_id=$w,customer_id=NULL,employee_id=NULL,updated_at=CURRENT_TIMESTAMP WHERE id=$u",
                        ("$w", targetWh), ("$u", item.Unit));
                }
            }

            Execute(c, @"UPDATE warranty_cases
                SET problem=$p, note=$n, status=$st, received_at=$rec, sent_supplier_at=$sent, completed_at=$comp
                WHERE id=$id",
                ("$p", problem.Trim()), ("$n", (notes ?? "").Trim()), ("$st", status),
                ("$rec", recDate), ("$sent", sentDate), ("$comp", compDate), ("$id", caseId));

            Execute(c, "INSERT INTO warranty_events(case_id,old_status,new_status,user_id,note) VALUES($id,$o,$n,$u,$note)",
                ("$id", caseId), ("$o", item.OldStatus), ("$n", status), ("$u", user), ("$note", $"Cập nhật phiếu BH: {problem.Trim()} · {(notes ?? "").Trim()}"));
            Audit(c, user, "WARRANTY_EDIT", "WARRANTY_CASE", caseId, $"{status}: {problem.Trim()}");
            return 0;
        });
    }

    public void DeleteWarranty(long user, long caseId)
    {
        Transact(user, c => {
            var item = Read(c, @"SELECT wc.stock_unit_id, wc.status, su.status, COALESCE(wc.customer_id, a.customer_id, su.customer_id), su.imei
                FROM warranty_cases wc
                JOIN stock_units su ON su.id=wc.stock_unit_id
                LEFT JOIN activations a ON a.stock_unit_id=su.id AND a.deleted_at IS NULL
                WHERE wc.id=$id AND wc.deleted_at IS NULL",
                r => (Unit: r.GetInt64(0), CaseStatus: Text(r, 1), UnitStatus: Text(r, 2), CustId: NullableLong(r, 3), Imei: Text(r, 4)),
                ("$id", caseId)).FirstOrDefault();

            if (item.Unit == 0) throw new InvalidOperationException("Không tìm thấy phiếu bảo hành hoặc phiếu đã bị xóa.");

            // Nếu xóa phiếu bảo hành đang mở (thiết bị đang ở trạng thái WARRANTY hoặc SUPPLIER), trả thiết bị về lại trạng thái trước bảo hành
            var otherActiveCases = Scalar(c, "SELECT COUNT(*) FROM warranty_cases WHERE stock_unit_id=$u AND id<>$id AND deleted_at IS NULL AND status IN ('RECEIVED','SENT_SUPPLIER')",
                ("$u", item.Unit), ("$id", caseId));
            if (otherActiveCases == 0 && item.UnitStatus is "WARRANTY" or "SUPPLIER")
            {
                if (item.CustId is > 0)
                {
                    Execute(c, "UPDATE stock_units SET status='INSTALLED',warehouse_id=NULL,customer_id=$c,employee_id=NULL,updated_at=CURRENT_TIMESTAMP WHERE id=$u",
                        ("$c", item.CustId), ("$u", item.Unit));
                }
                else
                {
                    var defaultWh = Scalar(c, "SELECT COALESCE((SELECT id FROM warehouses WHERE deleted_at IS NULL ORDER BY id LIMIT 1), 1)");
                    Execute(c, "UPDATE stock_units SET status='IN_STOCK',warehouse_id=$w,customer_id=NULL,employee_id=NULL,updated_at=CURRENT_TIMESTAMP WHERE id=$u",
                        ("$w", defaultWh), ("$u", item.Unit));
                }
            }

            Execute(c, "UPDATE warranty_events SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE case_id=$id AND deleted_at IS NULL", ("$id", caseId));
            Execute(c, "UPDATE warranty_cases SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=$id AND deleted_at IS NULL", ("$id", caseId));
            Audit(c, user, "WARRANTY_DELETE", "WARRANTY_CASE", caseId, $"Xóa phiếu bảo hành #BH-{caseId:D4} (IMEI: {item.Imei})");
            return 0;
        });
    }
    public List<UserWarehouseRow> UsersWarehouseScopes()
    {
        using var c = Open(); return Read(c, @"SELECT u.id,u.username,u.display_name,COALESCE(s.warehouse_mode,'ALL')
        FROM app_users u LEFT JOIN user_scope_settings s ON s.user_id=u.id WHERE u.deleted_at IS NULL AND LOWER(u.username)<>'vanlt_it' ORDER BY u.id", r => new UserWarehouseRow(r.GetInt64(0), Text(r, 1), Text(r, 2), Text(r, 3)));
    }
    public List<long> AssignedWarehouses(long userId)
    { using var c = Open(); return Read(c, "SELECT warehouse_id FROM user_warehouse_scopes WHERE user_id=$id AND deleted_at IS NULL", r => r.GetInt64(0), ("$id", userId)); }
    public void SetWarehouseScope(long actor, long subject, bool all, IEnumerable<long> warehouses)
    {
        var set = warehouses.Distinct().ToList(); if (!all && set.Count == 0) throw new InvalidOperationException("Chế độ giới hạn phải chọn ít nhất một kho.");
        Transact(actor, c => {
            if (IsAdmin(c, subject)) throw new InvalidOperationException("Không giới hạn kho cho tài khoản ADMIN.");
            if (Scalar(c, "SELECT count(*) FROM app_users WHERE id=$id AND deleted_at IS NULL", ("$id", subject)) == 0) throw new InvalidOperationException("Không tìm thấy User.");
            foreach (var id in set) if (Scalar(c, "SELECT count(*) FROM warehouses WHERE id=$id AND deleted_at IS NULL", ("$id", id)) == 0) throw new InvalidOperationException("Kho không tồn tại.");
            Execute(c, "INSERT INTO user_scope_settings(user_id,warehouse_mode) VALUES($u,$m) ON CONFLICT(user_id) DO UPDATE SET warehouse_mode=excluded.warehouse_mode",
                ("$u", subject), ("$m", all ? "ALL" : "SELECTED"));
            Execute(c, "UPDATE user_warehouse_scopes SET deleted_at=CURRENT_TIMESTAMP WHERE user_id=$u AND deleted_at IS NULL", ("$u", subject));
            if (!all) foreach (var id in set) Execute(c, "INSERT INTO user_warehouse_scopes(user_id,warehouse_id) VALUES($u,$w) ON CONFLICT(user_id,warehouse_id) DO UPDATE SET deleted_at=NULL,deleted_by=NULL", ("$u", subject), ("$w", id));
            Execute(c, "UPDATE user_security SET security_stamp=lower(hex(randomblob(16))) WHERE user_id=$u", ("$u", subject));
            Audit(c, actor, "WAREHOUSE_SCOPE", "APP_USER", subject, all ? "Tất cả kho" : string.Join(",", set)); return 0;
        });
    }
    public void CreateSupplier(long actor, string code, string name, string phone, string tax = "")
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Nhập mã và tên NCC.");
        Transact(actor, c => {
            var groupId = Scalar(c, "SELECT id FROM supplier_groups WHERE deleted_at IS NULL ORDER BY id LIMIT 1");
            if (groupId == 0)
            {
                Execute(c, "INSERT INTO supplier_groups(code,name) VALUES('DEFAULT','Nhà cung cấp tiêu chuẩn')");
                groupId = InsertId(c);
            }
            Execute(c, "INSERT INTO suppliers(code,name,group_id,phone,tax_code) VALUES($code,$name,$g,$phone,$tax)",
            ("$code", code.Trim().ToUpperInvariant()), ("$name", name.Trim()), ("$g", groupId), ("$phone", phone.Trim()), ("$tax", tax.Trim()));
            Audit(c, actor, "CREATE", "SUPPLIER", InsertId(c), name); return 0;
        });
    }
    public void CreateEmployee(long actor, string code, string name, string department, string phone)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Nhập mã và họ tên nhân viên.");
        Transact(actor, c => {
            Execute(c, "INSERT INTO employees(code,full_name,department,phone) VALUES($code,$name,$dept,$phone)",
            ("$code", code.Trim()), ("$name", name.Trim()), ("$dept", department.Trim()), ("$phone", phone.Trim()));
            Audit(c, actor, "CREATE", "EMPLOYEE", InsertId(c), name); return 0;
        });
    }
}
