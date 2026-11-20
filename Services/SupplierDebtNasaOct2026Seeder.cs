using Microsoft.Data.Sqlite;

namespace TNM.Management.Services;

/// <summary>
/// Đồng bộ bảng kê Chi tiết Công nợ Nhà cung cấp NCC Nasa (NCC00003) kỳ Tháng 10/2026:
/// Tổng phát sinh 61 mục = 64,460,000 ₫ − Cấn trừ Nasa thu hộ 7 xe = 1,780,000 ₫ → Còn nợ đến hết T10/2026 = 62,680,000 ₫.
/// </summary>
public static class SupplierDebtNasaOct2026Seeder
{
    private sealed record NasaRow(
        int Stt,
        string DateDdMmYyyy,
        string UserCol,
        string PlateCol,
        long ThuHoVnd,
        long SoTienVnd,
        string Note);

    public static void EnsureSeeded(SqliteConnection c, SqliteTransaction? tx, Action<long> setActor, Func<long> getActor)
    {
        var supId = Scalar(c, tx, @"
            SELECT COALESCE((
                SELECT id FROM suppliers
                WHERE deleted_at IS NULL AND (code='NCC00003' OR LOWER(name) LIKE '%nasa%')
                ORDER BY id LIMIT 1
            ), 0)");
        if (supId <= 0)
            return;

        const string invoiceNo = "NK-NCC00003-T10";
        if (Scalar(c, tx, "SELECT COUNT(*) FROM invoices WHERE invoice_no=$no AND deleted_at IS NULL", ("$no", invoiceNo)) > 0)
            return;

        var prevActor = getActor();
        var devId = Scalar(c, tx, "SELECT COALESCE((SELECT id FROM app_users WHERE LOWER(username)='vanlt_it' LIMIT 1), 0)");
        try
        {
            if (devId > 0) setActor(devId);

            // Cập nhật thông tin tài khoản thanh toán vào danh mục NCC Nasa (tax_code / ghi chú tham chiếu nếu đang trống)
            Exec(c, tx, @"
                UPDATE suppliers
                SET tax_code = CASE WHEN COALESCE(tax_code,'')='' THEN 'HD BANK: 0911887932 (Ha Phuc Vinh)' ELSE tax_code END
                WHERE id = $id", ("$id", supId));

            var prodDx00002 = ResolveProductId(c, tx, "DX00002");
            var prodSp000019 = ResolveProductId(c, tx, "SP000019");
            var prodDx00022 = ResolveProductId(c, tx, "DX00022");

            var rows = BuildOct2026Rows();
            var totalIncurred = rows.Sum(r => r.SoTienVnd);   // 64,460,000 ₫
            var totalThuHo = rows.Sum(r => r.ThuHoVnd);       // 1,780,000 ₫
            var netRemaining = totalIncurred - totalThuHo;    // 62,680,000 ₫

            var note = $"Chi tiết Công nợ NCC Nasa T10/2026 (01/10/2026 - 31/10/2026 · 61 mục: Tổng phát sinh {totalIncurred:N0} ₫ − Trừ thu hộ {totalThuHo:N0} ₫ = Còn nợ {netRemaining:N0} ₫) · TT Thanh toán: STK 0911887932 — Ngân hàng HD BANK — Chủ TK: Ha Phuc Vinh";

            Exec(c, tx, @"
                INSERT INTO invoices(invoice_no,invoice_type,supplier_id,issued_at,due_at,amount_vnd,offset_vnd,status,notes)
                VALUES($no,'PURCHASE',$sup,'2026-10-31 23:59:59','2026-11-15',$amt,$off,'PARTIAL',$note)",
                ("$no", invoiceNo),
                ("$sup", supId),
                ("$amt", totalIncurred),
                ("$off", totalThuHo),
                ("$note", note));
            var invId = Scalar(c, tx, "SELECT last_insert_rowid()");

            foreach (var r in rows)
            {
                long? prodId = null;
                long qty = 1;
                long unitPrice = r.SoTienVnd;
                string desc;

                if (r.UserCol.StartsWith("GIAO 20 THIẾT BỊ", StringComparison.OrdinalIgnoreCase))
                {
                    prodId = prodSp000019 > 0 ? prodSp000019 : null;
                    qty = 20;
                    unitPrice = 570_000;
                    desc = $"[#{r.Stt:D2} · {r.DateDdMmYyyy}] {r.UserCol} — Nasa GPS 4G nano";
                }
                else if (r.UserCol.StartsWith("GIAO 1 THIẾT BỊ", StringComparison.OrdinalIgnoreCase))
                {
                    prodId = prodSp000019 > 0 ? prodSp000019 : null;
                    qty = 1;
                    unitPrice = 570_000;
                    desc = $"[#{r.Stt:D2} · {r.DateDdMmYyyy}] {r.UserCol} — Nasa GPS 4G nano";
                }
                else if (r.ThuHoVnd > 0)
                {
                    prodId = prodDx00002 > 0 ? prodDx00002 : null;
                    qty = 1;
                    unitPrice = 0;
                    var userPlate = string.Equals(r.UserCol, r.PlateCol, StringComparison.OrdinalIgnoreCase)
                        ? $"BSX: {r.PlateCol}"
                        : $"User: {r.UserCol} · BSX: {r.PlateCol}";
                    desc = $"[#{r.Stt:D2} · {r.DateDdMmYyyy}] {userPlate} — Cấn trừ Thu hộ: -{r.ThuHoVnd:N0} ₫ ({r.Note})";
                }
                else if (r.Note.Contains("GIA HẠN", StringComparison.OrdinalIgnoreCase))
                {
                    prodId = prodDx00022 > 0 ? prodDx00022 : null;
                    qty = 1;
                    unitPrice = r.SoTienVnd;
                    var userPlate = string.Equals(r.UserCol, r.PlateCol, StringComparison.OrdinalIgnoreCase)
                        ? $"BSX: {r.PlateCol}"
                        : $"User: {r.UserCol} · BSX: {r.PlateCol}";
                    desc = $"[#{r.Stt:D2} · {r.DateDdMmYyyy}] {userPlate} — Gia hạn định vị Nasa";
                }
                else
                {
                    prodId = prodDx00002 > 0 ? prodDx00002 : null;
                    qty = 1;
                    unitPrice = r.SoTienVnd;
                    var userPlate = string.Equals(r.UserCol, r.PlateCol, StringComparison.OrdinalIgnoreCase)
                        ? $"BSX: {r.PlateCol}"
                        : $"User: {r.UserCol} · BSX: {r.PlateCol}";
                    var notePart = string.IsNullOrWhiteSpace(r.Note) ? "Định vị Nasa" : $"Định vị Nasa ({r.Note})";
                    desc = $"[#{r.Stt:D2} · {r.DateDdMmYyyy}] {userPlate} — {notePart}";
                }

                Exec(c, tx, @"
                    INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd)
                    VALUES($i,$d,$p,$q,$u)",
                    ("$i", invId),
                    ("$d", desc),
                    ("$p", prodId),
                    ("$q", qty),
                    ("$u", unitPrice));
            }
        }
        finally
        {
            setActor(prevActor);
        }
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

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static NasaRow[] BuildOct2026Rows() =>
    [
        new(1,  "01/10/2026", "81H05423", "81H05423", 0, 600_000, ""),
        new(2,  "01/10/2026", "65H08327", "65H08327", 0, 600_000, ""),
        new(3,  "01/10/2026", "ngocdiep", "68H17226", 0, 600_000, ""),
        new(4,  "01/10/2026", "133577",   "50F11492", 0, 600_000, ""),
        new(5,  "02/10/2026", "68H09277", "68H09277", 0, 600_000, ""),
        new(6,  "02/10/2026", "GIAO 20 THIẾT BỊ 3T02-S", "", 0, 11_400_000, ""),
        new(7,  "02/10/2026", "61E04009", "61E04009", 0, 600_000, ""),
        new(8,  "03/10/2026", "70H21475", "70H21475", 0, 600_000, ""),
        new(9,  "06/10/2026", "65H11564", "65H11564", 0, 600_000, "TK cũ: 001014"),
        new(10, "06/10/2026", "50E30314", "50E30314", 360_000, 0, "NASA THU 960K"),
        new(11, "06/10/2026", "50F10625", "50F10625", 0, 600_000, ""),
        new(12, "06/10/2026", "50F18087", "50F18087", 0, 600_000, "TK cũ: 833979"),
        new(13, "07/10/2026", "70H22373", "70H22373", 0, 600_000, ""),
        new(14, "07/10/2026", "50F23473", "50F23473", 0, 600_000, ""),
        new(15, "07/10/2026", "50F17557", "50F17557", 0, 600_000, ""),
        new(16, "07/10/2026", "50F17545", "50F17545", 360_000, 0, "NASA THU 960K"),
        new(17, "07/10/2026", "50G00811", "50G00811", 0, 600_000, ""),
        new(18, "08/10/2026", "50F20561", "50F20561", 0, 600_000, ""),
        new(19, "08/10/2026", "50F21761", "50F21761", 0, 600_000, ""),
        new(20, "08/10/2026", "lethanhbao", "50H79874", 0, 600_000, ""),
        new(21, "09/10/2026", "60H47736", "60H47736", 0, 600_000, "TK cũ: truongquocdung"),
        new(22, "09/10/2026", "68H14816", "68H14816", 0, 600_000, ""),
        new(23, "09/10/2026", "havanminh", "50H85692", 0, 540_000, "GIA HẠN"),
        new(24, "12/10/2026", "61E04248", "61E04248", 100_000, 0, "NASA THU 800K (100K PHÍ XA)"),
        new(25, "12/10/2026", "66H08325", "66H08325", 0, 600_000, "TK cũ: 014078"),
        new(26, "12/10/2026", "50H82754", "50H82754", 0, 650_000, "50K THẺ TX"),
        new(27, "12/10/2026", "50F23874", "50F23874", 0, 600_000, "TK cũ: 012071"),
        new(28, "12/10/2026", "83E00316", "83E00316", 0, 600_000, ""),
        new(29, "13/10/2026", "50E17106", "50E17106", 0, 540_000, "GIA HẠN"),
        new(30, "13/10/2026", "50F20911", "50F20911", 0, 600_000, ""),
        new(31, "13/10/2026", "60H44938", "60H44938", 300_000, 0, "NASA THU 900K"),
        new(32, "14/10/2026", "50F18031", "50F18031", 0, 600_000, ""),
        new(33, "15/10/2026", "50F14345", "50F14345", 0, 600_000, ""),
        new(34, "15/10/2026", "091896",   "50E95021", 0, 600_000, ""),
        new(35, "15/10/2026", "GIAO 20 THIẾT BỊ 3T02-S", "", 0, 11_400_000, ""),
        new(36, "16/10/2026", "50F20771", "50F20771", 0, 600_000, ""),
        new(37, "16/10/2026", "50E31743", "50E31743", 0, 540_000, "GIA HẠN"),
        new(38, "19/10/2026", "69H04620", "69H04620", 0, 600_000, ""),
        new(39, "19/10/2026", "49H06540", "49H06540", 200_000, 0, "NASA THU 800K"),
        new(40, "19/10/2026", "50F23285", "50F23285", 260_000, 0, "NASA THU 860K"),
        new(41, "22/10/2026", "750824",   "750824",   0, 600_000, ""),
        new(42, "22/10/2026", "50F14319", "50F14319", 0, 600_000, ""),
        new(43, "22/10/2026", "846517",   "70H21859", 0, 600_000, ""),
        new(44, "22/10/2026", "50E25923", "50E25923", 200_000, 0, "NASA THU 800K"),
        new(45, "22/10/2026", "60H38382", "60H38382", 0, 600_000, "TK cũ: 826516"),
        new(46, "24/10/2026", "50F14941", "50F14941", 0, 600_000, ""),
        new(47, "24/10/2026", "64H05560", "64H05560", 0, 540_000, "GIA HẠN"),
        new(48, "26/10/2026", "50E33647", "50E33647", 0, 600_000, ""),
        new(49, "26/10/2026", "GIAO 20 THIẾT BỊ 3T02-S", "", 0, 11_400_000, ""),
        new(50, "26/10/2026", "69H01763", "69H01763", 0, 600_000, ""),
        new(51, "27/10/2026", "50E29607", "50E29607", 0, 600_000, ""),
        new(52, "27/10/2026", "134311",   "134311",   0, 600_000, ""),
        new(53, "27/10/2026", "847760",   "847760",   0, 600_000, ""),
        new(54, "28/10/2026", "50F18059", "50F18059", 0, 600_000, ""),
        new(55, "29/10/2026", "81H07123", "81H07123", 0, 600_000, ""),
        new(56, "30/10/2026", "50F26295", "50F26295", 0, 600_000, ""),
        new(57, "30/10/2026", "50E54310", "50E54310", 0, 540_000, "GIA HẠN"),
        new(58, "31/10/2026", "68H13644", "68H13644", 0, 600_000, ""),
        new(59, "31/10/2026", "626994",   "626994",   0, 600_000, ""),
        new(60, "31/10/2026", "GIAO 1 THIẾT BỊ 3T02-S",  "", 0, 570_000, ""),
        new(61, "31/10/2026", "75H05601", "75H05601", 0, 540_000, "GIA HẠN"),
    ];
}
