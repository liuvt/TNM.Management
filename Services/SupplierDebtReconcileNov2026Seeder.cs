using Microsoft.Data.Sqlite;

namespace TNM.Management.Services;

/// <summary>
/// Đối soát &amp; chuẩn hóa toàn diện Công nợ Nhà cung cấp (TDC, Xanh GPS MID, NCC Nasa, CHEMTEC):
/// 1. Tồn kho cũ đầu kỳ (NK-NCC00001/2/3/5-TONKHO): tách đúng số lượng tồn đầu kỳ (không trùng lô nhập T11), chốt tất toán &amp; khóa sổ = 0 ₫.
/// 2. Công nợ kỳ Tháng 10/2026 chuyển sang (31/10/2026, chưa tất toán, chưa chốt số):
///    - NCC00001 (Tài xế công nghệ TDC): NK-NCC00001-T10 = 51,500,000 ₫ (OPEN)
///    - NCC00002 (Xanh GPS MID):         NK-NCC00002-T10 = 84,050,000 ₫ (OPEN)
///    - NCC00003 (NCC Nasa):             NK-NCC00003-T10 = 62,680,000 ₫ (PARTIAL: 64,460,000 − 1,780,000)
/// 3. Khôi phục đầy đủ 5 đơn nhập kho thực tế kỳ Tháng 11/2026 bị quét nhầm trước đó (chưa tất toán, chưa chốt số):
///    - NK-0F3F2F70F41D (11/11/2026 · NCC Nasa): 60 × SP000019 (Nasa GPS 4G nano) × 570,000 = 34,200,000 ₫
///    - NK-7DE6B233CEB6 (11/11/2026 · TDC):      5 × CAM4CH (Cam AI 4CH) × 5,005,000 = 25,025,000 ₫
///    - NK-EB3A031282F9 (11/11/2026 · TDC):      3 × CAM035 (Camera CM035) × 550,000 = 1,650,000 ₫
///    - NK-9A23A1C386BB (11/11/2026 · TDC):      20 × SP000006 (Định vị TDC - MGPS Biển trắng) × 610,000 = 12,200,000 ₫
///    - NK-19F9F611E00B (16/11/2026 · TDC):      5 × SP000005 (Định vị TDC) × 700,000 = 3,500,000 ₫
/// </summary>
public static class SupplierDebtReconcileNov2026Seeder
{
    private sealed record TonKhoSpec(
        string InvoiceNo,
        long SupplierId,
        string SupCode,
        string SupName,
        (string Sku, bool IsImei, long Qty, long UnitCost)[] Lines);

    private sealed record NovReceiptSpec(
        string InvoiceNo,
        long SupplierId,
        string IssuedAt,
        string DueAt,
        string TxOrRefGuardSql,
        string Sku,
        long Qty,
        long UnitCost,
        string Notes,
        string LineDescription);

    public static void EnsureSeeded(SqliteConnection c, SqliteTransaction? tx, Action<long> setActor, Func<long> getActor)
    {
        var tdcId = Scalar(c, tx, "SELECT COALESCE((SELECT id FROM suppliers WHERE deleted_at IS NULL AND code='NCC00001' AND LOWER(name) LIKE '%tdc%' LIMIT 1), 0)");
        var xanhId = Scalar(c, tx, "SELECT COALESCE((SELECT id FROM suppliers WHERE deleted_at IS NULL AND code='NCC00002' AND LOWER(name) LIKE '%xanh gps%' LIMIT 1), 0)");
        var nasaId = Scalar(c, tx, "SELECT COALESCE((SELECT id FROM suppliers WHERE deleted_at IS NULL AND code='NCC00003' AND LOWER(name) LIKE '%nasa%' LIMIT 1), 0)");
        if (tdcId <= 0 || xanhId <= 0 || nasaId <= 0)
            return;

        var prevActor = getActor();
        var devId = Scalar(c, tx, "SELECT COALESCE((SELECT id FROM app_users WHERE LOWER(username)='vanlt_it' LIMIT 1), 0)");
        try
        {
            if (devId > 0) setActor(devId);

            // 1. Đồng bộ snapshot giá vốn cho lô 5 IMEI CAM4CH (NK-A85559A548) nếu lúc nhập đầu tiên đơn giá còn = 0
            Exec(c, tx, @"
                UPDATE inventory_transaction_lines
                SET unit_cost_snapshot_vnd = 5005000
                WHERE unit_cost_snapshot_vnd = 0
                  AND transaction_id IN (
                      SELECT id FROM inventory_transactions WHERE code = 'NK-A85559A548'
                  )");

            // 2. Chuẩn hóa 4 chứng từ Tồn kho cũ đầu kỳ (đưa toàn bộ tồn kho cũ về PAID + Đã chốt số = 0 ₫, không trùng lô nhập T11)
            var tonKhoSpecs = new[]
            {
                new TonKhoSpec("NK-NCC00001-TONKHO", tdcId, "NCC00001", "Tài xế công nghệ TDC",
                [
                    ("SP000005", true,  2,   700_000),
                    ("SP000006", true,  4,   610_000),
                    ("SP000012", true, 14, 3_850_000),
                ]),
                new TonKhoSpec("NK-NCC00002-TONKHO", xanhId, "NCC00002", "Xanh GPS MID",
                [
                    ("SP000001", true,  16, 680_000),
                    ("SP000034", false, 24,  50_000),
                ]),
                new TonKhoSpec("NK-NCC00003-TONKHO", nasaId, "NCC00003", "NCC Nasa",
                [
                    ("SP000061", false, 5, 50_000),
                ]),
            };

            foreach (var spec in tonKhoSpecs)
            {
                NormalizeSettledOpeningStock(c, tx, devId, spec);
            }

            Exec(c, tx, @"
                UPDATE invoices
                SET issued_at = '2026-11-09 13:30:38',
                    due_at = '2026-12-09',
                    status = 'PAID',
                    is_locked = 1,
                    locked_at = COALESCE(NULLIF(locked_at,''), '2026-11-20 09:32:00')
                WHERE invoice_no = 'NK-NCC00005-TONKHO'
                  AND deleted_at IS NULL
                  AND (status <> 'PAID' OR COALESCE(is_locked, 0) = 0 OR issued_at LIKE '2026-11-20%')");

            // 3. Bổ sung Công nợ Nhà cung cấp kỳ Tháng 10/2026 chuyển sang cho TDC (51,500,000 ₫) & Xanh GPS MID (84,050,000 ₫)
            EnsureOct2026CarryOverStatement(
                c, tx,
                invoiceNo: "NK-NCC00001-T10",
                supplierId: tdcId,
                amountVnd: 51_500_000,
                notes: "Công nợ Nhà cung cấp kỳ Tháng 10/2026 chuyển sang (01/10/2026 - 31/10/2026 · NCC00001 — Tài xế công nghệ TDC: Còn nợ 51,500,000 ₫)",
                lineDesc: "[31/10/2026] Công nợ Nhà cung cấp chốt đến hết kỳ Tháng 10/2026 — Tài xế công nghệ TDC");

            EnsureOct2026CarryOverStatement(
                c, tx,
                invoiceNo: "NK-NCC00002-T10",
                supplierId: xanhId,
                amountVnd: 84_050_000,
                notes: "Công nợ Nhà cung cấp kỳ Tháng 10/2026 chuyển sang (01/10/2026 - 31/10/2026 · NCC00002 — Xanh GPS MID: Còn nợ 84,050,000 ₫)",
                lineDesc: "[31/10/2026] Công nợ Nhà cung cấp chốt đến hết kỳ Tháng 10/2026 — Xanh GPS MID");

            // 4. Khôi phục 5 đơn nhập kho thực tế kỳ Tháng 11/2026 (Nasa 60 định vị + TDC 4 phiếu nhập kho)
            var novReceipts = new[]
            {
                new NovReceiptSpec(
                    "NK-0F3F2F70F41D", nasaId, "2026-11-11 09:49:28", "2026-12-11",
                    "SELECT COUNT(*) FROM inventory_transactions WHERE code='NK-7C0F97EFD8'",
                    "SP000019", 60, 570_000,
                    "Nhập lô 60 IMEI (NK-7C0F97EFD8): SP000019 · Nasa GPS 4G nano",
                    "Nhập kho IMEI (NK-7C0F97EFD8): SP000019 · Nasa GPS 4G nano"),

                new NovReceiptSpec(
                    "NK-7DE6B233CEB6", tdcId, "2026-11-11 09:56:43", "2026-12-11",
                    "SELECT COUNT(*) FROM inventory_transactions WHERE code='NK-A85559A548'",
                    "CAM4CH", 5, 5_005_000,
                    "Nhập lô 5 IMEI (NK-A85559A548): CAM4CH · Cam AI 4CH",
                    "Nhập kho IMEI (NK-A85559A548): CAM4CH · Cam AI 4CH"),

                new NovReceiptSpec(
                    "NK-EB3A031282F9", tdcId, "2026-11-11 10:34:25", "2026-12-11",
                    "SELECT COUNT(*) FROM quantity_movements WHERE document_no='Q-2398dea7dcd134f3'",
                    "CAM035", 3, 550_000,
                    "Nhập kho số lượng (3 × CAM035 · Camera CM035)",
                    "Nhập kho số lượng (3 × CAM035 · Camera CM035)"),

                new NovReceiptSpec(
                    "NK-9A23A1C386BB", tdcId, "2026-11-11 10:52:11", "2026-12-11",
                    "SELECT COUNT(*) FROM inventory_transactions WHERE code='NK-5EEFA655D1'",
                    "SP000006", 20, 610_000,
                    "Nhập lô 20 IMEI (NK-5EEFA655D1): SP000006 · Định vị TDC - MGPS (Biển trắng)",
                    "Nhập kho IMEI (NK-5EEFA655D1): SP000006 · Định vị TDC - MGPS (Biển trắng)"),

                new NovReceiptSpec(
                    "NK-19F9F611E00B", tdcId, "2026-11-16 14:03:38", "2026-12-16",
                    "SELECT COUNT(*) FROM inventory_transactions WHERE code='NK-5840765FF8'",
                    "SP000005", 5, 700_000,
                    "Nhập lô 5 IMEI (NK-5840765FF8): SP000005 · Định vị TDC",
                    "Nhập kho IMEI (NK-5840765FF8): SP000005 · Định vị TDC"),
            };

            foreach (var r in novReceipts)
            {
                EnsureNov2026WarehouseReceipt(c, tx, r);
            }
        }
        finally
        {
            setActor(prevActor);
        }
    }

    private static void NormalizeSettledOpeningStock(SqliteConnection c, SqliteTransaction? tx, long devId, TonKhoSpec spec)
    {
        var invId = Scalar(c, tx, "SELECT COALESCE((SELECT id FROM invoices WHERE invoice_no=$no AND deleted_at IS NULL LIMIT 1), 0)", ("$no", spec.InvoiceNo));
        if (invId <= 0)
            return;

        // Không đụng vào nếu chứng từ đã phát sinh phiếu chi phân bổ thực tế trong sổ quỹ
        var hasPayment = Scalar(c, tx, "SELECT COUNT(*) FROM payment_allocations WHERE invoice_id=$id AND deleted_at IS NULL", ("$id", invId)) > 0;
        if (hasPayment)
            return;

        var expectedTotal = spec.Lines.Sum(x => x.Qty * x.UnitCost);
        var expectedQty = spec.Lines.Sum(x => x.Qty);
        var expectedNote = $"Tồn kho cũ đầu kỳ — Đã tất toán & chốt số đưa về 0 ({spec.SupCode} — {spec.SupName} · {expectedQty:N0} SP)";

        var curAmt = Scalar(c, tx, "SELECT amount_vnd FROM invoices WHERE id=$id", ("$id", invId));
        var curStatus = ScalarText(c, tx, "SELECT status FROM invoices WHERE id=$id", ("$id", invId));
        var curLocked = Scalar(c, tx, "SELECT COALESCE(is_locked,0) FROM invoices WHERE id=$id", ("$id", invId));
        var curIssued = ScalarText(c, tx, "SELECT COALESCE(issued_at,'') FROM invoices WHERE id=$id", ("$id", invId));
        var activeLineCount = Scalar(c, tx, "SELECT COUNT(*) FROM invoice_lines WHERE invoice_id=$id AND deleted_at IS NULL", ("$id", invId));

        if (curAmt == expectedTotal && curStatus == "PAID" && curLocked == 1 && curIssued == "2026-11-09 13:30:38" && activeLineCount == spec.Lines.Length)
            return;

        Exec(c, tx, @"
            UPDATE invoices
            SET supplier_id = $sup,
                issued_at = '2026-11-09 13:30:38',
                due_at = '2026-12-09',
                amount_vnd = $amt,
                offset_vnd = 0,
                status = 'PAID',
                is_locked = 1,
                locked_at = COALESCE(NULLIF(locked_at,''), '2026-11-20 09:32:00'),
                notes = $note
            WHERE id = $id",
            ("$sup", spec.SupplierId),
            ("$amt", expectedTotal),
            ("$note", expectedNote),
            ("$id", invId));

        if (curAmt != expectedTotal || activeLineCount != spec.Lines.Length)
        {
            if (devId > 0)
            {
                Exec(c, tx, "DELETE FROM invoice_lines WHERE invoice_id=$id", ("$id", invId));
            }
            else
            {
                Exec(c, tx, "UPDATE invoice_lines SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE invoice_id=$id AND deleted_at IS NULL", ("$id", invId));
            }

            foreach (var ln in spec.Lines)
            {
                var prodId = ResolveProductId(c, tx, ln.Sku);
                if (prodId <= 0) continue;
                var prodName = ScalarText(c, tx, "SELECT name FROM products WHERE id=$id", ("$id", prodId));
                var kindLabel = ln.IsImei ? "Tồn kho IMEI" : "Tồn kho hàng hóa";
                Exec(c, tx, @"
                    INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd)
                    VALUES($i,$d,$p,$q,$u)",
                    ("$i", invId),
                    ("$d", $"{kindLabel}: {ln.Sku} — {prodName}"),
                    ("$p", prodId),
                    ("$q", ln.Qty),
                    ("$u", ln.UnitCost));
            }
        }
    }

    private static void EnsureOct2026CarryOverStatement(
        SqliteConnection c,
        SqliteTransaction? tx,
        string invoiceNo,
        long supplierId,
        long amountVnd,
        string notes,
        string lineDesc)
    {
        var existingId = Scalar(c, tx, "SELECT COALESCE((SELECT id FROM invoices WHERE invoice_no=$no LIMIT 1), 0)", ("$no", invoiceNo));
        if (existingId > 0)
        {
            // Nếu từng bị xóa mềm thì khôi phục lại; nếu đang hoạt động thì giữ nguyên trạng thái thu/chi của kế toán
            var deletedAt = ScalarText(c, tx, "SELECT COALESCE(deleted_at,'') FROM invoices WHERE id=$id", ("$id", existingId));
            if (!string.IsNullOrEmpty(deletedAt))
            {
                Exec(c, tx, @"
                    UPDATE invoices
                    SET deleted_at = NULL,
                        deleted_by = NULL,
                        status = 'OPEN',
                        is_locked = 0,
                        locked_at = NULL,
                        locked_by = NULL
                    WHERE id = $id", ("$id", existingId));
                Exec(c, tx, "UPDATE invoice_lines SET deleted_at = NULL, deleted_by = NULL WHERE invoice_id = $id", ("$id", existingId));
            }
            return;
        }

        Exec(c, tx, @"
            INSERT INTO invoices(invoice_no,invoice_type,supplier_id,issued_at,due_at,amount_vnd,offset_vnd,status,is_locked,notes)
            VALUES($no,'PURCHASE',$sup,'2026-10-31 23:59:59','2026-11-15',$amt,0,'OPEN',0,$note)",
            ("$no", invoiceNo),
            ("$sup", supplierId),
            ("$amt", amountVnd),
            ("$note", notes));
        var newInvId = Scalar(c, tx, "SELECT last_insert_rowid()");

        Exec(c, tx, @"
            INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd)
            VALUES($i,$d,NULL,1,$u)",
            ("$i", newInvId),
            ("$d", lineDesc),
            ("$u", amountVnd));
    }

    private static void EnsureNov2026WarehouseReceipt(SqliteConnection c, SqliteTransaction? tx, NovReceiptSpec spec)
    {
        // Chỉ khôi phục khi phiếu nhập kho gốc thực sự tồn tại trong DB
        if (Scalar(c, tx, spec.TxOrRefGuardSql) <= 0)
            return;

        var prodId = ResolveProductId(c, tx, spec.Sku);
        if (prodId <= 0)
            return;

        var existingId = Scalar(c, tx, "SELECT COALESCE((SELECT id FROM invoices WHERE invoice_no=$no LIMIT 1), 0)", ("$no", spec.InvoiceNo));
        if (existingId > 0)
        {
            var deletedAt = ScalarText(c, tx, "SELECT COALESCE(deleted_at,'') FROM invoices WHERE id=$id", ("$id", existingId));
            if (!string.IsNullOrEmpty(deletedAt))
            {
                Exec(c, tx, @"
                    UPDATE invoices
                    SET deleted_at = NULL,
                        deleted_by = NULL,
                        status = 'OPEN',
                        is_locked = 0,
                        locked_at = NULL,
                        locked_by = NULL,
                        issued_at = $iss,
                        due_at = $due,
                        amount_vnd = $amt,
                        notes = $note
                    WHERE id = $id",
                    ("$iss", spec.IssuedAt),
                    ("$due", spec.DueAt),
                    ("$amt", spec.Qty * spec.UnitCost),
                    ("$note", spec.Notes),
                    ("$id", existingId));
                Exec(c, tx, "UPDATE invoice_lines SET deleted_at = NULL, deleted_by = NULL WHERE invoice_id = $id", ("$id", existingId));
            }
            return;
        }

        var totalAmt = spec.Qty * spec.UnitCost;
        Exec(c, tx, @"
            INSERT INTO invoices(invoice_no,invoice_type,supplier_id,issued_at,due_at,amount_vnd,offset_vnd,status,is_locked,notes)
            VALUES($no,'PURCHASE',$sup,$iss,$due,$amt,0,'OPEN',0,$note)",
            ("$no", spec.InvoiceNo),
            ("$sup", spec.SupplierId),
            ("$iss", spec.IssuedAt),
            ("$due", spec.DueAt),
            ("$amt", totalAmt),
            ("$note", spec.Notes));
        var newInvId = Scalar(c, tx, "SELECT last_insert_rowid()");

        Exec(c, tx, @"
            INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd)
            VALUES($i,$d,$p,$q,$u)",
            ("$i", newInvId),
            ("$d", spec.LineDescription),
            ("$p", prodId),
            ("$q", spec.Qty),
            ("$u", spec.UnitCost));
    }

    private static long ResolveProductId(SqliteConnection c, SqliteTransaction? tx, string sku) =>
        Scalar(c, tx, "SELECT COALESCE((SELECT id FROM products WHERE LOWER(sku)=LOWER($sku) AND deleted_at IS NULL LIMIT 1), 0)", ("$sku", sku));

    private static long Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
    }

    private static string ScalarText(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return Convert.ToString(cmd.ExecuteScalar() ?? "") ?? "";
    }

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
