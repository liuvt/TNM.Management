using Microsoft.Data.Sqlite;

namespace TNM.Management.Services;

public record V7OrderLineDraft(
    long ProductId,
    long Quantity,
    long? CustomUnitPrice = null,
    long? TargetActivationId = null,
    string? Imei = null,
    string? PlateNo = null,
    string? VinNo = null);

public record V7RenewalLineDraft(
    long ActivationId,
    long? CustomPriceVnd = null,
    string? CustomNewExpiry = null,
    string? NewPlateNo = null,
    string? NewOwnerName = null,
    string? NewOwnerPhone = null);

public record V7OrderLineReport(long LineId, string Imei, string PlateNo = "", string VinNo = "");
public record V7OrderLineEdit(long LineId, long? UnitPriceOverride = null, string Imei = "", string PlateNo = "", string VinNo = "");
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
    string VinNo,
    long? ActivationId = null,
    long? RenewalId = null,
    string PrevExpiry = "",
    string NewExpiry = "");

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
    long CostSnapshot = 0,
    bool NoInstallFee = false,
    long CustomerId = 0,
    bool IsAccountingLocked = false,
    string OrderKind = "INSTALL")
{
    public string OrderKindDisplay => OrderKind switch
    {
        "RENEWAL" => "Đơn gia hạn",
        "COMMERCIAL" => "Đơn thương mại",
        _ => "Đơn lắp đặt"
    };
    public string CustomerB2BDisplay => string.IsNullOrWhiteSpace(RepresentativeName) ? Customer : $"{Customer} (ĐD: {RepresentativeName})";
    public string DurationDisplay => DurationMonths == 12 ? "1 năm" : (DurationMonths > 0 && DurationMonths % 12 == 0) ? $"{DurationMonths / 12} năm" : $"{DurationMonths} tháng";
    public string InstallerTypeDisplay => OrderKind == "RENEWAL" ? "Gia hạn dịch vụ" : OrderKind == "COMMERCIAL" ? (Source == "PARTNER" ? "NCC / Hãng đi lắp" : "Bán thương mại") : (Source == "PARTNER" ? "Hãng lắp đặt" : "ĐV lắp đặt");
    public string StockEffectDisplay => OrderKind == "RENEWAL" ? "Không xuất kho (Gia hạn)" : (DeductStock ? "Trừ tồn kho" : "Không trừ tồn kho (Hãng)");
    public string InstallPriceModeDisplay => OrderKind == "RENEWAL" ? "Giá gia hạn" : (NoInstallFee || OrderKind == "COMMERCIAL" ? "Giá thương mại (Không công lắp)" : "Giá có lắp");
    public long GrossProfitSnapshot => Price - CostSnapshot - InstallerFee;
    public bool IsImmutable => Status == "COMPLETED";
    public bool HasMissingImei => OrderKind != "RENEWAL" && Lines.Any(l => l.IsSerialized && string.IsNullOrWhiteSpace(l.Imei));
    public int MissingImeiCount => OrderKind == "RENEWAL" ? 0 : Lines.Count(l => l.IsSerialized && string.IsNullOrWhiteSpace(l.Imei));
    public string StatusDisplay => Status switch
    {
        "PENDING" => OrderKind == "COMMERCIAL" ? "Chờ xuất hàng" : "Chờ điều phối",
        "APPROVED" => "Đã duyệt kho",
        "DISPATCHED" => OrderKind == "COMMERCIAL" ? "Đang giao hàng" : "Đang đi lắp đặt",
        "REPORTED" => "Đã báo IMEI",
        "COMPLETED" => HasMissingImei ? $"Đã hoàn thành (Chờ nhập {MissingImeiCount} IMEI)" : "Đã hoàn thành",
        "CANCELLED" => "Đã hủy",
        _ => Status
    };
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
    private T Tx<T>(Func<SqliteConnection, T> action) { using var c = Open(); Run(c, "BEGIN IMMEDIATE"); try { var r = action(c); Run(c, "COMMIT"); DemoDb.NotifyNavCountsChanged(); return r; } catch { Run(c, "ROLLBACK"); throw; } }

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
        if (!cols.Contains("no_install_fee")) Run(c, "ALTER TABLE v7_orders ADD COLUMN no_install_fee INTEGER NOT NULL DEFAULT 0");
        if (!cols.Contains("order_kind")) Run(c, "ALTER TABLE v7_orders ADD COLUMN order_kind TEXT NOT NULL DEFAULT 'INSTALL'");

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
              vin_no TEXT NOT NULL DEFAULT '',
              stock_unit_id INTEGER REFERENCES stock_units(id),
              activation_id INTEGER REFERENCES activations(id),
              renewal_id INTEGER REFERENCES renewals(id),
              prev_expiry TEXT NOT NULL DEFAULT '',
              new_expiry TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_v7_order_lines_order ON v7_order_lines(order_id);
            """);

        using (var lq = Cmd(c, "PRAGMA table_info(v7_order_lines)"))
        using (var lr = lq.ExecuteReader())
        {
            var lineCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (lr.Read()) lineCols.Add(lr.GetString(1));
            lr.Close();
            if (!lineCols.Contains("stock_unit_id"))
                Run(c, "ALTER TABLE v7_order_lines ADD COLUMN stock_unit_id INTEGER REFERENCES stock_units(id)");
            if (!lineCols.Contains("activation_id"))
                Run(c, "ALTER TABLE v7_order_lines ADD COLUMN activation_id INTEGER REFERENCES activations(id)");
            if (!lineCols.Contains("renewal_id"))
                Run(c, "ALTER TABLE v7_order_lines ADD COLUMN renewal_id INTEGER REFERENCES renewals(id)");
            if (!lineCols.Contains("prev_expiry"))
                Run(c, "ALTER TABLE v7_order_lines ADD COLUMN prev_expiry TEXT NOT NULL DEFAULT ''");
            if (!lineCols.Contains("new_expiry"))
                Run(c, "ALTER TABLE v7_order_lines ADD COLUMN new_expiry TEXT NOT NULL DEFAULT ''");
        }

        Run(c, """
            INSERT INTO v7_order_lines(order_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd,installer_fee_vnd,imei,plate_no,vin_no)
            SELECT o.id,o.product_id,o.quantity,o.unit_price_vnd,o.unit_cost_snapshot_vnd,COALESCE(o.installer_fee_vnd,0),o.imei,COALESCE(o.plate_no,''),COALESCE(o.vin_no,'')
            FROM v7_orders o
            WHERE NOT EXISTS (SELECT 1 FROM v7_order_lines l WHERE l.order_id=o.id);
            """);

        // Tạm gỡ trigger khóa đơn hoàn thành trước khi đồng bộ phân loại đơn cũ & ánh xạ lịch sử gia hạn thành Đơn gia hạn
        Run(c, """
            DROP TRIGGER IF EXISTS trg_v7_orders_lock_completed_update;
            DROP TRIGGER IF EXISTS trg_v7_order_lines_lock_completed_update;
            DROP TRIGGER IF EXISTS trg_v7_order_lines_lock_completed_insert;
            """);

        Run(c, @"UPDATE v7_orders
            SET order_kind = 'COMMERCIAL'
            WHERE COALESCE(order_kind,'INSTALL') = 'INSTALL'
              AND (COALESCE(no_install_fee,0) = 1 OR (source = 'PARTNER' AND COALESCE(installer_fee_vnd,0) = 0))");

        BackfillHistoricalRenewalsAsOrders(c);
        RepairCancelledAndDeletedOrdersStock(c);

        // Trigger khóa vĩnh viễn đơn hàng đã hoàn thành (COMPLETED) & giá snapshot cố định đối với user thường (cho phép Role Main điều chỉnh khi chưa chốt số, hoặc chuyển sang CANCELLED)
        Run(c, """
            CREATE TRIGGER trg_v7_orders_lock_completed_update
            BEFORE UPDATE OF code, customer_id, seller_id, seller_record_id, product_id, quantity, unit_price_vnd, unit_cost_snapshot_vnd, source, status, warehouse_id, installer, installer_employee_id, installer_fee_vnd, no_install_fee, contact_name, customer_phone, sale_source, duration_months, deduct_stock, collect_on_delivery, install_address, install_at, notes, completed_at ON v7_orders
            WHEN OLD.status = 'COMPLETED' AND NEW.status <> 'CANCELLED'
              AND LOWER(COALESCE((SELECT username FROM app_users WHERE id=NULLIF(tnm_actor(),0)),'')) <> 'vanlt_it'
            BEGIN
              SELECT RAISE(ABORT,'COMPLETED_ORDER_IMMUTABLE: Đơn hàng đã hoàn thành và khóa snapshot giá cố định, không thể thay đổi.');
            END;

            CREATE TRIGGER trg_v7_order_lines_lock_completed_update
            BEFORE UPDATE OF order_id, product_id, quantity, unit_price_vnd, unit_cost_snapshot_vnd, installer_fee_vnd ON v7_order_lines
            WHEN (SELECT status FROM v7_orders WHERE id = OLD.order_id) = 'COMPLETED'
              AND LOWER(COALESCE((SELECT username FROM app_users WHERE id=NULLIF(tnm_actor(),0)),'')) <> 'vanlt_it'
            BEGIN
              SELECT RAISE(ABORT,'COMPLETED_ORDER_LINE_IMMUTABLE: Dòng sản phẩm của đơn hàng đã hoàn thành và khóa snapshot giá cố định, không thể thay đổi.');
            END;

            CREATE TRIGGER trg_v7_order_lines_lock_completed_insert
            BEFORE INSERT ON v7_order_lines
            WHEN (SELECT status FROM v7_orders WHERE id = NEW.order_id) = 'COMPLETED'
              AND LOWER(COALESCE((SELECT username FROM app_users WHERE id=NULLIF(tnm_actor(),0)),'')) <> 'vanlt_it'
            BEGIN
              SELECT RAISE(ABORT,'COMPLETED_ORDER_LINE_IMMUTABLE: Đơn hàng đã hoàn thành, không thể thêm dòng sản phẩm mới.');
            END;

            DROP TRIGGER IF EXISTS trg_quantity_immutable_delete;
            CREATE TRIGGER trg_quantity_immutable_delete
            BEFORE DELETE ON quantity_movements
            WHEN LOWER(COALESCE((SELECT username FROM app_users WHERE id=NULLIF(tnm_actor(),0)),'')) <> 'vanlt_it'
            BEGIN
              SELECT RAISE(ABORT,'APPEND_ONLY_QUANTITY_LEDGER');
            END;
            """);

        if (cols.Contains("deleted_at"))
        {
            // Điều chỉnh tự động (idempotent) đơn SO7-000024 sang chế độ Không tính công lắp (Giá chưa lắp = 700,000đ thay vì Giá có lắp = 850,000đ)
            using (var fixQ = Cmd(c, "SELECT id, customer_id FROM v7_orders WHERE code='SO7-000024' AND deleted_at IS NULL AND status NOT IN ('COMPLETED','CANCELLED') AND COALESCE(no_install_fee,0)=0"))
            using (var fixR = fixQ.ExecuteReader())
            {
                if (fixR.Read())
                {
                    var so24Id = fixR.GetInt64(0);
                    var so24Cust = fixR.GetInt64(1);
                    fixR.Close();

                    var so24Lines = new List<(long LineId, long ProductId, long Qty)>();
                    using (var lq = Cmd(c, "SELECT id, product_id, quantity FROM v7_order_lines WHERE order_id=$o AND deleted_at IS NULL", ("$o", so24Id)))
                    using (var lr = lq.ExecuteReader())
                    {
                        while (lr.Read())
                            so24Lines.Add((lr.GetInt64(0), lr.GetInt64(1), lr.GetInt64(2)));
                    }

                    long newTotal = 0;
                    foreach (var ln in so24Lines)
                    {
                        var (stdUnitPrice, _) = QuoteOrderPrice(c, so24Cust, ln.ProductId, includeInstallPrice: false);
                        Run(c, "UPDATE v7_order_lines SET unit_price_vnd=$p, installer_fee_vnd=0 WHERE id=$id", ("$p", stdUnitPrice), ("$id", ln.LineId));
                        newTotal += stdUnitPrice * ln.Qty;
                    }
                    if (so24Lines.Count > 0)
                    {
                        Run(c, "UPDATE v7_orders SET no_install_fee=1, unit_price_vnd=$total, installer_fee_vnd=0 WHERE id=$id", ("$total", newTotal), ("$id", so24Id));
                    }
                }
            }

            // Điều chỉnh tự động (idempotent) đơn hoàn thành SO7-000010 sang chế độ Không tính công lắp (Giá chưa lắp = 620,000đ x 20 = 12,400,000đ) & đồng bộ Hóa đơn SALE phía sau khi chưa chốt số
            var so10NeedFix = Scalar(c, @"SELECT COALESCE((SELECT id FROM v7_orders o
                WHERE o.code='SO7-000010' AND o.deleted_at IS NULL AND o.status='COMPLETED' AND COALESCE(o.no_install_fee,0)=0
                  AND NOT EXISTS (SELECT 1 FROM invoices iv WHERE iv.v7_order_id=o.id AND iv.deleted_at IS NULL AND COALESCE(iv.is_locked,0)=1)
                LIMIT 1), 0)");
            if (so10NeedFix > 0)
            {
                var mainActorId = Scalar(c, "SELECT COALESCE((SELECT id FROM app_users WHERE is_active=1 AND LOWER(username)='vanlt_it' LIMIT 1), 0)");
                if (mainActorId > 0)
                {
                    long cId = 0, dur = 12;
                    long? whId = null, empId = null, sellerRecId = null;
                    bool cod = false, deduct = true, partner = false;
                    string contact = "", phone = "", addr = "", instAt = "", notes = "", installerName = "", saleSrc = "", actAt = "";
                    using (var o10Q = Cmd(c, @"SELECT customer_id, COALESCE(contact_name,''), COALESCE(customer_phone,''), COALESCE(install_address,''),
                        COALESCE(install_at,''), COALESCE(duration_months,12), COALESCE(notes,''), COALESCE(collect_on_delivery,0),
                        warehouse_id, source, COALESCE(installer,''), installer_employee_id, COALESCE(deduct_stock,1),
                        seller_record_id, COALESCE(sale_source,''), COALESCE(activated_at,'')
                        FROM v7_orders WHERE id=$id", ("$id", so10NeedFix)))
                    using (var o10R = o10Q.ExecuteReader())
                    {
                        if (o10R.Read())
                        {
                            cId = o10R.GetInt64(0);
                            contact = o10R.GetString(1);
                            phone = o10R.GetString(2);
                            addr = o10R.GetString(3);
                            instAt = o10R.GetString(4);
                            dur = o10R.GetInt64(5);
                            notes = o10R.GetString(6);
                            cod = o10R.GetInt64(7) == 1;
                            whId = o10R.IsDBNull(8) ? null : o10R.GetInt64(8);
                            partner = o10R.GetString(9) == "PARTNER";
                            installerName = o10R.GetString(10);
                            empId = o10R.IsDBNull(11) ? null : o10R.GetInt64(11);
                            deduct = o10R.GetInt64(12) == 1;
                            sellerRecId = o10R.IsDBNull(13) ? null : o10R.GetInt64(13);
                            saleSrc = o10R.GetString(14);
                            actAt = o10R.GetString(15);
                        }
                    }
                    c.Close();
                    UpdateCompletedOrderMain(
                        mainActorId, so10NeedFix, cId, contact, phone, addr, instAt, dur, notes, cod,
                        whId, partner, installerName, empId, deduct, noInstallFee: true, sellerRecId, saleSrc, actAt, null);
                    return;
                }
            }
        }
    }

    private void BackfillHistoricalRenewalsAsOrders(SqliteConnection c)
    {
        var renCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var rq = Cmd(c, "PRAGMA table_info(renewals)"))
        using (var rr = rq.ExecuteReader())
        {
            while (rr.Read()) renCols.Add(rr.GetString(1));
        }
        if (!renCols.Contains("v7_order_id")) return;

        var renDelFilter = renCols.Contains("deleted_at") ? "AND r.deleted_at IS NULL" : "";
        var actCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var aq = Cmd(c, "PRAGMA table_info(activations)"))
        using (var ar = aq.ExecuteReader())
        {
            while (ar.Read()) actCols.Add(ar.GetString(1));
        }
        var actDelFilter = actCols.Contains("deleted_at") ? "AND a.deleted_at IS NULL" : "";
        var hasOwnerCols = actCols.Contains("owner_name") && actCols.Contains("owner_phone");

        var defaultActorId = Scalar(c, "SELECT COALESCE((SELECT id FROM app_users WHERE LOWER(username)='vanlt_it' LIMIT 1), (SELECT id FROM app_users ORDER BY id LIMIT 1), 0)");
        if (defaultActorId <= 0) return;

        var unlinked = new List<(long RenId, long ActId, long CustId, long ProdId, long StockUnitId, long? SellerId, string SellerName, string SrcType, int DurMonths, string RenAt, string PrevExp, string NewExp, long Amt, string PayMethod, string Imei, string Plate, string Vin, string OwnerName, string OwnerPhone)>();
        var ownerSelect = hasOwnerCols ? "COALESCE(a.owner_name,''), COALESCE(a.owner_phone,'')" : "'', ''";
        using (var q = Cmd(c, $@"
            SELECT r.id, r.activation_id, COALESCE(r.customer_id, a.customer_id), a.product_id, a.stock_unit_id,
                   r.seller_id, COALESCE(r.seller_name,''), r.source_type, COALESCE(r.duration_months, 12),
                   r.renewed_at, r.previous_expiry, r.new_expiry, r.amount_vnd, r.payment_method,
                   u.imei, COALESCE(a.plate_no,''), COALESCE(a.vin_no,''), {ownerSelect}
            FROM renewals r
            JOIN activations a ON a.id = r.activation_id
            JOIN stock_units u ON u.id = a.stock_unit_id
            WHERE r.v7_order_id IS NULL {renDelFilter} {actDelFilter}
            ORDER BY r.id ASC"))
        using (var r = q.ExecuteReader())
        {
            while (r.Read())
            {
                unlinked.Add((
                    r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4),
                    r.IsDBNull(5) ? null : r.GetInt64(5), r.GetString(6), r.GetString(7), r.GetInt32(8),
                    r.GetString(9), r.GetString(10), r.GetString(11), r.GetInt64(12), r.GetString(13),
                    r.GetString(14), r.GetString(15), r.GetString(16), r.GetString(17), r.GetString(18)));
            }
        }
        if (unlinked.Count == 0) return;

        using var scope = AuditRuntime.As(defaultActorId);
        foreach (var item in unlinked)
        {
            DateTime? renDt = DateTime.TryParse(item.RenAt, out var parsed) ? parsed : null;
            DateTime? prevDt = DateTime.TryParse(item.PrevExp, out var pp) ? pp : null;
            DateTime? nextDt = DateTime.TryParse(item.NewExp, out var np) ? np : null;
            var estCost = GroupPriceResolver.ResolveRenewalCostForDuration(c, null, item.CustId, item.ProdId, item.DurMonths, prevDt, nextDt, renDt);

            Run(c, @"INSERT INTO v7_orders(code,order_kind,customer_id,seller_id,seller_record_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd,source,status,installer,installer_fee_vnd,no_install_fee,contact_name,customer_phone,sale_source,duration_months,deduct_stock,collect_on_delivery,plate_no,vin_no,notes,imei,activated_at,completed_at)
                VALUES($tmpCode,'RENEWAL',$c,$actor,$sellerRec,$p,1,$price,$cost,'INTERNAL','COMPLETED','Gia hạn dịch vụ',0,1,$contact,$phone,$src,$dur,0,$cod,$plate,$vin,$note,$imei,$act,$comp)",
                ("$tmpCode", "TMP-GH-" + Guid.NewGuid().ToString("N")[..12]),
                ("$c", item.CustId),
                ("$actor", defaultActorId),
                ("$sellerRec", item.SellerId),
                ("$p", item.ProdId),
                ("$price", item.Amt),
                ("$cost", estCost),
                ("$contact", item.OwnerName),
                ("$phone", item.OwnerPhone),
                ("$src", item.SellerName),
                ("$dur", item.DurMonths > 0 ? item.DurMonths : 12),
                ("$cod", item.PayMethod == "CASH" ? 1 : 0),
                ("$plate", item.Plate),
                ("$vin", item.Vin),
                ("$note", $"Đơn gia hạn ({item.SrcType}): {item.PrevExp} → {item.NewExp}"),
                ("$imei", item.Imei),
                ("$act", item.RenAt),
                ("$comp", item.RenAt.Length <= 10 ? item.RenAt + " 12:00:00" : item.RenAt));
            var newOrdId = Scalar(c, "SELECT last_insert_rowid()");
            Run(c, "UPDATE v7_orders SET code=$code WHERE id=$id", ("$code", $"GH7-{newOrdId:000000}"), ("$id", newOrdId));

            Run(c, @"INSERT INTO v7_order_lines(order_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd,installer_fee_vnd,imei,plate_no,vin_no,stock_unit_id,activation_id,renewal_id,prev_expiry,new_expiry)
                VALUES($o,$p,1,$price,$cost,0,$imei,$plate,$vin,$sid,$aid,$rid,$prev,$next)",
                ("$o", newOrdId), ("$p", item.ProdId), ("$price", item.Amt), ("$cost", estCost),
                ("$imei", item.Imei), ("$plate", item.Plate), ("$vin", item.Vin),
                ("$sid", item.StockUnitId), ("$aid", item.ActId), ("$rid", item.RenId),
                ("$prev", item.PrevExp), ("$next", item.NewExp));

            Run(c, "UPDATE renewals SET v7_order_id=$o WHERE id=$r", ("$o", newOrdId), ("$r", item.RenId));
            Run(c, "UPDATE invoices SET v7_order_id=$o WHERE renewal_id=$r AND v7_order_id IS NULL", ("$o", newOrdId), ("$r", item.RenId));
        }
    }

    private static bool HasCol(SqliteConnection c, string table, string col)
    {
        using var q = Cmd(c, $"PRAGMA table_info({table})");
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), col, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void UpsertStockEffect(SqliteConnection c, long orderId, long stockUnitId)
    {
        if (stockUnitId <= 0) return;
        var existingId = Scalar(c, "SELECT COALESCE((SELECT id FROM v7_stock_effects WHERE stock_unit_id=$s LIMIT 1), 0)", ("$s", stockUnitId));
        if (existingId > 0)
        {
            if (HasCol(c, "v7_stock_effects", "deleted_at"))
                Run(c, "UPDATE v7_stock_effects SET order_id=$o, deleted_at=NULL, deleted_by=NULL WHERE id=$id", ("$o", orderId), ("$id", existingId));
            else
                Run(c, "UPDATE v7_stock_effects SET order_id=$o WHERE id=$id", ("$o", orderId), ("$id", existingId));
        }
        else
        {
            Run(c, "INSERT INTO v7_stock_effects(order_id,stock_unit_id) VALUES($o,$s)", ("$o", orderId), ("$s", stockUnitId));
        }
    }

    private static void DetachStockEffectToArchive(SqliteConnection c, long stockUnitId, long? actor)
    {
        if (stockUnitId <= 0) return;
        var effId = Scalar(c, "SELECT COALESCE((SELECT id FROM v7_stock_effects WHERE stock_unit_id=$s LIMIT 1), 0)", ("$s", stockUnitId));
        if (effId <= 0) return;

        using var uq = Cmd(c, "SELECT product_id, imei, customer_id FROM stock_units WHERE id=$s", ("$s", stockUnitId));
        using var ur = uq.ExecuteReader();
        if (!ur.Read()) return;
        var prodId = ur.GetInt64(0);
        var imei = ur.GetString(1);
        long? custId = ur.IsDBNull(2) ? null : ur.GetInt64(2);
        ur.Close();

        var nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var archiveImei = $"{imei}#EFFDEL#{effId}_{Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}";
        if (HasCol(c, "stock_units", "deleted_at"))
        {
            Run(c, @"INSERT INTO stock_units(product_id,imei,status,warehouse_id,customer_id,employee_id,created_at,updated_at,deleted_at,deleted_by)
                VALUES($p,$imei,'INSTALLED',NULL,$c,NULL,datetime('now'),datetime('now'),$now,$actor)",
                ("$p", prodId), ("$imei", archiveImei), ("$c", custId), ("$now", nowIso), ("$actor", actor is > 0 ? actor : null));
        }
        else
        {
            Run(c, "INSERT INTO stock_units(product_id,imei,status,warehouse_id,customer_id,employee_id) VALUES($p,$imei,'INSTALLED',NULL,$c,NULL)",
                ("$p", prodId), ("$imei", archiveImei), ("$c", custId));
        }
        var archId = Scalar(c, "SELECT last_insert_rowid()");
        if (HasCol(c, "v7_stock_effects", "deleted_at"))
        {
            Run(c, "UPDATE v7_stock_effects SET stock_unit_id=$arch, deleted_at=COALESCE(deleted_at,$now), deleted_by=COALESCE(deleted_by,$actor) WHERE id=$id",
                ("$arch", archId), ("$now", nowIso), ("$actor", actor is > 0 ? actor : null), ("$id", effId));
        }
        else
        {
            Run(c, "UPDATE v7_stock_effects SET stock_unit_id=$arch WHERE id=$id", ("$arch", archId), ("$id", effId));
        }
    }

    /// <summary>
    /// Kiểm tra trùng số IMEI chuẩn xác:
    /// - BỎ QUA hoàn toàn đơn CANCELLED, đơn/dòng đã xóa mềm (deleted_at IS NOT NULL) và thiết bị đã xóa mềm.
    /// - Chỉ báo trùng khi: (1) IMEI đang kích hoạt thực tế trên xe ở bảng Thiết bị, hoặc (2) IMEI đang nằm trên 1 đơn đang xử lý khác chưa hủy.
    /// </summary>
    private static void ValidateImeiNotActiveOrPendingOnOtherOrder(SqliteConnection c, string? rawImei, long excludeOrderId)
    {
        var clean = (rawImei ?? "").Trim();
        if (string.IsNullOrWhiteSpace(clean)) return;

        var actDelCond = HasCol(c, "activations", "deleted_at") ? "AND a.deleted_at IS NULL" : "";
        var suDelCond = HasCol(c, "stock_units", "deleted_at") ? "AND u.deleted_at IS NULL" : "";
        var ordDelCond = HasCol(c, "v7_orders", "deleted_at") ? "AND o.deleted_at IS NULL" : "";
        var lineDelCond = HasCol(c, "v7_order_lines", "deleted_at") ? "AND l.deleted_at IS NULL" : "";

        // 1. Đã kích hoạt thực tế trên bảng Thiết bị (không tính xe khai báo sẵn chưa gắn IMEI hoặc đơn đã hủy/xóa)
        using (var aq = Cmd(c, $@"
            SELECT a.id, COALESCE(a.plate_no,''), COALESCE(cu.name,''), COALESCE(o.code,''), COALESCE(o.id,0)
            FROM activations a
            JOIN stock_units u ON u.id = a.stock_unit_id
            LEFT JOIN customers cu ON cu.id = a.customer_id
            LEFT JOIN v7_orders o ON o.id = a.v7_order_id
            WHERE UPPER(TRIM(u.imei)) = UPPER($imei)
              AND COALESCE(a.is_pre_declared, 0) = 0
              {actDelCond} {suDelCond}
              AND (a.v7_order_id IS NULL OR (o.status <> 'CANCELLED' {(HasCol(c, "v7_orders", "deleted_at") ? "AND o.deleted_at IS NULL" : "")}))
            LIMIT 1", ("$imei", clean)))
        using (var ar = aq.ExecuteReader())
        {
            if (ar.Read())
            {
                var ordId = ar.GetInt64(4);
                if (ordId != excludeOrderId)
                {
                    var plate = ar.GetString(1);
                    var cust = ar.GetString(2);
                    var ordCode = ar.GetString(3);
                    var plateInfo = string.IsNullOrWhiteSpace(plate) ? "" : $" (Biển số: {plate})";
                    var ordInfo = string.IsNullOrWhiteSpace(ordCode) ? "" : $" thuộc đơn {ordCode}";
                    throw new InvalidOperationException($"Mã IMEI '{clean}' đã được kích hoạt sử dụng cho [{cust}]{plateInfo}{ordInfo}.");
                }
            }
        }

        // 2. Đang được nhập chờ hoàn thành trên một đơn hàng đang xử lý khác (chưa hủy, chưa xóa mềm)
        using (var pq = Cmd(c, $@"
            SELECT o.code, COALESCE(cu.name,''), o.status
            FROM v7_order_lines l
            JOIN v7_orders o ON o.id = l.order_id
            LEFT JOIN customers cu ON cu.id = o.customer_id
            WHERE l.order_id <> $excl
              AND UPPER(TRIM(COALESCE(l.imei,''))) = UPPER($imei)
              AND o.status NOT IN ('CANCELLED', 'COMPLETED')
              AND COALESCE(o.order_kind, 'INSTALL') <> 'RENEWAL'
              {ordDelCond} {lineDelCond}
            ORDER BY o.id DESC LIMIT 1", ("$excl", excludeOrderId), ("$imei", clean)))
        using (var pr = pq.ExecuteReader())
        {
            if (pr.Read())
            {
                var otherCode = pr.GetString(0);
                var otherCust = pr.GetString(1);
                throw new InvalidOperationException($"Mã IMEI '{clean}' đang được gắn chờ xử lý trên đơn hàng {otherCode} ({otherCust}). Vui lòng kiểm tra, gỡ IMEI hoặc hủy đơn {otherCode} trước.");
            }
        }
    }

    /// <summary>
    /// Nếu một stock_unit có IMEI thật trong kho đang bị một đơn 'CHỜ-NHẬP-IMEI' (l.imei IS NULL) giữ chỗ tạm để trừ số lượng tồn kho
    /// (chưa hề có bản ghi kích hoạt activations), tự động chuyển slot trừ kho tạm của đơn kia sang một dòng IN_STOCK rảnh khác,
    /// trả mã IMEI thật này về lại trạng thái IN_STOCK tại kho để đơn hiện tại sử dụng bình thường.
    /// </summary>
    private static bool ReclaimAnonymouslyHeldStockUnitIfNeeded(SqliteConnection c, long stockUnitId, long preferredWh, long? actor = null, IReadOnlySet<string>? excludeImeis = null)
    {
        if (stockUnitId <= 0) return false;
        var suDelCond = HasCol(c, "stock_units", "deleted_at") ? "AND deleted_at IS NULL" : "";
        var actDelCond = HasCol(c, "activations", "deleted_at") ? "AND deleted_at IS NULL" : "";
        var ordDelCond = HasCol(c, "v7_orders", "deleted_at") ? "AND o.deleted_at IS NULL" : "";
        var lineDelCond = HasCol(c, "v7_order_lines", "deleted_at") ? "AND l.deleted_at IS NULL" : "";
        var tlDelCond = HasCol(c, "inventory_transaction_lines", "deleted_at") ? "AND deleted_at IS NULL" : "";

        var curStatus = Str(c, $"SELECT COALESCE((SELECT status FROM stock_units WHERE id=$id {suDelCond}), '')", ("$id", stockUnitId));
        if (curStatus != "INSTALLED") return curStatus is "IN_STOCK" or "CONSIGNED" or "TECHNICIAN";

        // Nếu đã có kích hoạt thực tế trên bảng Thiết bị thì KHÔNG được giải phóng
        if (Scalar(c, $"SELECT COUNT(*) FROM activations WHERE stock_unit_id=$s AND COALESCE(is_pre_declared,0)=0 {actDelCond}", ("$s", stockUnitId)) > 0)
            return false;

        var defaultWh = preferredWh > 0
            ? preferredWh
            : Scalar(c, "SELECT COALESCE((SELECT id FROM warehouses WHERE deleted_at IS NULL ORDER BY id LIMIT 1), 1)");

        // Kiểm tra xem stock_unit này có đang bị giữ bởi dòng đơn 'CHỜ-NHẬP-IMEI' (l.imei IS NULL) hay không
        long anonLineId = 0, anonOrderId = 0, anonCustId = 0, anonWh = defaultWh, unitProdId = 0;
        using (var lq = Cmd(c, $@"
            SELECT l.id, l.order_id, o.customer_id, COALESCE(o.warehouse_id, $w), u.product_id
            FROM v7_order_lines l
            JOIN v7_orders o ON o.id = l.order_id
            JOIN stock_units u ON u.id = l.stock_unit_id
            WHERE l.stock_unit_id = $s
              AND o.status = 'COMPLETED'
              AND (l.imei IS NULL OR TRIM(l.imei) = '')
              {ordDelCond} {lineDelCond}
            ORDER BY l.id DESC LIMIT 1", ("$s", stockUnitId), ("$w", defaultWh)))
        using (var lr = lq.ExecuteReader())
        {
            if (lr.Read())
            {
                anonLineId = lr.GetInt64(0);
                anonOrderId = lr.GetInt64(1);
                anonCustId = lr.GetInt64(2);
                anonWh = lr.GetInt64(3) > 0 ? lr.GetInt64(3) : defaultWh;
                unitProdId = lr.GetInt64(4);
            }
        }

        if (anonLineId > 0)
        {
            // Tìm 1 thiết bị IN_STOCK khác cùng product_id chưa bị gõ trên bất kỳ đơn chờ xử lý nào (và không nằm trong danh sách excludeImeis) để thế chỗ slot trừ kho tạm
            long replacementId = 0;
            using (var rq = Cmd(c, $@"
                SELECT u2.id, u2.imei FROM stock_units u2
                WHERE u2.product_id = $p
                  AND u2.id <> $sid
                  AND u2.status = 'IN_STOCK'
                  {(HasCol(c, "stock_units", "deleted_at") ? "AND u2.deleted_at IS NULL" : "")}
                  AND NOT EXISTS (
                      SELECT 1 FROM v7_order_lines lp
                      JOIN v7_orders op ON op.id = lp.order_id
                      WHERE UPPER(TRIM(COALESCE(lp.imei,''))) = UPPER(TRIM(u2.imei))
                        AND op.status <> 'COMPLETED'
                        {(HasCol(c, "v7_orders", "deleted_at") ? "AND op.deleted_at IS NULL" : "")}
                        {(HasCol(c, "v7_order_lines", "deleted_at") ? "AND lp.deleted_at IS NULL" : "")}
                  )
                ORDER BY CASE WHEN u2.warehouse_id = $w THEN 0 ELSE 1 END,
                         CASE WHEN u2.imei LIKE 'KHO-%' THEN 0 ELSE 1 END,
                         u2.id DESC", ("$p", unitProdId), ("$sid", stockUnitId), ("$w", anonWh)))
            using (var rr = rq.ExecuteReader())
            {
                while (rr.Read())
                {
                    var candId = rr.GetInt64(0);
                    var candImei = rr.GetString(1).Trim();
                    if (excludeImeis is not null && excludeImeis.Contains(candImei))
                        continue;
                    replacementId = candId;
                    break;
                }
            }

            if (replacementId <= 0)
                return false;

            if (Scalar(c, "SELECT COUNT(*) FROM v7_stock_effects WHERE stock_unit_id=$rep", ("$rep", replacementId)) > 0)
            {
                DetachStockEffectToArchive(c, replacementId, actor);
            }

            Run(c, "UPDATE stock_units SET status='INSTALLED', warehouse_id=NULL, customer_id=$c, employee_id=NULL, updated_at=CURRENT_TIMESTAMP WHERE id=$rep",
                ("$c", anonCustId), ("$rep", replacementId));
            Run(c, "UPDATE v7_order_lines SET stock_unit_id=$rep WHERE id=$lid",
                ("$rep", replacementId), ("$lid", anonLineId));
            Run(c, "UPDATE v7_stock_effects SET stock_unit_id=$rep WHERE stock_unit_id=$sid",
                ("$rep", replacementId), ("$sid", stockUnitId));
            Run(c, $@"UPDATE inventory_transaction_lines SET stock_unit_id=$rep
                WHERE stock_unit_id=$sid {tlDelCond}
                  AND transaction_id IN (SELECT id FROM inventory_transactions WHERE movement_type='SALE_OUT')
                  AND NOT EXISTS (SELECT 1 FROM inventory_transaction_lines x WHERE x.transaction_id=inventory_transaction_lines.transaction_id AND x.stock_unit_id=$rep)",
                ("$rep", replacementId), ("$sid", stockUnitId));
            Run(c, "UPDATE stock_units SET status='IN_STOCK', warehouse_id=$w, customer_id=NULL, employee_id=NULL, updated_at=CURRENT_TIMESTAMP WHERE id=$sid",
                ("$w", anonWh), ("$sid", stockUnitId));
            return true;
        }

        // Trường hợp mồ côi: status='INSTALLED', không có activations và cũng không gắn với đơn COMPLETED nào đang hoạt động -> hoàn về IN_STOCK
        var activeCompletedHold = Scalar(c, $@"
            SELECT COUNT(*) FROM v7_order_lines l
            JOIN v7_orders o ON o.id = l.order_id
            WHERE (l.stock_unit_id = $s OR UPPER(TRIM(COALESCE(l.imei,''))) = UPPER((SELECT imei FROM stock_units WHERE id=$s)))
              AND o.status = 'COMPLETED'
              {ordDelCond} {lineDelCond}", ("$s", stockUnitId));

        if (activeCompletedHold == 0)
        {
            DetachStockEffectToArchive(c, stockUnitId, actor);
            Run(c, "UPDATE stock_units SET status='IN_STOCK', warehouse_id=$w, customer_id=NULL, employee_id=NULL, updated_at=CURRENT_TIMESTAMP WHERE id=$sid",
                ("$w", defaultWh), ("$sid", stockUnitId));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Chạy idempotent lúc khởi động:
    /// 1) Đối với mọi đơn hàng đã HỦY (CANCELLED) hoặc đã XÓA MỀM (deleted_at IS NOT NULL) mà vẫn còn lưu IMEI / giữ thiết bị (VD: SO7-000019 gồm 10 IMEI):
    ///    - Giải phóng toàn bộ các IMEI chưa kích hoạt về lại trạng thái IN_STOCK tại Kho để dùng cho đơn khác.
    ///    - Xóa liên kết IMEI / stock_unit_id trên đơn hủy (lưu lại ghi chú đối chiếu vào cột notes).
    /// 2) Đối với các đơn đang chờ hoàn thành (như SO7-000026, SO7-000016) có mã IMEI đang bị đơn 'CHỜ-NHẬP-IMEI' giữ chỗ tạm:
    ///    - Tự động chuyển slot tạm để trả IMEI thật về IN_STOCK sẵn sàng bấm Hoàn thành ngay.
    /// </summary>
    private void RepairCancelledAndDeletedOrdersStock(SqliteConnection c)
    {
        if (!HasCol(c, "v7_orders", "status") || !HasCol(c, "v7_orders", "deleted_at") || !HasCol(c, "warehouses", "deleted_at") || !HasCol(c, "stock_units", "deleted_at") || !HasCol(c, "v7_order_lines", "imei")) return;
        var mainId = Scalar(c, "SELECT COALESCE((SELECT id FROM app_users WHERE LOWER(username)='vanlt_it' LIMIT 1), 0)");
        using var scope = mainId > 0 ? AuditRuntime.As(mainId) : null;

        var hasOrdDel = HasCol(c, "v7_orders", "deleted_at");
        var hasLineDel = HasCol(c, "v7_order_lines", "deleted_at");
        var defaultWh = Scalar(c, "SELECT COALESCE((SELECT id FROM warehouses WHERE deleted_at IS NULL ORDER BY id LIMIT 1), 1)");

        var cancelledCond = hasOrdDel
            ? "(o.status = 'CANCELLED' OR o.deleted_at IS NOT NULL)"
            : "o.status = 'CANCELLED'";

        var cancelledOrders = new List<(long OrderId, long WhId, string Notes)>();
        using (var oq = Cmd(c, $@"
            SELECT DISTINCT o.id, COALESCE(o.warehouse_id, $w), COALESCE(o.notes, '')
            FROM v7_orders o
            LEFT JOIN v7_order_lines l ON l.order_id = o.id
            WHERE {cancelledCond}
              AND COALESCE(o.order_kind, 'INSTALL') <> 'RENEWAL'
              AND (
                  (o.imei IS NOT NULL AND TRIM(o.imei) <> '' AND o.imei <> 'CHỜ-NHẬP-IMEI')
                  OR (l.imei IS NOT NULL AND TRIM(l.imei) <> '')
                  OR l.stock_unit_id IS NOT NULL
                  OR EXISTS (SELECT 1 FROM v7_stock_effects e WHERE e.order_id = o.id {(HasCol(c, "v7_stock_effects", "deleted_at") ? "AND e.deleted_at IS NULL" : "")})
              )", ("$w", defaultWh)))
        using (var or = oq.ExecuteReader())
        {
            while (or.Read())
                cancelledOrders.Add((or.GetInt64(0), or.GetInt64(1) > 0 ? or.GetInt64(1) : defaultWh, or.GetString(2)));
        }

        // Thu thập toàn bộ danh sách IMEI cần bảo vệ (từ đơn hủy/xóa mềm + đơn đang chờ xử lý) để khi đảo slot tạm không chạm vào các IMEI này
        var protectedImeis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var protQ = Cmd(c, $@"
            SELECT DISTINCT TRIM(l.imei)
            FROM v7_order_lines l
            JOIN v7_orders o ON o.id = l.order_id
            WHERE COALESCE(o.order_kind, 'INSTALL') <> 'RENEWAL'
              AND l.imei IS NOT NULL AND TRIM(l.imei) <> '' AND l.imei <> 'CHỜ-NHẬP-IMEI'
              AND ({cancelledCond} OR o.status NOT IN ('CANCELLED', 'COMPLETED'))"))
        using (var protR = protQ.ExecuteReader())
        {
            while (protR.Read())
                protectedImeis.Add(protR.GetString(0));
        }

        foreach (var ord in cancelledOrders)
        {
            var releasedImeis = new List<string>();
            var lineUnits = new List<(long StockUnitId, string Imei)>();
            using (var lq = Cmd(c, "SELECT COALESCE(stock_unit_id, 0), COALESCE(imei, '') FROM v7_order_lines WHERE order_id=$o", ("$o", ord.OrderId)))
            using (var lr = lq.ExecuteReader())
            {
                while (lr.Read())
                    lineUnits.Add((lr.GetInt64(0), lr.GetString(1).Trim()));
            }

            foreach (var (sid, imei) in lineUnits)
            {
                if (!string.IsNullOrWhiteSpace(imei) && imei != "CHỜ-NHẬP-IMEI")
                    releasedImeis.Add(imei);

                var resolvedSid = sid > 0
                    ? sid
                    : (!string.IsNullOrWhiteSpace(imei)
                        ? Scalar(c, "SELECT COALESCE((SELECT id FROM stock_units WHERE UPPER(TRIM(imei))=UPPER($i) AND deleted_at IS NULL LIMIT 1), 0)", ("$i", imei))
                        : 0);

                if (resolvedSid > 0)
                {
                    ReclaimAnonymouslyHeldStockUnitIfNeeded(c, resolvedSid, ord.WhId, mainId > 0 ? mainId : null, protectedImeis);
                }
            }

            // Giải phóng các v7_stock_effects của chính đơn hủy này (nếu có)
            var ownEffectUnits = new List<long>();
            using (var eq = Cmd(c, "SELECT stock_unit_id FROM v7_stock_effects WHERE order_id=$o", ("$o", ord.OrderId)))
            using (var er = eq.ExecuteReader())
            {
                while (er.Read()) ownEffectUnits.Add(er.GetInt64(0));
            }
            foreach (var esid in ownEffectUnits)
            {
                DetachStockEffectToArchive(c, esid, mainId > 0 ? mainId : null);
                if (Scalar(c, "SELECT COUNT(*) FROM activations WHERE stock_unit_id=$s AND deleted_at IS NULL", ("$s", esid)) == 0)
                {
                    Run(c, "UPDATE stock_units SET status='IN_STOCK', warehouse_id=$w, customer_id=NULL, employee_id=NULL, updated_at=CURRENT_TIMESTAMP WHERE id=$s",
                        ("$w", ord.WhId), ("$s", esid));
                }
            }

            var nextNotes = ord.Notes;
            if (releasedImeis.Count > 0 && !nextNotes.Contains("Đã hoàn kho IMEI", StringComparison.OrdinalIgnoreCase))
            {
                var imeiListStr = string.Join(", ", releasedImeis.Distinct(StringComparer.OrdinalIgnoreCase));
                nextNotes = string.IsNullOrWhiteSpace(nextNotes)
                    ? $"[Đã hoàn kho IMEI khi hủy: {imeiListStr}]"
                    : $"{nextNotes} · [Đã hoàn kho IMEI khi hủy: {imeiListStr}]";
            }

            Run(c, "UPDATE v7_order_lines SET imei=NULL, stock_unit_id=NULL, activation_id=NULL WHERE order_id=$o", ("$o", ord.OrderId));
            Run(c, "UPDATE v7_orders SET imei=NULL, notes=$n WHERE id=$o", ("$n", nextNotes), ("$o", ord.OrderId));
        }

        // Giải phóng trước các mã IMEI đang gõ trên các đơn chờ xử lý (như SO7-000026, SO7-000016) bị đơn 'CHỜ-NHẬP-IMEI' mượn tạm slot
        var pendingImeis = new List<(string Imei, long WhId)>();
        using (var pq = Cmd(c, $@"
            SELECT DISTINCT TRIM(l.imei), COALESCE(o.warehouse_id, $w)
            FROM v7_order_lines l
            JOIN v7_orders o ON o.id = l.order_id
            WHERE o.status NOT IN ('CANCELLED', 'COMPLETED')
              AND COALESCE(o.order_kind, 'INSTALL') <> 'RENEWAL'
              AND l.imei IS NOT NULL AND TRIM(l.imei) <> ''
              {(hasOrdDel ? "AND o.deleted_at IS NULL" : "")}
              {(hasLineDel ? "AND l.deleted_at IS NULL" : "")}", ("$w", defaultWh)))
        using (var pr = pq.ExecuteReader())
        {
            while (pr.Read())
                pendingImeis.Add((pr.GetString(0), pr.GetInt64(1) > 0 ? pr.GetInt64(1) : defaultWh));
        }
        foreach (var (pImei, pWh) in pendingImeis)
        {
            var sid = Scalar(c, "SELECT COALESCE((SELECT id FROM stock_units WHERE UPPER(TRIM(imei))=UPPER($i) AND deleted_at IS NULL LIMIT 1), 0)", ("$i", pImei));
            if (sid > 0)
                ReclaimAnonymouslyHeldStockUnitIfNeeded(c, sid, pWh, mainId > 0 ? mainId : null, protectedImeis);
        }
    }

    private static bool IsMainDeveloper(SqliteConnection c, long actor) =>
        actor > 0 && Scalar(c, "SELECT COUNT(*) FROM app_users WHERE id=$u AND is_active=1 AND LOWER(username)='vanlt_it'", ("$u", actor)) > 0;

    public bool IsMainDeveloperActor(long actor)
    {
        if (actor <= 0) return false;
        using var c = Open();
        return IsMainDeveloper(c, actor);
    }

    private static bool IsAdmin(SqliteConnection c, long actor) =>
        actor > 0 && (
            Scalar(c, "SELECT COUNT(*) FROM app_users WHERE id=$u AND is_active=1 AND (is_owner=1 OR LOWER(username)='vanlt_it')", ("$u", actor)) > 0 ||
            Scalar(c, "SELECT COUNT(*) FROM user_roles ur JOIN roles r ON r.id=ur.role_id WHERE ur.user_id=$u AND ur.deleted_at IS NULL AND r.code='ADMIN'", ("$u", actor)) > 0
        );

    public bool IsAdminActor(long actor)
    {
        if (actor <= 0) return false;
        using var c = Open();
        return IsAdmin(c, actor);
    }

    private static void Role(SqliteConnection c, long actor, params string[] roles)
    {
        if (Scalar(c, "SELECT COUNT(*) FROM app_users WHERE id=$u AND is_active=1 AND (is_owner=1 OR LOWER(username)='vanlt_it')", ("$u", actor)) > 0) return;
        foreach (var role in roles)
            if (Scalar(c, "SELECT COUNT(*) FROM user_roles ur JOIN roles r ON r.id=ur.role_id WHERE ur.user_id=$u AND ur.deleted_at IS NULL AND r.code=$r", ("$u", actor), ("$r", role)) > 0) return;
        throw new UnauthorizedAccessException("Tài khoản không có vai trò phù hợp.");
    }

    private static void Event(SqliteConnection c, long id, long actor, string name) =>
        Run(c, "INSERT INTO v7_order_events(order_id,actor_id,event) VALUES($o,$a,$e)", ("$o", id), ("$a", actor), ("$e", name));

    private static (long Price, long Cost) QuoteOrderPrice(SqliteConnection c, long customerId, long productId, bool includeInstallPrice = true, long durationMonths = 12)
    {
        var dayVn = VnDateTimeHelper.TodayVn.ToString("yyyy-MM-dd");
        var cost = Scalar(c, @"SELECT COALESCE(
            (SELECT gp.unit_cost_vnd FROM v7_supplier_group_prices gp WHERE gp.product_id=$p AND gp.deleted_at IS NULL AND gp.unit_cost_vnd>0 AND gp.effective_from<=$d ORDER BY gp.effective_from DESC,gp.id DESC LIMIT 1),
            NULLIF((SELECT base_cost_vnd FROM products WHERE id=$p), 0),
            0)", ("$p", productId), ("$d", dayVn));

        // Luồng chuẩn: Nhóm con HTX (sub_group_id) -> Nhóm cha (parent_id / group_id) -> Giá sản phẩm khai báo gốc (+ tự tính chuẩn theo thời hạn 6T / 12T / 24T / 36T)
        var mode = includeInstallPrice ? "INSTALLED" : "STANDARD";
        var unitPrice = GroupPriceResolver.ResolveOrderUnitPriceForDuration(c, null, customerId, productId, mode, durationMonths);
        return (unitPrice, cost);
    }

    public long PreviewUnitPrice(long customerId, long productId, bool includeInstallPrice = true, long durationMonths = 12)
    {
        if (customerId <= 0 || productId <= 0) return 0;
        using var c = Open();
        return QuoteOrderPrice(c, customerId, productId, includeInstallPrice, durationMonths).Price;
    }

    public (long StandardPrice, long InstalledPrice) PreviewPricePair(long customerId, long productId, long durationMonths = 12)
    {
        if (customerId <= 0 || productId <= 0) return (0, 0);
        using var c = Open();
        var std = QuoteOrderPrice(c, customerId, productId, includeInstallPrice: false, durationMonths: 12).Price;
        var inst = QuoteOrderPrice(c, customerId, productId, includeInstallPrice: true, durationMonths: durationMonths).Price;
        return (std, inst);
    }

    public List<V7Order> List()
    {
        using var c = Open();
        var linesByOrder = new Dictionary<long, List<V7OrderLine>>();
        using (var lq = Cmd(c, @"SELECT l.id,l.order_id,l.product_id,p.sku,p.name,p.is_serialized,l.quantity,l.unit_price_vnd,l.unit_cost_snapshot_vnd,COALESCE(l.installer_fee_vnd,0),l.imei,COALESCE(l.plate_no,''),COALESCE(l.vin_no,''),
            l.activation_id,l.renewal_id,COALESCE(l.prev_expiry,''),COALESCE(l.new_expiry,'')
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
                    lr.GetString(12),
                    lr.IsDBNull(13) ? null : lr.GetInt64(13),
                    lr.IsDBNull(14) ? null : lr.GetInt64(14),
                    lr.GetString(15),
                    lr.GetString(16));
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
            COALESCE(c.code,''),
            CASE
                WHEN sg.id IS NOT NULL THEN COALESCE(cg.name,'') || ' › ' || sg.name
                WHEN pg.id IS NOT NULL THEN pg.name || ' › ' || COALESCE(cg.name,'')
                ELSE COALESCE(cg.name,'-')
            END,
            COALESCE(c.representative_name,''),
            COALESCE(o.contact_name,''),COALESCE(o.duration_months,12),COALESCE(o.deduct_stock,1),
            COALESCE(o.activated_at,''),o.warehouse_id,COALESCE(o.notes,''),COALESCE(o.collect_on_delivery,0),
            COALESCE(o.seller_record_id, c.seller_id),COALESCE(o.unit_cost_snapshot_vnd,0),COALESCE(o.no_install_fee,0),
            o.customer_id,
            CASE WHEN EXISTS(SELECT 1 FROM invoices iv WHERE iv.v7_order_id=o.id AND iv.deleted_at IS NULL AND COALESCE(iv.is_locked,0)=1) THEN 1 ELSE 0 END,
            COALESCE(NULLIF(o.order_kind,''), CASE WHEN COALESCE(o.no_install_fee,0)=1 THEN 'COMMERCIAL' ELSE 'INSTALL' END)
            FROM v7_orders o JOIN customers c ON c.id=o.customer_id JOIN products p ON p.id=o.product_id
            LEFT JOIN customer_groups cg ON cg.id=c.group_id
            LEFT JOIN customer_groups pg ON pg.id=cg.parent_id
            LEFT JOIN customer_groups sg ON sg.id=c.sub_group_id AND sg.deleted_at IS NULL
            LEFT JOIN warehouses w ON w.id=o.warehouse_id
            LEFT JOIN app_users u ON u.id=o.seller_id
            LEFT JOIN v7_sellers sl ON sl.id=COALESCE(o.seller_record_id, c.seller_id)
            WHERE o.deleted_at IS NULL ORDER BY o.id DESC LIMIT 350");
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
                r.GetInt64(29),
                r.GetInt64(30) == 1,
                r.GetInt64(31),
                r.GetInt64(32) == 1,
                r.GetString(33)));
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
        long? sellerRecordId = null,
        bool noInstallFee = false,
        string orderKind = "INSTALL",
        bool completeImmediately = false,
        string? activatedDate = null)
    {
        var normKind = (orderKind ?? "").Trim().ToUpperInvariant() switch
        {
            "COMMERCIAL" => "COMMERCIAL",
            _ => "INSTALL"
        };
        if (normKind == "COMMERCIAL")
            noInstallFee = true;

        var validItems = (items ?? []).Where(x => x.ProductId > 0 && x.Quantity > 0).ToList();
        if (customer < 1 || validItems.Count == 0)
            throw new InvalidOperationException("Chọn khách hàng và ít nhất 1 sản phẩm cho đơn hàng.");
        if (!deductStock && !partner)
            throw new InvalidOperationException("Chỉ cho phép chọn 'Không trừ tồn kho' khi Đơn vị lắp đặt / giao hàng là Nhà cung cấp / Hãng lắp đặt.");
        if (!deductStock && string.IsNullOrWhiteSpace(installer) && !(installerEmployeeId is > 0))
            throw new InvalidOperationException("Khi chọn 'Không trừ tồn kho', bắt buộc phải nhập/chọn tên Nhà cung cấp / Hãng lắp đặt.");

        var createdOrderId = Tx(actor, c => {
            Role(c, actor, "SELLER", "CSKH", "MANAGER", "ADMIN", "ACCOUNTANT", "WAREHOUSE");
            if (Scalar(c, "SELECT COUNT(*) FROM customers WHERE id=$id AND is_active=1 AND deleted_at IS NULL", ("$id", customer)) == 0)
                throw new InvalidOperationException("Khách hàng không hợp lệ.");

            var dur = durationMonths > 0 ? durationMonths : 12;
            var expandedLines = new List<(long ProductId, long Qty, long UnitPrice, long UnitCost, long? TargetActId, string Imei, string PlateNo, string VinNo)>();
            foreach (var item in validItems)
            {
                if (item.Quantity > 100) throw new InvalidOperationException("Số lượng mỗi dòng tối đa 100.");
                if (Scalar(c, "SELECT COUNT(*) FROM products WHERE id=$id AND is_active=1 AND deleted_at IS NULL", ("$id", item.ProductId)) == 0)
                    throw new InvalidOperationException("Sản phẩm không hợp lệ.");
                var isSerialized = Scalar(c, "SELECT is_serialized FROM products WHERE id=$id", ("$id", item.ProductId)) == 1;
                var (autoPrice, unitCost) = QuoteOrderPrice(c, customer, item.ProductId, includeInstallPrice: !noInstallFee, durationMonths: dur);
                var effUnitPrice = item.CustomUnitPrice is >= 0 ? item.CustomUnitPrice.Value : autoPrice;
                var cleanImei = (item.Imei ?? "").Trim().ToUpperInvariant();
                if (!string.IsNullOrWhiteSpace(cleanImei))
                    ValidateImeiNotActiveOrPendingOnOtherOrder(c, cleanImei, 0);
                var cleanPlate = (item.PlateNo ?? "").Trim();
                var cleanVin = (item.VinNo ?? "").Trim();

                if (isSerialized)
                {
                    // Tách mỗi thiết bị IMEI thành 1 dòng riêng để báo IMEI / Biển số / VIN cho từng xe
                    for (long i = 0; i < item.Quantity; i++)
                    {
                        expandedLines.Add((
                            item.ProductId,
                            1,
                            effUnitPrice,
                            unitCost,
                            i == 0 ? item.TargetActivationId : null,
                            i == 0 ? cleanImei : "",
                            i == 0 ? cleanPlate : "",
                            i == 0 ? cleanVin : ""));
                    }
                }
                else
                {
                    expandedLines.Add((item.ProductId, item.Quantity, effUnitPrice, unitCost, null, "", "", ""));
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
            var headerImeis = expandedLines.Where(x => !string.IsNullOrWhiteSpace(x.Imei)).Select(x => x.Imei).Distinct().ToList();
            var headerPlates = expandedLines.Where(x => !string.IsNullOrWhiteSpace(x.PlateNo)).Select(x => x.PlateNo).Distinct().ToList();
            var headerVins = expandedLines.Where(x => !string.IsNullOrWhiteSpace(x.VinNo)).Select(x => x.VinNo).Distinct().ToList();

            var effInstaller = (installer ?? "").Trim();
            if (normKind == "COMMERCIAL" && string.IsNullOrWhiteSpace(effInstaller) && !(installerEmployeeId is > 0))
            {
                effInstaller = partner ? "Nhà cung cấp / Hãng đi lắp" : "Bán thương mại (Không lắp đặt)";
            }

            Run(c, @"INSERT INTO v7_orders(code,order_kind,customer_id,seller_id,seller_record_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd,contact_name,customer_phone,sale_source,duration_months,deduct_stock,no_install_fee,source,install_address,install_at,plate_no,vin_no,imei,notes,collect_on_delivery)
                VALUES(lower(hex(randomblob(16))),$kind,$c,$s,$sellerRec,$p,$qty,$price,$cost,$contact,$phone,$src,$dur,$deduct,$noInst,$source,$a,$at,$plate,$vin,$imei,$n,$cod)",
                ("$kind", normKind), ("$c", customer), ("$s", actor), ("$sellerRec", resolvedSellerId), ("$p", primaryProduct), ("$qty", totalQty), ("$price", totalPrice), ("$cost", totalCost),
                ("$contact", contact), ("$phone", phone), ("$src", src), ("$dur", dur), ("$deduct", deductStock ? 1 : 0),
                ("$noInst", noInstallFee ? 1 : 0),
                ("$source", partner ? "PARTNER" : "INTERNAL"),
                ("$a", addr), ("$at", when),
                ("$plate", string.Join(", ", headerPlates)),
                ("$vin", string.Join(", ", headerVins)),
                ("$imei", headerImeis.Count > 0 ? string.Join(", ", headerImeis) : null),
                ("$n", notes ?? ""), ("$cod", cod ? 1 : 0));
            var id = Scalar(c, "SELECT last_insert_rowid()");
            var codePrefix = normKind == "COMMERCIAL" ? "TM7" : "SO7";
            Run(c, "UPDATE v7_orders SET code=$code WHERE id=$id", ("$code", $"{codePrefix}-{id:000000}"), ("$id", id));

            foreach (var line in expandedLines)
            {
                Run(c, @"INSERT INTO v7_order_lines(order_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd,imei,plate_no,vin_no,activation_id)
                    VALUES($o,$p,$q,$price,$cost,$imei,$plate,$vin,$actId)",
                    ("$o", id), ("$p", line.ProductId), ("$q", line.Qty), ("$price", line.UnitPrice), ("$cost", line.UnitCost),
                    ("$imei", string.IsNullOrWhiteSpace(line.Imei) ? null : line.Imei),
                    ("$plate", line.PlateNo), ("$vin", line.VinNo), ("$actId", line.TargetActId));
            }

            Event(c, id, actor, "CREATED");

            // Nếu CSKH đã chọn Kho hoặc Đơn vị / Hãng lắp đặt ngay khi tạo đơn thì ghi nhận luôn (nhưng chưa trừ tồn kho cho đến khi Hoàn thành)
            if (warehouseId is > 0 && Scalar(c, "SELECT COUNT(*) FROM warehouses WHERE id=$w AND deleted_at IS NULL", ("$w", warehouseId.Value)) > 0)
            {
                Run(c, "UPDATE v7_orders SET status='APPROVED',warehouse_id=$w WHERE id=$id", ("$w", warehouseId.Value), ("$id", id));
                Event(c, id, actor, "APPROVED");
            }
            if ((installerEmployeeId is > 0) || !string.IsNullOrWhiteSpace(effInstaller))
            {
                ApplyDispatchInternal(c, actor, id, partner, effInstaller, installerEmployeeId, deductStock, warehouseId);
            }
            if (headerImeis.Count > 0)
            {
                Run(c, "UPDATE v7_orders SET status='REPORTED' WHERE id=$id AND status NOT IN ('COMPLETED','CANCELLED')", ("$id", id));
            }

            return id;
        });

        if (completeImmediately)
        {
            Complete(actor, createdOrderId, activatedDate);
        }

        return createdOrderId;
    }

    /// <summary>
    /// 3/ Đơn gia hạn: Cho phép chọn 1 hoặc nhiều thiết bị đang hoạt động, gói 1 tháng / 3 tháng / 12 tháng hoặc đánh mốc thời gian cụ thể,
    /// mở linh hoạt đơn giá thực thu trên từng xe, cập nhật mốc gia hạn sang bảng Thiết bị và sinh Hóa đơn công nợ RENEWAL gộp gọn bên Kế toán.
    /// </summary>
    public (long OrderId, string OrderCode, int RenewedCount, long TotalAmountVnd) CreateRenewalOrder(
        long actor,
        long? targetCustomerId,
        IReadOnlyList<V7RenewalLineDraft> lines,
        int durationMonths = 12,
        string? milestoneExpiryDate = null,
        string sourceType = "TNM",
        string paymentMethod = "DEBT",
        long? sellerRecordId = null,
        string? saleSource = null,
        string? renewedDate = null,
        string? notes = null)
    {
        if (lines is null || lines.Count == 0)
            throw new InvalidOperationException("Vui lòng chọn ít nhất 1 thiết bị / xe cần gia hạn.");
        if (sourceType is not ("TNM" or "NCC" or "HTX" or "B2C"))
            sourceType = "TNM";
        if (paymentMethod is not ("DEBT" or "CASH"))
            paymentMethod = "DEBT";

        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy"];
        var renDate = VnDateTimeHelper.TodayVn;
        if (!string.IsNullOrWhiteSpace(renewedDate) && DateTime.TryParseExact(renewedDate.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedRen))
            renDate = parsedRen.Date;

        DateTime? milestoneDt = null;
        if (!string.IsNullOrWhiteSpace(milestoneExpiryDate))
        {
            if (!DateTime.TryParseExact(milestoneExpiryDate.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var pm))
                throw new InvalidOperationException("Mốc thời gian gia hạn không hợp lệ.");
            milestoneDt = pm.Date;
        }

        var effDur = durationMonths > 0 ? durationMonths : 12;

        var res = Tx(actor, c =>
        {
            Role(c, actor, "SELLER", "CSKH", "MANAGER", "ADMIN", "ACCOUNTANT", "WAREHOUSE");

            bool canTransferOnRenew = Scalar(c, @"SELECT COUNT(*) FROM app_users u
                WHERE u.id=$u AND u.is_active=1 AND (
                    u.is_owner=1
                    OR LOWER(u.username)='vanlt_it'
                    OR EXISTS (
                        SELECT 1 FROM user_roles ur JOIN roles r ON r.id=ur.role_id
                        WHERE ur.user_id=u.id AND UPPER(r.code) IN ('OWNER','ADMIN','ACCOUNTANT')
                    )
                )", ("$u", actor)) > 0;

            var distinctLines = lines.GroupBy(x => x.ActivationId).Select(g => g.First()).ToList();
            var resolvedRows = new List<(
                long ActId,
                long StockUnitId,
                long OldCustId,
                string OldCustName,
                long EffCustId,
                string EffCustName,
                long ProdId,
                string DevLabel,
                string Imei,
                string OldPlate,
                string EffPlate,
                string Vin,
                string EffOwnerName,
                string EffOwnerPhone,
                string PrevExpStr,
                string NextExpStr,
                long UnitPrice,
                long UnitCost,
                int LineDurMonths)>();

            long headerCustomerId = targetCustomerId is > 0 ? targetCustomerId.Value : 0;

            foreach (var draft in distinctLines)
            {
                using var dq = Cmd(c, @"SELECT a.id, a.stock_unit_id, a.customer_id, cu.name, a.product_id,
                        COALESCE(a.expires_at,''), COALESCE(a.is_pre_declared,0), u.imei, COALESCE(a.plate_no,''), COALESCE(a.vin_no,''),
                        COALESCE(a.owner_name,''), COALESCE(a.owner_phone,''),
                        COALESCE(NULLIF(a.device_type_label,''),
                            CASE WHEN p.sku='SP000001' THEN 'XANHGPS'
                                 WHEN p.sku IN ('DX00002','SP000019') THEN 'NASAGPS'
                                 WHEN p.sku='SP000005' THEN 'TDCGPS'
                                 WHEN p.sku='SP000012' THEN 'AICAM'
                                 WHEN p.sku='SP000006' THEN 'mGPS'
                                 ELSE p.sku END)
                    FROM activations a
                    JOIN stock_units u ON u.id=a.stock_unit_id
                    JOIN products p ON p.id=a.product_id
                    JOIN customers cu ON cu.id=a.customer_id
                    WHERE a.id=$id AND a.deleted_at IS NULL", ("$id", draft.ActivationId));
                using var dr = dq.ExecuteReader();
                if (!dr.Read())
                    throw new InvalidOperationException($"Không tìm thấy thiết bị #{draft.ActivationId} để gia hạn.");

                var actId = dr.GetInt64(0);
                var stockUnitId = dr.GetInt64(1);
                var oldCustId = dr.GetInt64(2);
                var oldCustName = dr.GetString(3);
                var prodId = dr.GetInt64(4);
                var curExpRaw = dr.GetString(5);
                var isPreDec = dr.GetInt64(6) == 1;
                var imei = dr.GetString(7);
                var oldPlate = dr.GetString(8);
                var vin = dr.GetString(9);
                var curOwner = dr.GetString(10);
                var curPhone = dr.GetString(11);
                var devLabel = dr.GetString(12);
                dr.Close();

                if (isPreDec)
                    throw new InvalidOperationException($"Xe '{(string.IsNullOrWhiteSpace(oldPlate) ? imei : oldPlate)}' mới khai báo hồ sơ chưa lắp đặt thiết bị, vui lòng tạo Đơn lắp đặt trước khi gia hạn.");

                long effCustId;
                if (targetCustomerId is > 0)
                {
                    effCustId = targetCustomerId.Value;
                    headerCustomerId = effCustId;
                }
                else
                {
                    effCustId = oldCustId;
                    if (headerCustomerId == 0)
                        headerCustomerId = oldCustId;
                    else if (headerCustomerId != oldCustId)
                        throw new InvalidOperationException("Các xe gộp chung trong 1 Đơn gia hạn phải thuộc cùng một HTX / Khách hàng (hoặc chọn Đơn vị / HTX nhận hóa đơn gia hạn để chuyển về chung 1 đơn vị).");
                }

                var effCustName = oldCustName;
                if (effCustId != oldCustId)
                {
                    if (!canTransferOnRenew)
                        throw new InvalidOperationException("Chỉ tài khoản Kế toán, Quản trị (Admin) hoặc Chủ sở hữu (Owner) mới được phép chuyển đổi HTX / Khách hàng khi lập Đơn gia hạn.");
                    effCustName = Str(c, "SELECT COALESCE(name,'') FROM customers WHERE id=$id AND deleted_at IS NULL", ("$id", effCustId));
                    if (string.IsNullOrWhiteSpace(effCustName))
                        throw new InvalidOperationException("Không tìm thấy Đơn vị / HTX / Khách hàng mới đã chọn.");
                }

                var prevDt = DateTime.TryParseExact(curExpRaw, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var pExp)
                    ? pExp.Date
                    : renDate;
                var prevStr = prevDt.ToString("yyyy-MM-dd");
                var baseStart = prevDt > renDate ? prevDt : renDate;

                DateTime? lineCustomTarget = milestoneDt;
                if (!string.IsNullOrWhiteSpace(draft.CustomNewExpiry) && DateTime.TryParseExact(draft.CustomNewExpiry.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var lineTarget))
                    lineCustomTarget = lineTarget.Date;

                var nextDt = lineCustomTarget ?? baseStart.AddMonths(effDur);
                if (nextDt <= prevDt && nextDt <= renDate)
                    throw new InvalidOperationException($"Mốc thời gian gia hạn mới ({nextDt:dd/MM/yyyy}) của xe {(string.IsNullOrWhiteSpace(oldPlate) ? imei : oldPlate)} phải lớn hơn ngày hiện tại / hạn cũ ({prevDt:dd/MM/yyyy}).");

                var calcMonths = (int)Math.Max(1, Math.Round(GroupPriceResolver.ComputeEffectiveRenewalMonths(effDur, baseStart, lineCustomTarget)));
                var autoPrice = GroupPriceResolver.ResolveRenewalPriceForDuration(c, null, effCustId, prodId, effDur, baseStart, lineCustomTarget, renDate);
                var unitPrice = draft.CustomPriceVnd is >= 0 ? draft.CustomPriceVnd.Value : autoPrice;
                var unitCost = GroupPriceResolver.ResolveRenewalCostForDuration(c, null, effCustId, prodId, effDur, baseStart, lineCustomTarget, renDate);

                var effPlate = !string.IsNullOrWhiteSpace(draft.NewPlateNo) ? draft.NewPlateNo.Trim() : oldPlate;
                var effOwner = draft.NewOwnerName is not null ? draft.NewOwnerName.Trim() : curOwner;
                var effPhone = draft.NewOwnerPhone is not null ? draft.NewOwnerPhone.Trim() : curPhone;

                resolvedRows.Add((actId, stockUnitId, oldCustId, oldCustName, effCustId, effCustName, prodId, devLabel, imei, oldPlate, effPlate, vin, effOwner, effPhone, prevStr, nextDt.ToString("yyyy-MM-dd"), unitPrice, unitCost, calcMonths));
            }

            var (resolvedSellerId, resolvedSellerLabel) = ResolveOrderSeller(c, headerCustomerId, actor, sellerRecordId, saleSource);
            var totalPrice = resolvedRows.Sum(x => x.UnitPrice);
            var totalCost = resolvedRows.Sum(x => x.UnitCost);
            var totalQty = resolvedRows.Count;
            var primaryProdId = resolvedRows[0].ProdId;
            var firstOwner = resolvedRows.Select(x => x.EffOwnerName).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
            var firstPhone = resolvedRows.Select(x => x.EffOwnerPhone).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? Str(c, "SELECT COALESCE(phone,'') FROM customers WHERE id=$id", ("$id", headerCustomerId));
            var allImeis = string.Join(", ", resolvedRows.Select(x => x.Imei));
            var allPlates = string.Join(", ", resolvedRows.Select(x => x.EffPlate).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
            var renStr = renDate.ToString("yyyy-MM-dd");
            var renIssuedStr = $"{renStr} {VnDateTimeHelper.NowVn:HH:mm:ss}";
            var headerDur = resolvedRows[0].LineDurMonths;
            var cleanNote = (notes ?? "").Trim();

            // 1. Tạo bản ghi Đơn gia hạn (PENDING trước khi thêm dòng để đi qua trigger khóa đơn COMPLETED)
            Run(c, @"INSERT INTO v7_orders(code,order_kind,customer_id,seller_id,seller_record_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd,source,status,installer,installer_fee_vnd,no_install_fee,contact_name,customer_phone,sale_source,duration_months,deduct_stock,collect_on_delivery,plate_no,imei,notes,activated_at)
                VALUES(lower(hex(randomblob(16))),'RENEWAL',$c,$actor,$sellerRec,$p,$qty,$price,$cost,'INTERNAL','PENDING','Gia hạn dịch vụ',0,1,$contact,$phone,$src,$dur,0,$cod,$plates,$imeis,$notes,$act)",
                ("$c", headerCustomerId), ("$actor", actor), ("$sellerRec", resolvedSellerId), ("$p", primaryProdId),
                ("$qty", totalQty), ("$price", totalPrice), ("$cost", totalCost),
                ("$contact", firstOwner), ("$phone", firstPhone), ("$src", resolvedSellerLabel),
                ("$dur", headerDur), ("$cod", paymentMethod == "CASH" ? 1 : 0),
                ("$plates", allPlates), ("$imeis", allImeis), ("$notes", cleanNote), ("$act", renStr));
            var ordId = Scalar(c, "SELECT last_insert_rowid()");
            var ordCode = $"GH7-{ordId:000000}";
            Run(c, "UPDATE v7_orders SET code=$code WHERE id=$id", ("$code", ordCode), ("$id", ordId));

            long firstRenewalId = 0;
            foreach (var row in resolvedRows)
            {
                Run(c, @"INSERT INTO renewals(activation_id,v7_order_id,customer_id,seller_id,seller_name,source_type,duration_months,renewed_at,previous_expiry,new_expiry,amount_vnd,payment_method)
                    VALUES($a,$o,$cust,$seller,$sellerName,$src,$dur,$renAt,$prev,$next,$amt,$pay)",
                    ("$a", row.ActId), ("$o", ordId), ("$cust", row.EffCustId), ("$seller", resolvedSellerId),
                    ("$sellerName", resolvedSellerLabel), ("$src", sourceType), ("$dur", row.LineDurMonths),
                    ("$renAt", renStr), ("$prev", row.PrevExpStr), ("$next", row.NextExpStr), ("$amt", row.UnitPrice), ("$pay", paymentMethod));
                var renId = Scalar(c, "SELECT last_insert_rowid()");
                if (firstRenewalId == 0) firstRenewalId = renId;

                Run(c, @"INSERT INTO v7_order_lines(order_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd,installer_fee_vnd,imei,plate_no,vin_no,stock_unit_id,activation_id,renewal_id,prev_expiry,new_expiry)
                    VALUES($o,$p,1,$price,$cost,0,$imei,$plate,$vin,$sid,$aid,$rid,$prev,$next)",
                    ("$o", ordId), ("$p", row.ProdId), ("$price", row.UnitPrice), ("$cost", row.UnitCost),
                    ("$imei", row.Imei), ("$plate", row.EffPlate), ("$vin", row.Vin),
                    ("$sid", row.StockUnitId), ("$aid", row.ActId), ("$rid", renId),
                    ("$prev", row.PrevExpStr), ("$next", row.NextExpStr));

                var isTr = row.EffCustId != row.OldCustId;
                Run(c, @"UPDATE activations
                    SET customer_id=$cust,
                        agency_name=CASE WHEN $tr=1 THEN $agency ELSE agency_name END,
                        seller_id=COALESCE($seller, seller_id),
                        plate_no=$plate,
                        owner_name=$owner,
                        owner_phone=$phone,
                        expires_at=$next,
                        status='ACTIVE',
                        last_renewed_at=$renAt,
                        renewal_count=COALESCE(renewal_count,0)+1,
                        updated_at=datetime('now')
                    WHERE id=$aid",
                    ("$cust", row.EffCustId), ("$tr", isTr ? 1 : 0), ("$agency", row.EffCustName),
                    ("$seller", resolvedSellerId), ("$plate", row.EffPlate), ("$owner", row.EffOwnerName),
                    ("$phone", row.EffOwnerPhone), ("$next", row.NextExpStr), ("$renAt", renStr), ("$aid", row.ActId));

                if (isTr)
                {
                    Run(c, "UPDATE stock_units SET customer_id=$cust WHERE id=$sid", ("$cust", row.EffCustId), ("$sid", row.StockUnitId));
                }
            }

            // 2. Chuyển trạng thái Đơn gia hạn sang COMPLETED
            Run(c, "UPDATE v7_orders SET status='COMPLETED', completed_at=CURRENT_TIMESTAMP WHERE id=$id", ("$id", ordId));
            Event(c, ordId, actor, "CREATED");
            Event(c, ordId, actor, "COMPLETED");

            // 3. Tạo Hóa đơn công nợ RENEWAL gộp gọn cho Đơn gia hạn bên Kế toán (/finance)
            var invNo = "GH-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            var sellerSeg = !string.IsNullOrWhiteSpace(resolvedSellerLabel) ? $" · NV bán hàng: {resolvedSellerLabel}" : "";
            var summaryNote = resolvedRows.Count == 1
                ? $"Đơn gia hạn {ordCode} ({resolvedRows[0].LineDurMonths} tháng) · Thiết bị: {resolvedRows[0].DevLabel} (IMEI: {resolvedRows[0].Imei}{(string.IsNullOrWhiteSpace(resolvedRows[0].EffPlate) ? "" : $" · Biển số: {resolvedRows[0].EffPlate}")} · Hạn: {resolvedRows[0].PrevExpStr} → {resolvedRows[0].NextExpStr}){sellerSeg}"
                : $"Đơn gia hạn {ordCode} ({resolvedRows.Count} thiết bị){sellerSeg}{(string.IsNullOrWhiteSpace(cleanNote) ? "" : $" · {cleanNote}")}";

            Run(c, @"INSERT INTO invoices(invoice_no,invoice_type,customer_id,v7_order_id,renewal_id,issued_at,due_at,amount_vnd,status,notes)
                VALUES($no,'RENEWAL',$c,$o,$r,$issued,date($renAt,'+30 days'),$amt,$st,$notes)",
                ("$no", invNo), ("$c", headerCustomerId), ("$o", ordId),
                ("$r", resolvedRows.Count == 1 ? firstRenewalId : (long?)null),
                ("$issued", renIssuedStr), ("$renAt", renStr), ("$amt", totalPrice),
                ("$st", totalPrice <= 0 ? "PAID" : "OPEN"), ("$notes", summaryNote));
            var invId = Scalar(c, "SELECT last_insert_rowid()");

            foreach (var row in resolvedRows)
            {
                var lineDesc = $"Gia hạn ({row.LineDurMonths}T: {row.PrevExpStr} → {row.NextExpStr}) · {row.DevLabel} · IMEI: {row.Imei}{(string.IsNullOrWhiteSpace(row.EffPlate) ? "" : $" · BS: {row.EffPlate}")}";
                Run(c, "INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$d,$p,1,$u)",
                    ("$i", invId), ("$d", lineDesc), ("$p", row.ProdId), ("$u", row.UnitPrice));
            }

            if (paymentMethod == "CASH" && totalPrice > 0)
            {
                var ptNo = "PT-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                var catId = Scalar(c, "SELECT COALESCE((SELECT id FROM expense_categories WHERE code='THU_GIA_HAN' LIMIT 1), 0)");
                var custName = resolvedRows[0].EffCustName;
                Run(c, @"INSERT INTO payments(payment_no,direction,paid_at,method,amount_vnd,notes,expense_category_id,customer_id,v7_order_id,payee_name,reference_doc)
                    VALUES($no,'IN',datetime('now','+7 hours'),'CASH',$amt,$note,$cat,$cust,$ord,$payee,$ref)",
                    ("$no", ptNo), ("$amt", totalPrice), ("$note", $"Thu thanh toán Đơn gia hạn {ordCode} ({invNo})"),
                    ("$cat", catId > 0 ? catId : null), ("$cust", headerCustomerId), ("$ord", ordId), ("$payee", custName), ("$ref", invNo));
                var payId = Scalar(c, "SELECT last_insert_rowid()");
                Run(c, "INSERT INTO payment_allocations(payment_id,invoice_id,allocated_vnd) VALUES($p,$i,$amt)",
                    ("$p", payId), ("$i", invId), ("$amt", totalPrice));
                Run(c, "UPDATE invoices SET status='PAID' WHERE id=$i", ("$i", invId));
            }

            if (!IsMainDeveloper(c, actor))
            {
                Run(c, "INSERT INTO audit_logs(actor_user_id,action,entity,entity_id,details) VALUES($actor,'RENEWAL_ORDER','V7_ORDER',$id,$d)",
                    ("$actor", actor > 0 ? actor : null), ("$id", ordId),
                    ("$d", $"Tạo & hoàn thành Đơn gia hạn {ordCode} ({resolvedRows.Count} thiết bị · Tổng {totalPrice:N0} ₫ · {paymentMethod} · NV: {resolvedSellerLabel})"));
            }

            return (ordId, ordCode, resolvedRows.Count, totalPrice);
        });

        DemoDb.NotifyDevicesChanged();
        return res;
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
        bool isNoInstallFee = Scalar(c, "SELECT COALESCE(no_install_fee, 0) FROM v7_orders WHERE id=$id", ("$id", id)) == 1;

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
                long unitFee = 0;
                if (!isNoInstallFee)
                {
                    using var rq = Cmd(c, "SELECT install_rate_vnd FROM v7_installer_product_rates WHERE employee_id=$e AND product_id=$p AND deleted_at IS NULL", ("$e", empId.Value), ("$p", ln.ProductId));
                    var rateVal = rq.ExecuteScalar();
                    unitFee = rateVal is not null ? Convert.ToInt64(rateVal) : Scalar(c, "SELECT technical_fee_vnd FROM products WHERE id=$p", ("$p", ln.ProductId));
                }
                Run(c, "UPDATE v7_order_lines SET installer_fee_vnd=$fee WHERE id=$lid", ("$fee", unitFee), ("$lid", ln.LineId));
                totalFee += unitFee * ln.Qty;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(installerName))
            {
                throw new InvalidOperationException(partner
                    ? "Bắt buộc phải chọn hoặc nhập tên Hãng lắp đặt."
                    : "Cần chọn nhân sự lắp đặt hoặc nhập tên Đơn vị lắp đặt.");
            }
            Run(c, "UPDATE v7_order_lines SET installer_fee_vnd=0 WHERE order_id=$o AND deleted_at IS NULL", ("$o", id));
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
        string? saleSource = null,
        bool? noInstallFee = null,
        IReadOnlyList<(long LineId, long UnitPrice)>? customLinePrices = null)
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
            var oldNoInstall = Scalar(c, "SELECT COALESCE(no_install_fee, 0) FROM v7_orders WHERE id=$id", ("$id", id)) == 1;
            var oldDur = Scalar(c, "SELECT COALESCE(duration_months, 12) FROM v7_orders WHERE id=$id", ("$id", id));
            var effDur = durationMonths > 0 ? durationMonths : 12;
            var effectiveNoInstall = noInstallFee ?? oldNoInstall;
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
                no_install_fee=$noInst,
                source=$source,
                seller_record_id=COALESCE($sellerRec, seller_record_id),
                sale_source=CASE WHEN $src<>'' THEN $src ELSE sale_source END
                WHERE id=$id",
                ("$contact", (contactName ?? "").Trim()),
                ("$phone", (customerPhone ?? "").Trim()),
                ("$addr", (installAddress ?? "").Trim()),
                ("$at", FormatInstallDateTime(installAt)),
                ("$dur", effDur),
                ("$notes", (notes ?? "").Trim()),
                ("$cod", cod ? 1 : 0),
                ("$deduct", deductStock ? 1 : 0),
                ("$noInst", effectiveNoInstall ? 1 : 0),
                ("$source", partner ? "PARTNER" : "INTERNAL"),
                ("$sellerRec", resolvedSellerId),
                ("$src", resolvedSrc),
                ("$id", id));

            var priceMap = (customLinePrices ?? []).Where(x => x.LineId > 0 && x.UnitPrice >= 0).ToDictionary(x => x.LineId, x => x.UnitPrice);
            var modeOrDurChanged = (noInstallFee.HasValue && noInstallFee.Value != oldNoInstall) || (effDur != oldDur);
            if (priceMap.Count > 0 || modeOrDurChanged)
            {
                var orderLines = new List<(long LineId, long ProductId, long Qty, long CurPrice)>();
                using (var lq = Cmd(c, "SELECT id, product_id, quantity, unit_price_vnd FROM v7_order_lines WHERE order_id=$o AND deleted_at IS NULL", ("$o", id)))
                using (var lr = lq.ExecuteReader())
                {
                    while (lr.Read())
                        orderLines.Add((lr.GetInt64(0), lr.GetInt64(1), lr.GetInt64(2), lr.GetInt64(3)));
                }

                long newTotalPrice = 0;
                foreach (var ln in orderLines)
                {
                    long newUnitPrice;
                    if (modeOrDurChanged)
                    {
                        newUnitPrice = QuoteOrderPrice(c, custId, ln.ProductId, includeInstallPrice: !effectiveNoInstall, durationMonths: effDur).Price;
                        if (!modeOrDurChanged && priceMap.TryGetValue(ln.LineId, out var cp)) newUnitPrice = cp;
                    }
                    else if (priceMap.TryGetValue(ln.LineId, out var cp))
                    {
                        newUnitPrice = cp;
                    }
                    else
                    {
                        newUnitPrice = ln.CurPrice;
                    }
                    Run(c, "UPDATE v7_order_lines SET unit_price_vnd=$p WHERE id=$lid", ("$p", newUnitPrice), ("$lid", ln.LineId));
                    newTotalPrice += newUnitPrice * ln.Qty;
                }
                Run(c, "UPDATE v7_orders SET unit_price_vnd=$total WHERE id=$id", ("$total", newTotalPrice), ("$id", id));
            }

            if (effectiveNoInstall)
            {
                Run(c, "UPDATE v7_order_lines SET installer_fee_vnd=0 WHERE order_id=$o AND deleted_at IS NULL", ("$o", id));
                Run(c, "UPDATE v7_orders SET installer_fee_vnd=0 WHERE id=$id", ("$id", id));
            }

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

    public void ReportLines(long actor, long id, IReadOnlyList<V7OrderLineReport> reports, bool allowMissingImei = true)
    {
        Tx(actor, c => {
            Role(c, actor, "ADMIN", "MANAGER", "CSKH", "TECH", "INSTALLER", "WAREHOUSE", "ACCOUNTANT");
            var curSt = Status(c, id);
            if (curSt is not ("DISPATCHED" or "REPORTED")) throw new InvalidOperationException("Đơn chưa được điều phối.");

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
                reportMap.TryGetValue(ln.LineId, out var rep);
                var cleanImei = (rep?.Imei ?? "").Trim();
                if (string.IsNullOrWhiteSpace(cleanImei) && !allowMissingImei)
                    throw new InvalidOperationException($"Thiếu mã IMEI cho sản phẩm '{ln.ProductName}'.");
                if (!string.IsNullOrWhiteSpace(cleanImei) && !seenImeis.Add(cleanImei))
                    throw new InvalidOperationException($"Mã IMEI '{cleanImei}' bị trùng lặp trong cùng đơn hàng.");
                if (!string.IsNullOrWhiteSpace(cleanImei))
                    ValidateImeiNotActiveOrPendingOnOtherOrder(c, cleanImei, id);
                var cleanPlate = (rep?.PlateNo ?? "").Trim();
                var cleanVin = (rep?.VinNo ?? "").Trim();

                Run(c, "UPDATE v7_order_lines SET imei=$imei,plate_no=$plate,vin_no=$vin WHERE id=$lid",
                    ("$imei", string.IsNullOrWhiteSpace(cleanImei) ? null : cleanImei), ("$plate", cleanPlate), ("$vin", cleanVin), ("$lid", ln.LineId));

                if (!string.IsNullOrWhiteSpace(cleanImei)) allImeis.Add(cleanImei);
                if (!string.IsNullOrWhiteSpace(cleanPlate)) allPlates.Add(cleanPlate);
                if (!string.IsNullOrWhiteSpace(cleanVin)) allVins.Add(cleanVin);
            }

            var nextStatus = allImeis.Count > 0 ? "REPORTED" : curSt;
            Run(c, "UPDATE v7_orders SET status=$st,imei=$imei,plate_no=$plate,vin_no=$vin WHERE id=$id",
                ("$st", nextStatus),
                ("$imei", allImeis.Count > 0 ? string.Join(", ", allImeis) : "CHỜ-NHẬP-IMEI"),
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
            if (curStatus is "COMPLETED" or "CANCELLED")
                throw new InvalidOperationException("Đơn hàng đã hoàn thành hoặc đã hủy.");

            using var q = Cmd(c, @"SELECT customer_id,warehouse_id,source,unit_price_vnd,COALESCE(installer_fee_vnd,0),
                COALESCE(installer,''),COALESCE(code,''),COALESCE(duration_months,12),COALESCE(deduct_stock,1),COALESCE(activated_at,''),COALESCE(no_install_fee,0)
                FROM v7_orders WHERE id=$id", ("$id", id));
            long customer, warehouse, totalPrice, totalInstallerFee, durationMonths;
            bool deductStock, noInstallFee;
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
                noInstallFee = r.GetInt64(10) == 1;
            }
            if (noInstallFee)
            {
                totalInstallerFee = 0;
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
                COALESCE(l.imei,''),COALESCE(l.plate_no,''),COALESCE(l.vin_no,''),COALESCE(l.installer_fee_vnd,0),COALESCE(m.product_kind,'GOODS'),COALESCE(l.activation_id,0)
                FROM v7_order_lines l JOIN products p ON p.id=l.product_id
                LEFT JOIN product_catalog_metadata m ON m.product_id=p.id
                WHERE l.order_id=$o AND l.deleted_at IS NULL ORDER BY l.id", ("$o", id));
            var lines = new List<(long LineId, long ProductId, string ProductName, bool IsSerialized, long Qty, long UnitPrice, long UnitCost, string Imei, string PlateNo, string VinNo, long InstallerFee, string ProductKind, long TargetActId)>();
            using (var lr = lq.ExecuteReader())
            {
                while (lr.Read())
                    lines.Add((lr.GetInt64(0), lr.GetInt64(1), lr.GetString(2), lr.GetInt64(3) == 1, lr.GetInt64(4), lr.GetInt64(5), lr.GetInt64(6), lr.GetString(7), lr.GetString(8), lr.GetString(9), lr.GetInt64(10), lr.GetString(11), lr.GetInt64(12)));
            }

            long? saleOutTxId = null;
            foreach (var ln in lines)
            {
                if (ln.IsSerialized)
                {
                    var hasImei = !string.IsNullOrWhiteSpace(ln.Imei);
                    if (hasImei)
                        ValidateImeiNotActiveOrPendingOnOtherOrder(c, ln.Imei, id);

                    long stockUnitId = 0;
                    if (deductStock)
                    {
                        long txWarehouse = warehouse;
                        if (hasImei)
                        {
                            // Nếu mã IMEI này đang tồn tại trong stock_units và bị đơn 'CHỜ-NHẬP-IMEI' mượn tạm slot thì tự động giải phóng về IN_STOCK trước
                            var existingByImeiId = Scalar(c, "SELECT COALESCE((SELECT id FROM stock_units WHERE UPPER(TRIM(imei))=UPPER($imei) AND deleted_at IS NULL LIMIT 1), 0)", ("$imei", ln.Imei));
                            if (existingByImeiId > 0)
                            {
                                ReclaimAnonymouslyHeldStockUnitIfNeeded(c, existingByImeiId, warehouse, actor);
                            }

                            // Tìm theo đúng mã IMEI trong kho khả dụng (chấp nhận cả trường hợp nhập kho mã SP000019 nhưng đơn chọn DX00002 cùng dòng thiết bị)
                            using var find = Cmd(c, @"SELECT id, COALESCE(warehouse_id, $w) FROM stock_units
                                WHERE UPPER(TRIM(imei))=UPPER($imei) AND deleted_at IS NULL
                                  AND status IN ('IN_STOCK','CONSIGNED','TECHNICIAN')
                                ORDER BY CASE WHEN product_id=$p THEN 0 ELSE 1 END,
                                         CASE WHEN warehouse_id=$w THEN 0 ELSE 1 END,
                                         id ASC LIMIT 1",
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
                                if (existingByImeiId > 0)
                                {
                                    var curSt = Str(c, "SELECT status FROM stock_units WHERE id=$id", ("$id", existingByImeiId));
                                    var anonOrd = Str(c, @"SELECT COALESCE((
                                        SELECT o2.code FROM v7_order_lines l2 JOIN v7_orders o2 ON o2.id=l2.order_id
                                        WHERE l2.stock_unit_id=$sid AND o2.status='COMPLETED' AND o2.deleted_at IS NULL AND l2.deleted_at IS NULL
                                        LIMIT 1), '')", ("$sid", existingByImeiId));
                                    if (!string.IsNullOrWhiteSpace(anonOrd))
                                        throw new InvalidOperationException($"Sản phẩm '{ln.ProductName}' hiện đã hết số lượng tồn kho khả dụng (mã IMEI '{ln.Imei}' đang nằm trong số lượng đã trừ kho của đơn {anonOrd}). Vui lòng nhập kho bổ sung hoặc điền IMEI trực tiếp vào đơn {anonOrd}.");
                                    throw new InvalidOperationException($"Mã IMEI '{ln.Imei}' đang ở trạng thái '{curSt}' (không ở trong kho khả dụng). Vui lòng kiểm tra tại mục Kho → Kho IMEI.");
                                }

                                Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id WHERE UPPER(TRIM(imei))=UPPER($imei) AND deleted_at IS NOT NULL", ("$imei", ln.Imei));

                                var lnSku = Str(c, "SELECT UPPER(COALESCE(sku,'')) FROM products WHERE id=$p", ("$p", ln.ProductId));
                                var siblingProdId = lnSku is "DX00002" or "SP000019"
                                    ? Scalar(c, "SELECT COALESCE((SELECT id FROM products WHERE UPPER(sku) IN ('DX00002','SP000019') AND id<>$p AND deleted_at IS NULL LIMIT 1), 0)", ("$p", ln.ProductId))
                                    : 0;

                                using var findSlot = Cmd(c, @"SELECT u.id, COALESCE(u.warehouse_id, $w) FROM stock_units u
                                    WHERE (u.product_id=$p OR ($sib>0 AND u.product_id=$sib))
                                      AND u.status='IN_STOCK' AND u.deleted_at IS NULL
                                      AND NOT EXISTS (
                                          SELECT 1 FROM v7_order_lines lp JOIN v7_orders op ON op.id=lp.order_id
                                          WHERE op.id<>$ord AND UPPER(TRIM(COALESCE(lp.imei,'')))=UPPER(TRIM(u.imei))
                                            AND op.status NOT IN ('CANCELLED','COMPLETED') AND op.deleted_at IS NULL AND lp.deleted_at IS NULL
                                      )
                                    ORDER BY CASE WHEN u.product_id=$p THEN 0 ELSE 1 END,
                                             CASE WHEN u.warehouse_id=$w THEN 0 ELSE 1 END,
                                             CASE WHEN u.imei LIKE 'KHO-%' THEN 0 ELSE 1 END,
                                             u.id DESC LIMIT 1",
                                    ("$p", ln.ProductId), ("$sib", siblingProdId), ("$w", warehouse), ("$ord", id));
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
                                    throw new InvalidOperationException($"Sản phẩm '{ln.ProductName}' hiện đã hết tồn kho trong Kho (và mã IMEI '{ln.Imei}' chưa được nhập kho). Vui lòng nhập kho trước khi hoàn tất đơn có trừ tồn kho, hoặc chọn Hãng lắp đặt (Không trừ tồn kho).");
                                }
                            }
                        }
                        else
                        {
                            // Hoàn thành đơn nhưng chưa nhập IMEI ("nhập IMEI sau nhưng vẫn trừ tồn kho"):
                            // Ưu tiên lấy dòng KHO-% và tránh lấy các mã IMEI đang được gõ chờ hoàn thành trên đơn khác
                            var lnSku = Str(c, "SELECT UPPER(COALESCE(sku,'')) FROM products WHERE id=$p", ("$p", ln.ProductId));
                            var siblingProdId = lnSku is "DX00002" or "SP000019"
                                ? Scalar(c, "SELECT COALESCE((SELECT id FROM products WHERE UPPER(sku) IN ('DX00002','SP000019') AND id<>$p AND deleted_at IS NULL LIMIT 1), 0)", ("$p", ln.ProductId))
                                : 0;

                            using var findSlot = Cmd(c, @"SELECT u.id, COALESCE(u.warehouse_id, $w) FROM stock_units u
                                WHERE (u.product_id=$p OR ($sib>0 AND u.product_id=$sib))
                                  AND u.status='IN_STOCK' AND u.deleted_at IS NULL
                                ORDER BY CASE WHEN u.product_id=$p THEN 0 ELSE 1 END,
                                         CASE WHEN u.warehouse_id=$w THEN 0 ELSE 1 END,
                                         CASE WHEN u.imei LIKE 'KHO-%' THEN 0 ELSE 1 END,
                                         CASE WHEN EXISTS (
                                             SELECT 1 FROM v7_order_lines lp JOIN v7_orders op ON op.id=lp.order_id
                                             WHERE UPPER(TRIM(COALESCE(lp.imei,'')))=UPPER(TRIM(u.imei))
                                               AND op.status NOT IN ('CANCELLED','COMPLETED') AND op.deleted_at IS NULL AND lp.deleted_at IS NULL
                                         ) THEN 1 ELSE 0 END,
                                         u.id DESC LIMIT 1",
                                ("$p", ln.ProductId), ("$sib", siblingProdId), ("$w", warehouse));
                            using var sr = findSlot.ExecuteReader();
                            if (sr.Read())
                            {
                                stockUnitId = sr.GetInt64(0);
                                if (txWarehouse <= 0 && !sr.IsDBNull(1)) txWarehouse = sr.GetInt64(1);
                                sr.Close();
                            }
                            else
                            {
                                sr.Close();
                                throw new InvalidOperationException($"Sản phẩm '{ln.ProductName}' hiện đã hết tồn kho để trừ. Vui lòng nhập kho trước khi hoàn tất đơn có trừ tồn kho, hoặc chọn Hãng lắp đặt (Không trừ tồn kho).");
                            }
                        }

                        if (!saleOutTxId.HasValue)
                        {
                            var txCode = "XK-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
                            Run(c, "INSERT INTO inventory_transactions(code,movement_type,warehouse_id,customer_id,actor_user_id,notes) VALUES($code,'SALE_OUT',$w,$c,$actor,'Xuất lắp đặt đơn V7')",
                                ("$code", txCode), ("$w", txWarehouse > 0 ? txWarehouse : null), ("$c", customer), ("$actor", actor));
                            saleOutTxId = Scalar(c, "SELECT last_insert_rowid()");
                        }

                        var isReturnedUnit = Scalar(c, @"SELECT COUNT(*) FROM stock_units u
                            JOIN suppliers s ON s.id=u.supplier_id
                            LEFT JOIN supplier_groups sg ON sg.id=s.group_id
                            WHERE u.id=$sid
                              AND (UPPER(COALESCE(sg.code,''))='HOANTRA'
                                   OR LOWER(COALESCE(s.name,'')) LIKE '%hoàn trả%'
                                   OR LOWER(COALESCE(sg.name,'')) LIKE '%hoàn trả%')",
                            ("$sid", stockUnitId)) > 0;
                        var effectiveLineCost = isReturnedUnit ? 0 : ln.UnitCost;

                        Run(c, "INSERT INTO inventory_transaction_lines(transaction_id,stock_unit_id,product_id,unit_cost_snapshot_vnd) VALUES($t,$s,$p,$cost)",
                            ("$t", saleOutTxId.Value), ("$s", stockUnitId), ("$p", ln.ProductId), ("$cost", effectiveLineCost));

                        Run(c, "UPDATE stock_units SET status='INSTALLED',warehouse_id=NULL,customer_id=$c,employee_id=NULL,updated_at=CURRENT_TIMESTAMP WHERE id=$id AND status IN ('IN_STOCK','CONSIGNED','TECHNICIAN')",
                            ("$c", customer), ("$id", stockUnitId));
                        if (Scalar(c, "SELECT changes()") != 1) throw new InvalidOperationException($"Không thể trừ tồn kho sản phẩm '{ln.ProductName}'.");
                        UpsertStockEffect(c, id, stockUnitId);
                        Run(c, "UPDATE v7_order_lines SET stock_unit_id=$sid, unit_cost_snapshot_vnd=$cost WHERE id=$lid", ("$sid", stockUnitId), ("$cost", effectiveLineCost), ("$lid", ln.LineId));
                    }
                    else if (hasImei)
                    {
                        var existingId = Scalar(c, "SELECT COALESCE((SELECT id FROM stock_units WHERE UPPER(TRIM(imei))=UPPER($imei) AND deleted_at IS NULL LIMIT 1), 0)", ("$imei", ln.Imei));
                        if (existingId > 0)
                        {
                            ReclaimAnonymouslyHeldStockUnitIfNeeded(c, existingId, warehouse, actor);
                            var st = Str(c, "SELECT status FROM stock_units WHERE id=$id", ("$id", existingId));
                            if (st is "IN_STOCK" or "CONSIGNED" or "TECHNICIAN")
                            {
                                // Nếu mã IMEI này đang nằm sẵn trong kho nhưng đơn Hãng chọn Không trừ kho -> cảnh báo rõ ràng
                                throw new InvalidOperationException($"Mã IMEI '{ln.Imei}' hiện đang nằm trong Kho nội bộ. Vui lòng tắt 'Không trừ tồn kho' để xuất từ kho, hoặc nhập đúng mã IMEI của Hãng.");
                            }
                            throw new InvalidOperationException($"Mã IMEI '{ln.Imei}' đã tồn tại trong danh sách thiết bị đang sử dụng.");
                        }
                        Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id WHERE UPPER(TRIM(imei))=UPPER($imei) AND deleted_at IS NOT NULL", ("$imei", ln.Imei));
                        Run(c, "INSERT INTO stock_units(product_id,imei,status,customer_id) VALUES($p,$imei,'INSTALLED',$c)",
                            ("$p", ln.ProductId), ("$imei", ln.Imei), ("$c", customer));
                        stockUnitId = Scalar(c, "SELECT last_insert_rowid()");
                        Run(c, "UPDATE v7_order_lines SET stock_unit_id=$sid WHERE id=$lid", ("$sid", stockUnitId), ("$lid", ln.LineId));
                    }

                    // Nếu dòng đã có IMEI thì kích hoạt lên bảng Thiết bị (activations) ngay (nếu đã chọn sẵn xe khai báo trước thì nâng cấp bản ghi xe đó, tránh tạo trùng)
                    if (hasImei && stockUnitId > 0)
                    {
                        var preActId = ln.TargetActId > 0
                            ? Scalar(c, "SELECT COALESCE((SELECT id FROM activations WHERE id=$aid AND COALESCE(is_pre_declared,0)=1 AND deleted_at IS NULL LIMIT 1), 0)", ("$aid", ln.TargetActId))
                            : 0;
                        if (preActId > 0)
                        {
                            var oldPlaceholderUnit = Scalar(c, "SELECT COALESCE(stock_unit_id,0) FROM activations WHERE id=$aid", ("$aid", preActId));
                            Run(c, @"UPDATE activations
                                SET v7_order_id=$o, stock_unit_id=$s, customer_id=$c, product_id=$p,
                                    plate_no=CASE WHEN $plate<>'' THEN $plate ELSE plate_no END,
                                    vin_no=CASE WHEN $vin<>'' THEN $vin ELSE vin_no END,
                                    activated_at=$act, expires_at=$exp, is_pre_declared=0, status='ACTIVE', updated_at=datetime('now')
                                WHERE id=$aid",
                                ("$o", id), ("$s", stockUnitId), ("$c", customer), ("$p", ln.ProductId),
                                ("$plate", ln.PlateNo), ("$vin", ln.VinNo), ("$act", actDateStr), ("$exp", expDateStr), ("$aid", preActId));
                            if (oldPlaceholderUnit > 0 && oldPlaceholderUnit != stockUnitId)
                            {
                                Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id, deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=$uid AND imei LIKE 'CHO-LAP-%'",
                                    ("$uid", oldPlaceholderUnit));
                            }
                        }
                        else
                        {
                            Run(c, @"INSERT INTO activations(v7_order_id,stock_unit_id,customer_id,product_id,plate_no,vin_no,activated_at,expires_at,is_pre_declared,status)
                                VALUES($o,$s,$c,$p,$plate,$vin,$act,$exp,0,'ACTIVE')",
                                ("$o", id), ("$s", stockUnitId), ("$c", customer), ("$p", ln.ProductId), ("$plate", ln.PlateNo), ("$vin", ln.VinNo), ("$act", actDateStr), ("$exp", expDateStr));
                            var createdActId = Scalar(c, "SELECT last_insert_rowid()");
                            Run(c, "UPDATE v7_order_lines SET activation_id=$aid WHERE id=$lid", ("$aid", createdActId), ("$lid", ln.LineId));
                        }
                    }
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

            // Tự động tạo hóa đơn công nợ SALE gồm đầy đủ các dòng sản phẩm (lưu chuẩn yyyy-MM-dd HH:mm:ss giờ VN)
            var invoiceNo = "HD-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            var invNotes = noInstallFee ? $"Đơn hàng {orderCode} (Không tính công lắp — Giá chưa lắp)" : $"Đơn hàng lắp đặt {orderCode}";
            var lineDescPrefix = noInstallFee ? "Thiết bị (Không tính công lắp)" : "Thiết bị & lắp đặt";
            Run(c, @"INSERT INTO invoices(invoice_no,invoice_type,customer_id,v7_order_id,issued_at,due_at,amount_vnd,notes)
                VALUES($no,'SALE',$c,$o,datetime('now','+7 hours'),date('now','+7 hours','+30 days'),$price,$notes)",
                ("$no", invoiceNo), ("$c", customer), ("$o", id), ("$price", totalPrice), ("$notes", invNotes));
            var invoiceId = Scalar(c, "SELECT last_insert_rowid()");
            foreach (var grp in lines.GroupBy(x => (x.ProductId, x.ProductName, x.UnitPrice)))
            {
                Run(c, "INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$desc,$p,$q,$price)",
                    ("$i", invoiceId), ("$desc", $"{lineDescPrefix}: {grp.Key.ProductName}"), ("$p", grp.Key.ProductId), ("$q", grp.Sum(x => x.Qty)), ("$price", grp.Key.UnitPrice));
            }

            // Tự động tạo chứng từ công nợ chi phí công kỹ thuật lắp đặt (SERVICE) nếu đơn có công lắp > 0
            if (totalInstallerFee > 0)
            {
                var srvNo = "CL-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                var partnerLabel = !string.IsNullOrWhiteSpace(installerName) ? installerName : "Nhân sự lắp đặt";
                Run(c, @"INSERT INTO invoices(invoice_no,invoice_type,v7_order_id,issued_at,due_at,amount_vnd,notes)
                    VALUES($no,'SERVICE',$o,datetime('now','+7 hours'),date('now','+7 hours','+15 days'),$fee,$notes)",
                    ("$no", srvNo), ("$o", id), ("$fee", totalInstallerFee), ("$notes", $"{partnerLabel} — Công lắp đặt đơn {orderCode}"));
                var srvInvId = Scalar(c, "SELECT last_insert_rowid()");
                foreach (var grp in lines.Where(x => x.InstallerFee > 0).GroupBy(x => (x.ProductId, x.ProductName, x.InstallerFee)))
                {
                    Run(c, "INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$desc,$p,$q,$price)",
                        ("$i", srvInvId), ("$desc", $"Công lắp đặt ({orderCode}): {grp.Key.ProductName}"), ("$p", grp.Key.ProductId), ("$q", grp.Sum(x => x.Qty)), ("$price", grp.Key.InstallerFee));
                }
            }

            var finalOrderCost = Scalar(c, "SELECT COALESCE(SUM(quantity * unit_cost_snapshot_vnd), 0) FROM v7_order_lines WHERE order_id=$id AND deleted_at IS NULL", ("$id", id));
            Run(c, "UPDATE v7_orders SET unit_cost_snapshot_vnd=$cost, status='COMPLETED',activated_at=$act,completed_at=CURRENT_TIMESTAMP WHERE id=$id", ("$cost", finalOrderCost), ("$act", actDateStr), ("$id", id));
            Event(c, id, actor, "COMPLETED");
            return 0;
        });
        DemoDb.NotifyDevicesChanged();
    }

    public int SupplementCompletedOrderImeis(long actor, long id, IReadOnlyList<V7OrderLineReport> reports, string? activatedDate = null)
    {
        var newlyActivated = Tx(actor, c => {
            Role(c, actor, "ADMIN", "MANAGER", "CSKH", "TECH", "INSTALLER", "WAREHOUSE", "ACCOUNTANT");
            if (Status(c, id) != "COMPLETED")
                throw new InvalidOperationException("Chức năng bổ sung IMEI sau chỉ áp dụng cho đơn hàng đã hoàn thành.");

            using var q = Cmd(c, @"SELECT customer_id, COALESCE(warehouse_id, 0), source, COALESCE(duration_months, 12), COALESCE(deduct_stock, 1), COALESCE(activated_at, '')
                FROM v7_orders WHERE id=$id", ("$id", id));
            long customer, warehouse, durationMonths;
            bool deductStock;
            string source, existingActivatedAt;
            using (var r = q.ExecuteReader())
            {
                if (!r.Read()) throw new InvalidOperationException("Không thấy đơn.");
                customer = r.GetInt64(0);
                warehouse = r.GetInt64(1);
                source = r.GetString(2);
                durationMonths = r.GetInt64(3) > 0 ? r.GetInt64(3) : 12;
                deductStock = r.GetInt64(4) == 1 && source != "PARTNER" ? true : (r.GetInt64(4) == 1);
                existingActivatedAt = r.GetString(5);
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

            using var lq = Cmd(c, @"SELECT l.id, l.product_id, p.name, p.is_serialized, COALESCE(l.imei,''), COALESCE(l.plate_no,''), COALESCE(l.vin_no,''), COALESCE(l.stock_unit_id, 0)
                FROM v7_order_lines l JOIN products p ON p.id=l.product_id
                WHERE l.order_id=$o AND l.deleted_at IS NULL ORDER BY l.id", ("$o", id));
            var dbLines = new List<(long LineId, long ProductId, string ProductName, bool IsSerialized, string OldImei, string OldPlate, string OldVin, long StockUnitId)>();
            using (var lr = lq.ExecuteReader())
            {
                while (lr.Read())
                    dbLines.Add((lr.GetInt64(0), lr.GetInt64(1), lr.GetString(2), lr.GetInt64(3) == 1, lr.GetString(4), lr.GetString(5), lr.GetString(6), lr.GetInt64(7)));
            }

            var reportMap = (reports ?? []).ToDictionary(x => x.LineId, x => x);
            var seenImeis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allImeis = new List<string>();
            var allPlates = new List<string>();
            var allVins = new List<string>();
            int activatedCount = 0;

            foreach (var ln in dbLines)
            {
                if (!ln.IsSerialized) continue;
                reportMap.TryGetValue(ln.LineId, out var rep);
                var cleanImei = (rep?.Imei ?? ln.OldImei).Trim();
                var cleanPlate = (rep?.PlateNo ?? ln.OldPlate).Trim();
                var cleanVin = (rep?.VinNo ?? ln.OldVin).Trim();

                if (!string.IsNullOrWhiteSpace(cleanImei) && !seenImeis.Add(cleanImei))
                    throw new InvalidOperationException($"Mã IMEI '{cleanImei}' bị trùng lặp trong cùng đơn hàng.");

                if (string.IsNullOrWhiteSpace(cleanImei))
                {
                    Run(c, "UPDATE v7_order_lines SET plate_no=$plate, vin_no=$vin WHERE id=$lid",
                        ("$plate", cleanPlate), ("$vin", cleanVin), ("$lid", ln.LineId));
                    if (!string.IsNullOrWhiteSpace(cleanPlate)) allPlates.Add(cleanPlate);
                    if (!string.IsNullOrWhiteSpace(cleanVin)) allVins.Add(cleanVin);
                    continue;
                }

                long stockUnitId = ln.StockUnitId;
                if (stockUnitId <= 0 && !string.IsNullOrWhiteSpace(ln.OldImei))
                {
                    stockUnitId = Scalar(c, "SELECT COALESCE((SELECT id FROM stock_units WHERE imei=$imei AND deleted_at IS NULL LIMIT 1), 0)", ("$imei", ln.OldImei));
                }

                var existingActId = stockUnitId > 0
                    ? Scalar(c, "SELECT COALESCE((SELECT id FROM activations WHERE stock_unit_id=$sid AND deleted_at IS NULL LIMIT 1), 0)", ("$sid", stockUnitId))
                    : 0;

                if (existingActId > 0 && stockUnitId > 0)
                {
                    // Đã từng kích hoạt, cập nhật lại biển số / thông tin phụ / ngày kích hoạt hoặc sửa mã IMEI
                    if (!string.Equals(cleanImei, ln.OldImei, StringComparison.OrdinalIgnoreCase))
                    {
                        if (Scalar(c, "SELECT COUNT(*) FROM stock_units WHERE imei=$imei AND id<>$sid AND deleted_at IS NULL", ("$imei", cleanImei), ("$sid", stockUnitId)) > 0)
                            throw new InvalidOperationException($"IMEI '{cleanImei}' đã tồn tại trên thiết bị khác.");
                        Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id WHERE imei=$imei AND id<>$sid AND deleted_at IS NOT NULL", ("$imei", cleanImei), ("$sid", stockUnitId));
                        Run(c, "UPDATE stock_units SET imei=$imei, updated_at=CURRENT_TIMESTAMP WHERE id=$sid", ("$imei", cleanImei), ("$sid", stockUnitId));
                    }
                    Run(c, "UPDATE v7_order_lines SET imei=$imei, plate_no=$plate, vin_no=$vin, stock_unit_id=$sid WHERE id=$lid",
                        ("$imei", cleanImei), ("$plate", cleanPlate), ("$vin", cleanVin), ("$sid", stockUnitId), ("$lid", ln.LineId));
                    Run(c, "UPDATE activations SET plate_no=$plate, vin_no=$vin, activated_at=$act, expires_at=$exp WHERE id=$aid",
                        ("$plate", cleanPlate), ("$vin", cleanVin), ("$act", actDateStr), ("$exp", expDateStr), ("$aid", existingActId));
                }
                else
                {
                    // Dòng này hoàn thành trước khi có IMEI -> Nay bổ sung IMEI và kích hoạt thuê bao (không trừ tồn kho lần 2!)
                    ValidateImeiNotActiveOrPendingOnOtherOrder(c, cleanImei, id);

                    if (stockUnitId > 0)
                    {
                        // Tồn kho ĐÃ được trừ vào bản ghi stockUnitId khi bấm Hoàn thành đơn trước đó
                        var otherId = Scalar(c, "SELECT COALESCE((SELECT id FROM stock_units WHERE UPPER(TRIM(imei))=UPPER($imei) AND id<>$sid AND deleted_at IS NULL LIMIT 1), 0)",
                            ("$imei", cleanImei), ("$sid", stockUnitId));
                        if (otherId > 0)
                        {
                            ReclaimAnonymouslyHeldStockUnitIfNeeded(c, otherId, warehouse, actor);
                            var otherStatus = Str(c, "SELECT status FROM stock_units WHERE id=$id", ("$id", otherId));
                            if (otherStatus is not ("IN_STOCK" or "CONSIGNED" or "TECHNICIAN"))
                                throw new InvalidOperationException($"Mã IMEI '{cleanImei}' đang ở trạng thái '{otherStatus}' (không khả dụng trong kho).");

                            var restoreWh = warehouse > 0 ? warehouse : Scalar(c, "SELECT COALESCE((SELECT id FROM warehouses WHERE deleted_at IS NULL ORDER BY id LIMIT 1), 1)");
                            if (Scalar(c, "SELECT COUNT(*) FROM v7_stock_effects WHERE stock_unit_id=$oid AND order_id<>$o", ("$oid", otherId), ("$o", id)) > 0)
                            {
                                DetachStockEffectToArchive(c, otherId, actor);
                            }
                            // Hoàn lại slot tạm giữ về IN_STOCK và chuyển thiết bị thực tế sang INSTALLED để tổng tồn kho giữ nguyên (không bị trừ 2 lần)
                            Run(c, "UPDATE stock_units SET status='IN_STOCK', warehouse_id=$w, customer_id=NULL, employee_id=NULL, updated_at=CURRENT_TIMESTAMP WHERE id=$sid",
                                ("$w", restoreWh), ("$sid", stockUnitId));
                            Run(c, "UPDATE stock_units SET status='INSTALLED', warehouse_id=NULL, customer_id=$c, employee_id=NULL, updated_at=CURRENT_TIMESTAMP WHERE id=$oid",
                                ("$c", customer), ("$oid", otherId));
                            Run(c, "UPDATE inventory_transaction_lines SET stock_unit_id=$oid WHERE stock_unit_id=$sid AND NOT EXISTS (SELECT 1 FROM inventory_transaction_lines x WHERE x.transaction_id=inventory_transaction_lines.transaction_id AND x.stock_unit_id=$oid)",
                                ("$oid", otherId), ("$sid", stockUnitId));
                            Run(c, "UPDATE v7_stock_effects SET stock_unit_id=$oid WHERE order_id=$o AND stock_unit_id=$sid",
                                ("$oid", otherId), ("$o", id), ("$sid", stockUnitId));
                            stockUnitId = otherId;
                        }
                        else
                        {
                            Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id WHERE UPPER(TRIM(imei))=UPPER($imei) AND id<>$sid AND deleted_at IS NOT NULL",
                                ("$imei", cleanImei), ("$sid", stockUnitId));
                            Run(c, "UPDATE stock_units SET imei=$imei, status='INSTALLED', warehouse_id=NULL, customer_id=$c, updated_at=CURRENT_TIMESTAMP WHERE id=$sid",
                                ("$imei", cleanImei), ("$c", customer), ("$sid", stockUnitId));
                        }
                    }
                    else if (deductStock)
                    {
                        // Phòng trường hợp dòng cũ chưa gắn stock_unit_id
                        var matchId = Scalar(c, "SELECT COALESCE((SELECT id FROM stock_units WHERE UPPER(TRIM(imei))=UPPER($imei) AND deleted_at IS NULL LIMIT 1), 0)", ("$imei", cleanImei));
                        if (matchId > 0)
                        {
                            ReclaimAnonymouslyHeldStockUnitIfNeeded(c, matchId, warehouse, actor);
                            stockUnitId = matchId;
                            Run(c, "UPDATE stock_units SET status='INSTALLED', warehouse_id=NULL, customer_id=$c, updated_at=CURRENT_TIMESTAMP WHERE id=$sid",
                                ("$c", customer), ("$sid", stockUnitId));
                        }
                        else
                        {
                            Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id WHERE UPPER(TRIM(imei))=UPPER($imei) AND deleted_at IS NOT NULL", ("$imei", cleanImei));
                            Run(c, "INSERT INTO stock_units(product_id,imei,status,customer_id) VALUES($p,$imei,'INSTALLED',$c)",
                                ("$p", ln.ProductId), ("$imei", cleanImei), ("$c", customer));
                            stockUnitId = Scalar(c, "SELECT last_insert_rowid()");
                        }
                    }
                    else
                    {
                        // Đơn Hãng lắp đặt (Không trừ tồn kho) bổ sung IMEI sau
                        if (Scalar(c, "SELECT COUNT(*) FROM stock_units WHERE imei=$imei AND deleted_at IS NULL", ("$imei", cleanImei)) > 0)
                            throw new InvalidOperationException($"IMEI '{cleanImei}' đã tồn tại trong danh sách thiết bị.");
                        Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id WHERE imei=$imei AND deleted_at IS NOT NULL", ("$imei", cleanImei));
                        Run(c, "INSERT INTO stock_units(product_id,imei,status,customer_id) VALUES($p,$imei,'INSTALLED',$c)",
                            ("$p", ln.ProductId), ("$imei", cleanImei), ("$c", customer));
                        stockUnitId = Scalar(c, "SELECT last_insert_rowid()");
                    }

                    Run(c, "UPDATE v7_order_lines SET imei=$imei, plate_no=$plate, vin_no=$vin, stock_unit_id=$sid WHERE id=$lid",
                        ("$imei", cleanImei), ("$plate", cleanPlate), ("$vin", cleanVin), ("$sid", stockUnitId), ("$lid", ln.LineId));
                    Run(c, @"INSERT INTO activations(v7_order_id,stock_unit_id,customer_id,product_id,plate_no,vin_no,activated_at,expires_at)
                        VALUES($o,$s,$c,$p,$plate,$vin,$act,$exp)",
                        ("$o", id), ("$s", stockUnitId), ("$c", customer), ("$p", ln.ProductId), ("$plate", cleanPlate), ("$vin", cleanVin), ("$act", actDateStr), ("$exp", expDateStr));
                    activatedCount++;
                }

                allImeis.Add(cleanImei);
                if (!string.IsNullOrWhiteSpace(cleanPlate)) allPlates.Add(cleanPlate);
                if (!string.IsNullOrWhiteSpace(cleanVin)) allVins.Add(cleanVin);
            }

            Run(c, "UPDATE v7_orders SET imei=$imei, plate_no=$plate, vin_no=$vin, activated_at=$act WHERE id=$id",
                ("$imei", allImeis.Count > 0 ? string.Join(", ", allImeis) : "CHỜ-NHẬP-IMEI"),
                ("$plate", string.Join(", ", allPlates.Distinct())),
                ("$vin", string.Join(", ", allVins.Distinct())),
                ("$act", actDateStr),
                ("$id", id));
            Event(c, id, actor, "SUPPLEMENTED_IMEI");
            return activatedCount;
        });
        DemoDb.NotifyDevicesChanged();
        return newlyActivated;
    }

    public void Cancel(long actor, long id)
    {
        var notifyDevices = false;
        Tx(actor, c => {
            var curSt = Status(c, id);
            var isMainDev = IsMainDeveloper(c, actor);

            using var oq = Cmd(c, "SELECT COALESCE(warehouse_id, 0), source, COALESCE(deduct_stock, 1), COALESCE(order_kind, 'INSTALL'), COALESCE(notes, '') FROM v7_orders WHERE id=$id", ("$id", id));
            long orderWh = 0;
            string source = "INTERNAL";
            bool deductStock = true;
            string orderKind = "INSTALL";
            string curNotes = "";
            using (var or = oq.ExecuteReader())
            {
                if (!or.Read()) throw new InvalidOperationException("Không tìm thấy đơn hàng.");
                orderWh = or.GetInt64(0);
                source = or.GetString(1);
                deductStock = or.GetInt64(2) == 1 && source != "PARTNER" ? true : (or.GetInt64(2) == 1);
                orderKind = or.GetString(3);
                curNotes = or.GetString(4);
            }
            var defaultWh = orderWh > 0 ? orderWh : Scalar(c, "SELECT COALESCE((SELECT id FROM warehouses WHERE deleted_at IS NULL ORDER BY id LIMIT 1), 1)");

            if (!isMainDev)
            {
                if (curSt == "COMPLETED")
                    throw new UnauthorizedAccessException("Đơn hàng đã hoàn thành và khóa cố định, không thể hủy.");

                Role(c, actor, "ADMIN", "MANAGER", "CSKH", "WAREHOUSE", "ACCOUNTANT", "SELLER");

                // Hoàn trả toàn bộ IMEI / stock_unit_id trên đơn chưa hoàn thành về lại Kho (IN_STOCK) & xóa liên kết IMEI khỏi đơn hủy
                var releasedImeis = new List<string>();
                var cancelLines = new List<(long Sid, string Imei)>();
                using (var clq = Cmd(c, "SELECT COALESCE(stock_unit_id, 0), COALESCE(imei, '') FROM v7_order_lines WHERE order_id=$o", ("$o", id)))
                using (var clr = clq.ExecuteReader())
                {
                    while (clr.Read())
                        cancelLines.Add((clr.GetInt64(0), clr.GetString(1).Trim()));
                }

                var cancelOrderProtected = new HashSet<string>(
                    cancelLines.Select(x => x.Imei).Where(s => !string.IsNullOrWhiteSpace(s) && s != "CHỜ-NHẬP-IMEI"),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var (sid, imei) in cancelLines)
                {
                    if (!string.IsNullOrWhiteSpace(imei) && imei != "CHỜ-NHẬP-IMEI")
                        releasedImeis.Add(imei);

                    var resolvedSid = sid > 0
                        ? sid
                        : (!string.IsNullOrWhiteSpace(imei)
                            ? Scalar(c, "SELECT COALESCE((SELECT id FROM stock_units WHERE UPPER(TRIM(imei))=UPPER($i) AND deleted_at IS NULL LIMIT 1), 0)", ("$i", imei))
                            : 0);
                    if (resolvedSid > 0)
                    {
                        ReclaimAnonymouslyHeldStockUnitIfNeeded(c, resolvedSid, defaultWh, actor, cancelOrderProtected);
                        var activeOnOther = Scalar(c, @"SELECT COUNT(*) FROM activations a
                            WHERE a.stock_unit_id=$s AND a.deleted_at IS NULL AND COALESCE(a.is_pre_declared,0)=0", ("$s", resolvedSid)) > 0;
                        var heldByOtherCompleted = Scalar(c, @"SELECT COUNT(*) FROM v7_order_lines l2 JOIN v7_orders o2 ON o2.id=l2.order_id
                            WHERE l2.stock_unit_id=$s AND o2.id<>$o AND o2.status='COMPLETED' AND o2.deleted_at IS NULL AND l2.deleted_at IS NULL",
                            ("$s", resolvedSid), ("$o", id)) > 0;
                        if (!activeOnOther && !heldByOtherCompleted)
                        {
                            var unitStatus = Str(c, "SELECT COALESCE((SELECT status FROM stock_units WHERE id=$s), '')", ("$s", resolvedSid));
                            if (unitStatus is "CONSIGNED" or "TECHNICIAN" or "INSTALLED")
                            {
                                DetachStockEffectToArchive(c, resolvedSid, actor);
                                Run(c, "UPDATE stock_units SET status='IN_STOCK', warehouse_id=$w, customer_id=NULL, employee_id=NULL, updated_at=CURRENT_TIMESTAMP WHERE id=$s",
                                    ("$w", defaultWh), ("$s", resolvedSid));
                            }
                        }
                    }
                }

                var nextNotes = curNotes;
                if (releasedImeis.Count > 0 && !nextNotes.Contains("Đã hoàn kho IMEI", StringComparison.OrdinalIgnoreCase))
                {
                    var imeiListStr = string.Join(", ", releasedImeis.Distinct(StringComparer.OrdinalIgnoreCase));
                    nextNotes = string.IsNullOrWhiteSpace(nextNotes)
                        ? $"[Đã hoàn kho IMEI khi hủy: {imeiListStr}]"
                        : $"{nextNotes} · [Đã hoàn kho IMEI khi hủy: {imeiListStr}]";
                }

                Run(c, "UPDATE v7_order_lines SET imei=NULL, stock_unit_id=NULL, activation_id=NULL WHERE order_id=$id", ("$id", id));
                Run(c, "UPDATE v7_orders SET status='CANCELLED', imei=NULL, notes=$n WHERE id=$id", ("$n", nextNotes), ("$id", id));
                if (curSt != "CANCELLED")
                    Event(c, id, actor, "CANCELLED");
                notifyDevices = true;
                return 0;
            }

            // Role Main (ẩn): Hủy & Xóa cứng đơn hàng trên toàn hệ thống (áp dụng cho mọi trạng thái đơn hàng)
            notifyDevices = true;

            // Helper xóa cứng chứng từ kế toán (payment_allocations, payments, invoice_lines, invoices)
            static void HardDeleteInvoice(SqliteConnection conn, long invId)
            {
                var payIds = new List<long>();
                using (var pq = Cmd(conn, "SELECT payment_id FROM payment_allocations WHERE invoice_id=$i", ("$i", invId)))
                using (var pr = pq.ExecuteReader())
                {
                    while (pr.Read()) payIds.Add(pr.GetInt64(0));
                }
                Run(conn, "DELETE FROM payment_allocations WHERE invoice_id=$i", ("$i", invId));
                foreach (var pid in payIds.Distinct())
                {
                    if (Scalar(conn, "SELECT COUNT(*) FROM payment_allocations WHERE payment_id=$p", ("$p", pid)) == 0)
                        Run(conn, "DELETE FROM payments WHERE id=$p", ("$p", pid));
                }
                Run(conn, "DELETE FROM invoice_lines WHERE invoice_id=$i", ("$i", invId));
                Run(conn, "DELETE FROM invoices WHERE id=$i", ("$i", invId));
            }

            if (string.Equals(orderKind, "RENEWAL", StringComparison.OrdinalIgnoreCase))
            {
                // Xóa cứng Đơn gia hạn: chỉ xóa các bản ghi renewals & hóa đơn RENEWAL của đơn này và khôi phục hạn cũ của xe (KHÔNG xóa xe / thiết bị gốc!)
                var renItems = new List<(long RenId, long ActId, string PrevExp)>();
                using (var rq = Cmd(c, "SELECT id, activation_id, previous_expiry FROM renewals WHERE v7_order_id=$o", ("$o", id)))
                using (var rr = rq.ExecuteReader())
                {
                    while (rr.Read()) renItems.Add((rr.GetInt64(0), rr.GetInt64(1), rr.GetString(2)));
                }
                Run(c, "UPDATE v7_order_lines SET renewal_id=NULL, activation_id=NULL, stock_unit_id=NULL WHERE order_id=$o", ("$o", id));
                var renOrderInvs = new List<long>();
                using (var iq = Cmd(c, "SELECT id FROM invoices WHERE v7_order_id=$o", ("$o", id)))
                using (var ir = iq.ExecuteReader())
                {
                    while (ir.Read()) renOrderInvs.Add(ir.GetInt64(0));
                }
                foreach (var invId in renOrderInvs) HardDeleteInvoice(c, invId);
                foreach (var ri in renItems)
                {
                    var linkedInvs = new List<long>();
                    using (var riq = Cmd(c, "SELECT id FROM invoices WHERE renewal_id=$r", ("$r", ri.RenId)))
                    using (var rir = riq.ExecuteReader())
                    {
                        while (rir.Read()) linkedInvs.Add(rir.GetInt64(0));
                    }
                    foreach (var invId in linkedInvs) HardDeleteInvoice(c, invId);
                    Run(c, "DELETE FROM renewals WHERE id=$r", ("$r", ri.RenId));

                    var remExp = Str(c, "SELECT COALESCE((SELECT new_expiry FROM renewals WHERE activation_id=$a AND deleted_at IS NULL ORDER BY renewed_at DESC, id DESC LIMIT 1), '')", ("$a", ri.ActId));
                    var reverted = !string.IsNullOrWhiteSpace(remExp) ? remExp : ri.PrevExp;
                    var remCnt = Scalar(c, "SELECT COUNT(*) FROM renewals WHERE activation_id=$a AND deleted_at IS NULL", ("$a", ri.ActId));
                    var remLastAt = Str(c, "SELECT COALESCE((SELECT MAX(renewed_at) FROM renewals WHERE activation_id=$a AND deleted_at IS NULL), '')", ("$a", ri.ActId));
                    Run(c, "UPDATE activations SET expires_at=$exp, renewal_count=$cnt, last_renewed_at=$lat WHERE id=$a",
                        ("$exp", reverted), ("$cnt", remCnt), ("$lat", remLastAt), ("$a", ri.ActId));
                }
                Run(c, "UPDATE v7_orders SET status='CANCELLED' WHERE id=$o AND status='COMPLETED'", ("$o", id));
                Run(c, "DELETE FROM v7_order_events WHERE order_id=$o", ("$o", id));
                Run(c, "DELETE FROM v7_order_lines WHERE order_id=$o", ("$o", id));
                Run(c, "DELETE FROM v7_orders WHERE id=$o", ("$o", id));
                return 0;
            }

            // 1. Thu thập toàn bộ stock_unit_id và activation_id gắn trực tiếp với đơn hàng này
            var stockUnitIds = new HashSet<long>();
            var activationIds = new HashSet<long>();

            using (var aq = Cmd(c, "SELECT id, stock_unit_id FROM activations WHERE v7_order_id=$o", ("$o", id)))
            using (var ar = aq.ExecuteReader())
            {
                while (ar.Read())
                {
                    activationIds.Add(ar.GetInt64(0));
                    if (!ar.IsDBNull(1) && ar.GetInt64(1) > 0) stockUnitIds.Add(ar.GetInt64(1));
                }
            }

            using (var eq = Cmd(c, "SELECT stock_unit_id FROM v7_stock_effects WHERE order_id=$o", ("$o", id)))
            using (var er = eq.ExecuteReader())
            {
                while (er.Read())
                {
                    if (!er.IsDBNull(0) && er.GetInt64(0) > 0) stockUnitIds.Add(er.GetInt64(0));
                }
            }

            using (var lq = Cmd(c, "SELECT COALESCE(stock_unit_id, 0), COALESCE(imei, '') FROM v7_order_lines WHERE order_id=$o", ("$o", id)))
            using (var lr = lq.ExecuteReader())
            {
                var lineImeis = new List<string>();
                while (lr.Read())
                {
                    var sid = lr.GetInt64(0);
                    var imei = lr.GetString(1).Trim();
                    if (sid > 0) stockUnitIds.Add(sid);
                    else if (!string.IsNullOrWhiteSpace(imei) && imei != "CHỜ-NHẬP-IMEI") lineImeis.Add(imei);
                }
                lr.Close();
                foreach (var imei in lineImeis)
                {
                    var sid = Scalar(c, "SELECT COALESCE((SELECT id FROM stock_units WHERE UPPER(TRIM(imei))=UPPER($i) AND deleted_at IS NULL LIMIT 1), 0)", ("$i", imei));
                    if (sid > 0)
                    {
                        // Nếu đơn đang xóa cứng là đơn chưa hoàn thành mà IMEI đang bị đơn 'CHỜ-NHẬP-IMEI' khác mượn tạm slot -> chuyển slot tạm trước để giải phóng sid
                        ReclaimAnonymouslyHeldStockUnitIfNeeded(c, sid, defaultWh, actor);
                        var ownedByOtherOrder = Scalar(c, @"SELECT COUNT(*) FROM v7_order_lines l2 JOIN v7_orders o2 ON o2.id=l2.order_id
                            WHERE l2.stock_unit_id=$s AND o2.id<>$o AND o2.status='COMPLETED' AND o2.deleted_at IS NULL AND l2.deleted_at IS NULL",
                            ("$s", sid), ("$o", id)) > 0
                            || Scalar(c, "SELECT COUNT(*) FROM activations a WHERE a.stock_unit_id=$s AND COALESCE(a.v7_order_id,0)<>$o AND a.deleted_at IS NULL",
                            ("$s", sid), ("$o", id)) > 0;
                        if (!ownedByOtherOrder)
                            stockUnitIds.Add(sid);
                    }
                }
            }

            foreach (var sid in stockUnitIds)
            {
                using var aq2 = Cmd(c, "SELECT id FROM activations WHERE stock_unit_id=$s AND (v7_order_id IS NULL OR v7_order_id=$o)", ("$s", sid), ("$o", id));
                using var ar2 = aq2.ExecuteReader();
                while (ar2.Read()) activationIds.Add(ar2.GetInt64(0));
            }

            // 2. Hoàn tác & xóa cứng lịch sử gia hạn (renewals) + chứng từ RENEWAL + kích hoạt thiết bị (activations)
            Run(c, "UPDATE v7_order_lines SET activation_id=NULL, stock_unit_id=NULL WHERE order_id=$o", ("$o", id));
            foreach (var actId in activationIds)
            {
                var renIds = new List<long>();
                using (var rq = Cmd(c, "SELECT id FROM renewals WHERE activation_id=$a", ("$a", actId)))
                using (var rr = rq.ExecuteReader())
                {
                    while (rr.Read()) renIds.Add(rr.GetInt64(0));
                }
                foreach (var renId in renIds)
                {
                    var renInvIds = new List<long>();
                    using (var riq = Cmd(c, "SELECT id FROM invoices WHERE renewal_id=$r", ("$r", renId)))
                    using (var rir = riq.ExecuteReader())
                    {
                        while (rir.Read()) renInvIds.Add(rir.GetInt64(0));
                    }
                    foreach (var rInvId in renInvIds) HardDeleteInvoice(c, rInvId);
                    Run(c, "DELETE FROM renewals WHERE id=$r", ("$r", renId));
                }
                Run(c, "DELETE FROM activations WHERE id=$a", ("$a", actId));
            }
            Run(c, "DELETE FROM activations WHERE v7_order_id=$o", ("$o", id));

            // 3. Hoàn tác Tồn kho IMEI (stock_units, v7_stock_effects, inventory_transaction_lines, inventory_transactions)
            Run(c, "DELETE FROM v7_stock_effects WHERE order_id=$o", ("$o", id));

            var affectedSaleOutTxIds = new HashSet<long>();
            foreach (var sid in stockUnitIds)
            {
                Run(c, "DELETE FROM v7_stock_effects WHERE stock_unit_id=$s", ("$s", sid));
                long txWh = defaultWh;
                using (var txq = Cmd(c, @"SELECT l.id, tx.id, COALESCE(tx.warehouse_id, 0)
                    FROM inventory_transaction_lines l
                    JOIN inventory_transactions tx ON tx.id = l.transaction_id
                    WHERE l.stock_unit_id=$s AND tx.movement_type='SALE_OUT'
                    ORDER BY l.id DESC", ("$s", sid)))
                using (var txr = txq.ExecuteReader())
                {
                    var lineIdsToDel = new List<long>();
                    while (txr.Read())
                    {
                        lineIdsToDel.Add(txr.GetInt64(0));
                        affectedSaleOutTxIds.Add(txr.GetInt64(1));
                        if (txr.GetInt64(2) > 0) txWh = txr.GetInt64(2);
                    }
                    txr.Close();
                    foreach (var tlId in lineIdsToDel)
                        Run(c, "DELETE FROM inventory_transaction_lines WHERE id=$id", ("$id", tlId));
                }

                var hasSupplierOrReceive = deductStock
                    || Scalar(c, "SELECT CASE WHEN supplier_id IS NOT NULL THEN 1 ELSE 0 END FROM stock_units WHERE id=$s", ("$s", sid)) == 1
                    || Scalar(c, @"SELECT COUNT(*) FROM inventory_transaction_lines l
                        JOIN inventory_transactions tx ON tx.id=l.transaction_id
                        WHERE l.stock_unit_id=$s AND tx.movement_type='RECEIVE'", ("$s", sid)) > 0;

                if (hasSupplierOrReceive)
                {
                    // Hoàn thiết bị về lại trạng thái IN_STOCK tại kho xuất
                    Run(c, @"UPDATE stock_units
                        SET status='IN_STOCK', warehouse_id=$w, customer_id=NULL, employee_id=NULL, deleted_at=NULL, updated_at=CURRENT_TIMESTAMP
                        WHERE id=$s",
                        ("$w", txWh > 0 ? txWh : defaultWh), ("$s", sid));
                }
                else if (Scalar(c, "SELECT COUNT(*) FROM warranty_cases WHERE stock_unit_id=$s", ("$s", sid)) == 0 &&
                         Scalar(c, "SELECT COUNT(*) FROM inventory_transaction_lines WHERE stock_unit_id=$s", ("$s", sid)) == 0)
                {
                    Run(c, "DELETE FROM stock_units WHERE id=$s", ("$s", sid));
                }
                else
                {
                    Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id, deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=$s", ("$s", sid));
                }
            }

            foreach (var txId in affectedSaleOutTxIds)
            {
                if (Scalar(c, "SELECT COUNT(*) FROM inventory_transaction_lines WHERE transaction_id=$t", ("$t", txId)) == 0)
                    Run(c, "DELETE FROM inventory_transactions WHERE id=$t AND movement_type='SALE_OUT'", ("$t", txId));
            }

            // 4. Hoàn tác Tồn kho Số lượng (xóa cứng bút toán xuất quantity_movements của đơn hàng này)
            Run(c, "DELETE FROM quantity_movements WHERE ref_type IN ('V7_ORDER','V7_ORDER_CANCEL') AND ref_id=$o", ("$o", id));

            // 5. Hoàn tác & xóa cứng toàn bộ chứng từ kế toán (SALE, SERVICE, phiếu thu/chi) gắn với đơn hàng
            var orderInvIds = new List<long>();
            using (var iq = Cmd(c, "SELECT id FROM invoices WHERE v7_order_id=$o", ("$o", id)))
            using (var ir = iq.ExecuteReader())
            {
                while (ir.Read()) orderInvIds.Add(ir.GetInt64(0));
            }
            foreach (var invId in orderInvIds)
            {
                HardDeleteInvoice(c, invId);
            }

            // 6. XÓA CỨNG (Hard Delete) toàn bộ sự kiện, dòng sản phẩm và đơn hàng khỏi CSDL
            Run(c, "DELETE FROM v7_orders WHERE id=$id AND status='COMPLETED'", ("$id", -1)); // no-op
            Run(c, "UPDATE v7_orders SET status='CANCELLED' WHERE id=$id AND status='COMPLETED'", ("$id", id));
            Run(c, "DELETE FROM v7_order_events WHERE order_id=$id", ("$id", id));
            Run(c, "DELETE FROM v7_order_lines WHERE order_id=$id", ("$id", id));
            Run(c, "DELETE FROM v7_orders WHERE id=$id", ("$id", id));

            // 7. Đồng bộ lại chứng từ Hoa hồng Seller (COMMISSION) đang mở (chưa khóa sổ)
            SyncOpenSellerCommissions(c);

            return 0;
        });

        if (notifyDevices)
        {
            DemoDb.NotifyDevicesChanged();
        }
    }

    /// <summary>
    /// Xóa mềm đơn hàng (dành cho Owner / Admin / Kế toán hoặc Main):
    /// Chuyển đơn sang xóa mềm (deleted_at), hoàn trả toàn bộ thiết bị IMEI & hàng số lượng về lại Kho (IN_STOCK),
    /// xóa mềm thiết bị kích hoạt và hủy chứng từ kế toán chưa khóa sổ để Kho xuất lại cho đơn khác bình thường.
    /// </summary>
    public string SoftDeleteOrder(long actor, long id)
    {
        var msg = Tx(actor, c =>
        {
            if (!IsAdmin(c, actor) && Scalar(c, "SELECT COUNT(*) FROM user_roles ur JOIN roles r ON r.id=ur.role_id WHERE ur.user_id=$u AND ur.deleted_at IS NULL AND r.code='ACCOUNTANT'", ("$u", actor)) == 0)
                throw new UnauthorizedAccessException("Chỉ Quản trị (Admin), Kế toán hoặc Chủ sở hữu (Owner) mới được xóa mềm đơn hàng.");

            using var oq = Cmd(c, "SELECT code, status, COALESCE(warehouse_id, 0), source, COALESCE(deduct_stock, 1), COALESCE(order_kind, 'INSTALL') FROM v7_orders WHERE id=$id AND deleted_at IS NULL", ("$id", id));
            using var or = oq.ExecuteReader();
            if (!or.Read()) throw new InvalidOperationException("Không tìm thấy đơn hàng hoặc đơn đã bị xóa.");
            var code = or.GetString(0);
            var curSt = or.GetString(1);
            var orderWh = or.GetInt64(2);
            var source = or.GetString(3);
            var deductStock = or.GetInt64(4) == 1 && source != "PARTNER" ? true : (or.GetInt64(4) == 1);
            var orderKind = or.GetString(5);
            or.Close();

            var lockedInvs = Scalar(c, "SELECT COUNT(*) FROM invoices WHERE v7_order_id=$o AND deleted_at IS NULL AND COALESCE(is_locked,0)=1", ("$o", id));
            if (lockedInvs > 0 && !IsMainDeveloper(c, actor))
                throw new InvalidOperationException($"Đơn hàng {code} có chứng từ kế toán đã chốt số khóa sổ. Vui lòng mở khóa chứng từ trước khi xóa mềm.");

            var nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var defaultWh = orderWh > 0 ? orderWh : Scalar(c, "SELECT COALESCE((SELECT id FROM warehouses WHERE deleted_at IS NULL ORDER BY id LIMIT 1), 1)");

            void SoftVoidInv(long invId)
            {
                var payIds = new List<long>();
                using (var pq = Cmd(c, "SELECT payment_id FROM payment_allocations WHERE invoice_id=$i AND deleted_at IS NULL", ("$i", invId)))
                using (var pr = pq.ExecuteReader())
                {
                    while (pr.Read()) payIds.Add(pr.GetInt64(0));
                }
                Run(c, "UPDATE payment_allocations SET deleted_at=$now, deleted_by=$actor WHERE invoice_id=$i AND deleted_at IS NULL",
                    ("$now", nowIso), ("$actor", actor), ("$i", invId));
                foreach (var pid in payIds.Distinct())
                {
                    if (Scalar(c, "SELECT COUNT(*) FROM payment_allocations WHERE payment_id=$p AND deleted_at IS NULL", ("$p", pid)) == 0)
                        Run(c, "UPDATE payments SET deleted_at=$now, deleted_by=$actor WHERE id=$p AND deleted_at IS NULL", ("$now", nowIso), ("$actor", actor), ("$p", pid));
                }
                Run(c, "UPDATE invoice_lines SET deleted_at=$now, deleted_by=$actor WHERE invoice_id=$i AND deleted_at IS NULL",
                    ("$now", nowIso), ("$actor", actor), ("$i", invId));
                Run(c, "UPDATE invoices SET status='VOID', deleted_at=$now, deleted_by=$actor WHERE id=$i AND deleted_at IS NULL",
                    ("$now", nowIso), ("$actor", actor), ("$i", invId));
            }

            if (curSt == "COMPLETED")
                Run(c, "UPDATE v7_orders SET status='CANCELLED' WHERE id=$o", ("$o", id));

            if (string.Equals(orderKind, "RENEWAL", StringComparison.OrdinalIgnoreCase))
            {
                var renItems = new List<(long RenId, long ActId, string PrevExp)>();
                using (var rq = Cmd(c, "SELECT id, activation_id, previous_expiry FROM renewals WHERE v7_order_id=$o AND deleted_at IS NULL", ("$o", id)))
                using (var rr = rq.ExecuteReader())
                {
                    while (rr.Read()) renItems.Add((rr.GetInt64(0), rr.GetInt64(1), rr.GetString(2)));
                }
                var renInvs = new List<long>();
                using (var iq = Cmd(c, "SELECT id FROM invoices WHERE v7_order_id=$o AND deleted_at IS NULL", ("$o", id)))
                using (var ir = iq.ExecuteReader())
                {
                    while (ir.Read()) renInvs.Add(ir.GetInt64(0));
                }
                foreach (var invId in renInvs) SoftVoidInv(invId);
                foreach (var ri in renItems)
                {
                    Run(c, "UPDATE renewals SET deleted_at=$now, deleted_by=$actor WHERE id=$r", ("$now", nowIso), ("$actor", actor), ("$r", ri.RenId));
                    var remExp = Str(c, "SELECT COALESCE((SELECT new_expiry FROM renewals WHERE activation_id=$a AND deleted_at IS NULL ORDER BY renewed_at DESC, id DESC LIMIT 1), '')", ("$a", ri.ActId));
                    var reverted = !string.IsNullOrWhiteSpace(remExp) ? remExp : ri.PrevExp;
                    var remCnt = Scalar(c, "SELECT COUNT(*) FROM renewals WHERE activation_id=$a AND deleted_at IS NULL", ("$a", ri.ActId));
                    var remLastAt = Str(c, "SELECT COALESCE((SELECT MAX(renewed_at) FROM renewals WHERE activation_id=$a AND deleted_at IS NULL), '')", ("$a", ri.ActId));
                    Run(c, "UPDATE activations SET expires_at=$exp, renewal_count=$cnt, last_renewed_at=$lat WHERE id=$a",
                        ("$exp", reverted), ("$cnt", remCnt), ("$lat", remLastAt), ("$a", ri.ActId));
                }
                Run(c, "UPDATE v7_order_lines SET renewal_id=NULL, activation_id=NULL, stock_unit_id=NULL, deleted_at=$now, deleted_by=$actor WHERE order_id=$o AND deleted_at IS NULL",
                    ("$now", nowIso), ("$actor", actor), ("$o", id));
                Run(c, "UPDATE v7_orders SET status='CANCELLED', deleted_at=$now, deleted_by=$actor WHERE id=$o",
                    ("$now", nowIso), ("$actor", actor), ("$o", id));
                return $"Đã xóa mềm Đơn gia hạn {code}, khôi phục hạn cũ của xe và hủy chứng từ kế toán liên quan.";
            }

            var stockUnitIds = new HashSet<long>();
            var actList = new List<(long ActId, long Sid, long ProdId, long CustId, string Imei)>();
            using (var aq = Cmd(c, "SELECT a.id, a.stock_unit_id, a.product_id, a.customer_id, u.imei FROM activations a JOIN stock_units u ON u.id=a.stock_unit_id WHERE a.v7_order_id=$o AND a.deleted_at IS NULL", ("$o", id)))
            using (var ar = aq.ExecuteReader())
            {
                while (ar.Read())
                {
                    var sid = ar.GetInt64(1);
                    actList.Add((ar.GetInt64(0), sid, ar.GetInt64(2), ar.GetInt64(3), ar.GetString(4)));
                    if (sid > 0) stockUnitIds.Add(sid);
                }
            }

            using (var eq = Cmd(c, "SELECT stock_unit_id FROM v7_stock_effects WHERE order_id=$o AND deleted_at IS NULL", ("$o", id)))
            using (var er = eq.ExecuteReader())
            {
                while (er.Read())
                    if (!er.IsDBNull(0) && er.GetInt64(0) > 0) stockUnitIds.Add(er.GetInt64(0));
            }

            using (var lq = Cmd(c, "SELECT COALESCE(stock_unit_id, 0), COALESCE(imei, '') FROM v7_order_lines WHERE order_id=$o", ("$o", id)))
            using (var lr = lq.ExecuteReader())
            {
                var lineImeis = new List<string>();
                while (lr.Read())
                {
                    var sid = lr.GetInt64(0);
                    var imei = lr.GetString(1).Trim();
                    if (sid > 0) stockUnitIds.Add(sid);
                    else if (!string.IsNullOrWhiteSpace(imei) && imei != "CHỜ-NHẬP-IMEI") lineImeis.Add(imei);
                }
                lr.Close();
                foreach (var imei in lineImeis)
                {
                    var sid = Scalar(c, "SELECT COALESCE((SELECT id FROM stock_units WHERE UPPER(TRIM(imei))=UPPER($i) AND deleted_at IS NULL LIMIT 1), 0)", ("$i", imei));
                    if (sid > 0)
                    {
                        ReclaimAnonymouslyHeldStockUnitIfNeeded(c, sid, defaultWh, actor);
                        var ownedByOther = Scalar(c, "SELECT COUNT(*) FROM activations a WHERE a.stock_unit_id=$s AND COALESCE(a.v7_order_id,0)<>$o AND a.deleted_at IS NULL", ("$s", sid), ("$o", id)) > 0
                            || Scalar(c, "SELECT COUNT(*) FROM v7_order_lines l2 JOIN v7_orders o2 ON o2.id=l2.order_id WHERE l2.stock_unit_id=$s AND o2.id<>$o AND o2.status='COMPLETED' AND o2.deleted_at IS NULL AND l2.deleted_at IS NULL", ("$s", sid), ("$o", id)) > 0;
                        if (!ownedByOther) stockUnitIds.Add(sid);
                    }
                }
            }

            foreach (var act in actList)
            {
                var renIds = new List<long>();
                using (var rq = Cmd(c, "SELECT id FROM renewals WHERE activation_id=$a AND deleted_at IS NULL", ("$a", act.ActId)))
                using (var rr = rq.ExecuteReader())
                {
                    while (rr.Read()) renIds.Add(rr.GetInt64(0));
                }
                foreach (var rId in renIds)
                {
                    var rInvs = new List<long>();
                    using (var riq = Cmd(c, "SELECT id FROM invoices WHERE renewal_id=$r AND deleted_at IS NULL", ("$r", rId)))
                    using (var rir = riq.ExecuteReader())
                    {
                        while (rir.Read()) rInvs.Add(rir.GetInt64(0));
                    }
                    foreach (var rInvId in rInvs) SoftVoidInv(rInvId);
                    Run(c, "UPDATE renewals SET deleted_at=$now, deleted_by=$actor WHERE id=$r", ("$now", nowIso), ("$actor", actor), ("$r", rId));
                }

                // Tách archive stock_unit cho bản ghi activations xóa mềm để giải phóng UNIQUE(stock_unit_id)
                Run(c, @"INSERT INTO stock_units(product_id,imei,status,warehouse_id,customer_id,employee_id,created_at,updated_at,deleted_at,deleted_by)
                    VALUES($p,$imei,'INSTALLED',NULL,$c,NULL,datetime('now'),datetime('now'),$now,$actor)",
                    ("$p", act.ProdId), ("$imei", $"{act.Imei}#ORDDEL#{act.ActId}"), ("$c", act.CustId), ("$now", nowIso), ("$actor", actor));
                var archId = Scalar(c, "SELECT last_insert_rowid()");
                Run(c, "UPDATE activations SET stock_unit_id=$arch, status='DISABLED', deleted_at=$now, deleted_by=$actor WHERE id=$a",
                    ("$arch", archId), ("$now", nowIso), ("$actor", actor), ("$a", act.ActId));
            }

            int returnedImeiCount = 0;
            foreach (var sid in stockUnitIds)
            {
                DetachStockEffectToArchive(c, sid, actor);
                Run(c, @"UPDATE inventory_transaction_lines SET deleted_at=$now, deleted_by=$actor
                    WHERE stock_unit_id=$s AND deleted_at IS NULL
                      AND transaction_id IN (SELECT id FROM inventory_transactions WHERE movement_type='SALE_OUT')",
                    ("$now", nowIso), ("$actor", actor), ("$s", sid));

                var hasSupplierOrReceive = deductStock
                    || Scalar(c, "SELECT CASE WHEN supplier_id IS NOT NULL THEN 1 ELSE 0 END FROM stock_units WHERE id=$s", ("$s", sid)) == 1
                    || Scalar(c, "SELECT COUNT(*) FROM inventory_transaction_lines l JOIN inventory_transactions tx ON tx.id=l.transaction_id WHERE l.stock_unit_id=$s AND tx.movement_type='RECEIVE'", ("$s", sid)) > 0;
                if (hasSupplierOrReceive)
                {
                    Run(c, "UPDATE stock_units SET status='IN_STOCK', warehouse_id=$w, customer_id=NULL, employee_id=NULL, deleted_at=NULL, updated_at=CURRENT_TIMESTAMP WHERE id=$s",
                        ("$w", defaultWh), ("$s", sid));
                    returnedImeiCount++;
                }
                else
                {
                    Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id, deleted_at=$now, deleted_by=$actor WHERE id=$s",
                        ("$now", nowIso), ("$actor", actor), ("$s", sid));
                }
            }

            // Hoàn trả tồn kho Số lượng (quantity_movements) nếu đơn đã từng xuất
            var qtyIssues = new List<(long ProdId, long WhId, long LocId, long IssuedQty)>();
            using (var qq = Cmd(c, @"SELECT product_id, warehouse_id, location_id, ABS(SUM(delta_qty))
                FROM quantity_movements
                WHERE ref_type='V7_ORDER' AND ref_id=$o AND delta_qty < 0 AND deleted_at IS NULL
                GROUP BY product_id, warehouse_id, location_id", ("$o", id)))
            using (var qr = qq.ExecuteReader())
            {
                while (qr.Read())
                {
                    var qVal = qr.GetInt64(3);
                    if (qVal > 0) qtyIssues.Add((qr.GetInt64(0), qr.GetInt64(1), qr.GetInt64(2), qVal));
                }
            }
            foreach (var qi in qtyIssues)
            {
                var alreadyReversed = Scalar(c, "SELECT COUNT(*) FROM quantity_movements WHERE ref_type='V7_ORDER_CANCEL' AND ref_id=$o AND product_id=$p AND deleted_at IS NULL",
                    ("$o", id), ("$p", qi.ProdId)) > 0;
                if (!alreadyReversed)
                {
                    Run(c, @"INSERT INTO quantity_movements(document_no,product_id,warehouse_id,location_id,delta_qty,movement_type,ref_type,ref_id,note,actor_user_id)
                        VALUES('QR-'||lower(hex(randomblob(8))),$p,$w,$l,$d,'RECEIPT','V7_ORDER_CANCEL',$o,$note,$u)",
                        ("$p", qi.ProdId), ("$w", qi.WhId), ("$l", qi.LocId), ("$d", qi.IssuedQty), ("$o", id),
                        ("$note", $"Hoàn nhập kho do xóa mềm đơn {code}"), ("$u", actor));
                }
            }

            var ordInvIds = new List<long>();
            using (var iq = Cmd(c, "SELECT id FROM invoices WHERE v7_order_id=$o AND deleted_at IS NULL", ("$o", id)))
            using (var ir = iq.ExecuteReader())
            {
                while (ir.Read()) ordInvIds.Add(ir.GetInt64(0));
            }
            foreach (var invId in ordInvIds) SoftVoidInv(invId);

            Run(c, "UPDATE v7_order_lines SET imei=NULL, stock_unit_id=NULL, activation_id=NULL, deleted_at=$now, deleted_by=$actor WHERE order_id=$o AND deleted_at IS NULL",
                ("$now", nowIso), ("$actor", actor), ("$o", id));
            Run(c, "UPDATE v7_orders SET status='CANCELLED', imei=NULL, deleted_at=$now, deleted_by=$actor WHERE id=$o",
                ("$now", nowIso), ("$actor", actor), ("$o", id));

            SyncOpenSellerCommissions(c);
            return $"Đã xóa mềm đơn hàng {code}: Đã hoàn trả {returnedImeiCount} thiết bị IMEI{(qtyIssues.Count > 0 ? $" & {qtyIssues.Sum(x => x.IssuedQty)} SP số lượng" : "")} về lại Kho để thực hiện đơn khác.";
        });

        DemoDb.NotifyDevicesChanged();
        return msg;
    }

    private static void SyncOpenSellerCommissions(SqliteConnection c)
    {
        using var sq = Cmd(c, @"SELECT s.id, s.code, s.full_name, COALESCE(s.order_commission_vnd,0), COALESCE(s.renewal_commission_vnd,0),
            (SELECT COALESCE(SUM(o.quantity),0) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
             WHERE o.deleted_at IS NULL AND o.status='COMPLETED' AND COALESCE(o.order_kind,'INSTALL')<>'RENEWAL'
               AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%'))))
            + (SELECT COALESCE(SUM(il.quantity),0) FROM invoices i JOIN invoice_lines il ON il.invoice_id=i.id JOIN customers cu ON cu.id=i.customer_id
               WHERE i.deleted_at IS NULL AND il.deleted_at IS NULL AND i.invoice_type='SALE' AND i.v7_order_id IS NULL AND i.status<>'VOID' AND cu.seller_id=s.id) AS dev_qty,
            (SELECT COUNT(*) FROM renewals r
             WHERE r.deleted_at IS NULL
               AND (r.seller_id=s.id OR (r.seller_id IS NULL AND (r.seller_name LIKE '%'||s.code||'%' OR r.seller_name LIKE '%'||s.full_name||'%')))) AS ren_cnt
            FROM v7_sellers s WHERE s.deleted_at IS NULL");
        using var sr = sq.ExecuteReader();
        var sellers = new List<(long Id, string Code, string Name, long OrderRate, long RenRate, long DevQty, long RenCnt)>();
        while (sr.Read())
            sellers.Add((sr.GetInt64(0), sr.GetString(1), sr.GetString(2), sr.GetInt64(3), sr.GetInt64(4), sr.GetInt64(5), sr.GetInt64(6)));
        sr.Close();

        foreach (var s in sellers)
        {
            var openCommInvId = Scalar(c, @"SELECT COALESCE((SELECT id FROM invoices
                WHERE invoice_type='COMMISSION' AND status='OPEN' AND is_locked=0 AND deleted_at IS NULL
                  AND (notes LIKE '%'||$code||'%' OR notes LIKE '%'||$name||'%') LIMIT 1), 0)",
                ("$code", s.Code), ("$name", s.Name));
            if (openCommInvId > 0)
            {
                var est = s.DevQty * s.OrderRate + s.RenCnt * s.RenRate;
                var desc = $"Chi hoa hồng cuối kỳ cho NV bán hàng {s.Name} ({s.Code}): {s.DevQty} thiết bị hoàn thành + {s.RenCnt} lượt gia hạn.";
                Run(c, "UPDATE invoices SET amount_vnd=$amt, notes=$notes WHERE id=$id",
                    ("$amt", est), ("$notes", $"{s.Name} ({s.Code}) — {desc}"), ("$id", openCommInvId));
                Run(c, "UPDATE invoice_lines SET description=$desc, quantity=1, unit_price_vnd=$amt WHERE invoice_id=$id AND deleted_at IS NULL",
                    ("$desc", desc), ("$amt", est), ("$id", openCommInvId));
            }
        }
    }

    public void UpdateCompletedOrderMain(
        long actor,
        long id,
        long customerId,
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
        bool noInstallFee,
        long? sellerRecordId = null,
        string? saleSource = null,
        string? activatedDate = null,
        IReadOnlyList<V7OrderLineEdit>? lineEdits = null)
    {
        Tx(actor, c => {
            if (!IsMainDeveloper(c, actor))
                throw new UnauthorizedAccessException("Chỉ tài khoản quản trị hệ thống cao nhất mới có quyền điều chỉnh đơn hàng đã hoàn thành.");

            var st = Status(c, id);
            if (st != "COMPLETED")
                throw new InvalidOperationException("Đơn hàng chưa hoàn thành, vui lòng dùng thao tác lưu thông thường.");

            if (Scalar(c, "SELECT COUNT(*) FROM invoices WHERE v7_order_id=$id AND deleted_at IS NULL AND COALESCE(is_locked,0)=1", ("$id", id)) > 0)
                throw new InvalidOperationException("Đơn hàng đã có chứng từ kế toán chốt số / khóa sổ. Vui lòng mở khóa chứng từ bên Công nợ & Kế toán trước khi điều chỉnh.");

            if (!deductStock && !partner)
                throw new InvalidOperationException("Chỉ cho phép chọn 'Không trừ tồn kho' khi Đơn vị lắp đặt là Hãng lắp đặt.");
            if (!deductStock && string.IsNullOrWhiteSpace(installer) && !(installerEmployeeId is > 0))
                throw new InvalidOperationException("Khi chọn 'Không trừ tồn kho', bắt buộc phải có tên Hãng lắp đặt.");

            long oldCustomerId, oldWarehouseId;
            bool oldDeductStock, oldNoInstallFee;
            string oldSource, orderCode, existingActivatedAt;
            using (var oq = Cmd(c, @"SELECT customer_id, COALESCE(warehouse_id,0), source, COALESCE(deduct_stock,1), COALESCE(no_install_fee,0),
                COALESCE(code,''), COALESCE(activated_at,'') FROM v7_orders WHERE id=$id", ("$id", id)))
            using (var or = oq.ExecuteReader())
            {
                if (!or.Read()) throw new InvalidOperationException("Không tìm thấy đơn hàng.");
                oldCustomerId = or.GetInt64(0);
                oldWarehouseId = or.GetInt64(1);
                oldSource = or.GetString(2);
                oldDeductStock = or.GetInt64(3) == 1 && oldSource != "PARTNER" ? true : (or.GetInt64(3) == 1);
                oldNoInstallFee = or.GetInt64(4) == 1;
                orderCode = or.GetString(5);
                existingActivatedAt = or.GetString(6);
            }

            var effCustomerId = customerId > 0 ? customerId : oldCustomerId;
            if (Scalar(c, "SELECT COUNT(*) FROM customers WHERE id=$id AND deleted_at IS NULL", ("$id", effCustomerId)) == 0)
                throw new InvalidOperationException("Khách hàng đã chọn không hợp lệ.");

            var defaultWh = Scalar(c, "SELECT COALESCE((SELECT id FROM warehouses WHERE deleted_at IS NULL ORDER BY id LIMIT 1), 1)");
            var effWarehouse = (warehouseId is > 0 && Scalar(c, "SELECT COUNT(*) FROM warehouses WHERE id=$w AND deleted_at IS NULL", ("$w", warehouseId.Value)) > 0)
                ? warehouseId.Value
                : (oldWarehouseId > 0 ? oldWarehouseId : defaultWh);

            var effDuration = durationMonths > 0 ? durationMonths : 12;
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
            var expDateStr = actDate.AddMonths((int)effDuration).ToString("yyyy-MM-dd");

            long? empId = installerEmployeeId is > 0 ? installerEmployeeId : null;
            var installerName = (installer ?? "").Trim();
            if (empId.HasValue)
            {
                var empName = Str(c, "SELECT full_name FROM employees WHERE id=$id AND deleted_at IS NULL", ("$id", empId.Value));
                if (string.IsNullOrWhiteSpace(empName)) throw new InvalidOperationException("Nhân sự lắp đặt không tồn tại.");
                if (string.IsNullOrWhiteSpace(installerName)) installerName = empName;
            }
            else if (string.IsNullOrWhiteSpace(installerName))
            {
                if (partner) throw new InvalidOperationException("Bắt buộc phải nhập tên Hãng lắp đặt.");
                installerName = noInstallFee ? "Giao thiết bị (Không lắp đặt)" : "ĐV lắp đặt nội bộ";
            }

            var (resolvedSellerId, resolvedSrc) = ResolveOrderSeller(c, effCustomerId, actor, sellerRecordId, saleSource);

            // 1. Đọc danh sách dòng hiện tại của đơn hàng
            var dbLines = new List<(long LineId, long ProductId, string ProductName, bool IsSerialized, long Qty, long OldUnitPrice, long UnitCost, string OldImei, string OldPlate, string OldVin, long StockUnitId, string ProductKind)>();
            using (var lq = Cmd(c, @"SELECT l.id, l.product_id, p.name, p.is_serialized, l.quantity, l.unit_price_vnd, l.unit_cost_snapshot_vnd,
                COALESCE(l.imei,''), COALESCE(l.plate_no,''), COALESCE(l.vin_no,''), COALESCE(l.stock_unit_id,0), COALESCE(m.product_kind,'GOODS')
                FROM v7_order_lines l JOIN products p ON p.id=l.product_id
                LEFT JOIN product_catalog_metadata m ON m.product_id=p.id
                WHERE l.order_id=$o AND l.deleted_at IS NULL ORDER BY l.id", ("$o", id)))
            using (var lr = lq.ExecuteReader())
            {
                while (lr.Read())
                    dbLines.Add((lr.GetInt64(0), lr.GetInt64(1), lr.GetString(2), lr.GetInt64(3) == 1, lr.GetInt64(4), lr.GetInt64(5), lr.GetInt64(6), lr.GetString(7), lr.GetString(8), lr.GetString(9), lr.GetInt64(10), lr.GetString(11)));
            }

            // 2. Đồng bộ chế độ tồn kho nếu chuyển đổi giữa Trừ kho <-> Không trừ kho (Hãng)
            if (oldDeductStock != deductStock)
            {
                if (oldDeductStock && !deductStock)
                {
                    // Hoàn trả tồn kho đã trừ về lại kho xuất (khi chuyển sang Hãng lắp đặt - Không trừ kho)
                    var stockIds = new HashSet<long>();
                    using (var eq = Cmd(c, "SELECT stock_unit_id FROM v7_stock_effects WHERE order_id=$o", ("$o", id)))
                    using (var er = eq.ExecuteReader())
                    {
                        while (er.Read()) if (!er.IsDBNull(0) && er.GetInt64(0) > 0) stockIds.Add(er.GetInt64(0));
                    }
                    foreach (var ln in dbLines) if (ln.StockUnitId > 0) stockIds.Add(ln.StockUnitId);

                    Run(c, "DELETE FROM v7_stock_effects WHERE order_id=$o", ("$o", id));
                    var affectedSaleOutTx = new HashSet<long>();
                    var restoreWh = oldWarehouseId > 0 ? oldWarehouseId : effWarehouse;

                    foreach (var sid in stockIds)
                    {
                        long txWh = restoreWh;
                        using (var txq = Cmd(c, @"SELECT l.id, tx.id, COALESCE(tx.warehouse_id, 0)
                            FROM inventory_transaction_lines l JOIN inventory_transactions tx ON tx.id=l.transaction_id
                            WHERE l.stock_unit_id=$s AND tx.movement_type='SALE_OUT'", ("$s", sid)))
                        using (var txr = txq.ExecuteReader())
                        {
                            var delIds = new List<long>();
                            while (txr.Read())
                            {
                                delIds.Add(txr.GetInt64(0));
                                affectedSaleOutTx.Add(txr.GetInt64(1));
                                if (txr.GetInt64(2) > 0) txWh = txr.GetInt64(2);
                            }
                            txr.Close();
                            foreach (var tlId in delIds) Run(c, "DELETE FROM inventory_transaction_lines WHERE id=$id", ("$id", tlId));
                        }

                        var hasAct = Scalar(c, "SELECT COUNT(*) FROM activations WHERE stock_unit_id=$s AND deleted_at IS NULL", ("$s", sid)) > 0;
                        var unitImei = Str(c, "SELECT COALESCE(imei,'') FROM stock_units WHERE id=$s", ("$s", sid));
                        if (!hasAct && unitImei.StartsWith("KHO-", StringComparison.OrdinalIgnoreCase))
                        {
                            Run(c, "UPDATE stock_units SET status='IN_STOCK', warehouse_id=$w, customer_id=NULL, employee_id=NULL, updated_at=CURRENT_TIMESTAMP WHERE id=$s",
                                ("$w", txWh), ("$s", sid));
                            Run(c, "UPDATE v7_order_lines SET stock_unit_id=NULL WHERE order_id=$o AND stock_unit_id=$s", ("$o", id), ("$s", sid));
                        }
                    }
                    foreach (var txId in affectedSaleOutTx)
                    {
                        if (Scalar(c, "SELECT COUNT(*) FROM inventory_transaction_lines WHERE transaction_id=$t", ("$t", txId)) == 0)
                            Run(c, "DELETE FROM inventory_transactions WHERE id=$t AND movement_type='SALE_OUT'", ("$t", txId));
                    }
                    Run(c, "DELETE FROM quantity_movements WHERE ref_type='V7_ORDER' AND ref_id=$o", ("$o", id));
                }
                else if (!oldDeductStock && deductStock)
                {
                    // Chuyển từ Không trừ kho sang Trừ tồn kho: xuất bổ sung các dòng chưa trừ kho
                    long? saleOutTxId = null;
                    for (int idx = 0; idx < dbLines.Count; idx++)
                    {
                        var ln = dbLines[idx];
                        if (ln.IsSerialized && ln.StockUnitId <= 0)
                        {
                            using var findSlot = Cmd(c, @"SELECT id, COALESCE(warehouse_id, $w) FROM stock_units
                                WHERE product_id=$p AND status='IN_STOCK' AND deleted_at IS NULL
                                ORDER BY CASE WHEN warehouse_id=$w THEN 0 ELSE 1 END,
                                         CASE WHEN imei LIKE 'KHO-%' THEN 0 ELSE 1 END, id ASC LIMIT 1",
                                ("$p", ln.ProductId), ("$w", effWarehouse));
                            using var sr = findSlot.ExecuteReader();
                            if (!sr.Read())
                                throw new InvalidOperationException($"Sản phẩm '{ln.ProductName}' không đủ tồn kho để chuyển sang chế độ Trừ tồn kho.");
                            var slotId = sr.GetInt64(0);
                            var txWh = sr.IsDBNull(1) ? effWarehouse : sr.GetInt64(1);
                            sr.Close();

                            if (!saleOutTxId.HasValue)
                            {
                                var txCode = "XK-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
                                Run(c, "INSERT INTO inventory_transactions(code,movement_type,warehouse_id,customer_id,actor_user_id,notes) VALUES($code,'SALE_OUT',$w,$c,$actor,'Xuất lắp đặt đơn V7 (điều chỉnh)')",
                                    ("$code", txCode), ("$w", txWh), ("$c", effCustomerId), ("$actor", actor));
                                saleOutTxId = Scalar(c, "SELECT last_insert_rowid()");
                            }
                            Run(c, "INSERT INTO inventory_transaction_lines(transaction_id,stock_unit_id,product_id,unit_cost_snapshot_vnd) VALUES($t,$s,$p,$cost)",
                                ("$t", saleOutTxId.Value), ("$s", slotId), ("$p", ln.ProductId), ("$cost", ln.UnitCost));
                            Run(c, "UPDATE stock_units SET status='INSTALLED', warehouse_id=NULL, customer_id=$c, employee_id=NULL, updated_at=CURRENT_TIMESTAMP WHERE id=$id",
                                ("$c", effCustomerId), ("$id", slotId));
                            Run(c, "INSERT INTO v7_stock_effects(order_id,stock_unit_id) VALUES($o,$s)", ("$o", id), ("$s", slotId));
                            Run(c, "UPDATE v7_order_lines SET stock_unit_id=$sid WHERE id=$lid", ("$sid", slotId), ("$lid", ln.LineId));
                            dbLines[idx] = ln with { StockUnitId = slotId };
                        }
                        else if (!ln.IsSerialized && ln.ProductKind == "GOODS")
                        {
                            var locId = Scalar(c, @"SELECT l.id FROM warehouse_locations l
                                WHERE l.warehouse_id=$w AND l.is_active=1 AND l.deleted_at IS NULL
                                ORDER BY CASE WHEN l.code='MAIN' THEN 0 ELSE 1 END, l.id LIMIT 1", ("$w", effWarehouse));
                            var curQty = locId > 0
                                ? Scalar(c, "SELECT COALESCE(SUM(delta_qty),0) FROM quantity_movements WHERE product_id=$p AND warehouse_id=$w AND location_id=$l",
                                    ("$p", ln.ProductId), ("$w", effWarehouse), ("$l", locId))
                                : 0;
                            if (locId <= 0 || curQty < ln.Qty)
                                throw new InvalidOperationException($"Sản phẩm '{ln.ProductName}' không đủ tồn kho để trừ ({curQty}/{ln.Qty}).");
                            Run(c, @"INSERT INTO quantity_movements(document_no,product_id,warehouse_id,location_id,delta_qty,movement_type,ref_type,ref_id,note,actor_user_id)
                                VALUES('Q-'||lower(hex(randomblob(8))),$p,$w,$l,$d,'ISSUE','V7_ORDER',$o,'Xuất bổ sung điều chỉnh đơn SO',$u)",
                                ("$p", ln.ProductId), ("$w", effWarehouse), ("$l", locId), ("$d", -ln.Qty), ("$o", id), ("$u", actor));
                        }
                    }
                }
            }

            // Đọc lại stock_unit_id mới nhất của từng dòng
            dbLines.Clear();
            using (var lq2 = Cmd(c, @"SELECT l.id, l.product_id, p.name, p.is_serialized, l.quantity, l.unit_price_vnd, l.unit_cost_snapshot_vnd,
                COALESCE(l.imei,''), COALESCE(l.plate_no,''), COALESCE(l.vin_no,''), COALESCE(l.stock_unit_id,0), COALESCE(m.product_kind,'GOODS')
                FROM v7_order_lines l JOIN products p ON p.id=l.product_id
                LEFT JOIN product_catalog_metadata m ON m.product_id=p.id
                WHERE l.order_id=$o AND l.deleted_at IS NULL ORDER BY l.id", ("$o", id)))
            using (var lr2 = lq2.ExecuteReader())
            {
                while (lr2.Read())
                    dbLines.Add((lr2.GetInt64(0), lr2.GetInt64(1), lr2.GetString(2), lr2.GetInt64(3) == 1, lr2.GetInt64(4), lr2.GetInt64(5), lr2.GetInt64(6), lr2.GetString(7), lr2.GetString(8), lr2.GetString(9), lr2.GetInt64(10), lr2.GetString(11)));
            }

            // 3. Tính toán & cập nhật Đơn giá bán + Công lắp từng dòng
            var editMap = (lineEdits ?? []).ToDictionary(x => x.LineId, x => x);
            long totalPrice = 0;
            long totalInstallerFee = 0;
            var updatedLinePricing = new List<(long ProductId, string ProductName, long Qty, long UnitPrice, long InstallerFee)>();

            foreach (var ln in dbLines)
            {
                editMap.TryGetValue(ln.LineId, out var le);
                long newUnitPrice;
                if (le?.UnitPriceOverride is >= 0)
                {
                    newUnitPrice = le.UnitPriceOverride.Value;
                }
                else if (noInstallFee != oldNoInstallFee || effCustomerId != oldCustomerId)
                {
                    newUnitPrice = QuoteOrderPrice(c, effCustomerId, ln.ProductId, includeInstallPrice: !noInstallFee).Price;
                }
                else
                {
                    newUnitPrice = ln.OldUnitPrice;
                }

                long newUnitFee = 0;
                if (!noInstallFee && empId.HasValue)
                {
                    using var rq = Cmd(c, "SELECT install_rate_vnd FROM v7_installer_product_rates WHERE employee_id=$e AND product_id=$p AND deleted_at IS NULL",
                        ("$e", empId.Value), ("$p", ln.ProductId));
                    var rateVal = rq.ExecuteScalar();
                    newUnitFee = rateVal is not null ? Convert.ToInt64(rateVal) : Scalar(c, "SELECT technical_fee_vnd FROM products WHERE id=$p", ("$p", ln.ProductId));
                }

                Run(c, "UPDATE v7_order_lines SET unit_price_vnd=$price, installer_fee_vnd=$fee WHERE id=$lid",
                    ("$price", newUnitPrice), ("$fee", newUnitFee), ("$lid", ln.LineId));

                totalPrice += newUnitPrice * ln.Qty;
                totalInstallerFee += newUnitFee * ln.Qty;
                updatedLinePricing.Add((ln.ProductId, ln.ProductName, ln.Qty, newUnitPrice, newUnitFee));
            }

            // 4. Đồng bộ IMEI / Biển số / VIN / Thiết bị kích hoạt (activations & stock_units)
            if (lineEdits is { Count: > 0 })
            {
                var seenImeis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allImeis = new List<string>();
                var allPlates = new List<string>();
                var allVins = new List<string>();

                foreach (var ln in dbLines)
                {
                    if (!ln.IsSerialized) continue;
                    editMap.TryGetValue(ln.LineId, out var rep);
                    var cleanImei = (rep?.Imei ?? ln.OldImei).Trim();
                    var cleanPlate = (rep?.PlateNo ?? ln.OldPlate).Trim();
                    var cleanVin = (rep?.VinNo ?? ln.OldVin).Trim();

                    if (!string.IsNullOrWhiteSpace(cleanImei) && !seenImeis.Add(cleanImei))
                        throw new InvalidOperationException($"Mã IMEI '{cleanImei}' bị trùng lặp trong cùng đơn hàng.");

                    if (string.IsNullOrWhiteSpace(cleanImei))
                    {
                        Run(c, "UPDATE v7_order_lines SET plate_no=$plate, vin_no=$vin WHERE id=$lid",
                            ("$plate", cleanPlate), ("$vin", cleanVin), ("$lid", ln.LineId));
                        if (!string.IsNullOrWhiteSpace(cleanPlate)) allPlates.Add(cleanPlate);
                        if (!string.IsNullOrWhiteSpace(cleanVin)) allVins.Add(cleanVin);
                        continue;
                    }

                    long stockUnitId = ln.StockUnitId;
                    if (stockUnitId <= 0 && !string.IsNullOrWhiteSpace(ln.OldImei))
                    {
                        stockUnitId = Scalar(c, "SELECT COALESCE((SELECT id FROM stock_units WHERE imei=$imei AND deleted_at IS NULL LIMIT 1), 0)", ("$imei", ln.OldImei));
                    }

                    var existingActId = stockUnitId > 0
                        ? Scalar(c, "SELECT COALESCE((SELECT id FROM activations WHERE stock_unit_id=$sid AND deleted_at IS NULL LIMIT 1), 0)", ("$sid", stockUnitId))
                        : 0;

                    if (existingActId > 0 && stockUnitId > 0)
                    {
                        if (!string.Equals(cleanImei, ln.OldImei, StringComparison.OrdinalIgnoreCase))
                        {
                            if (Scalar(c, "SELECT COUNT(*) FROM stock_units WHERE imei=$imei AND id<>$sid AND deleted_at IS NULL", ("$imei", cleanImei), ("$sid", stockUnitId)) > 0)
                                throw new InvalidOperationException($"IMEI '{cleanImei}' đã tồn tại trên thiết bị khác.");
                            Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id WHERE imei=$imei AND id<>$sid AND deleted_at IS NOT NULL", ("$imei", cleanImei), ("$sid", stockUnitId));
                            Run(c, "UPDATE stock_units SET imei=$imei, customer_id=CASE WHEN $chg=1 OR customer_id IS NULL OR customer_id=$oldC THEN $c ELSE customer_id END, updated_at=CURRENT_TIMESTAMP WHERE id=$sid",
                                ("$imei", cleanImei), ("$chg", effCustomerId != oldCustomerId ? 1 : 0), ("$oldC", oldCustomerId), ("$c", effCustomerId), ("$sid", stockUnitId));
                        }
                        else
                        {
                            Run(c, "UPDATE stock_units SET customer_id=CASE WHEN $chg=1 OR customer_id IS NULL OR customer_id=$oldC THEN $c ELSE customer_id END WHERE id=$sid",
                                ("$chg", effCustomerId != oldCustomerId ? 1 : 0), ("$oldC", oldCustomerId), ("$c", effCustomerId), ("$sid", stockUnitId));
                        }
                        Run(c, "UPDATE v7_order_lines SET imei=$imei, plate_no=$plate, vin_no=$vin, stock_unit_id=$sid WHERE id=$lid",
                            ("$imei", cleanImei), ("$plate", cleanPlate), ("$vin", cleanVin), ("$sid", stockUnitId), ("$lid", ln.LineId));
                        Run(c, @"UPDATE activations
                            SET customer_id=CASE WHEN $chg=1 OR customer_id=$oldC THEN $c ELSE customer_id END,
                                agency_name=CASE WHEN $chg=1 THEN (SELECT name FROM customers WHERE id=$c) ELSE agency_name END,
                                plate_no=$plate, vin_no=$vin, activated_at=$act,
                                expires_at=CASE WHEN COALESCE(renewal_count,0)>0 THEN expires_at ELSE $exp END
                            WHERE id=$aid",
                            ("$chg", effCustomerId != oldCustomerId ? 1 : 0), ("$oldC", oldCustomerId), ("$c", effCustomerId), ("$plate", cleanPlate), ("$vin", cleanVin), ("$act", actDateStr), ("$exp", expDateStr), ("$aid", existingActId));
                    }
                    else
                    {
                        ValidateImeiNotActiveOrPendingOnOtherOrder(c, cleanImei, id);

                        if (stockUnitId > 0)
                        {
                            var otherId = Scalar(c, "SELECT COALESCE((SELECT id FROM stock_units WHERE UPPER(TRIM(imei))=UPPER($imei) AND id<>$sid AND deleted_at IS NULL LIMIT 1), 0)",
                                ("$imei", cleanImei), ("$sid", stockUnitId));
                            if (otherId > 0)
                            {
                                ReclaimAnonymouslyHeldStockUnitIfNeeded(c, otherId, effWarehouse, actor);
                                var otherStatus = Str(c, "SELECT status FROM stock_units WHERE id=$id", ("$id", otherId));
                                if (otherStatus is not ("IN_STOCK" or "CONSIGNED" or "TECHNICIAN"))
                                    throw new InvalidOperationException($"Mã IMEI '{cleanImei}' đang ở trạng thái '{otherStatus}' (không khả dụng trong kho).");
                                if (Scalar(c, "SELECT COUNT(*) FROM v7_stock_effects WHERE stock_unit_id=$oid AND order_id<>$o", ("$oid", otherId), ("$o", id)) > 0)
                                    DetachStockEffectToArchive(c, otherId, actor);
                                Run(c, "UPDATE stock_units SET status='IN_STOCK', warehouse_id=$w, customer_id=NULL, employee_id=NULL, updated_at=CURRENT_TIMESTAMP WHERE id=$sid",
                                    ("$w", effWarehouse), ("$sid", stockUnitId));
                                Run(c, "UPDATE stock_units SET status='INSTALLED', warehouse_id=NULL, customer_id=$c, employee_id=NULL, updated_at=CURRENT_TIMESTAMP WHERE id=$oid",
                                    ("$c", effCustomerId), ("$oid", otherId));
                                Run(c, "UPDATE inventory_transaction_lines SET stock_unit_id=$oid WHERE stock_unit_id=$sid AND NOT EXISTS (SELECT 1 FROM inventory_transaction_lines x WHERE x.transaction_id=inventory_transaction_lines.transaction_id AND x.stock_unit_id=$oid)",
                                    ("$oid", otherId), ("$sid", stockUnitId));
                                Run(c, "UPDATE v7_stock_effects SET stock_unit_id=$oid WHERE order_id=$o AND stock_unit_id=$sid",
                                    ("$oid", otherId), ("$o", id), ("$sid", stockUnitId));
                                stockUnitId = otherId;
                            }
                            else
                            {
                                Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id WHERE UPPER(TRIM(imei))=UPPER($imei) AND id<>$sid AND deleted_at IS NOT NULL",
                                    ("$imei", cleanImei), ("$sid", stockUnitId));
                                Run(c, "UPDATE stock_units SET imei=$imei, status='INSTALLED', warehouse_id=NULL, customer_id=$c, updated_at=CURRENT_TIMESTAMP WHERE id=$sid",
                                    ("$imei", cleanImei), ("$c", effCustomerId), ("$sid", stockUnitId));
                            }
                        }
                        else if (deductStock)
                        {
                            var matchId = Scalar(c, "SELECT COALESCE((SELECT id FROM stock_units WHERE UPPER(TRIM(imei))=UPPER($imei) AND deleted_at IS NULL LIMIT 1), 0)", ("$imei", cleanImei));
                            if (matchId > 0)
                            {
                                ReclaimAnonymouslyHeldStockUnitIfNeeded(c, matchId, effWarehouse, actor);
                                stockUnitId = matchId;
                                Run(c, "UPDATE stock_units SET status='INSTALLED', warehouse_id=NULL, customer_id=$c, updated_at=CURRENT_TIMESTAMP WHERE id=$sid",
                                    ("$c", effCustomerId), ("$sid", stockUnitId));
                            }
                            else
                            {
                                Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id WHERE imei=$imei AND deleted_at IS NOT NULL", ("$imei", cleanImei));
                                Run(c, "INSERT INTO stock_units(product_id,imei,status,customer_id) VALUES($p,$imei,'INSTALLED',$c)",
                                    ("$p", ln.ProductId), ("$imei", cleanImei), ("$c", effCustomerId));
                                stockUnitId = Scalar(c, "SELECT last_insert_rowid()");
                            }
                        }
                        else
                        {
                            if (Scalar(c, "SELECT COUNT(*) FROM stock_units WHERE imei=$imei AND deleted_at IS NULL", ("$imei", cleanImei)) > 0)
                                throw new InvalidOperationException($"IMEI '{cleanImei}' đã tồn tại trong danh sách thiết bị.");
                            Run(c, "UPDATE stock_units SET imei = imei || '#DEL#' || id WHERE imei=$imei AND deleted_at IS NOT NULL", ("$imei", cleanImei));
                            Run(c, "INSERT INTO stock_units(product_id,imei,status,customer_id) VALUES($p,$imei,'INSTALLED',$c)",
                                ("$p", ln.ProductId), ("$imei", cleanImei), ("$c", effCustomerId));
                            stockUnitId = Scalar(c, "SELECT last_insert_rowid()");
                        }

                        Run(c, "UPDATE v7_order_lines SET imei=$imei, plate_no=$plate, vin_no=$vin, stock_unit_id=$sid WHERE id=$lid",
                            ("$imei", cleanImei), ("$plate", cleanPlate), ("$vin", cleanVin), ("$sid", stockUnitId), ("$lid", ln.LineId));
                        Run(c, @"INSERT INTO activations(v7_order_id,stock_unit_id,customer_id,product_id,plate_no,vin_no,activated_at,expires_at)
                            VALUES($o,$s,$c,$p,$plate,$vin,$act,$exp)",
                            ("$o", id), ("$s", stockUnitId), ("$c", effCustomerId), ("$p", ln.ProductId), ("$plate", cleanPlate), ("$vin", cleanVin), ("$act", actDateStr), ("$exp", expDateStr));
                    }

                    allImeis.Add(cleanImei);
                    if (!string.IsNullOrWhiteSpace(cleanPlate)) allPlates.Add(cleanPlate);
                    if (!string.IsNullOrWhiteSpace(cleanVin)) allVins.Add(cleanVin);
                }

                Run(c, "UPDATE v7_orders SET imei=$imei, plate_no=$plate, vin_no=$vin WHERE id=$id",
                    ("$imei", allImeis.Count > 0 ? string.Join(", ", allImeis) : "CHỜ-NHẬP-IMEI"),
                    ("$plate", string.Join(", ", allPlates.Distinct())),
                    ("$vin", string.Join(", ", allVins.Distinct())),
                    ("$id", id));
            }
            else
            {
                Run(c, @"UPDATE activations
                    SET customer_id=CASE WHEN $chg=1 OR customer_id=$oldC THEN $c ELSE customer_id END,
                        agency_name=CASE WHEN $chg=1 THEN (SELECT name FROM customers WHERE id=$c) ELSE agency_name END,
                        activated_at=$act,
                        expires_at=CASE WHEN COALESCE(renewal_count,0)>0 THEN expires_at ELSE $exp END
                    WHERE v7_order_id=$o AND deleted_at IS NULL",
                    ("$chg", effCustomerId != oldCustomerId ? 1 : 0), ("$oldC", oldCustomerId), ("$c", effCustomerId), ("$act", actDateStr), ("$exp", expDateStr), ("$o", id));
                Run(c, "UPDATE stock_units SET customer_id=CASE WHEN $chg=1 OR customer_id IS NULL OR customer_id=$oldC THEN $c ELSE customer_id END WHERE id IN (SELECT stock_unit_id FROM v7_order_lines WHERE order_id=$o AND stock_unit_id IS NOT NULL)",
                    ("$chg", effCustomerId != oldCustomerId ? 1 : 0), ("$oldC", oldCustomerId), ("$c", effCustomerId), ("$o", id));
            }

            Run(c, @"UPDATE inventory_transactions SET customer_id=$c
                WHERE movement_type='SALE_OUT'
                  AND id IN (SELECT l.transaction_id FROM inventory_transaction_lines l JOIN v7_stock_effects e ON e.stock_unit_id=l.stock_unit_id WHERE e.order_id=$o)",
                ("$c", effCustomerId), ("$o", id));

            // 5. Cập nhật bản ghi v7_orders chính
            var finalOrderCost = Scalar(c, "SELECT COALESCE(SUM(quantity * unit_cost_snapshot_vnd), 0) FROM v7_order_lines WHERE order_id=$id AND deleted_at IS NULL", ("$id", id));
            Run(c, @"UPDATE v7_orders SET
                customer_id=$cust,
                seller_record_id=COALESCE($sellerRec, seller_record_id),
                sale_source=CASE WHEN $src<>'' THEN $src ELSE sale_source END,
                unit_price_vnd=$price,
                unit_cost_snapshot_vnd=$cost,
                source=$source,
                warehouse_id=$wh,
                installer=$inst,
                installer_employee_id=$emp,
                installer_fee_vnd=$fee,
                no_install_fee=$noInst,
                contact_name=$contact,
                customer_phone=$phone,
                duration_months=$dur,
                deduct_stock=$deduct,
                collect_on_delivery=$cod,
                install_address=$addr,
                install_at=$at,
                notes=$notes,
                activated_at=$act
                WHERE id=$id",
                ("$cust", effCustomerId),
                ("$sellerRec", resolvedSellerId),
                ("$src", resolvedSrc),
                ("$price", totalPrice),
                ("$cost", finalOrderCost),
                ("$source", partner ? "PARTNER" : "INTERNAL"),
                ("$wh", effWarehouse > 0 ? effWarehouse : null),
                ("$inst", installerName),
                ("$emp", empId),
                ("$fee", totalInstallerFee),
                ("$noInst", noInstallFee ? 1 : 0),
                ("$contact", (contactName ?? "").Trim()),
                ("$phone", (customerPhone ?? "").Trim()),
                ("$dur", effDuration),
                ("$deduct", deductStock ? 1 : 0),
                ("$cod", cod ? 1 : 0),
                ("$addr", (installAddress ?? "").Trim()),
                ("$at", FormatInstallDateTime(installAt)),
                ("$notes", (notes ?? "").Trim()),
                ("$act", actDateStr),
                ("$id", id));

            // 6. Đồng bộ các luồng Kế toán / Công nợ phía sau (SALE, SERVICE, COMMISSION)
            static void RecalcInvoiceAmountAndStatus(SqliteConnection conn, long invId, long newAmt)
            {
                var paid = Scalar(conn, "SELECT COALESCE(SUM(allocated_vnd), 0) FROM payment_allocations WHERE invoice_id=$i AND deleted_at IS NULL", ("$i", invId));
                var offset = Scalar(conn, "SELECT COALESCE(offset_vnd, 0) FROM invoices WHERE id=$i", ("$i", invId));
                var settled = paid + offset;
                var newStatus = newAmt <= 0 ? "PAID" : (settled <= 0 ? "OPEN" : (settled >= newAmt ? "PAID" : "PARTIAL"));
                Run(conn, "UPDATE invoices SET amount_vnd=$amt, status=$st WHERE id=$i", ("$amt", newAmt), ("$st", newStatus), ("$i", invId));
            }

            // 6A. Hóa đơn phải thu khách hàng (SALE)
            var saleInvId = Scalar(c, "SELECT COALESCE((SELECT id FROM invoices WHERE v7_order_id=$o AND invoice_type='SALE' AND deleted_at IS NULL AND status<>'VOID' ORDER BY id DESC LIMIT 1), 0)", ("$o", id));
            var invNotes = noInstallFee ? $"Đơn hàng {orderCode} (Không tính công lắp — Giá chưa lắp)" : $"Đơn hàng lắp đặt {orderCode}";
            var lineDescPrefix = noInstallFee ? "Thiết bị (Không tính công lắp)" : "Thiết bị & lắp đặt";
            if (saleInvId > 0)
            {
                Run(c, "UPDATE invoices SET customer_id=$c, notes=$notes WHERE id=$i", ("$c", effCustomerId), ("$notes", invNotes), ("$i", saleInvId));
                Run(c, "DELETE FROM invoice_lines WHERE invoice_id=$i", ("$i", saleInvId));
                foreach (var grp in updatedLinePricing.GroupBy(x => (x.ProductId, x.ProductName, x.UnitPrice)))
                {
                    Run(c, "INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$desc,$p,$q,$price)",
                        ("$i", saleInvId), ("$desc", $"{lineDescPrefix}: {grp.Key.ProductName}"), ("$p", grp.Key.ProductId), ("$q", grp.Sum(x => x.Qty)), ("$price", grp.Key.UnitPrice));
                }
                RecalcInvoiceAmountAndStatus(c, saleInvId, totalPrice);
            }
            else if (totalPrice > 0)
            {
                var invoiceNo = "HD-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                Run(c, @"INSERT INTO invoices(invoice_no,invoice_type,customer_id,v7_order_id,issued_at,due_at,amount_vnd,notes)
                    VALUES($no,'SALE',$c,$o,datetime('now','+7 hours'),date('now','+7 hours','+30 days'),$price,$notes)",
                    ("$no", invoiceNo), ("$c", effCustomerId), ("$o", id), ("$price", totalPrice), ("$notes", invNotes));
                var newInvId = Scalar(c, "SELECT last_insert_rowid()");
                foreach (var grp in updatedLinePricing.GroupBy(x => (x.ProductId, x.ProductName, x.UnitPrice)))
                {
                    Run(c, "INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$desc,$p,$q,$price)",
                        ("$i", newInvId), ("$desc", $"{lineDescPrefix}: {grp.Key.ProductName}"), ("$p", grp.Key.ProductId), ("$q", grp.Sum(x => x.Qty)), ("$price", grp.Key.UnitPrice));
                }
            }

            // 6B. Chứng từ công nợ chi phí công lắp kỹ thuật (SERVICE)
            var srvInvId = Scalar(c, "SELECT COALESCE((SELECT id FROM invoices WHERE v7_order_id=$o AND invoice_type='SERVICE' AND deleted_at IS NULL AND status<>'VOID' ORDER BY id DESC LIMIT 1), 0)", ("$o", id));
            if (totalInstallerFee > 0)
            {
                var partnerLabel = !string.IsNullOrWhiteSpace(installerName) ? installerName : "Nhân sự lắp đặt";
                var srvNotes = $"{partnerLabel} — Công lắp đặt đơn {orderCode}";
                if (srvInvId > 0)
                {
                    Run(c, "UPDATE invoices SET notes=$notes WHERE id=$i", ("$notes", srvNotes), ("$i", srvInvId));
                    Run(c, "DELETE FROM invoice_lines WHERE invoice_id=$i", ("$i", srvInvId));
                    foreach (var grp in updatedLinePricing.Where(x => x.InstallerFee > 0).GroupBy(x => (x.ProductId, x.ProductName, x.InstallerFee)))
                    {
                        Run(c, "INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$desc,$p,$q,$price)",
                            ("$i", srvInvId), ("$desc", $"Công lắp đặt ({orderCode}): {grp.Key.ProductName}"), ("$p", grp.Key.ProductId), ("$q", grp.Sum(x => x.Qty)), ("$price", grp.Key.InstallerFee));
                    }
                    RecalcInvoiceAmountAndStatus(c, srvInvId, totalInstallerFee);
                }
                else
                {
                    var srvNo = "CL-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                    Run(c, @"INSERT INTO invoices(invoice_no,invoice_type,v7_order_id,issued_at,due_at,amount_vnd,notes)
                        VALUES($no,'SERVICE',$o,datetime('now','+7 hours'),date('now','+7 hours','+15 days'),$fee,$notes)",
                        ("$no", srvNo), ("$o", id), ("$fee", totalInstallerFee), ("$notes", srvNotes));
                    var newSrvId = Scalar(c, "SELECT last_insert_rowid()");
                    foreach (var grp in updatedLinePricing.Where(x => x.InstallerFee > 0).GroupBy(x => (x.ProductId, x.ProductName, x.InstallerFee)))
                    {
                        Run(c, "INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$desc,$p,$q,$price)",
                            ("$i", newSrvId), ("$desc", $"Công lắp đặt ({orderCode}): {grp.Key.ProductName}"), ("$p", grp.Key.ProductId), ("$q", grp.Sum(x => x.Qty)), ("$price", grp.Key.InstallerFee));
                    }
                }
            }
            else if (srvInvId > 0)
            {
                var hasSrvAlloc = Scalar(c, "SELECT COUNT(*) FROM payment_allocations WHERE invoice_id=$i AND deleted_at IS NULL", ("$i", srvInvId)) > 0;
                Run(c, "DELETE FROM invoice_lines WHERE invoice_id=$i", ("$i", srvInvId));
                if (!hasSrvAlloc)
                {
                    Run(c, "DELETE FROM invoices WHERE id=$i", ("$i", srvInvId));
                }
                else
                {
                    Run(c, "UPDATE invoices SET amount_vnd=0, status='VOID' WHERE id=$i", ("$i", srvInvId));
                }
            }

            // 6C. Đồng bộ chứng từ Hoa hồng Seller (COMMISSION) đang mở
            SyncOpenSellerCommissions(c);

            Event(c, id, actor, "MAIN_EDITED_COMPLETED");
            return 0;
        });

        DemoDb.NotifyDevicesChanged();
    }
}
