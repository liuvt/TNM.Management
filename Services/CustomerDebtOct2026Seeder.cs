using Microsoft.Data.Sqlite;

namespace TNM.Management.Services;

/// <summary>
/// Đồng bộ bảng kê chi tiết công nợ khách hàng Tháng 09/2026 & Tháng 10/2026
/// và dọn các chứng từ gộp giả định tự sinh từ danh sách thiết bị đang hoạt động.
/// </summary>
public static class CustomerDebtOct2026Seeder
{
    private sealed record SeedLine(string CanonicalSku, string Description, long Qty, long UnitPriceVnd);
    private sealed record SeedPayment(string PaymentNo, string PaidAt, long AmountVnd, string Notes);
    private sealed record SeedStatement(
        string InvoiceNo,
        string CustomerMatchName,
        string LegacyCustomerCode,
        string CustomerPhone,
        string PeriodTag,
        string IssuedAt,
        string DueAt,
        SeedLine[] Lines,
        SeedPayment[] Payments);

    public static void EnsureSeeded(SqliteConnection c, SqliteTransaction? tx, Action<long> setActor, Func<long> getActor)
    {
        // Chỉ chạy khi database đã có danh sách khách hàng thực tế của doanh nghiệp
        if (Scalar(c, tx, "SELECT COUNT(*) FROM customers WHERE deleted_at IS NULL AND name LIKE '%HTX%'") == 0)
            return;

        var prevActor = getActor();
        var devId = Scalar(c, tx, "SELECT COALESCE((SELECT id FROM app_users WHERE LOWER(username)='vanlt_it' LIMIT 1), 0)");
        try
        {
            if (devId > 0) setActor(devId);

            // 1. Dọn các hóa đơn SALE gộp giả định ("Công nợ thiết bị lắp đặt đang hoạt động (... thiết bị)") chưa có thanh toán
            const string fakeBatchInvSql = @"
                SELECT i.id FROM invoices i
                WHERE i.invoice_type = 'SALE'
                  AND i.v7_order_id IS NULL
                  AND i.notes LIKE 'Công nợ thiết bị lắp đặt đang hoạt động (%'
                  AND NOT EXISTS (SELECT 1 FROM payment_allocations pa WHERE pa.invoice_id = i.id AND pa.deleted_at IS NULL)";

            if (devId > 0)
            {
                Exec(c, tx, $"DELETE FROM invoice_lines WHERE invoice_id IN ({fakeBatchInvSql})");
                Exec(c, tx, $"DELETE FROM invoices WHERE id IN ({fakeBatchInvSql})");
            }
            else
            {
                Exec(c, tx, $"UPDATE invoice_lines SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE deleted_at IS NULL AND invoice_id IN ({fakeBatchInvSql})");
                Exec(c, tx, $"UPDATE invoices SET status='VOID', deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE deleted_at IS NULL AND id IN ({fakeBatchInvSql})");
            }

            // 2. Đảm bảo các mã sản phẩm phụ tùng / dịch vụ phát sinh trong kỳ T09-T10/2026 tồn tại trong bảng products
            var autoPartsGrp = Scalar(c, tx, "SELECT COALESCE((SELECT id FROM product_groups WHERE code='AUTOPARTS' AND deleted_at IS NULL LIMIT 1), (SELECT id FROM product_groups WHERE deleted_at IS NULL ORDER BY id LIMIT 1), 1)");
            var afterSaleGrp = Scalar(c, tx, "SELECT COALESCE((SELECT id FROM product_groups WHERE code='AFTERSALE' AND deleted_at IS NULL LIMIT 1), (SELECT id FROM product_groups WHERE deleted_at IS NULL ORDER BY id DESC LIMIT 1), 1)");

            EnsureProduct(c, tx, "SP000062", "Sim 5G79 (30 ngày)", autoPartsGrp, 50000);
            EnsureProduct(c, tx, "12301V", "Lọc nhớt 16510-73013", autoPartsGrp, 35000);
            EnsureProduct(c, tx, "Sailun195/75R16C", "Lốp xe Sailun 195/75R16C", autoPartsGrp, 1300000);
            EnsureProduct(c, tx, "Sailun205/55R16", "Lốp xe Sailun 205/55R16", autoPartsGrp, 960000);
            EnsureProduct(c, tx, "Sailun185/65R15", "Lốp xe Sailun 185/65R15", autoPartsGrp, 670000);
            EnsureProduct(c, tx, "SAILUN205/55R17", "Lốp xe SAILUN 205/55R17 95V CS51", autoPartsGrp, 955000);
            EnsureProduct(c, tx, "Sailun195/60R16", "Lốp xe 195/60R16", autoPartsGrp, 980000);
            EnsureProduct(c, tx, "SP000077", "Bảo hiểm TNDS - HTX Chính Hiệp", afterSaleGrp, 500000);

            // 3. Đồng bộ từng bảng kê công nợ khách hàng theo đúng kỳ Tháng/Năm (T09/2026 & T10/2026)
            foreach (var st in BuildStatements())
            {
                var cust = FindCustomer(c, tx, st.CustomerMatchName);
                if (cust.Id <= 0) continue;

                if (!string.IsNullOrWhiteSpace(st.CustomerPhone))
                {
                    Exec(c, tx, "UPDATE customers SET phone=$ph WHERE id=$id AND (COALESCE(phone,'')='' OR phone IN ('136','123','1234','12345','123456','124','135'))",
                        ("$ph", st.CustomerPhone), ("$id", cust.Id));
                }

                var existingInvId = Scalar(c, tx, "SELECT COALESCE((SELECT id FROM invoices WHERE invoice_no=$no AND deleted_at IS NULL LIMIT 1), 0)", ("$no", st.InvoiceNo));
                if (existingInvId > 0)
                    continue; // Đã tồn tại (ví dụ HD-KH002833-T10), giữ nguyên vẹn

                var totalIncurred = st.Lines.Sum(l => l.Qty * l.UnitPriceVnd);
                var totalPaid = st.Payments.Sum(p => p.AmountVnd);
                var status = totalPaid <= 0 ? "OPEN" : totalPaid >= totalIncurred ? "PAID" : "PARTIAL";
                var dateRangeLabel = st.PeriodTag == "09/2026" ? "01/09/2026 - 30/09/2026" : "01/10/2026 - 31/10/2026";
                var note = $"Công nợ chi tiết khách hàng T{st.PeriodTag} ({dateRangeLabel} · Mã KH cũ: {st.LegacyCustomerCode}) — {cust.Code} — {cust.Name}";

                Exec(c, tx, @"INSERT INTO invoices(invoice_no,invoice_type,customer_id,issued_at,due_at,amount_vnd,status,notes)
                    VALUES($no,'SALE',$c,$issued,$due,$amt,$st,$note)",
                    ("$no", st.InvoiceNo), ("$c", cust.Id), ("$issued", st.IssuedAt), ("$due", st.DueAt),
                    ("$amt", totalIncurred), ("$st", status), ("$note", note));
                var invId = Scalar(c, tx, "SELECT last_insert_rowid()");

                foreach (var ln in st.Lines)
                {
                    var prodId = ResolveProductId(c, tx, ln.CanonicalSku);
                    Exec(c, tx, @"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd)
                        VALUES($i,$d,$p,$q,$u)",
                        ("$i", invId), ("$d", ln.Description), ("$p", prodId > 0 ? prodId : null),
                        ("$q", ln.Qty), ("$u", ln.UnitPriceVnd));
                }

                foreach (var pay in st.Payments)
                {
                    var existingPayId = Scalar(c, tx, "SELECT COALESCE((SELECT id FROM payments WHERE payment_no=$no AND deleted_at IS NULL LIMIT 1), 0)", ("$no", pay.PaymentNo));
                    if (existingPayId <= 0)
                    {
                        Exec(c, tx, @"INSERT INTO payments(payment_no,direction,paid_at,method,amount_vnd,notes)
                            VALUES($no,'IN',$at,'BANK',$amt,$note)",
                            ("$no", pay.PaymentNo), ("$at", pay.PaidAt), ("$amt", pay.AmountVnd), ("$note", pay.Notes));
                        existingPayId = Scalar(c, tx, "SELECT last_insert_rowid()");
                    }
                    if (Scalar(c, tx, "SELECT COUNT(*) FROM payment_allocations WHERE payment_id=$p AND invoice_id=$i AND deleted_at IS NULL", ("$p", existingPayId), ("$i", invId)) == 0)
                    {
                        Exec(c, tx, @"INSERT INTO payment_allocations(payment_id,invoice_id,allocated_vnd)
                            VALUES($p,$i,$amt)",
                            ("$p", existingPayId), ("$i", invId), ("$amt", pay.AmountVnd));
                    }
                }
            }
        }
        finally
        {
            setActor(prevActor);
        }
    }

    private static void EnsureProduct(SqliteConnection c, SqliteTransaction? tx, string sku, string name, long groupId, long baseCostVnd)
    {
        if (Scalar(c, tx, "SELECT COUNT(*) FROM products WHERE LOWER(sku)=LOWER($sku)", ("$sku", sku)) > 0)
            return;
        Exec(c, tx, @"INSERT INTO products(sku,name,group_id,base_cost_vnd,renewal_fee_vnd,install_fee_vnd,warranty_months,is_serialized,is_active)
            VALUES($sku,$name,$grp,$cost,0,0,0,0,1)",
            ("$sku", sku), ("$name", name), ("$grp", groupId), ("$cost", baseCostVnd));
    }

    private static long ResolveProductId(SqliteConnection c, SqliteTransaction? tx, string sku)
    {
        if (string.IsNullOrWhiteSpace(sku)) return 0;
        return Scalar(c, tx, "SELECT COALESCE((SELECT id FROM products WHERE LOWER(sku)=LOWER($sku) AND deleted_at IS NULL LIMIT 1), 0)", ("$sku", sku));
    }

    private static (long Id, string Code, string Name) FindCustomer(SqliteConnection c, SqliteTransaction? tx, string matchName)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, code, name FROM customers WHERE deleted_at IS NULL AND LOWER(name)=LOWER($n) LIMIT 1";
        cmd.Parameters.AddWithValue("$n", matchName);
        using var r = cmd.ExecuteReader();
        if (r.Read())
            return (r.GetInt64(0), r.GetString(1), r.GetString(2));
        return (0, "", "");
    }

    private static long Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
    }

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static SeedStatement[] BuildStatements() =>
    [

        new SeedStatement(
            "HD-KH001624-T10", "HTX Nam Hi", "KH001624", "", "10/2026", "2026-10-31", "2026-11-15",
            [
                new SeedLine("SP000019", "[01/10/2026 10:01 · HD003735.01] Nasa GPS 4G nano (SP000019)", 11, 770000),
                new SeedLine("SP000019", "[06/10/2026 10:26 · HD003823.01] Nasa GPS 4G nano (SP000019)", 10, 770000),
                new SeedLine("SP000019", "[14/10/2026 11:18 · HD003930.01] Nasa GPS 4G nano (SP000019)", 10, 770000),
                new SeedLine("SP000019", "[24/10/2026 14:49 · HD004136] Nasa GPS 4G nano (SP000019)", 7, 770000),
                new SeedLine("SP000034", "[29/10/2026 09:27 · HD004235] Thẻ lái xe dùng chung - Mới (SP000034)", 1, 100000),
                new SeedLine("SP000034", "[29/10/2026 10:28 · HD004241] Thẻ lái xe dùng chung - Mới (SP000034)", 1, 100000),
            ],
            [
            ]),
        new SeedStatement(
            "HD-KH002669-T10", "HTX Nam Phương", "KH002669", "", "10/2026", "2026-10-31", "2026-11-15",
            [
                new SeedLine("DX00019", "[01/10/2026 09:40 · HD004070] Gia hạn định vị Xanh GPS (SP000009) — 68H09082", 1, 700000),
                new SeedLine("DX00020", "[01/10/2026 09:41 · HD004071.01] Gia hạn định vị TDC (SP000010) — 68A08960", 1, 540000),
                new SeedLine("DX00020", "[01/10/2026 09:46 · HD004072] Gia hạn định vị TDC (SP000010) — 65G00114", 1, 540000),
                new SeedLine("DX00020", "[05/10/2026 09:49 · HD004073] Gia hạn định vị TDC (SP000010) — 68A08089", 1, 540000),
                new SeedLine("DX00019", "[05/10/2026 09:50 · HD004074] Gia hạn định vị Xanh GPS (SP000009) — 61A42327", 1, 700000),
                new SeedLine("DX00019", "[05/10/2026 09:55 · HD004075] Gia hạn định vị Xanh GPS (SP000009) — 66E00855", 1, 700000),
                new SeedLine("DX00019", "[07/10/2026 10:04 · HD004076] Gia hạn định vị Xanh GPS (SP000009) — 68H07382", 1, 700000),
                new SeedLine("DX00019", "[07/10/2026 10:06 · HD004077] Gia hạn định vị Xanh GPS (SP000009) — 47H10185", 1, 700000),
                new SeedLine("DX00020", "[07/10/2026 10:08 · HD004078] Gia hạn định vị TDC (SP000010) — 68H04621", 1, 540000),
                new SeedLine("DX00019", "[09/10/2026 10:15 · HD004082] Gia hạn định vị Xanh GPS (SP000009) — 20H03013", 1, 700000),
                new SeedLine("DX00019", "[10/10/2026 10:09 · HD004079] Gia hạn định vị Xanh GPS (SP000009) — 68H07781", 1, 700000),
                new SeedLine("DX00019", "[12/10/2026 10:13 · HD004081] Gia hạn định vị Xanh GPS (SP000009) — 68E01038", 1, 700000),
                new SeedLine("DX00023", "[15/10/2026 08:45 · HD004132] Dịch vụ lắp đặt -CAM (SP000004) — Tháo cam xe Hoàng Huyên 50e20107", 1, 300000),
                new SeedLine("DX00020", "[15/10/2026 10:18 · HD004084] Gia hạn định vị TDC (SP000010) — 67E00672", 1, 540000),
                new SeedLine("DX00020", "[16/10/2026 10:19 · HD004085] Gia hạn định vị TDC (SP000010) — 68E01613", 1, 540000),
                new SeedLine("SP000019", "[17/10/2026 09:50 · HD003991.01] Nasa GPS 4G nano (SP000019)", 20, 620000),
                new SeedLine("DX00019", "[19/10/2026 10:21 · HD004086] Gia hạn định vị Xanh GPS (SP000009) — 68H07377", 1, 700000),
                new SeedLine("DX00019", "[19/10/2026 10:22 · HD004087] Gia hạn định vị Xanh GPS (SP000009) — 68H06413", 1, 700000),
                new SeedLine("DX00019", "[20/10/2026 10:25 · HD004088] Gia hạn định vị Xanh GPS (SP000009) — 68H05515", 1, 700000),
                new SeedLine("DX00020", "[21/10/2026 10:26 · HD004089] Gia hạn định vị TDC (SP000010) — 68H04728", 1, 540000),
                new SeedLine("DX00019", "[22/10/2026 09:59 · HD004197] Gia hạn định vị Xanh GPS (SP000009) — 68A11176", 1, 700000),
                new SeedLine("DX00019", "[22/10/2026 10:27 · HD004090] Gia hạn định vị Xanh GPS (SP000009) — 68H09062", 1, 700000),
                new SeedLine("11307", "[23/10/2026 09:02 · HD004118] Lọc nhớt MD135737 MITSUBISHI L300, 4 chỗ, 7 Chổ, 9 chỗ, 12 chỗ, JOLIE, HYUNDAI i10 Mazda 323, Asia 550kg, Asia Towner, Kia Morning, Kia Pride, Xpander (11307)", 10, 33550),
                new SeedLine("NG5W30", "[23/10/2026 09:02 · HD004118] Nhớt động cơ G-Energy Synthetic Super Start 5W-30 (4 Lít/ Can) (NG5W30)", 9, 407000),
                new SeedLine("DX00019", "[24/10/2026 08:43 · HD004131] Gia hạn định vị Xanh GPS (SP000009) — 68H09019", 1, 700000),
                new SeedLine("SP000019", "[26/10/2026 14:55 · HD004185] Nasa GPS 4G nano (SP000019)", 20, 620000),
                new SeedLine("DX00019", "[27/10/2026 15:28 · HD004270] Gia hạn định vị Xanh GPS (SP000009) — 68H09772", 1, 700000),
                new SeedLine("DX00019", "[29/10/2026 15:28 · HD004271] Gia hạn định vị Xanh GPS (SP000009) — 68H07450", 1, 700000),
                new SeedLine("DX00019", "[30/10/2026 11:54 · HD004351] Gia hạn định vị Xanh GPS (SP000009) — 68H05508", 1, 700000),
            ],
            [
            ]),
        new SeedStatement(
            "HD-KHTDT-T10", "HTX Tân Đại Thành", "KHTDT", "", "10/2026", "2026-10-31", "2026-11-15",
            [
                new SeedLine("SP000019", "[#01 · 2026-10-01] Phạm Đăng Tri Chương", 1, 620000),
                new SeedLine("SP000001", "[#02 · 2026-10-01] 61E04009", 1, 700000),
                new SeedLine("SP000001", "[#03 · 2026-10-01] 50F11492", 1, 700000),
                new SeedLine("SP000001", "[#04 · 2026-10-01] 81H05423", 1, 700000),
                new SeedLine("SP000001", "[#05 · 2026-10-01] 65H07327", 1, 700000),
                new SeedLine("SP000001", "[#06 · 2026-10-01] 68H17226", 1, 700000),
                new SeedLine("SP000001", "[#07 · 2026-10-01] 68H09277", 1, 700000),
                new SeedLine("SP000001", "[#08 · 2026-10-03] 70H21475", 1, 700000),
                new SeedLine("SP000019", "[#09 · 2026-10-05] ĐỖ CHÍ CÔNG", 1, 620000),
                new SeedLine("SP000019", "[#10 · 2026-10-05] NGUYỄN THỊ HUYỀN", 1, 620000),
                new SeedLine("SP000001", "[#11 · 2026-10-06] 50F18087", 1, 700000),
                new SeedLine("SP000001", "[#12 · 2026-10-06] 51C72244", 1, 700000),
                new SeedLine("SP000001", "[#13 · 2026-10-06] 50F10625", 1, 700000),
                new SeedLine("SP000001", "[#14 · 2026-10-06] 65H11564", 1, 700000),
                new SeedLine("SP000001", "[#15 · 2026-10-06] 70H22373", 1, 700000),
                new SeedLine("SP000001", "[#16 · 2026-10-06] 50F23473", 1, 700000),
                new SeedLine("SP000001", "[#17 · 2026-10-07] 50F20561", 1, 700000),
                new SeedLine("SP000001", "[#18 · 2026-10-07] 50G00811", 1, 700000),
                new SeedLine("DX00019", "[#19 · 2026-10-08] 50E28057 — Gia hạn định vị Xanh", 1, 700000),
                new SeedLine("SP000001", "[#20 · 2026-10-08] 50F21761", 1, 700000),
                new SeedLine("SP000001", "[#21 · 2026-10-08] Trương Quốc Dũng", 1, 700000),
                new SeedLine("SP000001", "[#22 · 2026-10-08] Võ Trung Hiếu", 1, 700000),
                new SeedLine("SP000001", "[#23 · 2026-10-08] 50F23874", 1, 700000),
                new SeedLine("SP000001", "[#24 · 2026-10-08] 68h14816", 1, 700000),
                new SeedLine("DX00022", "[#25 · 2026-10-09] 50H85692 — Gia hạn định vị Nasa", 1, 550000),
                new SeedLine("DX00019", "[#26 · 2026-10-10] 50E38954 — Gia hạn định vị Xanh", 1, 700000),
                new SeedLine("SP000001", "[#27 · 2026-10-10] 66H08325", 1, 700000),
                new SeedLine("SP000001", "[#28 · 2026-10-12] 83E00316", 1, 700000),
                new SeedLine("SP000001", "[#29 · 2026-10-12] 50F20911", 1, 700000),
                new SeedLine("SP000001", "[#30 · 2026-10-12] 50F20771", 1, 700000),
                new SeedLine("DX00019", "[#31 · 2026-10-13] 50E48651 — Gia hạn định vị Xanh", 1, 700000),
                new SeedLine("DX00022", "[#32 · 2026-10-13] 50E17106 — Gia hạn định vị Nasa", 1, 550000),
                new SeedLine("DX00019", "[#33 · 2026-10-13] 50E28071 — Gia hạn định vị Xanh", 1, 700000),
                new SeedLine("SP000001", "[#34 · 2026-10-13] 50F18031", 1, 700000),
                new SeedLine("DX00020", "[#35 · 2026-10-14] 50H64128 — Gia hạn định vị TDC", 1, 540000),
                new SeedLine("SP000001", "[#36 · 2026-10-14] 69A32344 — Biển trắng định vị Xanh", 1, 750000),
                new SeedLine("SP000019", "[#37 · 2026-10-14] 60H16370 — Tự lắp", 1, 620000),
                new SeedLine("SP000001", "[#38 · 2026-10-14] 50F14345", 1, 700000),
                new SeedLine("SP000001", "[#39 · 2026-10-14] Nghiêm Trí Đạt", 1, 700000),
                new SeedLine("SP000001", "[#40 · 2026-10-14] Nguyễn Chí Tâm", 1, 700000),
                new SeedLine("DX00019", "[#41 · 2026-10-16] 63H07551 — Gia hạn định vị Xanh", 1, 700000),
                new SeedLine("DX00022", "[#42 · 2026-10-16] 50E31743 — Gia hạn định vị Nasa", 1, 550000),
                new SeedLine("DX00019", "[#43 · 2026-10-17] 50E38942 — Gia hạn định vị Xanh", 1, 700000),
                new SeedLine("SP000001", "[#44 · 2026-10-17] 69H04620", 1, 700000),
                new SeedLine("SP000001", "[#45 · 2026-10-20] 50F14319", 1, 700000),
                new SeedLine("SP000001", "[#46 · 2026-10-21] NGUYỄN THỊ KIM NGÂN", 1, 700000),
                new SeedLine("SP000001", "[#47 · 2026-10-22] 60H38382", 1, 700000),
                new SeedLine("SP000001", "[#48 · 2026-10-23] 50F14941", 1, 700000),
                new SeedLine("SP000001", "[#49 · 2026-10-26] 50F20673", 1, 700000),
                new SeedLine("SP000001", "[#50 · 2026-10-26] 50E29607", 1, 700000),
                new SeedLine("SP000001", "[#51 · 2026-10-26] 50F17655", 1, 700000),
                new SeedLine("SP000001", "[#52 · 2026-10-26] 69H01763", 1, 700000),
                new SeedLine("DX00024", "[#53 · 2026-10-27] 60A92193 — Tháo định vị", 1, 150000),
                new SeedLine("SP000001", "[#54 · 2026-10-28] 50F18059", 1, 700000),
                new SeedLine("DX00019", "[#55 · 2026-10-28] 50E37590 — Gia hạn định vị Xanh", 1, 700000),
                new SeedLine("SP000061", "[#56 · 2026-10-29] Thẻ lái xe Nasa", 1, 100000),
                new SeedLine("SP000001", "[#57 · 2026-10-29] 50F26295", 1, 700000),
                new SeedLine("DX00019", "[#58 · 2026-10-30] 50E48632 — Gia hạn định vị Xanh", 1, 700000),
                new SeedLine("SP000001", "[#59 · 2026-10-31] 68H13644", 1, 700000),
                new SeedLine("SP000001", "[#60 · 2026-10-31] Công Ty Vận Tải Hui Bao VN", 1, 700000),
                new SeedLine("SP000019", "[#61 · 2026-10-31] Anh Thương", 1, 620000),
            ],
            [
            ]),
        new SeedStatement(
            "HD-KH002954-T10", "HTX Tân Thành Đô", "KH002954", "", "10/2026", "2026-10-31", "2026-11-15",
            [
                new SeedLine("DX00002", "[12/10/2026 11:40 · HD004199.01] Định vị Nasa - NCC TNM (SP000119) — Trần Trọng Tú 50H82754, lấy thêm 1 thẻ hộp đen", 1, 800000),
            ],
            [
            ]),
        new SeedStatement(
            "HD-KH002833-T10", "Đăng Tiến (HTX Sao Việt)", "KH002833", "", "10/2026", "2026-10-31", "2026-11-15",
            [
                new SeedLine("DX00002", "[05/10/2026 11:11 · HD003879.01] Định vị Nasa - NCC TNM (SP000119) — Hữu Anh 50E-303.14 thu hộ 960k", 1, 700000),
                new SeedLine("DX00002", "[06/10/2026 08:45 · HD004331] Định vị Nasa - NCC TNM (SP000119) — Minh Vũ (cuối tháng lắp) 50E25923 Thu hộ 800k", 1, 700000),
                new SeedLine("DX00002", "[07/10/2026 15:12 · HD003973.01] Định vị Nasa - NCC TNM (SP000119) — Mạc Đỉnh Cao 50F-175.57", 1, 700000),
                new SeedLine("DX00002", "[07/10/2026 15:42 · HD004147] Định vị Nasa - NCC TNM (SP000119) — Phúc 50F17545 Thu hộ 960k", 1, 700000),
                new SeedLine("DX00002", "[12/10/2026 15:16 · HD003976.02] Định vị Nasa - NCC TNM (SP000119) — Nguyễn Thứ 61E-042.48 + phí di chuyển 100k, thu hộ 800k", 1, 800000),
                new SeedLine("DX00002", "[13/10/2026 09:20 · HD004017.01] Định vị Nasa - NCC TNM (SP000119) — Anh Bằng 60H-449.38 Thu hộ 900k", 1, 700000),
                new SeedLine("DX00002", "[19/10/2026 09:22 · HD004018.01] Định vị Nasa - NCC TNM (SP000119) — NGUYỄN TUẤN DUNG 50F-232.85 Thu hộ 860k", 1, 700000),
                new SeedLine("DX00002", "[19/10/2026 09:23 · HD004019.01] Định vị Nasa - NCC TNM (SP000119) — Quốc Sỹ 49H-065.40 Thu hộ 800k", 1, 700000),
                new SeedLine("DX00002", "[24/10/2026 13:38 · HD004212] Định vị Nasa - NCC TNM (SP000119) — Đức Hưng 50E33647", 1, 700000),
                new SeedLine("DX00002", "[29/10/2026 16:49 · HD004305] Định vị Nasa - NCC TNM (SP000119) — Văn Đương 81H07123", 1, 700000),
            ],
            [
                new SeedPayment("TT002098", "2026-10-31", 6180000, "TT002098 (31/10/2026 08:54) — Trừ thu hộ T10 · KH Đăng Tiến (HTX Sao Việt)"),
            ]),
        new SeedStatement(
            "HD-KH001687-T10", "Cty Đi Xanh", "KH001687", "", "10/2026", "2026-10-31", "2026-11-15",
            [
                new SeedLine("SP000062", "[01/10/2026 08:24 · HD003796] Sim 5G79 (30 ngày) (SP000062)", 2, 77000),
                new SeedLine("DX00023", "[02/10/2026 09:17 · HD003775.01] Dịch vụ lắp đặt -CAM (SP000004) — Tháo cam AI, Tháo định vị 50H36900", 1, 400000),
                new SeedLine("DX00023", "[02/10/2026 09:35 · HD003776] Dịch vụ lắp đặt -CAM (SP000004) — Tháo cam AI, Tháo định vị 50H64956", 1, 400000),
                new SeedLine("DX00023", "[02/10/2026 10:07 · HD003777] Dịch vụ lắp đặt -CAM (SP000004) — Tháo định vị 50H71829", 1, 100000),
                new SeedLine("DX00020", "[03/10/2026 09:58 · HD003818.01] Gia hạn định vị TDC (SP000010) — Gia hạn định vị 60H34640", 1, 500000),
                new SeedLine("DX00020", "[03/10/2026 09:59 · HD003819.01] Gia hạn định vị TDC (SP000010) — Gia hạn định vị 60B14081", 1, 500000),
                new SeedLine("DX00020", "[03/10/2026 10:00 · HD003820.01] Gia hạn định vị TDC (SP000010) — Gia hạn định vị 60H34437", 1, 500000),
                new SeedLine("NG5W30", "[05/10/2026 09:44 · HD003798.02] Nhớt động cơ G-Energy Synthetic Super Start 5W-30 (4 Lít/ Can) (NG5W30)", 1, 407000),
                new SeedLine("11307", "[05/10/2026 09:44 · HD003798.02] Lọc nhớt MD135737 MITSUBISHI L300, 4 chỗ, 7 Chổ, 9 chỗ, 12 chỗ, JOLIE, HYUNDAI i10 Mazda 323, Asia 550kg, Asia Towner, Kia Morning, Kia Pride, Xpander (11307) — Thay 2 lọc cho xe 60K90595 và 51M75337", 2, 33550),
                new SeedLine("DX00018", "[05/10/2026 10:01 · HD003821.01] Gia hạn Cam AI (1 năm) (SP000109) — Gia hạn Camera 71A20094", 1, 1780000),
                new SeedLine("DX00018", "[08/10/2026 08:46 · HD003947.01] Gia hạn Cam AI (1 năm) (SP000109) — Gia hạn Camera 60B14081C", 1, 1780000),
                new SeedLine("Sailun195/75R16C", "[12/10/2026 10:50 · HD003900.01] Lốp xe Sailun 195/75R16C (Sailun195/75R16C) — 60H39337", 2, 1431540),
                new SeedLine("12301V", "[12/10/2026 17:04 · HD003908.01] Lọc nhớt 16510-73013 (12301V)", 4, 39490),
                new SeedLine("Sailun205/55R16", "[12/10/2026 17:04 · HD003908.01] Lốp xe Sailun 205/55R16 (Sailun205/55R16)", 2, 1056132),
                new SeedLine("Sailun185/65R15", "[12/10/2026 17:04 · HD003908.01] Lốp xe Sailun 185/65R15 (Sailun185/65R15)", 4, 734550),
                new SeedLine("SAILUN205/55R17", "[12/10/2026 17:04 · HD003908.01] Lốp xe SAILUN 205/55R17 95V CS51 (SAILUN205/55R17)", 4, 1050752),
                new SeedLine("Sailun195/60R16", "[12/10/2026 17:04 · HD003908.01] Lốp xe 195/60R16 (Sailun195/60R16)", 5, 1076376),
                new SeedLine("11307", "[12/10/2026 17:04 · HD003908.01] Lọc nhớt MD135737 MITSUBISHI L300, 4 chỗ, 7 Chổ, 9 chỗ, 12 chỗ, JOLIE, HYUNDAI i10 Mazda 323, Asia 550kg, Asia Towner, Kia Morning, Kia Pride, Xpander (11307)", 20, 33550),
                new SeedLine("NG5W30", "[12/10/2026 17:04 · HD003908.01] Nhớt động cơ G-Energy Synthetic Super Start 5W-30 (4 Lít/ Can) (NG5W30)", 17, 407000),
                new SeedLine("CASTMA10W40", "[13/10/2026 16:27 · HD003926.01] Nhớt Castrol Magnatec 10w40 (6x4L) (CASTMA10W40)", 2, 549999),
                new SeedLine("DX00023", "[14/10/2026 14:41 · HD003969] Dịch vụ lắp đặt -CAM (SP000004) — Lắp định vị xe Đi Xanh (tb cũ xe 50H36900) 50F16851T", 1, 100000),
                new SeedLine("DX00023", "[15/10/2026 14:39 · HD003968] Dịch vụ lắp đặt -CAM (SP000004) — Lắp định vị xe Đi Xanh (tb cũ xe 50H71829) 50F14337T", 1, 100000),
                new SeedLine("DX00024", "[19/10/2026 09:50 · HD004120] Dịch vụ lắp đặt - Nano (SP000003) — Lắp định vị (Định vị Đi Xanh giữ)", 1, 150000),
                new SeedLine("DX00021", "[19/10/2026 11:42 · HD004045] Gia hạn định vị TDC - MGPS (SP000011) — Gia hạn định vị 60H34437", 1, 450000),
                new SeedLine("DX00018", "[19/10/2026 11:43 · HD004046] Gia hạn Cam AI (1 năm) (SP000109) — Gia hạn Camera 50H94757C", 1, 1780000),
                new SeedLine("DX00020", "[19/10/2026 13:29 · HD004048] Gia hạn định vị TDC (SP000010) — Gia hạn định vị 60H36943", 1, 500000),
                new SeedLine("DX00020", "[19/10/2026 13:30 · HD004049] Gia hạn định vị TDC (SP000010) — Gia hạn định vị 60H37467", 1, 500000),
                new SeedLine("DX00023", "[23/10/2026 11:05 · HD004223] Dịch vụ lắp đặt -CAM (SP000004) — Tháo Cam xe Đi Xanh 65H04602", 1, 300000),
                new SeedLine("SP000077", "[28/10/2026 08:38 · HD004281] Bảo hiểm TNDS - HTX Chính Hiệp (SP000077) — 60H36943", 1, 580240),
                new SeedLine("DX00023", "[28/10/2026 10:42 · HD004314.01] Dịch vụ lắp đặt -CAM (SP000004) — ĐI XANH (Tháo camera) 60H37467", 1, 300000),
            ],
            [
                new SeedPayment("TT002051", "2026-10-28", 23483310, "TT002051 (28/10/2026 16:01) — Thanh toán công nợ T10/2026 · KH Cty Đi Xanh"),
            ]),
        new SeedStatement(
            "HD-KH000080-T10", "Hải Vân Xe Hợp Đồng", "KH000080", "0327999532", "10/2026", "2026-10-31", "2026-11-15",
            [
                new SeedLine("SP000012", "[05/10/2026 10:03 · HD003822] Camera AI - mobicam (SP000012 · Lắp TB cũ — Không tính tiền TB) — Anh Hiếu (lắp cam cũ xe 120) 50H79874C", 1, 0),
                new SeedLine("SP000012", "[13/10/2026 08:50 · HD003948] Camera AI - mobicam (SP000012 · Lắp TB cũ — Không tính tiền TB) — Lê Văn Đại (Lắp cam cũ xe 50H49009) 50F17671", 1, 0),
                new SeedLine("DX00023", "[13/10/2026 08:51 · HD003949] Dịch vụ lắp đặt -CAM (SP000004) — Tháo cam xe 104 50h70880", 1, 300000),
                new SeedLine("SP000012", "[15/10/2026 14:41 · HD003970] Camera AI - mobicam (SP000012 · Lắp TB cũ — Không tính tiền TB) — Anh Hiếu (lắp cam cũ xe 122) 50E19565", 1, 0),
                new SeedLine("DX00018", "[19/10/2026 08:34 · HD004005] Gia hạn Cam AI (1 năm) (SP000109) — 50E11137 50F17671 50E04296 50E19565 50H79874C 51G68433", 6, 1980000),
                new SeedLine("SP000012", "[26/10/2026 11:07 · HD004224] Camera AI - mobicam (SP000012 · Lắp TB cũ — Không tính tiền TB) — Anh Thủy (Lắp camera cũ ) 51G66774", 1, 0),
                new SeedLine("DX00018", "[27/10/2026 11:07 · HD004225] Gia hạn Cam AI (1 năm) (SP000109) — Gia hạn định vị camera 51G66774", 1, 1980000),
                new SeedLine("SP000012", "[27/10/2026 11:12 · HD004226] Camera AI - mobicam (SP000012 · Lắp TB cũ — Không tính tiền TB) — Lắp camera cũ 50E01833", 1, 0),
                new SeedLine("DX00023", "[28/10/2026 16:23 · HD004356] Dịch vụ lắp đặt -CAM (SP000004) — Tháo camera xe Nguyễn Văn Quang", 2, 300000),
            ],
            [
            ]),
        new SeedStatement(
            "HD-KH000080-T09", "Hải Vân Xe Hợp Đồng", "KH000080", "0327999532", "09/2026", "2026-09-30", "2026-10-15",
            [
                new SeedLine("DX00023", "[03/09/2026 09:29 · HD003573] Dịch vụ lắp đặt -CAM (SP000004) — Tháo cam AI xe 114 51h59653", 1, 300000),
                new SeedLine("DX00023", "[03/09/2026 09:30 · HD003574] Dịch vụ lắp đặt -CAM (SP000004) — Tháo cam AI xe 132 50h33466", 1, 300000),
                new SeedLine("DX00023", "[21/09/2026 11:10 · HD003616] Dịch vụ lắp đặt -CAM (SP000004) — Tháo camera A Hưng 50H42651", 1, 300000),
                new SeedLine("DX00018", "[24/09/2026 09:15 · HD003656] Gia hạn Cam AI (1 năm) (SP000109) — Gia hạn Camera 51H64592", 1, 1980000),
            ],
            [
            ]),
    ];
}
