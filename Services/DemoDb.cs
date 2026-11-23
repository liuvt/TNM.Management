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
        try
        {
            var result = action(c,tx);
            tx.Commit();
            NotifyNavCountsChanged();
            return result;
        }
        catch { tx.Rollback(); throw; }
    }
    private bool IsMainDeveloper(SqliteConnection c, SqliteTransaction? tx, long actorId = 0)
    {
        var id = actorId > 0 ? actorId : ActorId;
        return id > 0 && N(c, tx, "SELECT COUNT(*) FROM app_users WHERE id=$u AND LOWER(username)='vanlt_it'", ("$u", id)) > 0;
    }
    private void Log(SqliteConnection c, SqliteTransaction tx, string action, string entity,
        long entityId, string message)
    {
        if (IsMainDeveloper(c, tx)) return;
        Exec(c, tx,
            "INSERT INTO audit_logs(actor_user_id,action,entity,entity_id,details) VALUES($actor,$a,$e,$id,$d)",
            ("$actor",ActorId > 0 ? ActorId : null),("$a",action),("$e",entity),("$id",entityId),("$d",message));
    }

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
        var cgCols = Query(c, "PRAGMA table_info(customer_groups)", r => S(r, 1));
        if (!cgCols.Any(x => string.Equals(x, "parent_id", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE customer_groups ADD COLUMN parent_id INTEGER REFERENCES customer_groups(id)");

        var custCols = Query(c, "PRAGMA table_info(customers)", r => S(r, 1));
        if (!custCols.Any(x => string.Equals(x, "representative_name", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE customers ADD COLUMN representative_name TEXT NOT NULL DEFAULT ''");
        if (!custCols.Any(x => string.Equals(x, "seller_id", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE customers ADD COLUMN seller_id INTEGER REFERENCES v7_sellers(id)");
        if (!custCols.Any(x => string.Equals(x, "sub_group_id", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE customers ADD COLUMN sub_group_id INTEGER REFERENCES customer_groups(id)");

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
        if (!invCols.Any(x => string.Equals(x, "offset_vnd", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE invoices ADD COLUMN offset_vnd INTEGER NOT NULL DEFAULT 0");

        Exec(c, null, @"
            CREATE TABLE IF NOT EXISTS expense_categories(
                id INTEGER PRIMARY KEY,
                code TEXT NOT NULL UNIQUE,
                direction TEXT NOT NULL DEFAULT 'OUT',
                name TEXT NOT NULL,
                group_name TEXT NOT NULL DEFAULT 'Chi phí Vận hành & Quản lý',
                target_entity TEXT NOT NULL DEFAULT 'FREE',
                default_note TEXT NOT NULL DEFAULT '',
                sort_order INTEGER NOT NULL DEFAULT 100,
                is_system INTEGER NOT NULL DEFAULT 0 CHECK(is_system IN (0,1)),
                is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1)),
                created_at TEXT,
                created_by INTEGER,
                updated_at TEXT,
                updated_by INTEGER,
                deleted_at TEXT,
                deleted_by INTEGER
            );");

        var expCatCols = Query(c, "PRAGMA table_info(expense_categories)", r => S(r, 1));
        if (!expCatCols.Any(x => string.Equals(x, "direction", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE expense_categories ADD COLUMN direction TEXT NOT NULL DEFAULT 'OUT'");

        (string Code, string Dir, string Name, string Group, string Target, string Note, int Sort, int Sys)[] defaultCashCats =
        [
            // Danh mục Khoản Thu (IN) mặc định
            ("THU_DON_LAP", "IN", "Thu tiền khách hàng — Đơn hàng lắp đặt", "1. Thu bán hàng & Lắp đặt", "CUSTOMER", "Thu tiền lắp đặt thiết bị từ khách hàng / đại lý / HTX", 10, 1),
            ("THU_GIA_HAN", "IN", "Thu tiền gia hạn thuê bao / thiết bị", "2. Thu gia hạn & Dịch vụ", "CUSTOMER", "Thu phí gia hạn định vị / camera từ khách hàng", 20, 1),
            ("THU_BAN_LE_VT", "IN", "Thu bán lẻ thiết bị, SIM & vật tư", "1. Thu bán hàng & Lắp đặt", "CUSTOMER", "Thu tiền bán lẻ thiết bị, phụ kiện, SIM data", 30, 0),
            ("THU_DAT_COC", "IN", "Thu tiền đặt cọc đơn hàng / hợp đồng", "1. Thu bán hàng & Lắp đặt", "CUSTOMER", "Thu tiền khách hàng đặt cọc trước khi lắp đặt", 40, 0),
            ("THU_HOAN_NCC", "IN", "Thu hoàn tiền / chiết khấu từ Nhà cung cấp", "3. Thu hoàn ứng & Đối tác", "SUPPLIER", "Nhận tiền chiết khấu thương mại hoặc hoàn tiền từ Nhà cung cấp", 50, 0),
            ("THU_HOAN_UNG_NS", "IN", "Thu hoàn tạm ứng Kỹ thuật / Nhân sự", "3. Thu hoàn ứng & Đối tác", "INSTALLER", "Thu hồi tiền tạm ứng công tác / vật tư từ nhân sự", 60, 0),
            ("THU_KHAC", "IN", "Khoản thu khác (Tùy chỉnh)", "4. Thu tài chính & Khác", "FREE", "Khoản thu phát sinh khác (lãi tiền gửi, thanh lý, thu khác...)", 70, 1),

            // Danh mục Khoản Chi (OUT) mặc định
            ("CHI_NCC", "OUT", "Chi trả tiền hàng Nhà cung cấp", "1. Giá vốn & Nhập hàng NCC", "SUPPLIER", "Thanh toán tiền nhập thiết bị / hàng hóa Nhà cung cấp", 10, 1),
            ("CHI_CONG_LAP", "OUT", "Chi công kỹ thuật / Đơn vị lắp đặt", "2. Bán hàng & Lắp đặt", "INSTALLER", "Thanh toán phụ cấp / công lắp đặt kỹ thuật", 20, 1),
            ("CHI_HOA_HONG", "OUT", "Chi hoa hồng Nhân viên bán hàng", "2. Bán hàng & Lắp đặt", "SELLER", "Thanh toán hoa hồng bán mới & gia hạn thiết bị", 30, 1),
            ("CHI_KH_HOAN_TIEN", "OUT", "Chi hoàn tiền / Chiết khấu Khách hàng & HTX", "2. Bán hàng & Lắp đặt", "CUSTOMER", "Hoàn tiền hoặc chi chiết khấu thương mại cho KH / Đại lý", 40, 0),
            ("CHI_SIM_SERVER", "OUT", "Chi cước SIM 4G / Máy chủ & Phần mềm GPS", "1. Giá vốn & Nhập hàng NCC", "SUPPLIER", "Thanh toán cước SIM Data 4G / duy trì hệ thống máy chủ", 50, 0),
            ("CHI_LUONG_THUONG", "OUT", "Chi lương, thưởng & phụ cấp nhân sự", "3. Nhân sự & Tiền lương", "INSTALLER", "Thanh toán lương, thưởng, phụ cấp nhân viên nội bộ", 60, 0),
            ("CHI_VAN_CHUYEN", "OUT", "Chi vận chuyển, gửi hàng & công tác phí", "2. Bán hàng & Lắp đặt", "FREE", "Chi phí gửi chành xe, chuyển phát nhanh, xăng xe đi lắp", 70, 0),
            ("CHI_BAO_HANH_VAT_TU", "OUT", "Chi vật tư phụ lắp đặt & sửa chữa bảo hành", "2. Bán hàng & Lắp đặt", "SUPPLIER", "Mua dây điện, rơ-le, băng keo, chi phí gửi bảo hành", 80, 0),
            ("CHI_VAN_PHONG", "OUT", "Chi văn phòng (Mặt bằng, Điện, Nước, Internet, VPP)", "4. Văn phòng & Quản lý", "FREE", "Thanh toán chi phí thuê mặt bằng, điện nước, văn phòng phẩm", 90, 0),
            ("CHI_TIEP_KHACH_MKT", "OUT", "Chi tiếp khách, quảng cáo & chăm sóc Đại lý", "2. Bán hàng & Lắp đặt", "CUSTOMER", "Chi phí tiếp khách, hội nghị HTX, marketing", 100, 0),
            ("CHI_THUE_PHI_NH", "OUT", "Chi thuế, lệ phí & phí giao dịch ngân hàng", "4. Văn phòng & Quản lý", "FREE", "Nộp thuế, lệ phí môn bài, phí chuyển khoản ngân hàng", 110, 0),
            ("CHI_KHAC", "OUT", "Khoản chi khác (Tùy chỉnh)", "5. Chi phí Khác", "FREE", "Chi phí phát sinh khác theo thực tế", 120, 1)
        ];
        foreach (var dc in defaultCashCats)
        {
            Exec(c, null, @"INSERT OR IGNORE INTO expense_categories(code,direction,name,group_name,target_entity,default_note,sort_order,is_system,is_active)
                VALUES($c,$dir,$n,$g,$t,$note,$sort,$sys,1)",
                ("$c", dc.Code), ("$dir", dc.Dir), ("$n", dc.Name), ("$g", dc.Group), ("$t", dc.Target), ("$note", dc.Note), ("$sort", dc.Sort), ("$sys", dc.Sys));
        }
        Exec(c, null, "UPDATE expense_categories SET direction='IN' WHERE code LIKE 'THU_%' AND COALESCE(direction,'OUT')<>'IN'");

        var payCols = Query(c, "PRAGMA table_info(payments)", r => S(r, 1));
        if (!payCols.Any(x => string.Equals(x, "expense_category_id", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE payments ADD COLUMN expense_category_id INTEGER REFERENCES expense_categories(id)");
        if (!payCols.Any(x => string.Equals(x, "supplier_id", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE payments ADD COLUMN supplier_id INTEGER REFERENCES suppliers(id)");
        if (!payCols.Any(x => string.Equals(x, "customer_id", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE payments ADD COLUMN customer_id INTEGER REFERENCES customers(id)");
        if (!payCols.Any(x => string.Equals(x, "seller_id", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE payments ADD COLUMN seller_id INTEGER REFERENCES v7_sellers(id)");
        if (!payCols.Any(x => string.Equals(x, "employee_id", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE payments ADD COLUMN employee_id INTEGER REFERENCES employees(id)");
        if (!payCols.Any(x => string.Equals(x, "v7_order_id", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE payments ADD COLUMN v7_order_id INTEGER REFERENCES v7_orders(id)");
        if (!payCols.Any(x => string.Equals(x, "payee_name", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE payments ADD COLUMN payee_name TEXT NOT NULL DEFAULT ''");
        if (!payCols.Any(x => string.Equals(x, "reference_doc", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE payments ADD COLUMN reference_doc TEXT NOT NULL DEFAULT ''");
        if (!payCols.Any(x => string.Equals(x, "is_locked", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE payments ADD COLUMN is_locked INTEGER NOT NULL DEFAULT 0");

        // Tự động map dữ liệu các Phiếu Thu (direction='IN') hiện có sang Quản lý Thu/Chi
        if (N(c, null, "SELECT COUNT(*) FROM payments WHERE direction='IN' AND expense_category_id IS NULL") > 0)
        {
            Exec(c, null, @"
                UPDATE payments
                SET expense_category_id = COALESCE(
                        (SELECT ec.id FROM payment_allocations pa
                         JOIN invoices i ON i.id=pa.invoice_id
                         JOIN expense_categories ec ON ec.code = CASE
                             WHEN i.invoice_type = 'RENEWAL' THEN 'THU_GIA_HAN'
                             WHEN i.invoice_type = 'SALE' THEN 'THU_DON_LAP'
                             ELSE 'THU_KHAC' END
                         WHERE pa.payment_id=payments.id LIMIT 1),
                        (SELECT ec2.id FROM expense_categories ec2
                         WHERE ec2.code = CASE
                             WHEN payments.notes LIKE '%gia hạn%' OR payments.notes LIKE '%Gia hạn%' THEN 'THU_GIA_HAN'
                             WHEN payments.notes LIKE '%Thu khách%' OR payments.notes LIKE '%công nợ%' OR payments.customer_id IS NOT NULL THEN 'THU_DON_LAP'
                             ELSE 'THU_KHAC' END LIMIT 1)
                    ),
                    customer_id = COALESCE(customer_id,
                        (SELECT i.customer_id FROM payment_allocations pa JOIN invoices i ON i.id=pa.invoice_id WHERE pa.payment_id=payments.id AND i.customer_id IS NOT NULL LIMIT 1)),
                    v7_order_id = COALESCE(v7_order_id,
                        (SELECT i.v7_order_id FROM payment_allocations pa JOIN invoices i ON i.id=pa.invoice_id WHERE pa.payment_id=payments.id AND i.v7_order_id IS NOT NULL LIMIT 1)),
                    payee_name = CASE WHEN COALESCE(payee_name,'')<>'' THEN payee_name ELSE COALESCE(
                        (SELECT COALESCE(cu.name, CASE WHEN INSTR(i.notes,' — ')>0 THEN SUBSTR(i.notes,1,INSTR(i.notes,' — ')-1) ELSE i.notes END, '')
                         FROM payment_allocations pa JOIN invoices i ON i.id=pa.invoice_id
                         LEFT JOIN customers cu ON cu.id=i.customer_id
                         WHERE pa.payment_id=payments.id LIMIT 1), '') END,
                    reference_doc = CASE WHEN COALESCE(reference_doc,'')<>'' THEN reference_doc ELSE COALESCE(
                        (SELECT i.invoice_no FROM payment_allocations pa JOIN invoices i ON i.id=pa.invoice_id WHERE pa.payment_id=payments.id LIMIT 1), '') END,
                    notes = CASE WHEN COALESCE(notes,'')<>'' THEN notes ELSE COALESCE(
                        (SELECT 'Thu thanh toán chứng từ ' || i.invoice_no || CASE WHEN COALESCE(i.notes,'')<>'' THEN ' (' || i.notes || ')' ELSE '' END
                         FROM payment_allocations pa JOIN invoices i ON i.id=pa.invoice_id WHERE pa.payment_id=payments.id LIMIT 1), '') END
                WHERE direction='IN' AND expense_category_id IS NULL;");
        }

        // Tự động map dữ liệu các Phiếu Chi (direction='OUT') hiện có sang Quản lý Thu/Chi
        if (N(c, null, "SELECT COUNT(*) FROM payments WHERE direction='OUT' AND expense_category_id IS NULL") > 0)
        {
            var ordColsForPay = Query(c, "PRAGMA table_info(v7_orders)", r => S(r, 1));
            var empSubquery = ordColsForPay.Any(x => string.Equals(x, "installer_employee_id", StringComparison.OrdinalIgnoreCase))
                ? "(SELECT o.installer_employee_id FROM payment_allocations pa JOIN invoices i ON i.id=pa.invoice_id JOIN v7_orders o ON o.id=i.v7_order_id WHERE pa.payment_id=payments.id AND o.installer_employee_id IS NOT NULL LIMIT 1)"
                : "NULL";

            Exec(c, null, $@"
                UPDATE payments
                SET expense_category_id = COALESCE(
                        (SELECT ec.id FROM payment_allocations pa
                         JOIN invoices i ON i.id=pa.invoice_id
                         JOIN expense_categories ec ON ec.code = CASE
                             WHEN i.invoice_type IN ('PURCHASE','SUPPLIER') THEN 'CHI_NCC'
                             WHEN i.invoice_type = 'SERVICE' THEN 'CHI_CONG_LAP'
                             WHEN i.invoice_type = 'COMMISSION' THEN 'CHI_HOA_HONG'
                             ELSE 'CHI_KHAC' END
                         WHERE pa.payment_id=payments.id LIMIT 1),
                        (SELECT ec2.id FROM expense_categories ec2
                         WHERE ec2.code = CASE
                             WHEN payments.notes LIKE '%NCC%' OR payments.notes LIKE '%Nhập hàng%' THEN 'CHI_NCC'
                             WHEN payments.notes LIKE '%hoa hồng%' THEN 'CHI_HOA_HONG'
                             WHEN payments.notes LIKE '%kỹ thuật%' OR payments.notes LIKE '%công lắp%' THEN 'CHI_CONG_LAP'
                             ELSE 'CHI_KHAC' END LIMIT 1)
                    ),
                    supplier_id = COALESCE(supplier_id,
                        (SELECT i.supplier_id FROM payment_allocations pa JOIN invoices i ON i.id=pa.invoice_id WHERE pa.payment_id=payments.id AND i.supplier_id IS NOT NULL LIMIT 1)),
                    v7_order_id = COALESCE(v7_order_id,
                        (SELECT i.v7_order_id FROM payment_allocations pa JOIN invoices i ON i.id=pa.invoice_id WHERE pa.payment_id=payments.id AND i.v7_order_id IS NOT NULL LIMIT 1)),
                    employee_id = COALESCE(employee_id, {empSubquery}),
                    payee_name = CASE WHEN COALESCE(payee_name,'')<>'' THEN payee_name ELSE COALESCE(
                        (SELECT COALESCE(s.name, o.installer, CASE WHEN INSTR(i.notes,' — ')>0 THEN SUBSTR(i.notes,1,INSTR(i.notes,' — ')-1) ELSE i.notes END, '')
                         FROM payment_allocations pa JOIN invoices i ON i.id=pa.invoice_id
                         LEFT JOIN suppliers s ON s.id=i.supplier_id
                         LEFT JOIN v7_orders o ON o.id=i.v7_order_id
                         WHERE pa.payment_id=payments.id LIMIT 1), '') END,
                    reference_doc = CASE WHEN COALESCE(reference_doc,'')<>'' THEN reference_doc ELSE COALESCE(
                        (SELECT i.invoice_no FROM payment_allocations pa JOIN invoices i ON i.id=pa.invoice_id WHERE pa.payment_id=payments.id LIMIT 1), '') END,
                    notes = CASE WHEN COALESCE(notes,'')<>'' THEN notes ELSE COALESCE(
                        (SELECT 'Chi thanh toán chứng từ ' || i.invoice_no || CASE WHEN COALESCE(i.notes,'')<>'' THEN ' (' || i.notes || ')' ELSE '' END
                         FROM payment_allocations pa JOIN invoices i ON i.id=pa.invoice_id WHERE pa.payment_id=payments.id LIMIT 1), '') END
                WHERE direction='OUT' AND expense_category_id IS NULL;");
        }

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
        if (!actCols.Any(x => string.Equals(x, "deleted_summary", StringComparison.OrdinalIgnoreCase)))
            Exec(c, null, "ALTER TABLE activations ADD COLUMN deleted_summary TEXT NOT NULL DEFAULT ''");

        var prodCols = Query(c, "PRAGMA table_info(products)", r => S(r, 1));
        if (!prodCols.Any(x => string.Equals(x, "standard_price_vnd", StringComparison.OrdinalIgnoreCase)))
        {
            Exec(c, null, "ALTER TABLE products ADD COLUMN standard_price_vnd INTEGER NOT NULL DEFAULT 0");
            var gpCols = Query(c, "PRAGMA table_info(v7_group_prices)", r => S(r, 1));
            if (gpCols.Any(x => string.Equals(x, "deleted_at", StringComparison.OrdinalIgnoreCase)))
            {
                // Khởi tạo một lần giá bán khai báo mặc định từ nhóm Khách lẻ (RETAIL) nếu có
                Exec(c, null, @"
                    UPDATE products
                    SET standard_price_vnd = COALESCE((
                        SELECT CASE WHEN gp.installed_price_vnd > 0 THEN gp.installed_price_vnd ELSE gp.standard_price_vnd END
                        FROM v7_group_prices gp
                        JOIN customer_groups cg ON cg.id = gp.group_id
                        WHERE gp.product_id = products.id
                          AND gp.deleted_at IS NULL
                          AND UPPER(COALESCE(cg.code,'')) = 'RETAIL'
                        ORDER BY gp.effective_from DESC, gp.id DESC
                        LIMIT 1
                    ), 0)
                    WHERE standard_price_vnd = 0");
            }
        }

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

            // Đồng bộ giá vốn mặc định từ bảng giá nhóm NCC về bảng products khi chưa khai báo
            Exec(c, null, @"UPDATE products SET base_cost_vnd = COALESCE(
                (SELECT gp.unit_cost_vnd FROM v7_supplier_group_prices gp WHERE gp.product_id=products.id AND gp.deleted_at IS NULL AND gp.unit_cost_vnd>0 ORDER BY gp.effective_from DESC, gp.id DESC LIMIT 1),
                base_cost_vnd)
                WHERE base_cost_vnd = 0 AND EXISTS (SELECT 1 FROM v7_supplier_group_prices gp WHERE gp.product_id=products.id AND gp.deleted_at IS NULL AND gp.unit_cost_vnd>0)");

            // Dọn các chứng từ công nợ PURCHASE phát sinh nhầm từ nguồn "Thiết bị hoàn trả - đã kích hoạt" (HOANTRA)
            CleanupReturnedDevicePurchaseInvoices(c, null);
            // Đồng bộ chuẩn Công nợ Nhà cung cấp theo đúng Tồn kho thực tế (tách biệt hoàn toàn khỏi danh sách TB khách hàng)
            SyncSupplierStockDebtBaseline(c, null);
            // Đồng bộ công nợ chi tiết khách hàng Tháng 09/2026 & Tháng 10/2026 và dọn các chứng từ gộp giả định
            CustomerDebtOct2026Seeder.EnsureSeeded(c, null, v => ActorId = v, () => ActorId);
            // Đồng bộ chi tiết công nợ Nhà cung cấp NCC Nasa kỳ Tháng 10/2026 (còn nợ 62,680,000 ₫)
            SupplierDebtNasaOct2026Seeder.EnsureSeeded(c, null, v => ActorId = v, () => ActorId);
            // Đối soát tồn kho cũ đầu kỳ (= 0 ₫), bổ sung CN T10 (TDC 51.5tr, Xanh GPS 84.05tr) & khôi phục các đơn nhập kho T11 (Nasa 60 ĐV + TDC 4 phiếu)
            SupplierDebtReconcileNov2026Seeder.EnsureSeeded(c, null, v => ActorId = v, () => ActorId);
            // Mở khóa toàn bộ các thiết bị khách hàng bị gắn trạng thái DISABLED (Khoá do quá hạn từ file import gốc) để cho phép gia hạn bình thường
            UnlockImportedLockedDevices(c, null);
            // Đồng bộ lại các đợt gia hạn chưa chốt số bị áp nhầm giá Khách lẻ/Doanh nghiệp (960k) về đúng giá Nhóm con HTX (740k / 540k)
            ReconcileUnlockedSubgroupRenewals(c, null);
        }
    }

    private void ReconcileUnlockedSubgroupRenewals(SqliteConnection c, SqliteTransaction? t)
    {
        var candidates = QueryAny(c, t, @"
            SELECT r.id, r.amount_vnd, a.customer_id, a.product_id, r.renewed_at
            FROM renewals r
            JOIN activations a ON a.id = r.activation_id
            JOIN customers cu ON cu.id = a.customer_id
            WHERE r.deleted_at IS NULL
              AND r.is_locked = 0
              AND cu.sub_group_id IS NOT NULL
              AND r.renewed_at = '2026-11-21'
              AND r.amount_vnd = 960000
              AND NOT EXISTS (
                  SELECT 1 FROM invoices i
                  WHERE i.renewal_id = r.id
                    AND i.deleted_at IS NULL
                    AND (COALESCE(i.is_locked,0) = 1 OR EXISTS (
                        SELECT 1 FROM payment_allocations pa WHERE pa.invoice_id = i.id AND pa.deleted_at IS NULL
                    ))
              )",
            r => (Id: L(r, 0), CurAmt: L(r, 1), CustId: L(r, 2), ProdId: L(r, 3), RenAt: S(r, 4)));

        if (candidates.Count == 0)
            return;

        var prevActor = ActorId;
        var devId = N(c, t, "SELECT COALESCE((SELECT id FROM app_users WHERE LOWER(username)='vanlt_it' LIMIT 1), 0)");
        try
        {
            if (devId > 0) ActorId = devId;
            foreach (var item in candidates)
            {
                DateTime? atDt = DateTime.TryParse(item.RenAt, out var parsed) ? parsed : null;
                var expected = GroupPriceResolver.ResolveRenewalPrice(c, t, item.CustId, item.ProdId, atDt);
                if (expected <= 0 || expected == item.CurAmt)
                    continue;

                Exec(c, t, "UPDATE renewals SET amount_vnd = $amt WHERE id = $id", ("$amt", expected), ("$id", item.Id));
                var invIds = QueryAny(c, t, "SELECT id FROM invoices WHERE renewal_id = $r AND deleted_at IS NULL", r => L(r, 0), ("$r", item.Id));
                foreach (var invId in invIds)
                {
                    Exec(c, t, "UPDATE invoices SET amount_vnd = $amt WHERE id = $id", ("$amt", expected), ("$id", invId));
                    Exec(c, t, "UPDATE invoice_lines SET unit_price_vnd = $amt WHERE invoice_id = $id AND deleted_at IS NULL", ("$amt", expected), ("$id", invId));
                }
            }
        }
        finally
        {
            ActorId = prevActor;
        }
    }

    private void UnlockImportedLockedDevices(SqliteConnection c, SqliteTransaction? t)
    {
        var lockedImportedCount = N(c, t, @"
            SELECT COUNT(*) FROM activations
            WHERE status='DISABLED' AND deleted_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM audit_logs al WHERE al.action='LOCK_DEVICE' AND al.entity_id=activations.id)");
        if (lockedImportedCount <= 0)
            return;

        var prevActor = ActorId;
        var devId = N(c, t, "SELECT COALESCE((SELECT id FROM app_users WHERE LOWER(username)='vanlt_it' LIMIT 1), 0)");
        try
        {
            if (devId > 0) ActorId = devId;
            Exec(c, t, @"
                UPDATE activations
                SET status = 'ACTIVE',
                    updated_at = datetime('now')
                WHERE status = 'DISABLED'
                  AND deleted_at IS NULL
                  AND NOT EXISTS (SELECT 1 FROM audit_logs al WHERE al.action='LOCK_DEVICE' AND al.entity_id=activations.id)");
        }
        finally
        {
            ActorId = prevActor;
        }
    }

    private void CleanupReturnedDevicePurchaseInvoices(SqliteConnection c, SqliteTransaction? t)
    {
        var prevActor = ActorId;
        var devId = N(c, t, "SELECT COALESCE((SELECT id FROM app_users WHERE LOWER(username)='vanlt_it' LIMIT 1), 0)");
        try
        {
            if (devId > 0) ActorId = devId;

            Exec(c, t, @"UPDATE inventory_transaction_lines SET unit_cost_snapshot_vnd = 0
                WHERE unit_cost_snapshot_vnd <> 0
                  AND stock_unit_id IN (
                    SELECT u.id FROM stock_units u
                    JOIN suppliers s ON s.id=u.supplier_id
                    LEFT JOIN supplier_groups sg ON sg.id=s.group_id
                    WHERE UPPER(COALESCE(sg.code,''))='HOANTRA'
                       OR LOWER(COALESCE(s.name,'')) LIKE '%hoàn trả%'
                       OR LOWER(COALESCE(sg.name,'')) LIKE '%hoàn trả%'
                )");

            Exec(c, t, @"UPDATE inventory_transactions
                SET notes = 'Nhập kho thiết bị hoàn trả - đã kích hoạt (Không tính công nợ)'
                WHERE movement_type = 'RECEIVE'
                  AND COALESCE(notes, '') = 'Nhập kho IMEI'
                  AND EXISTS (
                    SELECT 1 FROM inventory_transaction_lines tl
                    JOIN stock_units u ON u.id = tl.stock_unit_id
                    JOIN suppliers s ON s.id = u.supplier_id
                    LEFT JOIN supplier_groups sg ON sg.id = s.group_id
                    WHERE tl.transaction_id = inventory_transactions.id
                      AND (UPPER(COALESCE(sg.code,''))='HOANTRA'
                           OR LOWER(COALESCE(s.name,'')) LIKE '%hoàn trả%'
                           OR LOWER(COALESCE(sg.name,'')) LIKE '%hoàn trả%')
                  )");

            const string targetInvSql = @"
                SELECT i.id FROM invoices i
                JOIN suppliers s ON s.id=i.supplier_id
                LEFT JOIN supplier_groups sg ON sg.id=s.group_id
                WHERE i.invoice_type IN ('PURCHASE','SUPPLIER')
                  AND (UPPER(COALESCE(sg.code,''))='HOANTRA'
                       OR LOWER(COALESCE(s.name,'')) LIKE '%hoàn trả%'
                       OR LOWER(COALESCE(sg.name,'')) LIKE '%hoàn trả%')
                  AND NOT EXISTS (SELECT 1 FROM payment_allocations pa WHERE pa.invoice_id=i.id AND pa.deleted_at IS NULL)";

            if (devId > 0)
            {
                Exec(c, t, $"DELETE FROM invoice_lines WHERE invoice_id IN ({targetInvSql})");
                Exec(c, t, $"DELETE FROM invoices WHERE id IN ({targetInvSql})");
                // Dọn cứng các nhóm khách hàng test từng xóa bởi tài khoản ẩn trước khi nâng cấp hard-delete
                if (N(c, t, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='v7_group_campaigns'") > 0)
                {
                    Exec(c, t, @"DELETE FROM customer_groups
                        WHERE deleted_at IS NOT NULL AND deleted_by IS NULL AND parent_id IS NOT NULL
                          AND NOT EXISTS (SELECT 1 FROM customers cu WHERE cu.group_id=customer_groups.id OR cu.sub_group_id=customer_groups.id)
                          AND NOT EXISTS (SELECT 1 FROM v7_group_prices gp WHERE gp.group_id=customer_groups.id)
                          AND NOT EXISTS (SELECT 1 FROM v7_group_campaigns gc WHERE gc.group_id=customer_groups.id)");
                    Exec(c, t, @"DELETE FROM customer_groups
                        WHERE deleted_at IS NOT NULL AND deleted_by IS NULL AND parent_id IS NULL
                          AND NOT EXISTS (SELECT 1 FROM customer_groups ch WHERE ch.parent_id=customer_groups.id)
                          AND NOT EXISTS (SELECT 1 FROM customers cu WHERE cu.group_id=customer_groups.id OR cu.sub_group_id=customer_groups.id)
                          AND NOT EXISTS (SELECT 1 FROM v7_group_prices gp WHERE gp.group_id=customer_groups.id)
                          AND NOT EXISTS (SELECT 1 FROM v7_group_campaigns gc WHERE gc.group_id=customer_groups.id)");
                }
            }
            else
            {
                Exec(c, t, $"UPDATE invoice_lines SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE deleted_at IS NULL AND invoice_id IN ({targetInvSql})");
                Exec(c, t, $"UPDATE invoices SET status='VOID', deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE deleted_at IS NULL AND id IN ({targetInvSql})");
            }
        }
        finally
        {
            ActorId = prevActor;
        }
    }

    private static List<T> QueryAny<T>(SqliteConnection c, SqliteTransaction? t, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var cmd = Cmd(c, t, sql, args);
        using var reader = cmd.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(map(reader));
        return result;
    }

    /// <summary>
    /// Chuẩn hóa Công nợ Nhà cung cấp theo đúng Tồn kho thực tế hiện tại (loại bỏ hoàn toàn các chứng từ PURCHASE
    /// từng cộng nhầm thiết bị INSTALLED từ file thiết bị khách hàng & loại trừ Thiết bị hoàn trả HOANTRA).
    /// </summary>
    private void SyncSupplierStockDebtBaseline(SqliteConnection c, SqliteTransaction? t)
    {
        var prevActor = ActorId;
        var devId = N(c, t, "SELECT COALESCE((SELECT id FROM app_users WHERE LOWER(username)='vanlt_it' LIMIT 1), 0)");
        try
        {
            if (devId > 0) ActorId = devId;

            // Tồn kho cũ đầu kỳ (NK-NCC00001..5-TONKHO) đã tất toán & chốt số đưa về 0; công nợ T10 & các phiếu nhập kho T11 được tách riêng độc lập
            Exec(c, t, @"
                UPDATE invoices
                SET status = 'PAID',
                    is_locked = 1,
                    locked_at = COALESCE(NULLIF(locked_at,''), datetime('now','+7 hours')),
                    notes = REPLACE(notes, 'Công nợ Nhà cung cấp theo Tồn kho thực tế', 'Tồn kho cũ đầu kỳ — Đã tất toán & chốt số đưa về 0')
                WHERE invoice_no IN ('NK-NCC00001-TONKHO', 'NK-NCC00002-TONKHO', 'NK-NCC00003-TONKHO', 'NK-NCC00005-TONKHO')
                  AND deleted_at IS NULL
                  AND (status <> 'PAID' OR COALESCE(is_locked, 0) = 0)");

            // Chỉ thực thi chuyển đổi chuẩn một lần khi còn tồn tại chứng từ gộp cũ bị cộng lẫn thiết bị khách hàng
            var hasLegacyInflatedPurchase = N(c, t, @"
                SELECT COUNT(*) FROM invoices
                WHERE invoice_type IN ('PURCHASE','SUPPLIER')
                  AND deleted_at IS NULL
                  AND notes LIKE 'Công nợ nhập hàng / tồn kho NCC (%'") > 0;
            if (!hasLegacyInflatedPurchase)
                return;

            // 1. Gỡ liên kết supplier_id trên các thiết bị INSTALLED được import từ file thiết bị khách hàng (không qua phiếu kho)
            Exec(c, t, @"
                UPDATE stock_units
                SET supplier_id = NULL
                WHERE status = 'INSTALLED'
                  AND supplier_id IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM inventory_transaction_lines itl
                      WHERE itl.stock_unit_id = stock_units.id
                  )");

            // 2. Chỉ xóa các chứng từ gộp tự động từng cộng lẫn thiết bị khách hàng (tuyệt đối KHÔNG xóa phiếu nhập kho thực tế hay công nợ kỳ trước)
            const string oldUnpaidPurchaseSql = @"
                SELECT i.id FROM invoices i
                WHERE i.invoice_type IN ('PURCHASE','SUPPLIER')
                  AND i.notes LIKE 'Công nợ nhập hàng / tồn kho NCC (%'
                  AND NOT EXISTS (
                      SELECT 1 FROM payment_allocations pa
                      WHERE pa.invoice_id = i.id AND pa.deleted_at IS NULL
                  )";

            if (devId > 0)
            {
                Exec(c, t, $"DELETE FROM invoice_lines WHERE invoice_id IN ({oldUnpaidPurchaseSql})");
                Exec(c, t, $"DELETE FROM invoices WHERE id IN ({oldUnpaidPurchaseSql})");
            }
            else
            {
                Exec(c, t, $"UPDATE invoice_lines SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE deleted_at IS NULL AND invoice_id IN ({oldUnpaidPurchaseSql})");
                Exec(c, t, $"UPDATE invoices SET status='VOID', deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE deleted_at IS NULL AND id IN ({oldUnpaidPurchaseSql})");
            }

            // 3. Tổng hợp chính xác Tồn kho thực tế hiện tại (khớp 100% bảng Tồn kho, loại trừ Thiết bị hoàn trả HOANTRA)
            var bySupplier = new Dictionary<long, List<(long ProdId, string Sku, string Name, bool IsImei, long Qty, long UnitCost)>>();

            long LookupUnitCost(long supId, long prodId) => N(c, t, @"
                SELECT COALESCE(
                    NULLIF((SELECT gp.unit_cost_vnd FROM v7_supplier_group_prices gp
                            JOIN suppliers s ON s.group_id = gp.supplier_group_id
                            WHERE s.id = $sup AND gp.product_id = $prod AND gp.deleted_at IS NULL
                            ORDER BY gp.effective_from DESC, gp.id DESC LIMIT 1), 0),
                    NULLIF((SELECT gp2.unit_cost_vnd FROM v7_supplier_group_prices gp2
                            WHERE gp2.product_id = $prod AND gp2.deleted_at IS NULL
                            ORDER BY gp2.effective_from DESC, gp2.id DESC LIMIT 1), 0),
                    (SELECT base_cost_vnd FROM products WHERE id = $prod),
                    0)", ("$sup", supId), ("$prod", prodId));

            // 3A. Thiết bị có IMEI đang Tồn kho (status = 'IN_STOCK')
            var imeiStock = QueryAny(c, t, @"
                SELECT COALESCE(
                           u.supplier_id,
                           (SELECT s2.id FROM suppliers s2 LEFT JOIN supplier_groups sg2 ON sg2.id = s2.group_id
                            JOIN v7_supplier_group_prices gp2 ON gp2.supplier_group_id = s2.group_id
                            WHERE gp2.product_id = p.id AND s2.deleted_at IS NULL AND gp2.deleted_at IS NULL
                              AND UPPER(COALESCE(sg2.code,'')) <> 'HOANTRA'
                              AND LOWER(COALESCE(s2.name,'')) NOT LIKE '%hoàn trả%'
                              AND LOWER(COALESCE(sg2.name,'')) NOT LIKE '%hoàn trả%'
                            ORDER BY gp2.effective_from DESC, s2.id LIMIT 1),
                           0
                       ) AS resolved_sup_id,
                       p.id, p.sku, p.name, COUNT(*) AS stock_qty
                FROM stock_units u
                JOIN products p ON p.id = u.product_id
                LEFT JOIN suppliers s ON s.id = u.supplier_id
                LEFT JOIN supplier_groups sg ON sg.id = s.group_id
                WHERE u.deleted_at IS NULL AND p.deleted_at IS NULL
                  AND u.status = 'IN_STOCK'
                  AND UPPER(COALESCE(sg.code,'')) <> 'HOANTRA'
                  AND LOWER(COALESCE(s.name,'')) NOT LIKE '%hoàn trả%'
                  AND LOWER(COALESCE(sg.name,'')) NOT LIKE '%hoàn trả%'
                GROUP BY resolved_sup_id, p.id
                HAVING COUNT(*) > 0
                ORDER BY p.sku",
                r => (SupId: L(r, 0), ProdId: L(r, 1), Sku: S(r, 2), Name: S(r, 3), Qty: L(r, 4)));

            foreach (var row in imeiStock)
            {
                if (row.SupId <= 0) continue;
                var cost = LookupUnitCost(row.SupId, row.ProdId);
                if (cost <= 0) continue;
                if (!bySupplier.TryGetValue(row.SupId, out var list))
                {
                    list = [];
                    bySupplier[row.SupId] = list;
                }
                list.Add((row.ProdId, row.Sku, row.Name, true, row.Qty, cost));
            }

            // 3B. Hàng hóa không IMEI đang Tồn kho (tổng delta_qty > 0 tại các kho & vị trí hoạt động)
            var qtyStock = QueryAny(c, t, @"
                SELECT COALESCE(
                           (SELECT s2.id FROM suppliers s2 LEFT JOIN supplier_groups sg2 ON sg2.id = s2.group_id
                            JOIN v7_supplier_group_prices gp2 ON gp2.supplier_group_id = s2.group_id
                            WHERE gp2.product_id = p.id AND s2.deleted_at IS NULL AND gp2.deleted_at IS NULL
                              AND UPPER(COALESCE(sg2.code,'')) <> 'HOANTRA'
                              AND LOWER(COALESCE(s2.name,'')) NOT LIKE '%hoàn trả%'
                              AND LOWER(COALESCE(sg2.name,'')) NOT LIKE '%hoàn trả%'
                            ORDER BY gp2.effective_from DESC, s2.id LIMIT 1),
                           0
                       ) AS resolved_sup_id,
                       p.id, p.sku, p.name, COALESCE(SUM(q.delta_qty), 0) AS stock_qty
                FROM quantity_movements q
                JOIN products p ON p.id = q.product_id
                JOIN warehouses w ON w.id = q.warehouse_id
                JOIN warehouse_locations l ON l.id = q.location_id
                WHERE q.deleted_at IS NULL AND p.deleted_at IS NULL AND w.deleted_at IS NULL AND p.is_serialized = 0
                GROUP BY p.id
                HAVING COALESCE(SUM(q.delta_qty), 0) > 0
                ORDER BY p.sku",
                r => (SupId: L(r, 0), ProdId: L(r, 1), Sku: S(r, 2), Name: S(r, 3), Qty: L(r, 4)));

            foreach (var row in qtyStock)
            {
                if (row.SupId <= 0) continue;
                var cost = LookupUnitCost(row.SupId, row.ProdId);
                if (cost <= 0) continue;
                if (!bySupplier.TryGetValue(row.SupId, out var list))
                {
                    list = [];
                    bySupplier[row.SupId] = list;
                }
                list.Add((row.ProdId, row.Sku, row.Name, false, row.Qty, cost));
            }

            // 4. Tạo chứng từ Công nợ Nhà cung cấp chuẩn theo đúng Tồn kho cho từng Nhà cung cấp
            foreach (var (supId, items) in bySupplier.OrderBy(x => x.Key))
            {
                var ordered = items.OrderBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ToList();
                var totalAmt = ordered.Sum(x => x.Qty * x.UnitCost);
                var totalQty = ordered.Sum(x => x.Qty);
                if (totalAmt <= 0 || totalQty <= 0) continue;

                var supInfo = QueryAny(c, t, "SELECT code, name FROM suppliers WHERE id=$id", r => (Code: S(r, 0), Name: S(r, 1)), ("$id", supId)).FirstOrDefault();
                var supCode = string.IsNullOrWhiteSpace(supInfo.Code) ? $"NCC{supId:D5}" : supInfo.Code;
                var invNo = $"NK-{supCode}-TONKHO";
                var isSettledOldStock = true;
                var note = $"Tồn kho cũ đầu kỳ — Đã tất toán & chốt số đưa về 0 ({supCode} — {supInfo.Name} · {totalQty:N0} SP)";

                Exec(c, t, @"INSERT INTO invoices(invoice_no,invoice_type,supplier_id,issued_at,due_at,amount_vnd,status,is_locked,locked_at,notes)
                    VALUES($no,'PURCHASE',$sup,datetime('now','+7 hours'),date('now','+7 hours','+30 days'),$amt,$st,$lk,CASE WHEN $lk=1 THEN datetime('now','+7 hours') ELSE NULL END,$note)",
                    ("$no", invNo), ("$sup", supId), ("$amt", totalAmt),
                    ("$st", isSettledOldStock ? "PAID" : "OPEN"),
                    ("$lk", isSettledOldStock ? 1 : 0),
                    ("$note", note));
                var invId = N(c, t, "SELECT last_insert_rowid()");

                foreach (var it in ordered)
                {
                    var kindLabel = it.IsImei ? "Tồn kho IMEI" : "Tồn kho hàng hóa";
                    Exec(c, t, @"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd)
                        VALUES($i,$d,$p,$q,$c)",
                        ("$i", invId),
                        ("$d", $"{kindLabel}: {it.Sku} — {it.Name}"),
                        ("$p", it.ProdId),
                        ("$q", it.Qty),
                        ("$c", it.UnitCost));
                }
            }
        }
        finally
        {
            ActorId = prevActor;
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
          K("SELECT COALESCE(SUM(CASE WHEN i.status='PAID' THEN 0 ELSE i.amount_vnd-COALESCE(i.offset_vnd,0)-COALESCE(p.paid,0) END),0) FROM invoices i LEFT JOIN (SELECT invoice_id,SUM(allocated_vnd) paid FROM payment_allocations WHERE deleted_at IS NULL GROUP BY invoice_id) p ON p.invoice_id=i.id WHERE i.deleted_at IS NULL AND i.status!='VOID' AND i.invoice_type IN ('SALE','RENEWAL')"),
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
            COALESCE(p.renewal_fee_vnd, 0),
            COALESCE(p.install_fee_vnd, 0),
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
        return Query(c, @"SELECT c.id,c.code,c.name,
            CASE
                WHEN sg.id IS NOT NULL THEN COALESCE(g.name,'') || ' › ' || sg.name
                WHEN pg.id IS NOT NULL THEN pg.name || ' › ' || COALESCE(g.name,'')
                ELSE COALESCE(g.name,'-')
            END,
            COALESCE(s.code||' — '||s.full_name, NULLIF(c.source_name,''), ''),
            c.phone,c.address,COALESCE(c.representative_name,''),c.seller_id
            FROM customers c
            LEFT JOIN customer_groups g ON g.id=c.group_id
            LEFT JOIN customer_groups pg ON pg.id=g.parent_id
            LEFT JOIN customer_groups sg ON sg.id=c.sub_group_id AND sg.deleted_at IS NULL
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
            var resolvedSup = supplierId.HasValue && supplierId.Value > 0
                ? supplierId.Value
                : N(c,t,@"SELECT COALESCE(
                    (SELECT s.id FROM suppliers s
                     LEFT JOIN supplier_groups sg ON sg.id=s.group_id
                     JOIN v7_supplier_group_prices gp ON gp.supplier_group_id=s.group_id
                     WHERE gp.product_id=$id AND s.deleted_at IS NULL AND gp.deleted_at IS NULL
                       AND UPPER(COALESCE(sg.code,''))<>'HOANTRA'
                       AND LOWER(COALESCE(s.name,'')) NOT LIKE '%hoàn trả%'
                       AND LOWER(COALESCE(sg.name,'')) NOT LIKE '%hoàn trả%'
                     ORDER BY gp.effective_from DESC,s.id LIMIT 1),
                    (SELECT s.id FROM suppliers s LEFT JOIN supplier_groups sg ON sg.id=s.group_id
                     WHERE s.deleted_at IS NULL
                       AND UPPER(COALESCE(sg.code,''))<>'HOANTRA'
                       AND LOWER(COALESCE(s.name,'')) NOT LIKE '%hoàn trả%'
                       AND LOWER(COALESCE(sg.name,'')) NOT LIKE '%hoàn trả%'
                     ORDER BY s.id LIMIT 1),
                    0)",("$id",productId));
            var isReturnSup = resolvedSup > 0 && N(c,t,@"
                SELECT COUNT(*) FROM suppliers s
                LEFT JOIN supplier_groups sg ON sg.id=s.group_id
                WHERE s.id=$sup
                  AND (UPPER(COALESCE(sg.code,''))='HOANTRA'
                       OR LOWER(COALESCE(s.name,'')) LIKE '%hoàn trả%'
                       OR LOWER(COALESCE(sg.name,'')) LIKE '%hoàn trả%')",
                ("$sup", resolvedSup)) > 0;
            var cost = isReturnSup ? 0 : N(c,t,@"SELECT COALESCE(
                NULLIF((SELECT gp.unit_cost_vnd FROM v7_supplier_group_prices gp
                        JOIN suppliers s ON s.group_id=gp.supplier_group_id
                        WHERE s.id=$sup AND gp.product_id=$id AND gp.deleted_at IS NULL AND gp.effective_from<=date('now')
                        ORDER BY gp.effective_from DESC,gp.id DESC LIMIT 1), 0),
                NULLIF((SELECT gp.unit_cost_vnd FROM v7_supplier_group_prices gp
                        WHERE gp.product_id=$id AND gp.deleted_at IS NULL AND gp.effective_from<=date('now')
                        ORDER BY gp.effective_from DESC,gp.id DESC LIMIT 1), 0),
                NULLIF((SELECT base_cost_vnd FROM products WHERE id=$id), 0),
                0)",("$sup",resolvedSup),("$id",productId));
            var prodName = QueryTx(c,t,"SELECT sku||' · '||name FROM products WHERE id=$id", r => S(r,0), ("$id",productId)).FirstOrDefault() ?? $"SP #{productId}";
            var code = "NK-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            Exec(c,t,"INSERT INTO inventory_transactions(code,movement_type,warehouse_id,actor_user_id,notes) VALUES($n,'RECEIVE',$w,$actor,$note)",
              ("$n",code),("$w",warehouseId),("$actor",ActorId),("$note", isReturnSup ? "Nhập kho thiết bị hoàn trả - đã kích hoạt (Không tính công nợ)" : "Nhập kho IMEI"));
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
            if (!isReturnSup && resolvedSup > 0 && cost * importedCount > 0)
            {
                var invNo = "NK-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                var totalCost = cost * importedCount;
                Exec(c,t,@"INSERT INTO invoices(invoice_no,invoice_type,supplier_id,issued_at,due_at,amount_vnd,notes)
                    VALUES($no,'PURCHASE',$sup,datetime('now','+7 hours'),date('now','+7 hours','+30 days'),$amt,$note)",
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
    public static event Action? OnNavCountsChanged;
    public static void NotifyNavCountsChanged() => OnNavCountsChanged?.Invoke();
    public static void NotifyDevicesChanged()
    {
        OnDevicesChanged?.Invoke();
        OnNavCountsChanged?.Invoke();
    }

    public (long AvailableStock, long OpenOrders, int Due10Devices) NavSidebarSummary(long userId = 0)
    {
        var today = DateTime.Today;
        using var c = Open();

        bool restricted = false;
        if (userId > 0)
        {
            var isAdm = N(c, null, @"SELECT COUNT(*) FROM app_users u
                WHERE u.id=$u AND (u.is_owner=1 OR LOWER(u.username)='vanlt_it'
                    OR EXISTS (SELECT 1 FROM user_roles ur JOIN roles r ON r.id=ur.role_id WHERE ur.user_id=u.id AND ur.deleted_at IS NULL AND r.code='ADMIN'))",
                ("$u", userId)) > 0;
            if (!isAdm)
            {
                restricted = N(c, null, "SELECT COUNT(*) FROM user_scope_settings WHERE user_id=$u AND warehouse_mode='SELECTED'", ("$u", userId)) > 0;
            }
        }

        long availStock;
        long openOrders;
        if (restricted)
        {
            const string whScope = "SELECT warehouse_id FROM user_warehouse_scopes WHERE user_id=$u AND deleted_at IS NULL";
            availStock = N(c, null, $@"
                SELECT COALESCE(SUM(
                    CASE
                      WHEN p.is_serialized = 1 THEN
                        (SELECT COUNT(*) FROM stock_units u WHERE u.product_id=p.id AND u.status='IN_STOCK' AND u.deleted_at IS NULL AND u.warehouse_id IN ({whScope}))
                        + COALESCE((SELECT SUM(qm.delta_qty) FROM quantity_movements qm WHERE qm.product_id=p.id AND qm.deleted_at IS NULL AND qm.warehouse_id IN ({whScope})), 0)
                      ELSE
                        COALESCE((SELECT SUM(qm.delta_qty) FROM quantity_movements qm WHERE qm.product_id=p.id AND qm.deleted_at IS NULL AND qm.warehouse_id IN ({whScope})), 0)
                    END
                ), 0)
                FROM products p
                LEFT JOIN product_catalog_metadata m ON m.product_id=p.id
                WHERE p.is_active=1 AND p.deleted_at IS NULL AND COALESCE(m.product_kind,'GOODS')='GOODS'",
                ("$u", userId));

            openOrders = N(c, null, $@"
                SELECT COUNT(*) FROM v7_orders
                WHERE status IN ('PENDING','APPROVED','DISPATCHED','REPORTED')
                  AND deleted_at IS NULL
                  AND (warehouse_id IS NULL OR warehouse_id IN ({whScope}))",
                ("$u", userId));
        }
        else
        {
            availStock = N(c, null, @"
                SELECT COALESCE(SUM(
                    CASE
                      WHEN p.is_serialized = 1 THEN
                        (SELECT COUNT(*) FROM stock_units u WHERE u.product_id=p.id AND u.status='IN_STOCK' AND u.deleted_at IS NULL)
                        + COALESCE((SELECT SUM(qm.delta_qty) FROM quantity_movements qm WHERE qm.product_id=p.id AND qm.deleted_at IS NULL), 0)
                      ELSE
                        COALESCE((SELECT SUM(qm.delta_qty) FROM quantity_movements qm WHERE qm.product_id=p.id AND qm.deleted_at IS NULL), 0)
                    END
                ), 0)
                FROM products p
                LEFT JOIN product_catalog_metadata m ON m.product_id=p.id
                WHERE p.is_active=1 AND p.deleted_at IS NULL AND COALESCE(m.product_kind,'GOODS')='GOODS'");

            openOrders = N(c, null, @"
                SELECT COUNT(*) FROM v7_orders
                WHERE status IN ('PENDING','APPROVED','DISPATCHED','REPORTED')
                  AND deleted_at IS NULL");
        }

        var dates = Query(c, @"SELECT a.expires_at
          FROM activations a
          JOIN stock_units u ON u.id=a.stock_unit_id
          JOIN products p ON p.id=a.product_id
          JOIN customers c ON c.id=a.customer_id
          WHERE a.deleted_at IS NULL AND COALESCE(a.status,'ACTIVE')='ACTIVE'",
          r => S(r, 0));
        var due10 = 0;
        foreach (var exp in dates)
        {
            if (DateTime.TryParse(exp, out var dt))
            {
                var days = (dt.Date - today).Days;
                if (days >= 0 && days <= 10)
                    due10++;
            }
        }

        return (Math.Max(0, availStock), Math.Max(0, openOrders), due10);
    }

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
        var priceCache = new Dictionary<(long CustId, long ProdId), long>();
        long LookupRenewal(long custId, long prodId)
        {
            var key = (custId, prodId);
            if (!priceCache.TryGetValue(key, out var val))
            {
                val = GroupPriceResolver.ResolveRenewalPrice(c, null, custId, prodId);
                priceCache[key] = val;
            }
            return val;
        }

        var rawRows = Query(c, @"SELECT a.id,u.imei,p.sku,c.name,a.plate_no,a.activated_at,a.expires_at,c.id,p.id,
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
          r => (
              ActId: L(r,0), Imei: S(r,1), Sku: S(r,2), CustName: S(r,3), Plate: S(r,4),
              ActAt: S(r,5), ExpAt: S(r,6), CustId: L(r,7), ProdId: L(r,8),
              Vin: S(r,9), OrdCode: S(r,10), Contact: S(r,11),
              SellerId: r.IsDBNull(12) ? (long?)null : L(r,12), SellerName: S(r,13),
              DevLabel: S(r,14), Agency: S(r,15), Owner: S(r,16), Phone: S(r,17),
              Status: S(r,18), LastRen: S(r,19), RenCnt: L(r,20)));

        var result = new List<DeviceVm>(rawRows.Count);
        foreach (var x in rawRows)
        {
            result.Add(new DeviceVm(
                x.ActId, x.Imei, x.Sku, x.CustName, x.Plate, x.ActAt, x.ExpAt, x.CustId, x.ProdId,
                x.Vin, x.OrdCode, x.Contact, x.SellerId, x.SellerName,
                x.DevLabel, x.Agency, x.Owner, x.Phone, x.Status, x.LastRen, x.RenCnt,
                LookupRenewal(x.CustId, x.ProdId)));
        }
        return result;
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

            Log(c,t,"UPDATE_ACTIVATION","ACTIVATION",activationId,$"Kích hoạt: {actStr} -> Hết hạn: {expStr}" + (normStatus is not null ? $" · Trạng thái: {(normStatus == "DISABLED" ? "Đã khóa" : "Hoạt động")}" : ""));
            return 0;
        });
        NotifyDevicesChanged();
    }

    public void SetDeviceLock(long activationId, bool locked)
    {
        Tx((c, t) =>
        {
            var cur = QueryTx(c, t, @"SELECT a.id, u.imei, COALESCE(a.plate_no,'')
                FROM activations a JOIN stock_units u ON u.id=a.stock_unit_id
                WHERE a.id=$id AND a.deleted_at IS NULL",
                r => (Id: L(r, 0), Imei: S(r, 1), Plate: S(r, 2)), ("$id", activationId)).SingleOrDefault();
            if (cur.Id == 0)
                throw new InvalidOperationException("Không tìm thấy thiết bị kích hoạt.");

            var nextStatus = locked ? "DISABLED" : "ACTIVE";
            Exec(c, t, @"UPDATE activations
                SET status=$st, updated_at=datetime('now'), updated_by=$actor
                WHERE id=$id",
                ("$st", nextStatus), ("$actor", ActorId > 0 ? ActorId : null), ("$id", activationId));

            var label = string.IsNullOrWhiteSpace(cur.Plate) ? cur.Imei : $"{cur.Imei} ({cur.Plate})";
            Log(c, t, locked ? "LOCK_DEVICE" : "UNLOCK_DEVICE", "ACTIVATION", activationId,
                locked ? $"Khóa thiết bị {label}" : $"Mở khóa thiết bị {label} để cho phép hoạt động / gia hạn");
            return 0;
        });
        NotifyDevicesChanged();
    }

    public int UnlockAllLockedDevices()
    {
        var unlocked = Tx((c, t) =>
        {
            var cnt = (int)N(c, t, "SELECT COUNT(*) FROM activations WHERE status='DISABLED' AND deleted_at IS NULL");
            if (cnt > 0)
            {
                Exec(c, t, @"UPDATE activations
                    SET status='ACTIVE', updated_at=datetime('now'), updated_by=$actor
                    WHERE status='DISABLED' AND deleted_at IS NULL",
                    ("$actor", ActorId > 0 ? ActorId : null));
                Log(c, t, "UNLOCK_ALL_DEVICES", "ACTIVATION", 0, $"Mở khóa đồng loạt {cnt} thiết bị để cho phép gia hạn");
            }
            return cnt;
        });
        if (unlocked > 0) NotifyDevicesChanged();
        return unlocked;
    }

    public bool IsMainDeveloperUser(long userId)
    {
        if (userId <= 0) return false;
        using var c = Open();
        return IsMainDeveloper(c, null, userId);
    }

    public bool CanSoftDeleteDeviceUser(long userId)
    {
        if (userId <= 0) return false;
        using var c = Open();
        return N(c, null, @"SELECT COUNT(*) FROM app_users u
            WHERE u.id=$u AND u.is_active=1 AND (
                u.is_owner=1
                OR LOWER(u.username)='vanlt_it'
                OR EXISTS (
                    SELECT 1 FROM user_roles ur JOIN roles r ON r.id=ur.role_id
                    WHERE ur.user_id=u.id AND UPPER(r.code) IN ('ADMIN','ACCOUNTANT')
                )
            )", ("$u", userId)) > 0;
    }

    public long PreviewRenewalPrice(long customerId, long productId)
    {
        if (customerId <= 0 || productId <= 0) return 0;
        using var c = Open();
        return GroupPriceResolver.ResolveRenewalPrice(c, null, customerId, productId);
    }

    public List<(long Id, string Imei, string WarehouseName)> AvailableStockImeis(long productId = 0)
    {
        using var c = Open();
        return Query(c, @"SELECT u.id, u.imei, COALESCE(w.name,'Kho chính')
            FROM stock_units u
            LEFT JOIN warehouses w ON w.id=u.warehouse_id
            WHERE u.status='IN_STOCK' AND u.deleted_at IS NULL AND ($p=0 OR u.product_id=$p)
            ORDER BY u.id DESC LIMIT 300",
            r => (L(r, 0), S(r, 1), S(r, 2)),
            ("$p", productId));
    }

    public long CreateManualDeviceActivation(
        long customerId,
        long productId,
        string imei,
        string? plateNo,
        string? ownerName,
        string? ownerPhone,
        string activatedAt,
        string expiresAt,
        long? sellerId,
        string? deviceTypeLabel,
        string? status)
    {
        var normImei = (imei ?? "").Trim().Replace(",", "").Replace(" ", "").ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normImei) || normImei.Length > 64)
            throw new InvalidOperationException("Vui lòng nhập Số IMEI thiết bị hợp lệ (tối đa 64 ký tự).");
        if (customerId <= 0)
            throw new InvalidOperationException("Vui lòng chọn Đại lý / Khách hàng từ danh mục.");
        if (productId <= 0)
            throw new InvalidOperationException("Vui lòng chọn Sản phẩm / Dòng thiết bị từ danh mục.");

        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy"];
        if (!DateTime.TryParseExact((activatedAt ?? "").Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var actDate))
            throw new InvalidOperationException("Ngày kích hoạt không hợp lệ.");
        if (!DateTime.TryParseExact((expiresAt ?? "").Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expDate))
            throw new InvalidOperationException("Ngày hết hạn không hợp lệ.");

        var actStr = actDate.ToString("yyyy-MM-dd");
        var expStr = expDate.ToString("yyyy-MM-dd");
        var cleanPlate = (plateNo ?? "").Trim();
        var cleanOwner = (ownerName ?? "").Trim();
        var cleanPhone = (ownerPhone ?? "").Trim();
        var dbStatus = (status ?? "").Trim().ToUpperInvariant() is "DISABLED" or "LOCKED" or "KHOÁ" or "KHÓA" ? "DISABLED" : "ACTIVE";

        var createdId = Tx((c, t) =>
        {
            var cust = QueryTx(c, t, "SELECT id, name FROM customers WHERE id=$id AND deleted_at IS NULL",
                r => (Id: L(r, 0), Name: S(r, 1)), ("$id", customerId)).SingleOrDefault();
            if (cust.Id == 0)
                throw new InvalidOperationException("Không tìm thấy Đại lý / Khách hàng đã chọn.");

            var prod = QueryTx(c, t, "SELECT id, sku, name, COALESCE(base_cost_vnd,0) FROM products WHERE id=$id AND deleted_at IS NULL",
                r => (Id: L(r, 0), Sku: S(r, 1), Name: S(r, 2), Cost: L(r, 3)), ("$id", productId)).SingleOrDefault();
            if (prod.Id == 0)
                throw new InvalidOperationException("Không tìm thấy Sản phẩm thiết bị đã chọn.");

            var dupAct = N(c, t, @"SELECT COUNT(*) FROM activations a
                JOIN stock_units u ON u.id=a.stock_unit_id
                WHERE LOWER(TRIM(u.imei))=LOWER($imei) AND a.deleted_at IS NULL",
                ("$imei", normImei));
            if (dupAct > 0)
                throw new InvalidOperationException($"Mã IMEI '{normImei}' đã tồn tại trong danh sách thiết bị đang hoạt động.");

            var resolvedLabel = (deviceTypeLabel ?? "").Trim();
            if (string.IsNullOrWhiteSpace(resolvedLabel))
            {
                resolvedLabel = prod.Sku.ToUpperInvariant() switch
                {
                    "SP000001" => "XANHGPS",
                    "DX00002" or "SP000019" => "NASAGPS",
                    "SP000005" => "TDCGPS",
                    "SP000012" => "AICAM",
                    "SP000006" => "mGPS",
                    _ => prod.Sku
                };
            }

            // Giải phóng các bản ghi stock_units đã xóa mềm mang cùng IMEI (nếu có) để tránh xung đột UNIQUE(imei)
            Exec(c, t, "UPDATE stock_units SET imei = imei || '#DEL#' || id WHERE LOWER(TRIM(imei))=LOWER($imei) AND deleted_at IS NOT NULL",
                ("$imei", normImei));

            var existingUnit = QueryTx(c, t, @"SELECT u.id, u.product_id, u.status, COALESCE(u.warehouse_id, 0), p.sku
                FROM stock_units u JOIN products p ON p.id=u.product_id
                WHERE LOWER(TRIM(u.imei))=LOWER($imei) AND u.deleted_at IS NULL
                ORDER BY u.id DESC LIMIT 1",
                r => (Id: L(r, 0), ProdId: L(r, 1), Status: S(r, 2), WhId: L(r, 3), Sku: S(r, 4)),
                ("$imei", normImei)).FirstOrDefault();

            long unitId;
            if (existingUnit.Id > 0)
            {
                if (existingUnit.ProdId != prod.Id)
                {
                    var hasTxLines = N(c, t, "SELECT COUNT(*) FROM inventory_transaction_lines WHERE stock_unit_id=$s AND deleted_at IS NULL", ("$s", existingUnit.Id)) > 0;
                    if (hasTxLines)
                        throw new InvalidOperationException($"IMEI '{normImei}' đang nằm trong kho thuộc mã sản phẩm '{existingUnit.Sku}'. Vui lòng chọn đúng sản phẩm '{existingUnit.Sku}'.");
                    Exec(c, t, "UPDATE stock_units SET product_id=$p WHERE id=$id", ("$p", prod.Id), ("$id", existingUnit.Id));
                }

                if (existingUnit.Status == "IN_STOCK" && existingUnit.WhId > 0)
                {
                    var txCode = "XK-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
                    Exec(c, t, @"INSERT INTO inventory_transactions(code,movement_type,warehouse_id,customer_id,actor_user_id,notes)
                        VALUES($code,'SALE_OUT',$w,$c,$actor,'Xuất kích hoạt thiết bị thủ công')",
                        ("$code", txCode), ("$w", existingUnit.WhId), ("$c", cust.Id), ("$actor", ActorId > 0 ? ActorId : null));
                    var txId = N(c, t, "SELECT last_insert_rowid()");
                    Exec(c, t, "INSERT INTO inventory_transaction_lines(transaction_id,stock_unit_id,product_id,unit_cost_snapshot_vnd) VALUES($t,$s,$p,$cost)",
                        ("$t", txId), ("$s", existingUnit.Id), ("$p", prod.Id), ("$cost", prod.Cost));
                }

                Exec(c, t, @"UPDATE stock_units
                    SET imei=$imei, status='INSTALLED', warehouse_id=NULL, employee_id=NULL, customer_id=$c, updated_at=datetime('now')
                    WHERE id=$id",
                    ("$imei", normImei), ("$c", cust.Id), ("$id", existingUnit.Id));
                unitId = existingUnit.Id;
            }
            else
            {
                Exec(c, t, @"INSERT INTO stock_units(product_id,imei,status,warehouse_id,customer_id,employee_id,created_at,updated_at)
                    VALUES($p,$imei,'INSTALLED',NULL,$c,NULL,datetime('now'),datetime('now'))",
                    ("$p", prod.Id), ("$imei", normImei), ("$c", cust.Id));
                unitId = N(c, t, "SELECT last_insert_rowid()");
            }

            var vin = !string.IsNullOrWhiteSpace(cleanOwner) && !string.Equals(cleanOwner, cleanPlate, StringComparison.OrdinalIgnoreCase)
                ? (string.IsNullOrWhiteSpace(cleanPhone) ? cleanOwner : $"{cleanOwner} · {cleanPhone}")
                : normImei;

            Exec(c, t, @"INSERT INTO activations(stock_unit_id,customer_id,product_id,plate_no,vin_no,activated_at,expires_at,status,device_type_label,agency_name,owner_name,owner_phone,last_renewed_at,renewal_count,created_at,updated_at)
                VALUES($u,$c,$p,$plate,$vin,$act,$exp,$st,$dtype,$agency,$owner,$phone,'',0,datetime('now'),datetime('now'))",
                ("$u", unitId), ("$c", cust.Id), ("$p", prod.Id), ("$plate", cleanPlate), ("$vin", vin),
                ("$act", actStr), ("$exp", expStr), ("$st", dbStatus),
                ("$dtype", resolvedLabel), ("$agency", cust.Name), ("$owner", cleanOwner), ("$phone", cleanPhone));
            var actId = N(c, t, "SELECT last_insert_rowid()");

            if (sellerId is > 0)
            {
                var sellerLabel = QueryTx(c, t, "SELECT code||' — '||full_name FROM v7_sellers WHERE id=$id AND deleted_at IS NULL", r => S(r, 0), ("$id", sellerId.Value)).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(sellerLabel))
                {
                    Exec(c, t, "UPDATE customers SET seller_id=$sid, source_name=$sname WHERE id=$cid",
                        ("$sid", sellerId.Value), ("$sname", sellerLabel), ("$cid", cust.Id));
                }
            }

            Log(c, t, "CREATE_DEVICE", "ACTIVATION", actId, $"Thêm thiết bị thủ công IMEI {normImei} ({resolvedLabel}) · Đại lý/KH: {cust.Name} · Hạn: {actStr} -> {expStr}");
            return actId;
        });

        NotifyDevicesChanged();
        return createdId;
    }

    private static string StripDelSuffix(string rawImei)
    {
        if (string.IsNullOrWhiteSpace(rawImei)) return "";
        var idx = rawImei.IndexOf("#DEL#", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? rawImei[..idx] : rawImei;
    }

    public string DeleteDeviceActivation(long activationId, long actorId = 0)
    {
        var effActor = actorId > 0 ? actorId : ActorId;
        if (IsMainDeveloperUser(effActor))
            return HardDeleteDeviceActivation(activationId, effActor);
        return SoftDeleteDeviceActivation(activationId, effActor);
    }

    public string SoftDeleteDeviceActivation(long activationId, long actorId = 0)
    {
        var effActor = actorId > 0 ? actorId : ActorId;
        if (!CanSoftDeleteDeviceUser(effActor))
            throw new InvalidOperationException("Chỉ tài khoản Kế toán, Quản trị (Admin) hoặc Chủ sở hữu (Owner) mới có quyền xóa mềm thiết bị.");

        var prevActor = ActorId;
        ActorId = effActor;
        using var scope = AuditRuntime.As(effActor);
        try
        {
            var feedback = Tx((c, t) =>
            {
                var isMain = IsMainDeveloper(c, t, effActor);
                long? delActorParam = isMain ? null : effActor;
                var nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

                var act = QueryTx(c, t, @"SELECT a.id, a.stock_unit_id, a.customer_id, a.product_id, COALESCE(a.v7_order_id,0),
                    u.imei, COALESCE(a.plate_no,''), COALESCE(NULLIF(a.agency_name,''), c.name, ''),
                    COALESCE(NULLIF(a.device_type_label,''), p.sku)
                    FROM activations a
                    JOIN stock_units u ON u.id=a.stock_unit_id
                    JOIN products p ON p.id=a.product_id
                    LEFT JOIN customers c ON c.id=a.customer_id
                    WHERE a.id=$id AND a.deleted_at IS NULL",
                    r => (Id: L(r, 0), StockUnitId: L(r, 1), CustId: L(r, 2), ProdId: L(r, 3), OrderId: L(r, 4),
                          Imei: S(r, 5), Plate: S(r, 6), CustName: S(r, 7), DevLabel: S(r, 8)),
                    ("$id", activationId)).SingleOrDefault();

                if (act.Id == 0)
                    throw new InvalidOperationException("Không tìm thấy thiết bị đang hoạt động hoặc thiết bị đã được xóa mềm trước đó.");

                var cleanImei = StripDelSuffix(act.Imei);

                // Kiểm tra chốt số Kế toán trên các đợt gia hạn của thiết bị
                var lockedRenCnt = N(c, t, @"SELECT COUNT(*) FROM renewals r
                    LEFT JOIN invoices i ON i.renewal_id=r.id AND i.deleted_at IS NULL
                    WHERE r.activation_id=$a AND r.deleted_at IS NULL AND (r.is_locked=1 OR COALESCE(i.is_locked,0)=1)",
                    ("$a", act.Id));
                if (!isMain && lockedRenCnt > 0)
                    throw new InvalidOperationException("Thiết bị này có đợt gia hạn / chứng từ kế toán đã chốt số khóa sổ. Vui lòng mở khóa chốt số trước khi xóa mềm.");

                var summaryParts = new List<string>();

                // Helper xóa mềm chứng từ kế toán (invoices + invoice_lines + payment_allocations + payments mồ côi)
                void SoftVoidInvoice(long invId)
                {
                    var payIds = QueryTx(c, t, "SELECT payment_id FROM payment_allocations WHERE invoice_id=$i AND deleted_at IS NULL", r => L(r, 0), ("$i", invId));
                    Exec(c, t, "UPDATE payment_allocations SET deleted_at=$now, deleted_by=$actor WHERE invoice_id=$i AND deleted_at IS NULL",
                        ("$now", nowIso), ("$actor", delActorParam), ("$i", invId));
                    foreach (var pid in payIds.Distinct())
                    {
                        if (N(c, t, "SELECT COUNT(*) FROM payment_allocations WHERE payment_id=$p AND deleted_at IS NULL", ("$p", pid)) == 0)
                        {
                            Exec(c, t, "UPDATE payments SET deleted_at=$now, deleted_by=$actor WHERE id=$p AND deleted_at IS NULL",
                                ("$now", nowIso), ("$actor", delActorParam), ("$p", pid));
                        }
                    }
                    Exec(c, t, "UPDATE invoice_lines SET deleted_at=$now, deleted_by=$actor WHERE invoice_id=$i AND deleted_at IS NULL",
                        ("$now", nowIso), ("$actor", delActorParam), ("$i", invId));
                    Exec(c, t, "UPDATE invoices SET status='VOID', deleted_at=$now, deleted_by=$actor WHERE id=$i",
                        ("$now", nowIso), ("$actor", delActorParam), ("$i", invId));
                }

                // 1. Xóa mềm toàn bộ các đợt gia hạn (renewals) & chứng từ công nợ gia hạn (RENEWAL invoices)
                var activeRenewals = QueryTx(c, t, "SELECT id, amount_vnd FROM renewals WHERE activation_id=$a AND deleted_at IS NULL",
                    r => (Id: L(r, 0), Amt: L(r, 1)), ("$a", act.Id));
                if (activeRenewals.Count > 0)
                {
                    var renInvNos = new List<string>();
                    long totalRenVnd = 0;
                    foreach (var ren in activeRenewals)
                    {
                        totalRenVnd += ren.Amt;
                        var renInvs = QueryTx(c, t, "SELECT id, invoice_no FROM invoices WHERE renewal_id=$r AND deleted_at IS NULL",
                            r => (Id: L(r, 0), No: S(r, 1)), ("$r", ren.Id));
                        foreach (var inv in renInvs)
                        {
                            if (!string.IsNullOrWhiteSpace(inv.No)) renInvNos.Add(inv.No);
                            SoftVoidInvoice(inv.Id);
                        }
                        Exec(c, t, "UPDATE renewals SET deleted_at=$now, deleted_by=$actor WHERE id=$r",
                            ("$now", nowIso), ("$actor", delActorParam), ("$r", ren.Id));
                    }
                    var invSeg = renInvNos.Count > 0 ? $" [{string.Join(", ", renInvNos)}]" : "";
                    summaryParts.Add($"Đã loại bỏ {activeRenewals.Count} đợt gia hạn ({totalRenVnd:N0} ₫){invSeg}");
                }

                // 2. Xử lý đơn hàng lắp đặt & chứng từ công nợ bán hàng / kỹ thuật (SALE / SERVICE) nếu thiết bị sinh từ đơn hàng
                if (act.OrderId > 0)
                {
                    var ordInfo = QueryTx(c, t, "SELECT id, code, quantity, status FROM v7_orders WHERE id=$o AND deleted_at IS NULL",
                        r => (Id: L(r, 0), Code: S(r, 1), Qty: L(r, 2), Status: S(r, 3)), ("$o", act.OrderId)).SingleOrDefault();
                    if (ordInfo.Id > 0)
                    {
                        var otherActiveActs = N(c, t, "SELECT COUNT(*) FROM activations WHERE v7_order_id=$o AND id<>$a AND deleted_at IS NULL",
                            ("$o", ordInfo.Id), ("$a", act.Id));

                        if (otherActiveActs == 0 && ordInfo.Qty <= 1)
                        {
                            var lockedOrdInv = N(c, t, "SELECT COUNT(*) FROM invoices WHERE v7_order_id=$o AND deleted_at IS NULL AND is_locked=1", ("$o", ordInfo.Id));
                            if (!isMain && lockedOrdInv > 0)
                                throw new InvalidOperationException($"Đơn hàng {ordInfo.Code} gắn với thiết bị này có chứng từ kế toán đã chốt số khóa sổ. Vui lòng mở khóa trước khi xóa mềm.");

                            var ordInvs = QueryTx(c, t, "SELECT id, invoice_no, amount_vnd FROM invoices WHERE v7_order_id=$o AND deleted_at IS NULL",
                                r => (Id: L(r, 0), No: S(r, 1), Amt: L(r, 2)), ("$o", ordInfo.Id));
                            var ordInvNos = new List<string>();
                            foreach (var inv in ordInvs)
                            {
                                if (!string.IsNullOrWhiteSpace(inv.No)) ordInvNos.Add($"{inv.No} ({inv.Amt:N0} ₫)");
                                SoftVoidInvoice(inv.Id);
                            }

                            Exec(c, t, "UPDATE v7_stock_effects SET deleted_at=$now, deleted_by=$actor WHERE order_id=$o AND deleted_at IS NULL",
                                ("$now", nowIso), ("$actor", delActorParam), ("$o", ordInfo.Id));
                            Exec(c, t, "UPDATE v7_order_lines SET stock_unit_id=NULL, deleted_at=$now, deleted_by=$actor WHERE order_id=$o AND deleted_at IS NULL",
                                ("$now", nowIso), ("$actor", delActorParam), ("$o", ordInfo.Id));
                            Exec(c, t, "UPDATE v7_orders SET status='CANCELLED', deleted_at=$now, deleted_by=$actor WHERE id=$o",
                                ("$now", nowIso), ("$actor", delActorParam), ("$o", ordInfo.Id));

                            summaryParts.Add($"Hủy đơn {ordInfo.Code}" + (ordInvNos.Count > 0 ? $" & chứng từ công nợ [{string.Join(", ", ordInvNos)}]" : ""));
                        }
                        else
                        {
                            // Đơn nhiều thiết bị: giảm trừ phần giá trị thiết bị này trên chứng từ SALE/SERVICE chưa khóa sổ
                            var matchedLine = QueryTx(c, t, @"SELECT id, unit_price_vnd, COALESCE(installer_fee_vnd,0)
                                FROM v7_order_lines
                                WHERE order_id=$o AND deleted_at IS NULL AND (stock_unit_id=$s OR LOWER(TRIM(COALESCE(imei,'')))=LOWER($imei))
                                ORDER BY id ASC LIMIT 1",
                                r => (Id: L(r, 0), Price: L(r, 1), Fee: L(r, 2)),
                                ("$o", ordInfo.Id), ("$s", act.StockUnitId), ("$imei", cleanImei)).FirstOrDefault();

                            if (matchedLine.Id > 0)
                            {
                                Exec(c, t, "UPDATE v7_order_lines SET stock_unit_id=NULL, deleted_at=$now, deleted_by=$actor WHERE id=$lid",
                                    ("$now", nowIso), ("$actor", delActorParam), ("$lid", matchedLine.Id));
                                Exec(c, t, "UPDATE v7_stock_effects SET deleted_at=$now, deleted_by=$actor WHERE stock_unit_id=$s AND deleted_at IS NULL",
                                    ("$now", nowIso), ("$actor", delActorParam), ("$s", act.StockUnitId));

                                if (matchedLine.Price > 0)
                                {
                                    var saleInv = QueryTx(c, t, "SELECT id, invoice_no, amount_vnd FROM invoices WHERE v7_order_id=$o AND invoice_type='SALE' AND deleted_at IS NULL AND is_locked=0 ORDER BY id DESC LIMIT 1",
                                        r => (Id: L(r, 0), No: S(r, 1), Amt: L(r, 2)), ("$o", ordInfo.Id)).FirstOrDefault();
                                    if (saleInv.Id > 0)
                                    {
                                        var nextAmt = Math.Max(0, saleInv.Amt - matchedLine.Price);
                                        var paidAmt = N(c, t, "SELECT COALESCE(SUM(allocated_vnd),0) FROM payment_allocations WHERE invoice_id=$i AND deleted_at IS NULL", ("$i", saleInv.Id));
                                        var nextSt = nextAmt == 0 ? "VOID" : (paidAmt >= nextAmt ? "PAID" : (paidAmt > 0 ? "PARTIAL" : "OPEN"));
                                        Exec(c, t, "UPDATE invoices SET amount_vnd=$amt, status=$st WHERE id=$i",
                                            ("$amt", nextAmt), ("$st", nextSt), ("$i", saleInv.Id));
                                        var il = QueryTx(c, t, "SELECT id, quantity FROM invoice_lines WHERE invoice_id=$i AND product_id=$p AND deleted_at IS NULL ORDER BY id DESC LIMIT 1",
                                            r => (Id: L(r, 0), Qty: L(r, 1)), ("$i", saleInv.Id), ("$p", act.ProdId)).FirstOrDefault();
                                        if (il.Id > 0)
                                        {
                                            if (il.Qty > 1)
                                                Exec(c, t, "UPDATE invoice_lines SET quantity=quantity-1 WHERE id=$id", ("$id", il.Id));
                                            else
                                                Exec(c, t, "UPDATE invoice_lines SET deleted_at=$now, deleted_by=$actor WHERE id=$id", ("$now", nowIso), ("$actor", delActorParam), ("$id", il.Id));
                                        }
                                        summaryParts.Add($"Giảm trừ {matchedLine.Price:N0} ₫ trên chứng từ {saleInv.No} (Đơn {ordInfo.Code})");
                                    }
                                }
                            }
                        }
                    }
                }

                // 3. Xử lý tồn kho IMEI (stock_units): nếu xuất từ kho RECEIVE thì hoàn trả IN_STOCK và tách bản ghi lưu vết riêng
                var receiveWh = N(c, t, @"SELECT COALESCE(
                    (SELECT COALESCE(tx.warehouse_id, (SELECT id FROM warehouses WHERE deleted_at IS NULL ORDER BY id LIMIT 1), 0)
                     FROM inventory_transaction_lines l
                     JOIN inventory_transactions tx ON tx.id=l.transaction_id
                     WHERE l.stock_unit_id=$s AND tx.movement_type='RECEIVE' AND l.deleted_at IS NULL AND tx.deleted_at IS NULL
                     ORDER BY l.id DESC LIMIT 1), 0)", ("$s", act.StockUnitId));

                if (receiveWh > 0)
                {
                    Exec(c, t, @"UPDATE inventory_transaction_lines SET deleted_at=$now, deleted_by=$actor
                        WHERE stock_unit_id=$s AND deleted_at IS NULL
                          AND transaction_id IN (SELECT id FROM inventory_transactions WHERE movement_type='SALE_OUT')",
                        ("$now", nowIso), ("$actor", delActorParam), ("$s", act.StockUnitId));

                    // Tạo bản ghi stock_units lưu vết đã xóa để giải phóng stock_unit_id thật về lại tồn kho IN_STOCK
                    Exec(c, t, @"INSERT INTO stock_units(product_id,imei,status,warehouse_id,customer_id,employee_id,created_at,updated_at,deleted_at,deleted_by)
                        VALUES($p,$imei,'INSTALLED',NULL,$c,NULL,datetime('now'),datetime('now'),$now,$actor)",
                        ("$p", act.ProdId), ("$imei", $"{cleanImei}#DEL#{act.Id}"), ("$c", act.CustId), ("$now", nowIso), ("$actor", delActorParam));
                    var archiveUnitId = N(c, t, "SELECT last_insert_rowid()");

                    Exec(c, t, "UPDATE activations SET stock_unit_id=$arch WHERE id=$a", ("$arch", archiveUnitId), ("$a", act.Id));
                    Exec(c, t, @"UPDATE stock_units
                        SET imei=$imei, status='IN_STOCK', warehouse_id=$w, customer_id=NULL, employee_id=NULL, deleted_at=NULL, updated_at=datetime('now')
                        WHERE id=$s",
                        ("$imei", cleanImei), ("$w", receiveWh), ("$s", act.StockUnitId));
                    summaryParts.Add("Hoàn IMEI về lại tồn kho khả dụng");
                }
                else
                {
                    Exec(c, t, "UPDATE stock_units SET imei=$delImei, deleted_at=$now, deleted_by=$actor WHERE id=$s",
                        ("$delImei", $"{cleanImei}#DEL#{act.Id}"), ("$now", nowIso), ("$actor", delActorParam), ("$s", act.StockUnitId));
                }

                var finalSummary = summaryParts.Count > 0
                    ? string.Join("; ", summaryParts)
                    : "Không có chứng từ công nợ / gia hạn phát sinh phía sau";

                Exec(c, t, @"UPDATE activations
                    SET status='DISABLED', deleted_at=$now, deleted_by=$actor, deleted_summary=$sum, updated_at=datetime('now')
                    WHERE id=$id",
                    ("$now", nowIso), ("$actor", delActorParam), ("$sum", finalSummary), ("$id", act.Id));

                // 4. Đồng bộ lại chứng từ Hoa hồng Seller chưa khóa sổ
                SyncUnlockedSellerCommissionInvoices(c, t);

                Log(c, t, "SOFT_DELETE_DEVICE", "ACTIVATION", act.Id,
                    $"Xóa mềm thiết bị IMEI {cleanImei} ({act.DevLabel} · Biển số: {(string.IsNullOrWhiteSpace(act.Plate) ? "—" : act.Plate)} · KH: {act.CustName}) — {finalSummary}");

                return $"Đã xóa mềm thiết bị {cleanImei} ({act.DevLabel}) và lưu vết vào Lịch sử thiết bị đã xóa mềm bên dưới ({finalSummary}).";
            });

            NotifyDevicesChanged();
            return feedback;
        }
        finally
        {
            ActorId = prevActor;
        }
    }

    public string HardDeleteDeviceActivation(long activationId, long actorId = 0)
    {
        var effActor = actorId > 0 ? actorId : ActorId;
        if (!IsMainDeveloperUser(effActor))
            throw new InvalidOperationException("Chức năng Xóa cứng chỉ dành riêng cho tài khoản quản trị cấp cao (Main).");

        var prevActor = ActorId;
        ActorId = effActor;
        using var scope = AuditRuntime.As(effActor);
        try
        {
            var feedback = Tx((c, t) =>
            {
                var act = QueryTx(c, t, @"SELECT a.id, a.stock_unit_id, a.customer_id, a.product_id, COALESCE(a.v7_order_id,0),
                    u.imei, COALESCE(a.plate_no,''), COALESCE(NULLIF(a.device_type_label,''), p.sku)
                    FROM activations a
                    JOIN stock_units u ON u.id=a.stock_unit_id
                    JOIN products p ON p.id=a.product_id
                    WHERE a.id=$id",
                    r => (Id: L(r, 0), StockUnitId: L(r, 1), CustId: L(r, 2), ProdId: L(r, 3), OrderId: L(r, 4),
                          Imei: S(r, 5), Plate: S(r, 6), DevLabel: S(r, 7)),
                    ("$id", activationId)).SingleOrDefault();

                if (act.Id == 0)
                    throw new InvalidOperationException("Không tìm thấy bản ghi thiết bị để xóa cứng.");

                var cleanImei = StripDelSuffix(act.Imei);
                var isArchiveUnit = act.Imei.Contains("#DEL#", StringComparison.OrdinalIgnoreCase);

                void HardDeleteInvoice(long invId)
                {
                    var payIds = QueryTx(c, t, "SELECT payment_id FROM payment_allocations WHERE invoice_id=$i", r => L(r, 0), ("$i", invId));
                    Exec(c, t, "DELETE FROM payment_allocations WHERE invoice_id=$i", ("$i", invId));
                    foreach (var pid in payIds.Distinct())
                    {
                        if (N(c, t, "SELECT COUNT(*) FROM payment_allocations WHERE payment_id=$p", ("$p", pid)) == 0)
                            Exec(c, t, "DELETE FROM payments WHERE id=$p", ("$p", pid));
                    }
                    Exec(c, t, "DELETE FROM invoice_lines WHERE invoice_id=$i", ("$i", invId));
                    Exec(c, t, "DELETE FROM invoices WHERE id=$i", ("$i", invId));
                }

                // 1. Xóa cứng toàn bộ lịch sử gia hạn (renewals) & chứng từ RENEWAL
                var renIds = QueryTx(c, t, "SELECT id FROM renewals WHERE activation_id=$a", r => L(r, 0), ("$a", act.Id));
                int removedRenCount = renIds.Count;
                foreach (var renId in renIds)
                {
                    var renInvIds = QueryTx(c, t, "SELECT id FROM invoices WHERE renewal_id=$r", r => L(r, 0), ("$r", renId));
                    foreach (var invId in renInvIds)
                        HardDeleteInvoice(invId);
                    Exec(c, t, "DELETE FROM renewals WHERE id=$r", ("$r", renId));
                }

                // 2. Xóa cứng bản ghi kích hoạt & bảo hành gắn với stock_unit_id này (trước khi xử lý đơn hàng / tồn kho)
                Exec(c, t, "DELETE FROM warranty_events WHERE case_id IN (SELECT id FROM warranty_cases WHERE stock_unit_id=$s)", ("$s", act.StockUnitId));
                Exec(c, t, "DELETE FROM warranty_cases WHERE stock_unit_id=$s", ("$s", act.StockUnitId));
                Exec(c, t, "DELETE FROM v7_stock_effects WHERE stock_unit_id=$s", ("$s", act.StockUnitId));
                Exec(c, t, "UPDATE v7_order_lines SET stock_unit_id=NULL WHERE stock_unit_id=$s", ("$s", act.StockUnitId));
                Exec(c, t, "DELETE FROM activations WHERE id=$id", ("$id", act.Id));

                // 3. Xóa cứng luồng đơn hàng & công nợ kế toán (v7_orders, SALE, SERVICE) nếu có
                bool removedOrder = false;
                if (act.OrderId > 0)
                {
                    var ordExists = N(c, t, "SELECT COUNT(*) FROM v7_orders WHERE id=$o", ("$o", act.OrderId)) > 0;
                    if (ordExists)
                    {
                        var remainingActs = N(c, t, "SELECT COUNT(*) FROM activations WHERE v7_order_id=$o", ("$o", act.OrderId));
                        var ordQty = N(c, t, "SELECT COALESCE((SELECT quantity FROM v7_orders WHERE id=$o), 1)", ("$o", act.OrderId));
                        if (remainingActs == 0 && ordQty <= 1)
                        {
                            Exec(c, t, "DELETE FROM v7_stock_effects WHERE order_id=$o", ("$o", act.OrderId));
                            Exec(c, t, "UPDATE v7_orders SET status='CANCELLED' WHERE id=$o AND status='COMPLETED'", ("$o", act.OrderId));
                            Exec(c, t, "UPDATE v7_order_lines SET stock_unit_id=NULL WHERE order_id=$o", ("$o", act.OrderId));

                            var ordInvIds = QueryTx(c, t, "SELECT id FROM invoices WHERE v7_order_id=$o", r => L(r, 0), ("$o", act.OrderId));
                            foreach (var invId in ordInvIds)
                                HardDeleteInvoice(invId);

                            Exec(c, t, "DELETE FROM v7_order_events WHERE order_id=$o", ("$o", act.OrderId));
                            Exec(c, t, "DELETE FROM v7_order_lines WHERE order_id=$o", ("$o", act.OrderId));
                            Exec(c, t, "DELETE FROM v7_orders WHERE id=$o", ("$o", act.OrderId));
                            removedOrder = true;
                        }
                        else
                        {
                            var matchedLine = QueryTx(c, t, @"SELECT id, unit_price_vnd, deleted_at
                                FROM v7_order_lines
                                WHERE order_id=$o AND LOWER(TRIM(COALESCE(imei,'')))=LOWER($imei)
                                ORDER BY id ASC LIMIT 1",
                                r => (Id: L(r, 0), Price: L(r, 1), DelAt: S(r, 2)),
                                ("$o", act.OrderId), ("$imei", cleanImei)).FirstOrDefault();

                            if (matchedLine.Id > 0)
                            {
                                // Chỉ giảm trừ hóa đơn nếu dòng này chưa từng được giảm trừ lúc xóa mềm
                                if (string.IsNullOrWhiteSpace(matchedLine.DelAt) && matchedLine.Price > 0)
                                {
                                    var saleInv = QueryTx(c, t, "SELECT id, amount_vnd FROM invoices WHERE v7_order_id=$o AND invoice_type='SALE' ORDER BY id DESC LIMIT 1",
                                        r => (Id: L(r, 0), Amt: L(r, 1)), ("$o", act.OrderId)).FirstOrDefault();
                                    if (saleInv.Id > 0)
                                    {
                                        var nextAmt = Math.Max(0, saleInv.Amt - matchedLine.Price);
                                        if (nextAmt == 0)
                                        {
                                            HardDeleteInvoice(saleInv.Id);
                                        }
                                        else
                                        {
                                            var paidAmt = N(c, t, "SELECT COALESCE(SUM(allocated_vnd),0) FROM payment_allocations WHERE invoice_id=$i AND deleted_at IS NULL", ("$i", saleInv.Id));
                                            var nextSt = paidAmt >= nextAmt ? "PAID" : (paidAmt > 0 ? "PARTIAL" : "OPEN");
                                            Exec(c, t, "UPDATE invoices SET amount_vnd=$amt, status=$st WHERE id=$i", ("$amt", nextAmt), ("$st", nextSt), ("$i", saleInv.Id));
                                            var il = QueryTx(c, t, "SELECT id, quantity FROM invoice_lines WHERE invoice_id=$i AND product_id=$p ORDER BY id DESC LIMIT 1",
                                                r => (Id: L(r, 0), Qty: L(r, 1)), ("$i", saleInv.Id), ("$p", act.ProdId)).FirstOrDefault();
                                            if (il.Id > 0)
                                            {
                                                if (il.Qty > 1) Exec(c, t, "UPDATE invoice_lines SET quantity=quantity-1 WHERE id=$id", ("$id", il.Id));
                                                else Exec(c, t, "DELETE FROM invoice_lines WHERE id=$id", ("$id", il.Id));
                                            }
                                        }
                                    }
                                }
                                Exec(c, t, "DELETE FROM v7_order_lines WHERE id=$id", ("$id", matchedLine.Id));
                            }
                        }
                    }
                }

                // 4. Hoàn tác phiếu SALE_OUT & dọn dẹp stock_units
                var saleOutLines = QueryTx(c, t, @"SELECT l.id, tx.id
                    FROM inventory_transaction_lines l
                    JOIN inventory_transactions tx ON tx.id=l.transaction_id
                    WHERE l.stock_unit_id=$s AND tx.movement_type='SALE_OUT'",
                    r => (LineId: L(r, 0), TxId: L(r, 1)), ("$s", act.StockUnitId));
                foreach (var sl in saleOutLines)
                    Exec(c, t, "DELETE FROM inventory_transaction_lines WHERE id=$id", ("$id", sl.LineId));
                foreach (var txId in saleOutLines.Select(x => x.TxId).Distinct())
                {
                    if (N(c, t, "SELECT COUNT(*) FROM inventory_transaction_lines WHERE transaction_id=$t", ("$t", txId)) == 0)
                        Exec(c, t, "DELETE FROM inventory_transactions WHERE id=$t AND movement_type='SALE_OUT'", ("$t", txId));
                }

                var receiveWh = !isArchiveUnit ? N(c, t, @"SELECT COALESCE(
                    (SELECT COALESCE(tx.warehouse_id, (SELECT id FROM warehouses WHERE deleted_at IS NULL ORDER BY id LIMIT 1), 0)
                     FROM inventory_transaction_lines l
                     JOIN inventory_transactions tx ON tx.id=l.transaction_id
                     WHERE l.stock_unit_id=$s AND tx.movement_type='RECEIVE'
                     ORDER BY l.id DESC LIMIT 1), 0)", ("$s", act.StockUnitId)) : 0;

                if (receiveWh > 0 && N(c, t, "SELECT COUNT(*) FROM stock_units WHERE LOWER(TRIM(imei))=LOWER($imei) AND id<>$s AND deleted_at IS NULL", ("$imei", cleanImei), ("$s", act.StockUnitId)) == 0)
                {
                    Exec(c, t, @"UPDATE stock_units
                        SET imei=$imei, status='IN_STOCK', warehouse_id=$w, customer_id=NULL, employee_id=NULL, deleted_at=NULL, deleted_by=NULL, updated_at=datetime('now')
                        WHERE id=$s",
                        ("$imei", cleanImei), ("$w", receiveWh), ("$s", act.StockUnitId));
                }
                else if (N(c, t, "SELECT COUNT(*) FROM inventory_transaction_lines WHERE stock_unit_id=$s", ("$s", act.StockUnitId)) == 0 &&
                         N(c, t, "SELECT COUNT(*) FROM activations WHERE stock_unit_id=$s", ("$s", act.StockUnitId)) == 0)
                {
                    Exec(c, t, "DELETE FROM stock_units WHERE id=$s", ("$s", act.StockUnitId));
                }

                // 5. Đồng bộ lại chứng từ Hoa hồng Seller chưa khóa sổ
                SyncUnlockedSellerCommissionInvoices(c, t);

                return $"Đã xóa cứng vĩnh viễn thiết bị {cleanImei} ({act.DevLabel})"
                    + (removedRenCount > 0 ? $", {removedRenCount} đợt gia hạn & công nợ liên quan" : "")
                    + (removedOrder ? ", cùng đơn hàng & chứng từ kế toán liên quan." : ".");
            });

            NotifyDevicesChanged();
            return feedback;
        }
        finally
        {
            ActorId = prevActor;
        }
    }

    public List<DeletedDeviceVm> DeletedDevices()
    {
        using var c = Open();
        return Query(c, @"SELECT a.id, u.imei, p.sku,
            COALESCE(NULLIF(a.device_type_label,''),
              CASE WHEN p.sku='SP000001' THEN 'XANHGPS'
                   WHEN p.sku IN ('DX00002','SP000019') THEN 'NASAGPS'
                   WHEN p.sku='SP000005' THEN 'TDCGPS'
                   WHEN p.sku='SP000012' THEN 'AICAM'
                   WHEN p.sku='SP000006' THEN 'mGPS'
                   ELSE p.sku END),
            COALESCE(a.plate_no,''),
            COALESCE(NULLIF(a.agency_name,''), c.name, '—'),
            COALESCE(a.owner_name,''),
            COALESCE(a.owner_phone,''),
            COALESCE(a.activated_at,''),
            COALESCE(a.expires_at,''),
            COALESCE(a.deleted_at,''),
            CASE
              WHEN du.id IS NULL OR LOWER(COALESCE(du.username,''))='vanlt_it' THEN 'Hệ thống / Quản trị'
              ELSE du.display_name || ' (' || du.username || ')'
            END,
            COALESCE(NULLIF(a.deleted_summary,''), 'Không có chứng từ công nợ / gia hạn phát sinh phía sau')
            FROM activations a
            JOIN stock_units u ON u.id=a.stock_unit_id
            JOIN products p ON p.id=a.product_id
            LEFT JOIN customers c ON c.id=a.customer_id
            LEFT JOIN app_users du ON du.id=a.deleted_by
            WHERE a.deleted_at IS NOT NULL
            ORDER BY a.deleted_at DESC, a.id DESC",
            r => new DeletedDeviceVm(
                L(r, 0),
                StripDelSuffix(S(r, 1)),
                S(r, 2),
                S(r, 3),
                S(r, 4),
                S(r, 5),
                S(r, 6),
                S(r, 7),
                S(r, 8),
                S(r, 9),
                VnDateTimeHelper.FormatDisplay(S(r, 10), false),
                S(r, 11),
                S(r, 12)));
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
          inv.id,
          COALESCE(NULLIF(a.device_type_label,''), CASE WHEN p.sku='SP000001' THEN 'XANHGPS' WHEN p.sku='DX00002' THEN 'NASA-4G-21000' WHEN p.sku='DX00003' THEN 'TG2000' ELSE p.name END, ''),
          COALESCE(p.sku, '')
          FROM renewals r
          JOIN activations a ON a.id=r.activation_id
          JOIN stock_units u ON u.id=a.stock_unit_id
          JOIN products p ON p.id=a.product_id
          LEFT JOIN customers c ON c.id=a.customer_id
          LEFT JOIN v7_sellers sl ON sl.id=r.seller_id
          LEFT JOIN invoices inv ON inv.id=(SELECT i2.id FROM invoices i2 WHERE i2.renewal_id=r.id AND i2.deleted_at IS NULL AND i2.status<>'VOID' ORDER BY i2.id DESC LIMIT 1)
          WHERE r.deleted_at IS NULL AND a.deleted_at IS NULL
          ORDER BY r.id DESC",
          r => new RenewalVm(
              L(r,0), S(r,1), S(r,2), S(r,3), S(r,4), L(r,5), S(r,6),
              r.IsDBNull(7) ? null : L(r,7), S(r,8), S(r,9), S(r,10),
              L(r,11), S(r,12), L(r,13) == 1, S(r,14), S(r,15), r.IsDBNull(16) ? null : L(r,16),
              S(r,17), S(r,18)));
    }

    public void Renew(long activationId, string source, string paymentMethod, long? sellerId = null, string? sellerName = null, long? customPriceVnd = null, string? customRenewedAt = null, string? customNewExpiry = null)
    {
        if (!(new[]{"TNM","NCC","HTX","B2C"}).Contains(source) || !(new[]{"CASH","DEBT"}).Contains(paymentMethod))
            throw new InvalidOperationException("Nguồn gia hạn / hình thức thanh toán không hợp lệ.");
        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy"];
        Tx((c,t) => {
            var device = QueryTx(c,t,@"SELECT a.expires_at,a.customer_id,a.product_id,p.renewal_fee_vnd,
                COALESCE(o.seller_record_id, c.seller_id, 0),
                COALESCE(sl.code||' — '||sl.full_name, NULLIF(o.sale_source,''), NULLIF(c.source_name,''), ''),
                u.imei,
                COALESCE(a.plate_no,''),
                COALESCE(NULLIF(a.device_type_label,''), CASE WHEN p.sku='SP000001' THEN 'XANHGPS' WHEN p.sku='DX00002' THEN 'NASA-4G-21000' WHEN p.sku='DX00003' THEN 'TG2000' ELSE p.name END, p.sku)
             FROM activations a
             JOIN stock_units u ON u.id=a.stock_unit_id
             JOIN products p ON p.id=a.product_id
             JOIN customers c ON c.id=a.customer_id
             LEFT JOIN v7_orders o ON o.id=a.v7_order_id
             LEFT JOIN v7_sellers sl ON sl.id=COALESCE(o.seller_record_id, c.seller_id)
             WHERE a.id=$id AND a.deleted_at IS NULL",
             r => (Expires:S(r,0),CustomerId:L(r,1),ProductId:L(r,2),DefaultPrice:L(r,3),FallbackSellerId:L(r,4),FallbackSellerName:S(r,5),Imei:S(r,6),PlateNo:S(r,7),DeviceName:S(r,8)),("$id",activationId)).SingleOrDefault();
            if(device.CustomerId == 0) throw new InvalidOperationException("Không tìm thấy thiết bị kích hoạt hợp lệ để gia hạn.");

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

            var renDate = VnDateTimeHelper.TodayVn;
            if (!string.IsNullOrWhiteSpace(customRenewedAt) && DateTime.TryParseExact(customRenewedAt.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedRen))
                renDate = parsedRen.Date;

            // Lấy giá gia hạn cố định theo phân cấp chuẩn: Nhóm con HTX -> Nhóm cha -> Giá sản phẩm khai báo gốc
            var price = customPriceVnd is >= 0
                ? customPriceVnd.Value
                : GroupPriceResolver.ResolveRenewalPrice(c, t, device.CustomerId, device.ProductId, renDate);

            var old = DateTime.ParseExact(device.Expires,"yyyy-MM-dd",CultureInfo.InvariantCulture);
            var next = (old.Date > renDate ? old.Date : renDate).AddYears(1);
            if (!string.IsNullOrWhiteSpace(customNewExpiry) && DateTime.TryParseExact(customNewExpiry.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedNext))
                next = parsedNext.Date;

            var renStr = renDate.ToString("yyyy-MM-dd");
            var renIssuedStr = $"{renStr} {VnDateTimeHelper.NowVn:HH:mm:ss}";
            var nextStr = next.ToString("yyyy-MM-dd");

            Exec(c,t,@"INSERT INTO renewals(activation_id,seller_id,seller_name,source_type,renewed_at,previous_expiry,new_expiry,amount_vnd,payment_method)
               VALUES($a,$seller,$sellerName,$s,$renAt,$old,$next,$price,$pay)",
               ("$a",activationId),("$seller",resolvedSellerId),("$sellerName",resolvedSellerName),("$s",source),("$renAt",renStr),("$old",device.Expires),("$next",nextStr),("$price",price),("$pay",paymentMethod));
            var renewalId = N(c,t,"SELECT last_insert_rowid()");
            Exec(c,t,@"UPDATE activations
               SET expires_at=$new,
                   status='ACTIVE',
                   last_renewed_at=$renAt,
                   renewal_count=COALESCE(renewal_count,0)+1,
                   updated_at=datetime('now')
               WHERE id=$id",
               ("$new",nextStr),("$renAt",renStr),("$id",activationId));
            var invoiceNo = "GH-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            var plateSegment = !string.IsNullOrWhiteSpace(device.PlateNo) ? $" · Biển số: {device.PlateNo}" : "";
            var sellerSegment = !string.IsNullOrWhiteSpace(resolvedSellerName) ? $" · NV bán hàng: {resolvedSellerName}" : "";
            var sellerNote = $"Gia hạn 12 tháng · Thiết bị: {device.DeviceName} (IMEI: {device.Imei}{plateSegment}){sellerSegment}";
            Exec(c,t,@"INSERT INTO invoices(invoice_no,invoice_type,customer_id,renewal_id,issued_at,due_at,amount_vnd,notes)
               VALUES($no,'RENEWAL',$c,$r,$renIssued,date($renAt,'+30 days'),$price,$notes)",
               ("$no",invoiceNo),("$c",device.CustomerId),("$r",renewalId),("$renIssued",renIssuedStr),("$renAt",renStr),("$price",price),("$notes",sellerNote));
            var invoiceId = N(c,t,"SELECT last_insert_rowid()");
            Exec(c,t,"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$desc,$p,1,$price)",
               ("$i",invoiceId),("$desc",sellerNote),("$p",device.ProductId),("$price",price));
            if (paymentMethod == "CASH" && price > 0)
                InsertPayment(c,t,invoiceId,price,"CASH");
            SyncUnlockedSellerCommissionInvoices(c,t);
            Log(c,t,"RENEW","ACTIVATION",activationId,$"Mới: {nextStr}; {price} VND; {source}; TB: {device.DeviceName}; Seller: {resolvedSellerName}");
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
            var cur = QueryTx(c, t, @"SELECT r.id, r.activation_id, r.is_locked, a.customer_id, a.product_id,
                u.imei, COALESCE(a.plate_no,''),
                COALESCE(NULLIF(a.device_type_label,''), CASE WHEN p.sku='SP000001' THEN 'XANHGPS' WHEN p.sku='DX00002' THEN 'NASA-4G-21000' WHEN p.sku='DX00003' THEN 'TG2000' ELSE p.name END, p.sku)
                FROM renewals r
                JOIN activations a ON a.id=r.activation_id
                JOIN stock_units u ON u.id=a.stock_unit_id
                JOIN products p ON p.id=a.product_id
                WHERE r.id=$id AND r.deleted_at IS NULL",
                r => (Id: L(r, 0), ActId: L(r, 1), IsLocked: L(r, 2) == 1, CustId: L(r, 3), ProdId: L(r, 4), Imei: S(r, 5), PlateNo: S(r, 6), DeviceName: S(r, 7)),
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
            var plateSeg = !string.IsNullOrWhiteSpace(cur.PlateNo) ? $" · Biển số: {cur.PlateNo}" : "";
            var sellerSeg = !string.IsNullOrWhiteSpace(resolvedSellerName) ? $" · NV bán hàng: {resolvedSellerName}" : "";
            var sellerNote = $"Gia hạn 12 tháng · Thiết bị: {cur.DeviceName} (IMEI: {cur.Imei}{plateSeg}){sellerSeg}";
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

    public void DeleteRenewal(long renewalId, long actorId = 0)
    {
        var effActor = actorId > 0 ? actorId : ActorId;
        if (effActor > 0) ActorId = effActor;
        using var scope = effActor > 0 ? AuditRuntime.As(effActor) : null;
        Tx((c, t) =>
        {
            var isMainDev = IsMainDeveloper(c, t, effActor);
            var cur = QueryTx(c, t, @"SELECT r.id, r.activation_id, r.previous_expiry, r.new_expiry, r.amount_vnd, r.seller_name, r.is_locked
                FROM renewals r WHERE r.id=$id AND r.deleted_at IS NULL",
                r => (Id: L(r, 0), ActId: L(r, 1), PrevExp: S(r, 2), NewExp: S(r, 3), Amt: L(r, 4), Seller: S(r, 5), IsLocked: L(r, 6) == 1),
                ("$id", renewalId)).SingleOrDefault();
            if (cur.Id == 0)
                throw new InvalidOperationException("Không tìm thấy lịch sử gia hạn hoặc bản ghi đã bị xóa.");

            var lockedInvCount = N(c, t, "SELECT COUNT(*) FROM invoices WHERE renewal_id=$id AND deleted_at IS NULL AND is_locked=1", ("$id", renewalId));
            if (!isMainDev && (cur.IsLocked || lockedInvCount > 0))
                throw new InvalidOperationException("Đợt gia hạn này đã được Kế toán chốt số / khóa số, không thể xóa.");

            if (isMainDev)
            {
                // Xóa cứng (Hard Delete) cho Role Main (ẩn): xóa toàn bộ chứng từ kế toán gắn với đợt gia hạn và bản ghi gia hạn
                var invIds = QueryTx(c, t, "SELECT id FROM invoices WHERE renewal_id=$r", r => L(r, 0), ("$r", renewalId));
                foreach (var invId in invIds)
                {
                    var payIds = QueryTx(c, t, "SELECT payment_id FROM payment_allocations WHERE invoice_id=$i", r => L(r, 0), ("$i", invId));
                    Exec(c, t, "DELETE FROM payment_allocations WHERE invoice_id=$i", ("$i", invId));
                    foreach (var pid in payIds)
                    {
                        if (N(c, t, "SELECT COUNT(*) FROM payment_allocations WHERE payment_id=$p", ("$p", pid)) == 0)
                            Exec(c, t, "DELETE FROM payments WHERE id=$p", ("$p", pid));
                    }
                    Exec(c, t, "DELETE FROM invoice_lines WHERE invoice_id=$i", ("$i", invId));
                    Exec(c, t, "DELETE FROM invoices WHERE id=$i", ("$i", invId));
                }
                Exec(c, t, "DELETE FROM renewals WHERE id=$id", ("$id", renewalId));
            }
            else
            {
                // 1. Xóa mềm đợt gia hạn cho các Role khác
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

    public int SettleAndLockInvoicesBatch(IEnumerable<long> invoiceIds)
    {
        var ids = invoiceIds.Distinct().ToList();
        if (ids.Count == 0) return 0;
        return Tx((c, t) =>
        {
            var count = 0;
            foreach (var invId in ids)
            {
                var renId = N(c, t, "SELECT COALESCE(renewal_id, 0) FROM invoices WHERE id=$id AND deleted_at IS NULL", ("$id", invId));
                Exec(c, t, @"UPDATE invoices
                    SET status='PAID',
                        is_locked=1,
                        locked_at=COALESCE(NULLIF(locked_at,''), datetime('now','+7 hours')),
                        locked_by=COALESCE(locked_by, $actor)
                    WHERE id=$id AND deleted_at IS NULL AND status<>'VOID'",
                    ("$actor", ActorId > 0 ? ActorId : null), ("$id", invId));
                if (renId > 0)
                {
                    Exec(c, t, @"UPDATE renewals
                        SET is_locked=1,
                            locked_at=COALESCE(NULLIF(locked_at,''), datetime('now','+7 hours')),
                            locked_by=COALESCE(locked_by, $actor)
                        WHERE id=$rid AND deleted_at IS NULL",
                        ("$actor", ActorId > 0 ? ActorId : null), ("$rid", renId));
                }
                count++;
            }
            Log(c, t, "SETTLE_LOCK_BATCH", "FINANCE", 0, $"Tất toán & chốt số đưa công nợ về 0 cho {count} chứng từ");
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
                   + (SELECT COALESCE(SUM(il.quantity),0) FROM invoices i JOIN invoice_lines il ON il.invoice_id=i.id JOIN products p ON p.id=il.product_id JOIN customers cu ON cu.id=i.customer_id
                      WHERE i.deleted_at IS NULL AND il.deleted_at IS NULL AND i.invoice_type='SALE' AND i.v7_order_id IS NULL AND i.invoice_no NOT LIKE 'HD-KH%' AND p.is_serialized=1 AND i.status<>'VOID' AND cu.seller_id=s.id) AS dev_qty,
                   (SELECT COUNT(*) FROM renewals r JOIN activations a ON a.id=r.activation_id
                    WHERE r.deleted_at IS NULL AND a.deleted_at IS NULL
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
          i.amount_vnd,CASE WHEN i.status='PAID' THEN i.amount_vnd ELSE COALESCE(pa.paid,0)+COALESCE(i.offset_vnd,0) END,i.status,
          CASE
            WHEN length(trim(COALESCE(i.issued_at,''))) > 10 THEN replace(substr(trim(i.issued_at),1,19),'T',' ')
            WHEN o.completed_at IS NOT NULL AND substr(datetime(o.completed_at,'+7 hours'),1,10)=trim(i.issued_at)
              THEN datetime(o.completed_at,'+7 hours')
            WHEN i.created_at IS NOT NULL AND substr(datetime(replace(substr(i.created_at,1,19),'T',' '),'+7 hours'),1,10)=trim(i.issued_at)
              THEN datetime(replace(substr(i.created_at,1,19),'T',' '),'+7 hours')
            WHEN i.invoice_no LIKE 'HD-KH%' OR i.invoice_no LIKE 'CL-KT%'
              THEN trim(i.issued_at) || ' 23:59:59'
            ELSE trim(COALESCE(i.issued_at,'')) || ' 00:00:00'
          END AS resolved_issued_at,
          i.due_at,
          CASE
            WHEN i.renewal_id IS NOT NULL AND rp.id IS NOT NULL AND INSTR(COALESCE(i.notes,''), 'Thiết bị:') = 0
              THEN 'Thiết bị: ' || COALESCE(NULLIF(ra.device_type_label,''), CASE WHEN rp.sku='SP000001' THEN 'XANHGPS' WHEN rp.sku='DX00002' THEN 'NASA-4G-21000' WHEN rp.sku='DX00003' THEN 'TG2000' ELSE rp.name END, rp.sku)
                   || ' (IMEI: ' || COALESCE(ru.imei,'') || CASE WHEN COALESCE(ra.plate_no,'')<>'' THEN ' · Biển số: ' || ra.plate_no ELSE '' END || ') · ' || COALESCE(i.notes,'')
            ELSE COALESCE(i.notes,'')
          END,
          COALESCE(o.code,
            CASE WHEN i.renewal_id IS NOT NULL THEN 'GH#'||i.renewal_id || CASE WHEN rp.id IS NOT NULL THEN ' · ' || COALESCE(NULLIF(ra.device_type_label,''), CASE WHEN rp.sku='SP000001' THEN 'XANHGPS' WHEN rp.sku='DX00002' THEN 'NASA-4G-21000' WHEN rp.sku='DX00003' THEN 'TG2000' ELSE rp.name END, rp.sku) ELSE '' END ELSE '' END,
            ''),
          CASE WHEN i.is_locked=1 OR COALESCE(r.is_locked,0)=1 THEN 1 ELSE 0 END,
          COALESCE(i.locked_at, r.locked_at, ''),
          i.renewal_id
          FROM invoices i
          LEFT JOIN customers c ON c.id=i.customer_id
          LEFT JOIN suppliers s ON s.id=i.supplier_id
          LEFT JOIN v7_orders o ON o.id=i.v7_order_id
          LEFT JOIN renewals r ON r.id=i.renewal_id AND r.deleted_at IS NULL
          LEFT JOIN activations ra ON ra.id=r.activation_id
          LEFT JOIN stock_units ru ON ru.id=ra.stock_unit_id
          LEFT JOIN products rp ON rp.id=ra.product_id
          LEFT JOIN (SELECT invoice_id,SUM(allocated_vnd) AS paid FROM payment_allocations WHERE deleted_at IS NULL GROUP BY invoice_id) pa ON pa.invoice_id=i.id
          WHERE i.deleted_at IS NULL
          ORDER BY resolved_issued_at DESC, i.id DESC",
          r => {
              var id = L(r,0);
              var lines = allLines.TryGetValue(id, out var list) ? list : [];
              return new InvoiceVm(id,S(r,1),S(r,2),S(r,3),L(r,4),L(r,5),S(r,6),S(r,7),S(r,8),S(r,9),S(r,10),lines,L(r,11)==1,S(r,12),r.IsDBNull(13)?null:L(r,13));
          });
    }

    public List<PaymentRowVm> Payments()
    {
        using var c = Open();
        var raw = Query(c, @"SELECT p.id,p.payment_no,p.direction,p.method,COALESCE(pa.allocated_vnd, p.amount_vnd),
          COALESCE(i.invoice_no, NULLIF(p.reference_doc,''), ec.code, '—'),
          COALESCE(i.invoice_type,
            CASE
              WHEN p.direction='IN' THEN 'SALE'
              WHEN ec.code='CHI_NCC' OR p.supplier_id IS NOT NULL THEN 'PURCHASE'
              WHEN ec.code='CHI_CONG_LAP' THEN 'SERVICE'
              WHEN ec.code='CHI_HOA_HONG' THEN 'COMMISSION'
              ELSE 'EXPENSE'
            END),
          CASE
            WHEN COALESCE(p.payee_name,'')<>'' THEN p.payee_name
            WHEN c.id IS NOT NULL THEN c.name
            WHEN s.id IS NOT NULL THEN s.name
            WHEN ps.id IS NOT NULL THEN ps.name
            WHEN pc.id IS NOT NULL THEN pc.name
            WHEN psl.id IS NOT NULL THEN psl.code || ' — ' || psl.full_name
            WHEN pe.id IS NOT NULL THEN pe.full_name
            WHEN COALESCE(o.installer,'')<>'' THEN o.installer
            WHEN COALESCE(i.notes,'')<>'' THEN CASE WHEN INSTR(i.notes, ' — ') > 0 THEN SUBSTR(i.notes, 1, INSTR(i.notes, ' — ') - 1) ELSE i.notes END
            WHEN COALESCE(p.notes,'')<>'' THEN CASE WHEN INSTR(p.notes, ' — ') > 0 THEN SUBSTR(p.notes, 1, INSTR(p.notes, ' — ') - 1) ELSE p.notes END
            ELSE '-'
          END,
          CASE
            WHEN length(trim(COALESCE(p.paid_at,''))) > 10 THEN replace(substr(trim(p.paid_at),1,19),'T',' ')
            WHEN p.created_at IS NOT NULL AND substr(datetime(replace(substr(p.created_at,1,19),'T',' '),'+7 hours'),1,10)=trim(p.paid_at)
              THEN datetime(replace(substr(p.created_at,1,19),'T',' '),'+7 hours')
            ELSE trim(COALESCE(p.paid_at,'')) || ' 00:00:00'
          END AS resolved_paid_at,
          COALESCE(NULLIF(p.notes,''), COALESCE(i.notes,'')),
          i.id,
          CASE WHEN COALESCE(p.is_locked,0)=1 OR COALESCE(i.is_locked,0)=1 THEN 1 ELSE 0 END,
          ec.id,
          COALESCE(ec.name, '')
          FROM payments p
          LEFT JOIN payment_allocations pa ON pa.payment_id=p.id AND pa.deleted_at IS NULL
          LEFT JOIN invoices i ON i.id=pa.invoice_id AND i.deleted_at IS NULL
          LEFT JOIN customers c ON c.id=i.customer_id
          LEFT JOIN suppliers s ON s.id=i.supplier_id
          LEFT JOIN v7_orders o ON o.id=COALESCE(i.v7_order_id, p.v7_order_id)
          LEFT JOIN expense_categories ec ON ec.id=p.expense_category_id
          LEFT JOIN suppliers ps ON ps.id=p.supplier_id
          LEFT JOIN customers pc ON pc.id=p.customer_id
          LEFT JOIN v7_sellers psl ON psl.id=p.seller_id
          LEFT JOIN employees pe ON pe.id=p.employee_id
          WHERE p.deleted_at IS NULL
          ORDER BY resolved_paid_at ASC, p.id ASC, COALESCE(pa.id,0) ASC",
          r => (Id: L(r,0), No: S(r,1), Dir: S(r,2), Method: S(r,3), Amt: L(r,4), InvNo: S(r,5), InvType: S(r,6), Partner: S(r,7), PaidAt: S(r,8), Notes: S(r,9), InvId: r.IsDBNull(10)?(long?)null:L(r,10), Locked: L(r,11)==1, CatId: r.IsDBNull(12)?(long?)null:L(r,12), CatName: S(r,13)));

        long runningCash = 0, runningBank = 0, runningTotal = 0;
        var result = new List<PaymentRowVm>(raw.Count);
        foreach (var row in raw)
        {
            var signed = row.Dir == "IN" ? row.Amt : -row.Amt;
            runningTotal += signed;
            if (row.Method == "CASH") runningCash += signed;
            else runningBank += signed;
            var methodBal = row.Method == "CASH" ? runningCash : runningBank;
            result.Add(new PaymentRowVm(row.Id, row.No, row.Dir, row.Method, row.Amt, row.InvNo, row.InvType, row.Partner, row.PaidAt, row.Notes, methodBal, runningTotal, row.InvId, row.Locked, row.CatId, row.CatName));
        }
        result.Reverse();
        return result;
    }

    public CashBookSummaryVm CashBookSummary() => ComputeCashBookSummary(Payments());

    public static CashBookSummaryVm ComputeCashBookSummary(IEnumerable<PaymentRowVm> sourceRows)
    {
        var rows = sourceRows as IReadOnlyList<PaymentRowVm> ?? sourceRows.ToList();
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

    public List<PartnerDebtSummaryVm> PartnerDebtSummaries(DateTime? from = null, DateTime? to = null)
    {
        var allValidInvs = Invoices().Where(x => x.Status != "VOID").ToList();
        var invs = (from.HasValue || to.HasValue)
            ? allValidInvs.Where(x => x.MatchesRange(from, to)).ToList()
            : allValidInvs;
        var sellers = SellerPeriodStats(from, to);
        var list = new List<PartnerDebtSummaryVm>();

        static DateTime ResolveCarryCutoff(string periodKey, DateTime? rangeFrom)
        {
            var pStart = DateTime.MinValue;
            if (!string.IsNullOrWhiteSpace(periodKey) && periodKey.Length >= 7 &&
                int.TryParse(periodKey[..4], out var py) && int.TryParse(periodKey[5..7], out var pm) && pm is >= 1 and <= 12)
            {
                pStart = new DateTime(py, pm, 1, 0, 0, 0);
            }
            return rangeFrom.HasValue && rangeFrom.Value > pStart ? rangeFrom.Value : pStart;
        }

        // 1. Công nợ Nhà cung cấp (Nhập hàng) — tách riêng theo từng Kỳ Tháng/Năm
        var allSupInvs = allValidInvs.Where(x => x.Type is "PURCHASE" or "SUPPLIER").ToList();
        var supInvs = invs.Where(x => x.Type is "PURCHASE" or "SUPPLIER").ToList();
        foreach (var g in supInvs
            .GroupBy(x => (x.PeriodKey, x.PeriodLabel, Partner: x.Customer))
            .OrderByDescending(g => g.Key.PeriodKey)
            .ThenBy(g => g.Key.Partner))
        {
            var cutoff = ResolveCarryCutoff(g.Key.PeriodKey, from);
            var carryOld = allSupInvs
                .Where(x => string.Equals(x.Customer, g.Key.Partner, StringComparison.OrdinalIgnoreCase)
                         && x.TimestampDt.HasValue && x.TimestampDt.Value < cutoff)
                .Sum(x => x.Balance);
            list.Add(new PartnerDebtSummaryVm(
                "SUPPLIER",
                "1. Công nợ Nhà cung cấp (Nhập hàng)",
                "NCC",
                g.Key.Partner,
                $"Kỳ {g.Key.PeriodLabel} · Nhập hàng & thiết bị từ NCC",
                g.Count(),
                g.Sum(x => x.Total),
                g.Sum(x => x.Paid),
                g.Sum(x => x.Balance),
                g.Key.PeriodKey,
                g.Key.PeriodLabel,
                carryOld));
        }

        // 2. Công nợ Khách hàng (Đơn lắp đặt & Gia hạn) — tách riêng theo từng Kỳ Tháng/Năm + tính Nợ cũ chuyển sang
        var allCustInvs = allValidInvs.Where(x => x.Type is "SALE" or "RENEWAL").ToList();
        var custInvs = invs.Where(x => x.Type is "SALE" or "RENEWAL").ToList();
        foreach (var g in custInvs
            .GroupBy(x => (x.PeriodKey, x.PeriodLabel, Partner: x.Customer))
            .OrderByDescending(g => g.Key.PeriodKey)
            .ThenBy(g => g.Key.Partner))
        {
            var saleCount = g.Count(x => x.Type == "SALE");
            var renCount = g.Count(x => x.Type == "RENEWAL");
            var cutoff = ResolveCarryCutoff(g.Key.PeriodKey, from);
            var carryOld = allCustInvs
                .Where(x => string.Equals(x.Customer, g.Key.Partner, StringComparison.OrdinalIgnoreCase)
                         && x.TimestampDt.HasValue && x.TimestampDt.Value < cutoff)
                .Sum(x => x.Balance);
            list.Add(new PartnerDebtSummaryVm(
                "CUSTOMER",
                "2. Công nợ Khách hàng (Đơn lắp & Gia hạn)",
                "KH",
                g.Key.Partner,
                $"Kỳ {g.Key.PeriodLabel} · Đơn/Bảng kê: {saleCount} · Gia hạn: {renCount}",
                g.Count(),
                g.Sum(x => x.Total),
                g.Sum(x => x.Paid),
                g.Sum(x => x.Balance),
                g.Key.PeriodKey,
                g.Key.PeriodLabel,
                carryOld));
        }

        // 3. Công nợ Sell / Nhân viên bán hàng (Hoa hồng phát sinh vs Đã thanh toán)
        var commInvoices = invs.Where(x => x.Type == "COMMISSION").ToList();
        var refDt = to ?? from ?? VnDateTimeHelper.TodayVn;
        var curYm = refDt.ToString("yyyy-MM");
        var curYmLabel = $"Tháng {refDt.Month:D2}/{refDt.Year}";
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
            var pKey = matchedComm.FirstOrDefault()?.PeriodKey ?? curYm;
            var pLabel = matchedComm.FirstOrDefault()?.PeriodLabel ?? curYmLabel;

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
                    remaining,
                    pKey,
                    pLabel,
                    0));
            }
        }

        // 4. Công nợ Kỹ thuật lắp đặt (Phụ cấp / Công lắp) — tách riêng theo từng Kỳ Tháng/Năm
        var allSrvInvs = allValidInvs.Where(x => x.Type == "SERVICE").ToList();
        var srvInvs = invs.Where(x => x.Type == "SERVICE").ToList();
        foreach (var g in srvInvs
            .GroupBy(x => (x.PeriodKey, x.PeriodLabel, Partner: x.Customer))
            .OrderByDescending(g => g.Key.PeriodKey)
            .ThenBy(g => g.Key.Partner))
        {
            var cutoff = ResolveCarryCutoff(g.Key.PeriodKey, from);
            var carryOld = allSrvInvs
                .Where(x => string.Equals(x.Customer, g.Key.Partner, StringComparison.OrdinalIgnoreCase)
                         && x.TimestampDt.HasValue && x.TimestampDt.Value < cutoff)
                .Sum(x => x.Balance);
            list.Add(new PartnerDebtSummaryVm(
                "TECH",
                "4. Công nợ Kỹ thuật lắp đặt",
                "KT",
                g.Key.Partner,
                $"Kỳ {g.Key.PeriodLabel} · Phụ cấp & công lắp đặt thiết bị",
                g.Count(),
                g.Sum(x => x.Total),
                g.Sum(x => x.Paid),
                g.Sum(x => x.Balance),
                g.Key.PeriodKey,
                g.Key.PeriodLabel,
                carryOld));
        }

        return list
            .OrderByDescending(x => x.PeriodKey)
            .ThenBy(x => x.CategoryCode switch { "CUSTOMER" => 1, "SUPPLIER" => 2, "SELLER" => 3, _ => 4 })
            .ThenBy(x => x.PartnerName)
            .ToList();
    }

    public long RecordDirectCashBookEntry(string direction, string method, string category, long amount, string partnerName, string note, long? invoiceId = null, long? categoryId = null)
    {
        if (direction is not ("IN" or "OUT"))
            throw new InvalidOperationException("Loại phiếu Sổ quỹ phải là Khoản Thu hoặc Khoản Chi.");
        if (method is not ("CASH" or "BANK"))
            throw new InvalidOperationException("Quỹ thanh toán phải là Tiền mặt hoặc Ngân hàng.");
        if (amount <= 0)
            throw new InvalidOperationException("Số tiền phải lớn hơn 0.");

        if (invoiceId is > 0)
        {
            PayInvoice(invoiceId.Value, amount, method, categoryId);
            return invoiceId.Value;
        }

        return Tx((c, t) =>
        {
            var prefix = direction == "IN" ? "PT-" : "PC-";
            var no = prefix + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            long? expCatId = null;
            string catLabel = "";
            if (categoryId is > 0)
            {
                var catInfo = QueryTx(c, t, "SELECT id, name FROM expense_categories WHERE id=$id AND deleted_at IS NULL",
                    r => (Id: L(r, 0), Name: S(r, 1)), ("$id", categoryId.Value)).SingleOrDefault();
                if (catInfo.Id > 0)
                {
                    expCatId = catInfo.Id;
                    catLabel = catInfo.Name;
                }
            }
            if (expCatId is null)
            {
                var catCode = direction == "IN"
                    ? (category == "RENEWAL_DEBT" ? "THU_GIA_HAN" : category == "CUSTOMER_DEBT" ? "THU_DON_LAP" : "THU_KHAC")
                    : category switch
                    {
                        "PURCHASE" or "SUPPLIER_PURCHASE" => "CHI_NCC",
                        "SELLER_COMMISSION" or "COMMISSION" => "CHI_HOA_HONG",
                        "TECH_SERVICE" or "SERVICE" => "CHI_CONG_LAP",
                        _ => "CHI_KHAC"
                    };
                var catInfo = QueryTx(c, t, "SELECT id, name FROM expense_categories WHERE code=$c AND deleted_at IS NULL LIMIT 1",
                    r => (Id: L(r, 0), Name: S(r, 1)), ("$c", catCode)).SingleOrDefault();
                if (catInfo.Id > 0)
                {
                    expCatId = catInfo.Id;
                    catLabel = catInfo.Name;
                }
            }
            if (string.IsNullOrWhiteSpace(catLabel))
                catLabel = direction == "IN" ? "Thu trực tiếp Sổ quỹ" : "Chi trực tiếp Sổ quỹ";

            var cleanPartner = string.IsNullOrWhiteSpace(partnerName) ? "Đối tác" : partnerName.Trim();
            var fullNote = string.IsNullOrWhiteSpace(note) ? $"{catLabel} — {cleanPartner}" : note.Trim();
            Exec(c, t, "INSERT INTO payments(payment_no,direction,paid_at,method,amount_vnd,notes,expense_category_id,payee_name) VALUES($n,$d,datetime('now','+7 hours'),$m,$a,$note,$cat,$payee)",
                ("$n", no), ("$d", direction), ("$m", method), ("$a", amount), ("$note", fullNote), ("$cat", expCatId), ("$payee", cleanPartner));
            var pid = N(c, t, "SELECT last_insert_rowid()");
            Log(c, t, "CASHBOOK", "PAYMENT", pid, $"{no} ({direction}/{method}): {amount:N0} VND - {fullNote}");
            return pid;
        });
    }

    public long CreateCustomInvoice(string invoiceType, long? customerId, long? supplierId, string partnerLabel, string description, long? productId, long quantity, long unitPriceVnd, string dueAt, string? issuedAt = null)
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
        string issued;
        if (string.IsNullOrWhiteSpace(issuedAt))
        {
            issued = VnDateTimeHelper.NowSql();
        }
        else if (VnDateTimeHelper.TryParseUserFilterInput(issuedAt, false, out var parsedIssued, out _))
        {
            // Nếu chỉ nhập ngày (00:00:00) thì gắn giờ thực tế hiện tại giờ VN
            var rawTrim = issuedAt.Trim();
            if (rawTrim.Length <= 10)
                parsedIssued = parsedIssued.Date + VnDateTimeHelper.NowVn.TimeOfDay;
            issued = parsedIssued.ToString(VnDateTimeHelper.SqlFormat, CultureInfo.InvariantCulture);
        }
        else
        {
            issued = issuedAt.Trim();
        }
        var due = string.IsNullOrWhiteSpace(dueAt) ? VnDateTimeHelper.TodayVn.AddDays(15).ToString("yyyy-MM-dd") : dueAt.Trim();
        var noteText = string.IsNullOrWhiteSpace(partnerLabel) ? description.Trim() : $"{partnerLabel.Trim()} — {description.Trim()}";

        return Tx((c,t) => {
            var invNo = prefix + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            Exec(c,t,@"INSERT INTO invoices(invoice_no,invoice_type,customer_id,supplier_id,issued_at,due_at,amount_vnd,notes)
                VALUES($no,$type,$cust,$sup,$issued,$due,$amt,$note)",
                ("$no",invNo),("$type",invoiceType),
                ("$cust",customerId is > 0 ? customerId : null),
                ("$sup",supplierId is > 0 ? supplierId : null),
                ("$issued",issued),("$due",due),("$amt",total),("$note",noteText));
            var invId = N(c,t,"SELECT last_insert_rowid()");
            Exec(c,t,"INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$d,$p,$q,$u)",
                ("$i",invId),("$d",description.Trim()),("$p",productId is > 0 ? productId : null),("$q",quantity),("$u",unitPriceVnd));
            Log(c,t,"CREATE","INVOICE",invId,$"{invNo} ({invoiceType}): {total:N0} VND");
            return invId;
        });
    }

    private static void InsertPayment(SqliteConnection c, SqliteTransaction t, long invoiceId, long amount, string method, long? categoryId = null)
    {
        var invMeta = QueryTx(c, t, @"SELECT i.invoice_type, i.invoice_no, i.supplier_id, i.customer_id, i.v7_order_id,
                COALESCE(cu.name, s.name, o.installer, CASE WHEN INSTR(i.notes,' — ')>0 THEN SUBSTR(i.notes,1,INSTR(i.notes,' — ')-1) ELSE i.notes END, ''),
                o.installer_employee_id
            FROM invoices i
            LEFT JOIN customers cu ON cu.id=i.customer_id
            LEFT JOIN suppliers s ON s.id=i.supplier_id
            LEFT JOIN v7_orders o ON o.id=i.v7_order_id
            WHERE i.id=$id",
            r => (Type: S(r, 0), No: S(r, 1), SupId: r.IsDBNull(2) ? (long?)null : L(r, 2), CustId: r.IsDBNull(3) ? (long?)null : L(r, 3), OrdId: r.IsDBNull(4) ? (long?)null : L(r, 4), Payee: S(r, 5), EmpId: r.IsDBNull(6) ? (long?)null : L(r, 6)),
            ("$id", invoiceId)).Single();
        var invoiceType = invMeta.Type;
        var direction = invoiceType is "SALE" or "RENEWAL" ? "IN" : "OUT";
        var prefix = direction == "IN" ? "PT-" : "PC-";
        var no = prefix + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        long? expCatId = categoryId is > 0 ? categoryId : null;
        if (expCatId is null)
        {
            var catCode = direction == "IN"
                ? (invoiceType == "RENEWAL" ? "THU_GIA_HAN" : "THU_DON_LAP")
                : invoiceType switch
                {
                    "PURCHASE" or "SUPPLIER" => "CHI_NCC",
                    "SERVICE" => "CHI_CONG_LAP",
                    "COMMISSION" => "CHI_HOA_HONG",
                    _ => "CHI_KHAC"
                };
            var cid = N(c, t, "SELECT COALESCE((SELECT id FROM expense_categories WHERE code=$c LIMIT 1), 0)", ("$c", catCode));
            if (cid > 0) expCatId = cid;
        }
        var payNote = direction == "OUT" ? $"Chi thanh toán chứng từ {invMeta.No}" : $"Thu thanh toán chứng từ {invMeta.No}";
        Exec(c, t, @"INSERT INTO payments(payment_no,direction,paid_at,method,amount_vnd,notes,expense_category_id,supplier_id,customer_id,v7_order_id,employee_id,payee_name,reference_doc)
            VALUES($n,$d,datetime('now','+7 hours'),$m,$a,$note,$cat,$sup,$cust,$ord,$emp,$payee,$ref)",
            ("$n", no), ("$d", direction), ("$m", method), ("$a", amount), ("$note", payNote),
            ("$cat", expCatId), ("$sup", invMeta.SupId), ("$cust", invMeta.CustId),
            ("$ord", invMeta.OrdId), ("$emp", direction == "OUT" ? invMeta.EmpId : null),
            ("$payee", invMeta.Payee), ("$ref", invMeta.No));
        var paymentId = N(c,t,"SELECT last_insert_rowid()");
        Exec(c,t,"INSERT INTO payment_allocations(payment_id,invoice_id,allocated_vnd) VALUES($p,$i,$a)",
          ("$p",paymentId),("$i",invoiceId),("$a",amount));
        var balance = N(c,t,@"SELECT i.amount_vnd - COALESCE(i.offset_vnd,0) - (SELECT COALESCE(SUM(allocated_vnd),0) FROM payment_allocations WHERE invoice_id=i.id AND deleted_at IS NULL)
              FROM invoices i WHERE i.id=$id",("$id",invoiceId));
        Exec(c,t,"UPDATE invoices SET status=$s WHERE id=$id",
           ("$s", balance <= 0 ? "PAID" : "PARTIAL"),("$id",invoiceId));
    }

    public void PayInvoice(long invoiceId, long amount, string method, long? categoryId = null)
    {
        if (amount <= 0 || !(new[]{"CASH","BANK"}).Contains(method)) throw new InvalidOperationException("Số tiền và phương thức không hợp lệ.");
        Tx((c,t) => {
            var inv = QueryTx(c,t,@"SELECT CASE WHEN i.status='PAID' THEN 0 ELSE i.amount_vnd-COALESCE(i.offset_vnd,0)-COALESCE((SELECT SUM(allocated_vnd) FROM payment_allocations WHERE invoice_id=i.id AND deleted_at IS NULL),0) END,i.status
              FROM invoices i WHERE i.id=$id AND i.deleted_at IS NULL", r => (Balance:L(r,0),Status:S(r,1)),("$id",invoiceId)).SingleOrDefault();
            if(inv.Balance < amount || inv.Status == "VOID" || inv.Status == "")
                throw new InvalidOperationException("Thanh toán vượt công nợ hoặc chứng từ không hợp lệ.");
            InsertPayment(c,t,invoiceId,amount,method,categoryId);
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

            // 0. Dọn các chứng từ công nợ PURCHASE phát sinh nhầm từ nguồn "Thiết bị hoàn trả - đã kích hoạt" (HOANTRA) & chuẩn hóa Tồn kho NCC
            CleanupReturnedDevicePurchaseInvoices(c, t);
            SyncSupplierStockDebtBaseline(c, t);
            CustomerDebtOct2026Seeder.EnsureSeeded(c, t, v => ActorId = v, () => ActorId);
            SupplierDebtNasaOct2026Seeder.EnsureSeeded(c, t, v => ActorId = v, () => ActorId);
            SupplierDebtReconcileNov2026Seeder.EnsureSeeded(c, t, v => ActorId = v, () => ActorId);

            // 1A. Đối soát đơn mua hàng PO đã nhận (RECEIVED) nhưng chưa có chứng từ PURCHASE (loại trừ nguồn Thiết bị hoàn trả)
            var missingPos = QueryTx(c, t, @"
                SELECT po.id, po.po_no, po.supplier_id
                FROM purchase_orders po
                JOIN suppliers s ON s.id=po.supplier_id
                LEFT JOIN supplier_groups sg ON sg.id=s.group_id
                WHERE po.status='RECEIVED' AND po.deleted_at IS NULL
                  AND UPPER(COALESCE(sg.code,''))<>'HOANTRA'
                  AND LOWER(COALESCE(s.name,'')) NOT LIKE '%hoàn trả%'
                  AND LOWER(COALESCE(sg.name,'')) NOT LIKE '%hoàn trả%'
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

            // 1B. Đối soát Tồn kho thực tế hiện tại (stock_units IN_STOCK & quantity_movements tồn kho > 0) chưa có chứng từ PURCHASE
            // Tuyệt đối KHÔNG lấy thiết bị INSTALLED từ danh sách khách hàng và KHÔNG lấy Thiết bị hoàn trả (HOANTRA)
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
                LEFT JOIN supplier_groups sg ON sg.id=s.group_id
                JOIN products p ON p.id=u.product_id
                WHERE u.deleted_at IS NULL AND p.deleted_at IS NULL AND s.deleted_at IS NULL AND u.supplier_id IS NOT NULL
                  AND u.status IN ('IN_STOCK','CONSIGNED','TECHNICIAN')
                  AND UPPER(COALESCE(sg.code,''))<>'HOANTRA'
                  AND LOWER(COALESCE(s.name,'')) NOT LIKE '%hoàn trả%'
                  AND LOWER(COALESCE(sg.name,'')) NOT LIKE '%hoàn trả%'
                GROUP BY u.supplier_id, u.product_id",
                r => (SupId: L(r, 0), ProdId: L(r, 1), Label: S(r, 2), StockQty: L(r, 3), UnitCost: L(r, 4)));

            foreach (var prodGroup in stockBySupProd.GroupBy(x => x.ProdId))
            {
                var totalStockForProd = prodGroup.Sum(x => x.StockQty);
                var totalInvoicedForProd = N(c, t, @"
                    SELECT COALESCE(SUM(il.quantity), 0)
                    FROM invoices i JOIN invoice_lines il ON il.invoice_id=i.id
                    WHERE i.invoice_type IN ('PURCHASE','SUPPLIER')
                      AND i.invoice_no NOT LIKE 'NK-NCC%-T__'
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
                          AND i.invoice_no NOT LIKE 'NK-NCC%-T__'
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
                        list.Add((row.ProdId, $"Tồn kho IMEI: {row.Label}", missingQty, row.UnitCost));
                        remainingForProd -= missingQty;
                    }
                }
            }

            var qtyReceipts = QueryTx(c, t, @"
                SELECT q.product_id, p.sku||' — '||p.name, COALESCE(SUM(q.delta_qty), 0) AS total_stock_qty,
                       COALESCE(
                           (SELECT s.id FROM suppliers s LEFT JOIN supplier_groups sg ON sg.id=s.group_id JOIN v7_supplier_group_prices gp ON gp.supplier_group_id=s.group_id WHERE gp.product_id=q.product_id AND s.deleted_at IS NULL AND gp.deleted_at IS NULL AND UPPER(COALESCE(sg.code,''))<>'HOANTRA' AND LOWER(COALESCE(s.name,'')) NOT LIKE '%hoàn trả%' AND LOWER(COALESCE(sg.name,'')) NOT LIKE '%hoàn trả%' ORDER BY gp.effective_from DESC, s.id LIMIT 1),
                           (SELECT s.id FROM suppliers s LEFT JOIN supplier_groups sg ON sg.id=s.group_id WHERE s.deleted_at IS NULL AND UPPER(COALESCE(sg.code,''))<>'HOANTRA' AND LOWER(COALESCE(s.name,'')) NOT LIKE '%hoàn trả%' AND LOWER(COALESCE(sg.name,'')) NOT LIKE '%hoàn trả%' ORDER BY s.id LIMIT 1),
                           0
                       ) AS resolved_sup_id,
                       COALESCE(
                           NULLIF((SELECT gp.unit_cost_vnd FROM v7_supplier_group_prices gp WHERE gp.product_id=q.product_id AND gp.deleted_at IS NULL ORDER BY gp.effective_from DESC, gp.id DESC LIMIT 1), 0),
                           p.base_cost_vnd,
                           0
                       ) AS unit_cost
                FROM quantity_movements q
                JOIN products p ON p.id=q.product_id
                JOIN warehouses w ON w.id=q.warehouse_id
                JOIN warehouse_locations l ON l.id=q.location_id
                WHERE q.deleted_at IS NULL AND p.deleted_at IS NULL AND w.deleted_at IS NULL AND p.is_serialized=0
                GROUP BY q.product_id
                HAVING COALESCE(SUM(q.delta_qty), 0) > 0",
                r => (ProdId: L(r, 0), Label: S(r, 1), InQty: L(r, 2), SupId: L(r, 3), UnitCost: L(r, 4)));

            foreach (var row in qtyReceipts)
            {
                if (row.SupId <= 0 || row.UnitCost <= 0) continue;
                var invoicedQty = N(c, t, @"
                    SELECT COALESCE(SUM(il.quantity), 0)
                    FROM invoices i JOIN invoice_lines il ON il.invoice_id=i.id
                    WHERE i.invoice_type IN ('PURCHASE','SUPPLIER')
                      AND i.invoice_no NOT LIKE 'NK-NCC%-T__'
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
                    list.Add((row.ProdId, $"Tồn kho hàng hóa: {row.Label}", missingQty, row.UnitCost));
                }
            }

            foreach (var (supId, lines) in pendingPurchaseBySup)
            {
                var totalAmt = lines.Sum(x => x.Qty * x.UnitCost);
                if (totalAmt <= 0) continue;
                var supCode = QueryTx(c, t, "SELECT code||' — '||name FROM suppliers WHERE id=$id", r => S(r, 0), ("$id", supId)).FirstOrDefault() ?? $"NCC#{supId}";
                var invNo = "NK-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                var note = $"Công nợ Nhà cung cấp theo Tồn kho bổ sung ({supCode} · {lines.Sum(x => x.Qty):N0} SP)";
                Exec(c, t, @"INSERT INTO invoices(invoice_no,invoice_type,supplier_id,issued_at,due_at,amount_vnd,notes)
                    VALUES($no,'PURCHASE',$sup,datetime('now','+7 hours'),date('now','+7 hours','+30 days'),$amt,$note)",
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
                SELECT o.id, o.code, o.customer_id, o.unit_price_vnd, COALESCE(o.installer_fee_vnd,0), COALESCE(o.installer,''),
                       COALESCE(NULLIF(substr(o.completed_at,1,10),''), NULLIF(substr(o.created_at,1,10),''), date('now')),
                       COALESCE(o.no_install_fee,0)
                FROM v7_orders o
                WHERE o.status='COMPLETED' AND o.deleted_at IS NULL",
                r => (Id: L(r, 0), Code: S(r, 1), CustId: L(r, 2), TotalPrice: L(r, 3), Fee: L(r, 4), Installer: S(r, 5), IssuedDate: S(r, 6), NoInst: L(r, 7) == 1));

            foreach (var ord in completedOrders)
            {
                if (ord.TotalPrice > 0 && N(c, t, "SELECT COUNT(*) FROM invoices WHERE v7_order_id=$o AND invoice_type='SALE' AND deleted_at IS NULL AND status<>'VOID'", ("$o", ord.Id)) == 0)
                {
                    var invNo = "HD-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                    var ordNote = ord.NoInst ? $"Đơn hàng {ord.Code} (Không tính công lắp — Giá chưa lắp)" : $"Đơn hàng lắp đặt {ord.Code}";
                    var ordDescPrefix = ord.NoInst ? "Thiết bị (Không tính công lắp)" : "Thiết bị & lắp đặt";
                    Exec(c, t, @"INSERT INTO invoices(invoice_no,invoice_type,customer_id,v7_order_id,issued_at,due_at,amount_vnd,notes)
                        VALUES($no,'SALE',$c,$o,$issued,date($issued,'+30 days'),$amt,$note)",
                        ("$no", invNo), ("$c", ord.CustId), ("$o", ord.Id), ("$issued", ord.IssuedDate), ("$amt", ord.TotalPrice), ("$note", ordNote));
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
                            ("$i", invId), ("$d", $"{ordDescPrefix}: {ln.Name}"), ("$p", ln.ProdId), ("$q", ln.Qty), ("$u", ln.Price));
                    }
                    saleCreated++;
                }

                if (!ord.NoInst && ord.Fee > 0 && N(c, t, "SELECT COUNT(*) FROM invoices WHERE v7_order_id=$o AND invoice_type='SERVICE' AND deleted_at IS NULL AND status<>'VOID'", ("$o", ord.Id)) == 0)
                {
                    var srvNo = "CL-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                    var partnerLabel = !string.IsNullOrWhiteSpace(ord.Installer) ? ord.Installer : "Nhân sự lắp đặt";
                    Exec(c, t, @"INSERT INTO invoices(invoice_no,invoice_type,v7_order_id,issued_at,due_at,amount_vnd,notes)
                        VALUES($no,'SERVICE',$o,$issued,date($issued,'+15 days'),$fee,$note)",
                        ("$no", srvNo), ("$o", ord.Id), ("$issued", ord.IssuedDate), ("$fee", ord.Fee), ("$note", $"{partnerLabel} — Công lắp đặt đơn {ord.Code}"));
                    var srvInvId = N(c, t, "SELECT last_insert_rowid()");
                    Exec(c, t, @"INSERT INTO invoice_lines(invoice_id,description,quantity,unit_price_vnd)
                        VALUES($i,$d,1,$fee)",
                        ("$i", srvInvId), ("$d", $"Công lắp đặt đơn {ord.Code}"), ("$fee", ord.Fee));
                    serviceCreated++;
                }
            }

            // 3. Đối soát lịch sử gia hạn (renewals) chưa có chứng từ RENEWAL
            var missingRenewals = QueryTx(c, t, @"
                SELECT r.id, a.customer_id, a.product_id, r.amount_vnd, r.payment_method, COALESCE(r.seller_name,''),
                       u.imei, COALESCE(a.plate_no,''),
                       COALESCE(NULLIF(a.device_type_label,''), CASE WHEN p.sku='SP000001' THEN 'XANHGPS' WHEN p.sku='DX00002' THEN 'NASA-4G-21000' WHEN p.sku='DX00003' THEN 'TG2000' ELSE p.name END, p.sku),
                       COALESCE(NULLIF(substr(r.renewed_at,1,10),''), date('now'))
                FROM renewals r
                JOIN activations a ON a.id=r.activation_id
                JOIN stock_units u ON u.id=a.stock_unit_id
                JOIN products p ON p.id=a.product_id
                WHERE r.deleted_at IS NULL AND a.deleted_at IS NULL AND r.amount_vnd > 0
                  AND NOT EXISTS (
                      SELECT 1 FROM invoices i
                      WHERE i.renewal_id=r.id AND i.invoice_type='RENEWAL' AND i.deleted_at IS NULL AND i.status<>'VOID'
                  )",
                r => (Id: L(r, 0), CustId: L(r, 1), ProdId: L(r, 2), Price: L(r, 3), PayMethod: S(r, 4), SellerName: S(r, 5), Imei: S(r, 6), PlateNo: S(r, 7), DeviceName: S(r, 8), RenewedDate: S(r, 9)));

            foreach (var ren in missingRenewals)
            {
                var invNo = "GH-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                var plateSeg = !string.IsNullOrWhiteSpace(ren.PlateNo) ? $" · Biển số: {ren.PlateNo}" : "";
                var sellerSeg = !string.IsNullOrWhiteSpace(ren.SellerName) ? $" · NV bán hàng: {ren.SellerName}" : "";
                var note = $"Gia hạn 12 tháng · Thiết bị: {ren.DeviceName} (IMEI: {ren.Imei}{plateSeg}){sellerSeg}";
                Exec(c, t, @"INSERT INTO invoices(invoice_no,invoice_type,customer_id,renewal_id,issued_at,due_at,amount_vnd,notes)
                    VALUES($no,'RENEWAL',$c,$r,$issued,date($issued,'+30 days'),$amt,$note)",
                    ("$no", invNo), ("$c", ren.CustId), ("$r", ren.Id), ("$issued", ren.RenewedDate), ("$amt", ren.Price), ("$note", note));
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
        var orderLinesMap = new Dictionary<long, List<OrderSnapshotLineVm>>();
        var rawOrderLines = Query(c, @"
            SELECT l.order_id, COALESCE(p.sku,''), COALESCE(p.name,''), l.quantity, l.unit_price_vnd,
                   COALESCE(l.imei,''), COALESCE(l.plate_no,''), COALESCE(l.vin_no,'')
            FROM v7_order_lines l
            JOIN products p ON p.id=l.product_id
            JOIN v7_orders o ON o.id=l.order_id
            WHERE l.deleted_at IS NULL AND o.deleted_at IS NULL AND o.status<>'CANCELLED'
            ORDER BY l.order_id, l.id",
            r => (OrderId: L(r, 0), Sku: S(r, 1), Name: S(r, 2), Qty: L(r, 3), Price: L(r, 4), Imei: S(r, 5), Plate: S(r, 6), Vin: S(r, 7)));

        foreach (var rl in rawOrderLines)
        {
            if (!orderLinesMap.TryGetValue(rl.OrderId, out var list))
            {
                list = [];
                orderLinesMap[rl.OrderId] = list;
            }
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(rl.Imei)) notes.Add($"IMEI: {rl.Imei}");
            if (!string.IsNullOrWhiteSpace(rl.Plate)) notes.Add($"BS: {rl.Plate}");
            if (!string.IsNullOrWhiteSpace(rl.Vin) && !string.Equals(rl.Vin, rl.Imei, StringComparison.OrdinalIgnoreCase)) notes.Add(rl.Vin);
            list.Add(new OrderSnapshotLineVm(rl.Sku, "", rl.Name, string.Join(" · ", notes), rl.Qty, rl.Price));
        }

        var orders = Query(c, @"
            SELECT o.id, o.code,
                   CASE
                     WHEN o.completed_at IS NOT NULL AND COALESCE(trim(o.activated_at),'')<>''
                       THEN trim(o.activated_at) || substr(datetime(o.completed_at,'+7 hours'),11,9)
                     WHEN o.completed_at IS NOT NULL
                       THEN datetime(o.completed_at,'+7 hours')
                     WHEN o.created_at IS NOT NULL
                       THEN datetime(o.created_at,'+7 hours')
                     ELSE ''
                   END AS resolved_completed_at,
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
                   o.status,
                   COALESCE(p.sku,''),
                   COALESCE(p.name,'')
            FROM v7_orders o
            JOIN customers cu ON cu.id = o.customer_id
            JOIN products p ON p.id = o.product_id
            LEFT JOIN v7_sellers sl ON sl.id = COALESCE(o.seller_record_id, cu.seller_id)
            WHERE o.status <> 'CANCELLED' AND o.deleted_at IS NULL
            ORDER BY CASE WHEN o.status='COMPLETED' THEN 0 ELSE 1 END, resolved_completed_at DESC, o.id DESC",
            r =>
            {
                var ordId = L(r, 0);
                var qty = L(r, 10);
                var totalRev = L(r, 11);
                var lines = orderLinesMap.TryGetValue(ordId, out var mapped) && mapped.Count > 0
                    ? mapped
                    : [new OrderSnapshotLineVm(S(r, 19), "", S(r, 20), "", qty > 0 ? qty : 1, qty > 0 ? totalRev / qty : totalRev)];
                return new CompletedOrderSnapshotVm(
                    ordId, S(r, 1), S(r, 2), S(r, 3), S(r, 4), S(r, 5), S(r, 6),
                    r.IsDBNull(7) ? null : L(r, 7),
                    S(r, 8), S(r, 9), qty, totalRev, L(r, 12), L(r, 13), S(r, 14), L(r, 15) == 1, S(r, 16), S(r, 17), S(r, 18), lines);
            });

        var batchLinesMap = new Dictionary<long, List<OrderSnapshotLineVm>>();
        var rawBatchLines = Query(c, @"
            SELECT il.invoice_id, COALESCE(p.sku,''), il.description, il.quantity, il.unit_price_vnd
            FROM invoice_lines il
            JOIN invoices i ON i.id=il.invoice_id
            LEFT JOIN products p ON p.id=il.product_id
            WHERE il.deleted_at IS NULL AND i.deleted_at IS NULL
              AND i.invoice_type='SALE' AND i.v7_order_id IS NULL AND i.status<>'VOID'
            ORDER BY il.invoice_id, il.id",
            r => (InvId: L(r, 0), Sku: S(r, 1), Desc: S(r, 2), Qty: L(r, 3), Price: L(r, 4)));

        foreach (var bl in rawBatchLines)
        {
            if (!batchLinesMap.TryGetValue(bl.InvId, out var list))
            {
                list = [];
                batchLinesMap[bl.InvId] = list;
            }
            list.Add(OrderSnapshotLineVm.FromDescription(bl.Sku, bl.Desc, bl.Qty, bl.Price));
        }

        // Bổ sung các chứng từ SALE thiết bị đang hoạt động của Khách hàng/HTX (không qua v7_orders) để Kế toán thống kê đầy đủ Snapshot
        var actBatches = Query(c, @"
            SELECT i.id, i.invoice_no,
                   CASE
                     WHEN length(trim(COALESCE(i.issued_at,''))) > 10 THEN replace(substr(trim(i.issued_at),1,19),'T',' ')
                     WHEN i.created_at IS NOT NULL AND substr(datetime(replace(substr(i.created_at,1,19),'T',' '),'+7 hours'),1,10)=trim(i.issued_at)
                       THEN datetime(replace(substr(i.created_at,1,19),'T',' '),'+7 hours')
                     WHEN i.invoice_no LIKE 'HD-KH%'
                       THEN trim(i.issued_at) || ' 23:59:59'
                     ELSE trim(COALESCE(i.issued_at,'')) || ' 00:00:00'
                   END AS resolved_issued_at,
                   COALESCE(substr(trim(i.issued_at),1,10), ''),
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
                   COALESCE((SELECT SUM(CASE WHEN il3.unit_price_vnd > 0 THEN il3.quantity * COALESCE(p3.base_cost_vnd, 0) ELSE 0 END) FROM invoice_lines il3 LEFT JOIN products p3 ON p3.id=il3.product_id WHERE il3.invoice_id=i.id AND il3.deleted_at IS NULL), 0),
                   0,
                   CASE WHEN i.invoice_no LIKE 'HD-KH%' THEN 'Bảng kê công nợ kỳ trước' ELSE 'Chứng từ bán hàng' END,
                   1,
                   i.invoice_no,
                   i.status,
                   COALESCE(i.notes,'')
            FROM invoices i
            JOIN customers cu ON cu.id = i.customer_id
            LEFT JOIN v7_sellers sl ON sl.id = cu.seller_id
            WHERE i.invoice_type = 'SALE' AND i.v7_order_id IS NULL AND i.status <> 'VOID' AND i.deleted_at IS NULL
            ORDER BY resolved_issued_at DESC, i.id DESC",
            r =>
            {
                var invId = L(r, 0);
                var qty = L(r, 10);
                var amt = L(r, 11);
                var lines = batchLinesMap.TryGetValue(invId, out var mapped) && mapped.Count > 0
                    ? mapped
                    : [OrderSnapshotLineVm.FromDescription("", S(r, 18), qty > 0 ? qty : 1, amt)];
                return new CompletedOrderSnapshotVm(
                    -invId, S(r, 1), S(r, 2), S(r, 3), S(r, 4), S(r, 5), S(r, 6),
                    r.IsDBNull(7) ? null : L(r, 7),
                    S(r, 8), S(r, 9), qty, amt, L(r, 12), L(r, 13), S(r, 14), L(r, 15) == 1, S(r, 16), S(r, 17), "ACTIVATION_BATCH", lines);
            });

        orders.AddRange(actBatches);
        return orders;
    }

    public List<SellerPeriodStatVm> SellerPeriodStats(DateTime? from = null, DateTime? to = null)
    {
        using var c = Open();
        string? fromSql = from?.ToString(VnDateTimeHelper.SqlFormat, CultureInfo.InvariantCulture);
        string? toSql = to?.ToString(VnDateTimeHelper.SqlFormat, CultureInfo.InvariantCulture);

        const string ordTs = "CASE WHEN o.completed_at IS NOT NULL AND COALESCE(trim(o.activated_at),'')<>'' THEN trim(o.activated_at)||substr(datetime(o.completed_at,'+7 hours'),11,9) WHEN o.completed_at IS NOT NULL THEN datetime(o.completed_at,'+7 hours') ELSE datetime(o.created_at,'+7 hours') END";
        const string invTs = "CASE WHEN length(trim(COALESCE(i.issued_at,'')))>10 THEN replace(substr(trim(i.issued_at),1,19),'T',' ') WHEN i.created_at IS NOT NULL AND substr(datetime(replace(substr(i.created_at,1,19),'T',' '),'+7 hours'),1,10)=trim(i.issued_at) THEN datetime(replace(substr(i.created_at,1,19),'T',' '),'+7 hours') ELSE trim(COALESCE(i.issued_at,''))||' 00:00:00' END";
        const string renTs = "CASE WHEN length(trim(COALESCE(r.renewed_at,'')))>10 THEN replace(substr(trim(r.renewed_at),1,19),'T',' ') WHEN r.created_at IS NOT NULL AND substr(datetime(replace(substr(r.created_at,1,19),'T',' '),'+7 hours'),1,10)=trim(r.renewed_at) THEN datetime(replace(substr(r.created_at,1,19),'T',' '),'+7 hours') ELSE trim(COALESCE(r.renewed_at,''))||' 00:00:00' END";

        var ordRangeCond = $"AND ($from IS NULL OR ({ordTs}) >= $from) AND ($to IS NULL OR ({ordTs}) <= $to)";
        var invRangeCond = $"AND ($from IS NULL OR ({invTs}) >= $from) AND ($to IS NULL OR ({invTs}) <= $to)";
        var renRangeCond = $"AND ($from IS NULL OR ({renTs}) >= $from) AND ($to IS NULL OR ({renTs}) <= $to)";

        return Query(c, $@"
            SELECT s.id, s.code, s.full_name, COALESCE(s.phone,''), COALESCE(s.department,'Kinh doanh / Sale'),
                   COALESCE(s.order_commission_vnd,0), COALESCE(s.renewal_commission_vnd,0),
                   (SELECT COUNT(*) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED' {ordRangeCond}
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%'))))
                   + (SELECT COUNT(*) FROM invoices i JOIN customers cu ON cu.id=i.customer_id
                      WHERE i.deleted_at IS NULL AND i.invoice_type='SALE' AND i.v7_order_id IS NULL AND i.invoice_no NOT LIKE 'HD-KH%' AND i.status<>'VOID' AND cu.seller_id=s.id {invRangeCond}),
                   (SELECT COALESCE(SUM(o.quantity),0) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED' {ordRangeCond}
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%'))))
                   + (SELECT COALESCE(SUM(il.quantity),0) FROM invoices i JOIN invoice_lines il ON il.invoice_id=i.id JOIN products p ON p.id=il.product_id JOIN customers cu ON cu.id=i.customer_id
                      WHERE i.deleted_at IS NULL AND il.deleted_at IS NULL AND i.invoice_type='SALE' AND i.v7_order_id IS NULL AND i.invoice_no NOT LIKE 'HD-KH%' AND p.is_serialized=1 AND i.status<>'VOID' AND cu.seller_id=s.id {invRangeCond}),
                   (SELECT COALESCE(SUM(o.unit_price_vnd),0) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED' {ordRangeCond}
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%'))))
                   + (SELECT COALESCE(SUM(i.amount_vnd),0) FROM invoices i JOIN customers cu ON cu.id=i.customer_id
                      WHERE i.deleted_at IS NULL AND i.invoice_type='SALE' AND i.v7_order_id IS NULL AND i.invoice_no NOT LIKE 'HD-KH%' AND i.status<>'VOID' AND cu.seller_id=s.id {invRangeCond}),
                   (SELECT COALESCE(SUM(o.unit_cost_snapshot_vnd),0) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED' {ordRangeCond}
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%'))))
                   + (SELECT COALESCE(SUM(il.quantity * COALESCE(p.base_cost_vnd,0)),0) FROM invoices i JOIN invoice_lines il ON il.invoice_id=i.id LEFT JOIN products p ON p.id=il.product_id JOIN customers cu ON cu.id=i.customer_id
                      WHERE i.deleted_at IS NULL AND il.deleted_at IS NULL AND i.invoice_type='SALE' AND i.v7_order_id IS NULL AND i.invoice_no NOT LIKE 'HD-KH%' AND i.status<>'VOID' AND cu.seller_id=s.id {invRangeCond}),
                   (SELECT COALESCE(SUM(COALESCE(o.installer_fee_vnd,0)),0) FROM v7_orders o LEFT JOIN customers cu ON cu.id=o.customer_id
                    WHERE o.deleted_at IS NULL AND o.status='COMPLETED' {ordRangeCond}
                      AND (COALESCE(o.seller_record_id, cu.seller_id)=s.id OR (o.seller_record_id IS NULL AND (o.sale_source LIKE '%'||s.code||'%' OR o.sale_source LIKE '%'||s.full_name||'%')))),
                   (SELECT COUNT(*) FROM renewals r JOIN activations a ON a.id=r.activation_id
                    WHERE r.deleted_at IS NULL AND a.deleted_at IS NULL {renRangeCond}
                      AND (r.seller_id=s.id OR (r.seller_id IS NULL AND (r.seller_name LIKE '%'||s.code||'%' OR r.seller_name LIKE '%'||s.full_name||'%')))),
                   (SELECT COALESCE(SUM(r.amount_vnd),0) FROM renewals r JOIN activations a ON a.id=r.activation_id
                    WHERE r.deleted_at IS NULL AND a.deleted_at IS NULL {renRangeCond}
                      AND (r.seller_id=s.id OR (r.seller_id IS NULL AND (r.seller_name LIKE '%'||s.code||'%' OR r.seller_name LIKE '%'||s.full_name||'%')))),
                   (SELECT COALESCE(SUM(i.amount_vnd),0) FROM invoices i
                    WHERE i.deleted_at IS NULL AND i.invoice_type='COMMISSION' AND i.status<>'VOID' {invRangeCond}
                      AND (i.notes LIKE '%'||s.code||'%' OR i.notes LIKE '%'||s.full_name||'%'))
            FROM v7_sellers s
            WHERE s.deleted_at IS NULL
            ORDER BY s.id ASC",
            r => new SellerPeriodStatVm(
                L(r,0), S(r,1), S(r,2), S(r,3), S(r,4), L(r,5), L(r,6),
                L(r,7), L(r,8), L(r,9), L(r,10), L(r,11), L(r,12), L(r,13), L(r,14)),
            ("$from", fromSql), ("$to", toSql));
    }

    public bool IsMainDeveloperActor(long actorId)
    {
        if (actorId <= 0) return false;
        using var c = Open();
        return IsMainDeveloper(c, null, actorId);
    }

    public List<(long Id, string Code, string Label)> OrderRefChoices()
    {
        using var c = Open();
        return Query(c, @"
            SELECT o.id, o.code, o.code || ' — ' || cu.name || ' (' || o.quantity || ' SP · ' || COALESCE(o.installer, 'Chưa ĐP') || ')'
            FROM v7_orders o
            JOIN customers cu ON cu.id=o.customer_id
            WHERE o.deleted_at IS NULL AND o.status<>'CANCELLED'
            ORDER BY o.id DESC LIMIT 200",
            r => (L(r, 0), S(r, 1), S(r, 2)));
    }

    public List<ExpenseCategoryVm> ExpenseCategories(bool includeInactive = false, string? direction = null)
    {
        using var c = Open();
        var activeCond = includeInactive ? "" : "AND ec.is_active=1";
        var dirCond = direction is "IN" or "OUT" ? "AND COALESCE(ec.direction,'OUT')=$dir" : "";
        return Query(c, $@"
            SELECT ec.id, ec.code, ec.name,
                   COALESCE(ec.group_name, CASE WHEN COALESCE(ec.direction,'OUT')='IN' THEN '4. Thu tài chính & Khác' ELSE '5. Chi phí Khác' END),
                   COALESCE(ec.target_entity,'FREE'), COALESCE(ec.default_note,''),
                   COALESCE(ec.sort_order,100), COALESCE(ec.is_system,0), COALESCE(ec.is_active,1),
                   (SELECT COUNT(*) FROM payments p WHERE p.deleted_at IS NULL AND p.expense_category_id=ec.id),
                   (SELECT COALESCE(SUM(p.amount_vnd),0) FROM payments p WHERE p.deleted_at IS NULL AND p.expense_category_id=ec.id),
                   COALESCE(ec.direction, 'OUT')
            FROM expense_categories ec
            WHERE ec.deleted_at IS NULL {activeCond} {dirCond}
            ORDER BY CASE WHEN COALESCE(ec.direction,'OUT')='IN' THEN 0 ELSE 1 END, ec.sort_order ASC, ec.id ASC",
            r => new ExpenseCategoryVm(
                L(r, 0), S(r, 1), S(r, 2), S(r, 3), S(r, 4), S(r, 5),
                (int)L(r, 6), L(r, 7) == 1, L(r, 8) == 1, L(r, 9), L(r, 10), S(r, 11)),
            ("$dir", direction ?? ""));
    }

    private static string SlugCashCategoryCode(string direction, string rawName)
    {
        var prefix = direction == "IN" ? "THU_" : "CHI_";
        var norm = (rawName ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(prefix);
        bool lastUnderscore = true;
        foreach (var ch in norm)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            var mapped = ch switch { 'đ' or 'Đ' => 'D', _ => char.ToUpperInvariant(ch) };
            if (char.IsLetterOrDigit(mapped) && mapped <= 127)
            {
                sb.Append(mapped);
                lastUnderscore = false;
            }
            else if (!lastUnderscore && sb.Length > 4)
            {
                sb.Append('_');
                lastUnderscore = true;
            }
            if (sb.Length >= 22) break;
        }
        var res = sb.ToString().TrimEnd('_');
        return res.Length <= 4 ? prefix + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant() : res;
    }

    public long SaveExpenseCategory(long? id, string? code, string name, string groupName, string targetEntity, string defaultNote, int sortOrder, bool isActive, string direction = "OUT")
    {
        var cleanDir = direction == "IN" ? "IN" : "OUT";
        var dirLabel = cleanDir == "IN" ? "khoản thu" : "khoản chi";
        var cleanName = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleanName))
            throw new InvalidOperationException($"Vui lòng nhập Tên {dirLabel}.");
        var defaultGrp = cleanDir == "IN" ? "4. Thu tài chính & Khác" : "5. Chi phí Khác";
        var cleanGroup = string.IsNullOrWhiteSpace(groupName) ? defaultGrp : groupName.Trim();
        var cleanTarget = targetEntity is "SUPPLIER" or "INSTALLER" or "SELLER" or "CUSTOMER" ? targetEntity : "FREE";
        var cleanNote = (defaultNote ?? "").Trim();
        var effSort = sortOrder > 0 ? sortOrder : 100;

        return Tx((c, t) =>
        {
            if (id is > 0)
            {
                var existing = QueryTx(c, t, "SELECT id, code, COALESCE(direction,'OUT'), is_system FROM expense_categories WHERE id=$id AND deleted_at IS NULL",
                    r => (Id: L(r, 0), Code: S(r, 1), Dir: S(r, 2), Sys: L(r, 3) == 1), ("$id", id.Value)).SingleOrDefault();
                if (existing.Id == 0) throw new InvalidOperationException("Không tìm thấy khoản thu/chi.");

                var nextDir = existing.Sys ? existing.Dir : cleanDir;
                var nextCode = existing.Sys || string.IsNullOrWhiteSpace(code)
                    ? existing.Code
                    : code.Trim().ToUpperInvariant().Replace(' ', '_');
                if (!string.Equals(nextCode, existing.Code, StringComparison.OrdinalIgnoreCase) &&
                    N(c, t, "SELECT COUNT(*) FROM expense_categories WHERE UPPER(code)=UPPER($c) AND id<>$id", ("$c", nextCode), ("$id", existing.Id)) > 0)
                {
                    throw new InvalidOperationException($"Mã khoản '{nextCode}' đã tồn tại.");
                }

                Exec(c, t, @"UPDATE expense_categories
                    SET code=$c, direction=$dir, name=$n, group_name=$g, target_entity=$t, default_note=$note, sort_order=$sort, is_active=$act
                    WHERE id=$id",
                    ("$c", nextCode), ("$dir", nextDir), ("$n", cleanName), ("$g", cleanGroup), ("$t", cleanTarget),
                    ("$note", cleanNote), ("$sort", effSort), ("$act", isActive ? 1 : 0), ("$id", existing.Id));
                Log(c, t, "SAVE_EXPENSE_CATEGORY", "EXPENSE_CATEGORY", existing.Id, $"Cập nhật {(nextDir == "IN" ? "khoản thu" : "khoản chi")} {nextCode} — {cleanName}");
                return existing.Id;
            }
            else
            {
                var newCode = string.IsNullOrWhiteSpace(code) ? SlugCashCategoryCode(cleanDir, cleanName) : code.Trim().ToUpperInvariant().Replace(' ', '_');
                var baseCode = newCode;
                int suffix = 2;
                while (N(c, t, "SELECT COUNT(*) FROM expense_categories WHERE UPPER(code)=UPPER($c)", ("$c", newCode)) > 0)
                {
                    newCode = $"{baseCode}_{suffix++}";
                }

                Exec(c, t, @"INSERT INTO expense_categories(code,direction,name,group_name,target_entity,default_note,sort_order,is_system,is_active)
                    VALUES($c,$dir,$n,$g,$t,$note,$sort,0,$act)",
                    ("$c", newCode), ("$dir", cleanDir), ("$n", cleanName), ("$g", cleanGroup), ("$t", cleanTarget),
                    ("$note", cleanNote), ("$sort", effSort), ("$act", isActive ? 1 : 0));
                var newId = N(c, t, "SELECT last_insert_rowid()");
                Log(c, t, "SAVE_EXPENSE_CATEGORY", "EXPENSE_CATEGORY", newId, $"Tạo mới {dirLabel} {newCode} — {cleanName}");
                return newId;
            }
        });
    }

    public string DeleteExpenseCategory(long id)
    {
        return Tx((c, t) =>
        {
            var cur = QueryTx(c, t, "SELECT id, code, name, is_system, COALESCE(direction,'OUT') FROM expense_categories WHERE id=$id AND deleted_at IS NULL",
                r => (Id: L(r, 0), Code: S(r, 1), Name: S(r, 2), Sys: L(r, 3) == 1, Dir: S(r, 4)), ("$id", id)).SingleOrDefault();
            if (cur.Id == 0) throw new InvalidOperationException("Không tìm thấy khoản thu/chi.");
            var dirLabel = cur.Dir == "IN" ? "khoản thu" : "khoản chi";

            var usedCount = N(c, t, "SELECT COUNT(*) FROM payments WHERE expense_category_id=$id AND deleted_at IS NULL", ("$id", id));
            if (usedCount > 0 || cur.Sys)
            {
                Exec(c, t, "UPDATE expense_categories SET is_active=0 WHERE id=$id", ("$id", id));
                Log(c, t, "SAVE_EXPENSE_CATEGORY", "EXPENSE_CATEGORY", id, $"Ngừng sử dụng {dirLabel} {cur.Code} — {cur.Name} (đã map {usedCount} phiếu)");
                return usedCount > 0
                    ? $"Khoản '{cur.Name}' đang map với {usedCount} phiếu Thu/Chi bên Sổ quỹ nên đã chuyển sang Ngừng sử dụng (ẩn khi lập phiếu mới, giữ nguyên lịch sử đã map)."
                    : $"Đã ẩn {dirLabel} mặc định '{cur.Name}' khỏi danh sách chọn khi lập phiếu (có thể bấm Sửa để bật lại bất kỳ lúc nào).";
            }

            if (IsMainDeveloper(c, t))
                Exec(c, t, "DELETE FROM expense_categories WHERE id=$id", ("$id", id));
            else
                Exec(c, t, "UPDATE expense_categories SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now'), is_active=0 WHERE id=$id", ("$id", id));

            Log(c, t, "SAVE_EXPENSE_CATEGORY", "EXPENSE_CATEGORY", id, $"Xóa {dirLabel} {cur.Code} — {cur.Name}");
            return $"Đã xóa {dirLabel} '{cur.Name}'.";
        });
    }

    public List<ExpenseVoucherVm> ExpenseVouchers(string? direction = null)
    {
        using var c = Open();
        var dirCond = direction is "IN" or "OUT" ? "AND p.direction=$dir" : "";
        return Query(c, $@"
            SELECT p.id,
                   p.payment_no,
                   CASE
                     WHEN length(trim(COALESCE(p.paid_at,''))) > 10 THEN replace(substr(trim(p.paid_at),1,19),'T',' ')
                     WHEN p.created_at IS NOT NULL AND substr(datetime(replace(substr(p.created_at,1,19),'T',' '),'+7 hours'),1,10)=trim(p.paid_at)
                       THEN datetime(replace(substr(p.created_at,1,19),'T',' '),'+7 hours')
                     ELSE trim(COALESCE(p.paid_at,'')) || ' 00:00:00'
                   END AS resolved_paid_at,
                   p.method,
                   p.amount_vnd,
                   ec.id,
                   COALESCE(ec.code, CASE WHEN p.direction='IN' THEN 'THU_KHAC' ELSE 'CHI_KHAC' END),
                   COALESCE(ec.name, CASE WHEN p.direction='IN' THEN 'Khoản thu khác' ELSE 'Khoản chi khác' END),
                   COALESCE(ec.group_name, CASE WHEN p.direction='IN' THEN '4. Thu tài chính & Khác' ELSE '5. Chi phí Khác' END),
                   CASE
                     WHEN COALESCE(p.payee_name,'')<>'' THEN p.payee_name
                     WHEN pc.id IS NOT NULL THEN pc.name
                     WHEN ic.id IS NOT NULL THEN ic.name
                     WHEN ps.id IS NOT NULL THEN ps.name
                     WHEN s.id IS NOT NULL THEN s.name
                     WHEN pe.id IS NOT NULL THEN pe.full_name
                     WHEN psl.id IS NOT NULL THEN psl.code || ' — ' || psl.full_name
                     WHEN COALESCE(o.installer,'')<>'' AND p.direction='OUT' THEN o.installer
                     WHEN COALESCE(i.notes,'')<>'' THEN CASE WHEN INSTR(i.notes,' — ')>0 THEN SUBSTR(i.notes,1,INSTR(i.notes,' — ')-1) ELSE i.notes END
                     ELSE '—'
                   END AS resolved_payee,
                   CASE
                     WHEN COALESCE(p.customer_id, i.customer_id) IS NOT NULL THEN 'KH/HTX: ' || COALESCE(pc.name, ic.name, '')
                     WHEN COALESCE(p.supplier_id, i.supplier_id) IS NOT NULL THEN 'NCC: ' || COALESCE(ps.name, s.name, '')
                     WHEN p.employee_id IS NOT NULL THEN 'Kỹ thuật/NS: ' || COALESCE(pe.full_name, '')
                     WHEN p.seller_id IS NOT NULL THEN 'NV bán hàng: ' || COALESCE(psl.code || ' — ' || psl.full_name, '')
                     WHEN COALESCE(p.v7_order_id, i.v7_order_id) IS NOT NULL THEN 'Đơn hàng: ' || COALESCE(o.code, '')
                     ELSE CASE WHEN p.direction='IN' THEN 'Thu trực tiếp' ELSE 'Chi trực tiếp' END
                   END AS linked_entity_label,
                   COALESCE(p.supplier_id, i.supplier_id),
                   COALESCE(p.customer_id, i.customer_id),
                   p.seller_id,
                   COALESCE(p.employee_id, CASE WHEN p.direction='OUT' THEN o.installer_employee_id ELSE NULL END),
                   COALESCE(p.v7_order_id, i.v7_order_id),
                   COALESCE(o.code, ''),
                   i.id,
                   COALESCE(i.invoice_no, ''),
                   COALESCE(i.invoice_type, ''),
                   COALESCE(NULLIF(p.reference_doc,''), COALESCE(i.invoice_no,'')),
                   COALESCE(NULLIF(p.notes,''), COALESCE(i.notes,'')),
                   CASE WHEN COALESCE(p.is_locked,0)=1 OR COALESCE(i.is_locked,0)=1 THEN 1 ELSE 0 END,
                   CASE
                     WHEN u.id IS NULL OR LOWER(COALESCE(u.username,''))='vanlt_it' THEN 'Hệ thống / Kế toán'
                     ELSE COALESCE(u.display_name, u.username)
                   END,
                   p.direction
            FROM payments p
            LEFT JOIN expense_categories ec ON ec.id=p.expense_category_id
            LEFT JOIN payment_allocations pa ON pa.payment_id=p.id AND pa.deleted_at IS NULL
            LEFT JOIN invoices i ON i.id=pa.invoice_id AND i.deleted_at IS NULL
            LEFT JOIN suppliers ps ON ps.id=p.supplier_id
            LEFT JOIN suppliers s ON s.id=i.supplier_id
            LEFT JOIN customers pc ON pc.id=p.customer_id
            LEFT JOIN customers ic ON ic.id=i.customer_id
            LEFT JOIN v7_sellers psl ON psl.id=p.seller_id
            LEFT JOIN employees pe ON pe.id=p.employee_id
            LEFT JOIN v7_orders o ON o.id=COALESCE(p.v7_order_id, i.v7_order_id)
            LEFT JOIN app_users u ON u.id=p.created_by
            WHERE p.deleted_at IS NULL {dirCond}
            ORDER BY resolved_paid_at DESC, p.id DESC",
            r => new ExpenseVoucherVm(
                L(r, 0), S(r, 1), S(r, 2), S(r, 3), L(r, 4),
                r.IsDBNull(5) ? null : L(r, 5), S(r, 6), S(r, 7), S(r, 8),
                S(r, 9), S(r, 10),
                r.IsDBNull(11) ? null : L(r, 11),
                r.IsDBNull(12) ? null : L(r, 12),
                r.IsDBNull(13) ? null : L(r, 13),
                r.IsDBNull(14) ? null : L(r, 14),
                r.IsDBNull(15) ? null : L(r, 15),
                S(r, 16),
                r.IsDBNull(17) ? null : L(r, 17),
                S(r, 18), S(r, 19), S(r, 20), S(r, 21),
                L(r, 22) == 1, S(r, 23), S(r, 24)),
            ("$dir", direction ?? ""));
    }

    private static void RecalculateInvoiceSettlementStatus(SqliteConnection c, SqliteTransaction t, long invId)
    {
        var info = QueryTx(c, t, @"
            SELECT i.amount_vnd, COALESCE(i.offset_vnd,0), i.status,
                   COALESCE((SELECT SUM(allocated_vnd) FROM payment_allocations WHERE invoice_id=i.id AND deleted_at IS NULL), 0)
            FROM invoices i WHERE i.id=$id AND i.deleted_at IS NULL",
            r => (Total: L(r, 0), Offset: L(r, 1), Status: S(r, 2), Paid: L(r, 3)), ("$id", invId)).SingleOrDefault();
        if (string.IsNullOrEmpty(info.Status) || info.Status == "VOID") return;
        var settled = info.Paid + info.Offset;
        var nextStatus = info.Total <= 0 ? "PAID" : (settled <= 0 ? "OPEN" : (settled >= info.Total ? "PAID" : "PARTIAL"));
        Exec(c, t, "UPDATE invoices SET status=$st WHERE id=$id", ("$st", nextStatus), ("$id", invId));
    }

    public long SaveExpenseVoucher(
        long? id,
        long categoryId,
        string method,
        long amountVnd,
        string? paidAt,
        string payeeName,
        long? supplierId,
        long? customerId,
        long? sellerId,
        long? employeeId,
        long? orderId,
        long? invoiceId,
        string referenceDoc,
        string notes,
        string? expectedDirection = null)
    {
        if (categoryId <= 0)
            throw new InvalidOperationException("Vui lòng chọn Khoản Thu / Khoản Chi trong danh sách.");
        if (method is not ("CASH" or "BANK"))
            throw new InvalidOperationException("Nguồn quỹ thanh toán phải là Tiền mặt hoặc Ngân hàng.");
        if (amountVnd <= 0)
            throw new InvalidOperationException("Số tiền giao dịch phải lớn hơn 0 ₫.");

        string paidSql;
        if (string.IsNullOrWhiteSpace(paidAt))
        {
            paidSql = VnDateTimeHelper.NowSql();
        }
        else if (VnDateTimeHelper.TryParseUserFilterInput(paidAt, false, out var parsedPaid, out _))
        {
            if (paidAt.Trim().Length <= 10)
                parsedPaid = parsedPaid.Date + VnDateTimeHelper.NowVn.TimeOfDay;
            paidSql = parsedPaid.ToString(VnDateTimeHelper.SqlFormat, CultureInfo.InvariantCulture);
        }
        else if (DateTime.TryParse(paidAt.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtIso))
        {
            if (paidAt.Trim().Length <= 10)
                dtIso = dtIso.Date + VnDateTimeHelper.NowVn.TimeOfDay;
            paidSql = dtIso.ToString(VnDateTimeHelper.SqlFormat, CultureInfo.InvariantCulture);
        }
        else
        {
            paidSql = VnDateTimeHelper.NowSql();
        }

        return Tx((c, t) =>
        {
            var cat = QueryTx(c, t, "SELECT id, code, name, default_note, COALESCE(direction,'OUT') FROM expense_categories WHERE id=$id AND deleted_at IS NULL",
                r => (Id: L(r, 0), Code: S(r, 1), Name: S(r, 2), DefaultNote: S(r, 3), Dir: S(r, 4)), ("$id", categoryId)).SingleOrDefault();
            if (cat.Id == 0)
                throw new InvalidOperationException("Khoản Thu/Chi được chọn không tồn tại.");

            var effDir = cat.Dir == "IN" ? "IN" : "OUT";
            if (expectedDirection is "IN" or "OUT" && effDir != expectedDirection)
                throw new InvalidOperationException($"Khoản '{cat.Name}' thuộc nhóm {(effDir == "IN" ? "Khoản Thu (+)" : "Khoản Chi (−)")}, vui lòng chọn đúng khoản tương ứng với loại phiếu.");

            var dirWord = effDir == "IN" ? "Thu" : "Chi";
            long? effSup = supplierId is > 0 ? supplierId : null;
            long? effCust = customerId is > 0 ? customerId : null;
            long? effSeller = sellerId is > 0 ? sellerId : null;
            long? effEmp = employeeId is > 0 ? employeeId : null;
            long? effOrd = orderId is > 0 ? orderId : null;
            long? effInv = invoiceId is > 0 ? invoiceId : null;

            string resolvedPayee = (payeeName ?? "").Trim();
            string resolvedRef = (referenceDoc ?? "").Trim();
            string resolvedNotes = (notes ?? "").Trim();

            if (effInv.HasValue)
            {
                var invRow = QueryTx(c, t, @"SELECT i.id, i.invoice_no, i.invoice_type, i.status, COALESCE(i.is_locked,0),
                        i.supplier_id, i.customer_id, i.v7_order_id,
                        i.amount_vnd - COALESCE(i.offset_vnd,0) - COALESCE((SELECT SUM(allocated_vnd) FROM payment_allocations WHERE invoice_id=i.id AND deleted_at IS NULL AND ($editPid IS NULL OR payment_id<>$editPid)), 0) AS rem_bal,
                        COALESCE(cu.name, s.name, o.installer, CASE WHEN INSTR(i.notes,' — ')>0 THEN SUBSTR(i.notes,1,INSTR(i.notes,' — ')-1) ELSE i.notes END, '')
                    FROM invoices i
                    LEFT JOIN customers cu ON cu.id=i.customer_id
                    LEFT JOIN suppliers s ON s.id=i.supplier_id
                    LEFT JOIN v7_orders o ON o.id=i.v7_order_id
                    WHERE i.id=$inv AND i.deleted_at IS NULL",
                    r => (Id: L(r, 0), No: S(r, 1), Type: S(r, 2), Status: S(r, 3), Locked: L(r, 4) == 1,
                          SupId: r.IsDBNull(5) ? (long?)null : L(r, 5),
                          CustId: r.IsDBNull(6) ? (long?)null : L(r, 6),
                          OrdId: r.IsDBNull(7) ? (long?)null : L(r, 7),
                          MaxRem: L(r, 8), Partner: S(r, 9)),
                    ("$inv", effInv.Value), ("$editPid", id is > 0 ? id.Value : null)).SingleOrDefault();

                if (invRow.Id == 0 || invRow.Status == "VOID")
                    throw new InvalidOperationException("Chứng từ công nợ được chọn không hợp lệ.");
                var invIsReceivable = invRow.Type is "SALE" or "RENEWAL";
                if (effDir == "IN" && !invIsReceivable)
                    throw new InvalidOperationException($"Phiếu Thu không thể cấn trừ vào chứng từ phải trả {invRow.No}.");
                if (effDir == "OUT" && invIsReceivable)
                    throw new InvalidOperationException($"Phiếu Chi không thể cấn trừ vào chứng từ phải thu {invRow.No}.");
                if (invRow.Locked && !IsMainDeveloper(c, t))
                    throw new InvalidOperationException($"Chứng từ {invRow.No} đã chốt số / khóa sổ.");
                if (amountVnd > invRow.MaxRem)
                    throw new InvalidOperationException($"Số tiền {dirWord.ToLowerInvariant()} ({amountVnd:N0} ₫) vượt số dư còn nợ của chứng từ {invRow.No} ({Math.Max(0, invRow.MaxRem):N0} ₫). Vui lòng nhập ≤ {Math.Max(0, invRow.MaxRem):N0} ₫ hoặc chọn 'Không cấn trừ chứng từ công nợ'.");

                effSup ??= invRow.SupId;
                effCust ??= invRow.CustId;
                effOrd ??= invRow.OrdId;
                if (string.IsNullOrWhiteSpace(resolvedPayee)) resolvedPayee = invRow.Partner;
                if (string.IsNullOrWhiteSpace(resolvedRef)) resolvedRef = invRow.No;
                if (string.IsNullOrWhiteSpace(resolvedNotes)) resolvedNotes = $"{dirWord} thanh toán chứng từ {invRow.No} ({cat.Name})";
            }

            if (string.IsNullOrWhiteSpace(resolvedPayee))
            {
                if (effCust.HasValue) resolvedPayee = QueryTx(c, t, "SELECT name FROM customers WHERE id=$id", r => S(r, 0), ("$id", effCust.Value)).FirstOrDefault() ?? "";
                else if (effSup.HasValue) resolvedPayee = QueryTx(c, t, "SELECT name FROM suppliers WHERE id=$id", r => S(r, 0), ("$id", effSup.Value)).FirstOrDefault() ?? "";
                else if (effEmp.HasValue) resolvedPayee = QueryTx(c, t, "SELECT full_name FROM employees WHERE id=$id", r => S(r, 0), ("$id", effEmp.Value)).FirstOrDefault() ?? "";
                else if (effSeller.HasValue) resolvedPayee = QueryTx(c, t, "SELECT code||' — '||full_name FROM v7_sellers WHERE id=$id", r => S(r, 0), ("$id", effSeller.Value)).FirstOrDefault() ?? "";
            }
            if (string.IsNullOrWhiteSpace(resolvedRef) && effOrd.HasValue)
            {
                resolvedRef = QueryTx(c, t, "SELECT code FROM v7_orders WHERE id=$id", r => S(r, 0), ("$id", effOrd.Value)).FirstOrDefault() ?? "";
            }
            if (string.IsNullOrWhiteSpace(resolvedNotes))
            {
                resolvedNotes = !string.IsNullOrWhiteSpace(cat.DefaultNote) ? cat.DefaultNote : cat.Name;
            }

            if (id is > 0)
            {
                var curPay = QueryTx(c, t, "SELECT id, payment_no, direction, COALESCE(is_locked,0) FROM payments WHERE id=$id AND deleted_at IS NULL",
                    r => (Id: L(r, 0), No: S(r, 1), Dir: S(r, 2), Locked: L(r, 3) == 1), ("$id", id.Value)).SingleOrDefault();
                if (curPay.Id == 0) throw new InvalidOperationException("Không tìm thấy phiếu Thu/Chi.");
                if (curPay.Locked && !IsMainDeveloper(c, t))
                    throw new InvalidOperationException($"Phiếu {curPay.No} đã chốt số / khóa sổ, cần mở khóa trước khi chỉnh sửa.");

                var oldInvIds = QueryTx(c, t, "SELECT invoice_id FROM payment_allocations WHERE payment_id=$p AND deleted_at IS NULL", r => L(r, 0), ("$p", curPay.Id));
                if (IsMainDeveloper(c, t))
                    Exec(c, t, "DELETE FROM payment_allocations WHERE payment_id=$p", ("$p", curPay.Id));
                else
                    Exec(c, t, "UPDATE payment_allocations SET deleted_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE payment_id=$p AND deleted_at IS NULL", ("$p", curPay.Id));

                foreach (var oldInvId in oldInvIds.Distinct())
                    RecalculateInvoiceSettlementStatus(c, t, oldInvId);

                var nextPayNo = curPay.No;
                if (curPay.Dir != effDir)
                {
                    if (effDir == "IN" && nextPayNo.StartsWith("PC-", StringComparison.OrdinalIgnoreCase))
                        nextPayNo = "PT-" + nextPayNo[3..];
                    else if (effDir == "OUT" && nextPayNo.StartsWith("PT-", StringComparison.OrdinalIgnoreCase))
                        nextPayNo = "PC-" + nextPayNo[3..];
                }

                Exec(c, t, @"UPDATE payments SET
                        payment_no=$no,
                        direction=$dir,
                        paid_at=$paid,
                        method=$m,
                        amount_vnd=$amt,
                        notes=$notes,
                        expense_category_id=$cat,
                        supplier_id=$sup,
                        customer_id=$cust,
                        seller_id=$sel,
                        employee_id=$emp,
                        v7_order_id=$ord,
                        payee_name=$payee,
                        reference_doc=$ref
                    WHERE id=$id",
                    ("$no", nextPayNo), ("$dir", effDir),
                    ("$paid", paidSql), ("$m", method), ("$amt", amountVnd), ("$notes", resolvedNotes),
                    ("$cat", cat.Id), ("$sup", effSup), ("$cust", effCust), ("$sel", effSeller),
                    ("$emp", effEmp), ("$ord", effOrd), ("$payee", resolvedPayee), ("$ref", resolvedRef),
                    ("$id", curPay.Id));

                if (effInv.HasValue)
                {
                    Exec(c, t, "INSERT INTO payment_allocations(payment_id,invoice_id,allocated_vnd) VALUES($p,$i,$a)",
                        ("$p", curPay.Id), ("$i", effInv.Value), ("$a", amountVnd));
                    RecalculateInvoiceSettlementStatus(c, t, effInv.Value);
                }

                Log(c, t, "UPDATE_CASHBOOK", "PAYMENT", curPay.Id, $"{nextPayNo} [{cat.Name}]: {amountVnd:N0} VND ({method}) — {resolvedPayee}");
                return curPay.Id;
            }
            else
            {
                var payNo = (effDir == "IN" ? "PT-" : "PC-") + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                Exec(c, t, @"INSERT INTO payments(
                        payment_no,direction,paid_at,method,amount_vnd,notes,
                        expense_category_id,supplier_id,customer_id,seller_id,employee_id,v7_order_id,payee_name,reference_doc)
                    VALUES($no,$dir,$paid,$m,$amt,$notes,$cat,$sup,$cust,$sel,$emp,$ord,$payee,$ref)",
                    ("$no", payNo), ("$dir", effDir), ("$paid", paidSql), ("$m", method), ("$amt", amountVnd), ("$notes", resolvedNotes),
                    ("$cat", cat.Id), ("$sup", effSup), ("$cust", effCust), ("$sel", effSeller),
                    ("$emp", effEmp), ("$ord", effOrd), ("$payee", resolvedPayee), ("$ref", resolvedRef));
                var newPayId = N(c, t, "SELECT last_insert_rowid()");

                if (effInv.HasValue)
                {
                    Exec(c, t, "INSERT INTO payment_allocations(payment_id,invoice_id,allocated_vnd) VALUES($p,$i,$a)",
                        ("$p", newPayId), ("$i", effInv.Value), ("$a", amountVnd));
                    RecalculateInvoiceSettlementStatus(c, t, effInv.Value);
                }

                Log(c, t, "CREATE_CASHBOOK", "PAYMENT", newPayId, $"{payNo} [{cat.Name}]: {amountVnd:N0} VND ({method}) — {resolvedPayee} · {resolvedNotes}");
                return newPayId;
            }
        });
    }

    public void SetExpenseLock(long paymentId, bool locked)
    {
        Tx((c, t) =>
        {
            var p = QueryTx(c, t, "SELECT id, payment_no, direction FROM payments WHERE id=$id AND deleted_at IS NULL",
                r => (Id: L(r, 0), No: S(r, 1), Dir: S(r, 2)), ("$id", paymentId)).SingleOrDefault();
            if (p.Id == 0) throw new InvalidOperationException("Không tìm thấy phiếu Thu/Chi.");
            Exec(c, t, "UPDATE payments SET is_locked=$l WHERE id=$id", ("$l", locked ? 1 : 0), ("$id", p.Id));
            Log(c, t, locked ? "LOCK_PAYMENT" : "UNLOCK_PAYMENT", "PAYMENT", p.Id, $"{(locked ? "Chốt số" : "Mở khóa")} phiếu {(p.Dir == "IN" ? "thu" : "chi")} {p.No}");
            return 0;
        });
    }

    public string DeleteExpenseVoucher(long paymentId)
    {
        return DeleteCashBookPayment(paymentId);
    }

    public string UpdateCashBookPayment(long paymentId, string method, long amountVnd, string? paidAt, string partnerName, string notes, long? categoryId = null)
    {
        if (method is not ("CASH" or "BANK"))
            throw new InvalidOperationException("Nguồn quỹ phải là Tiền mặt hoặc Ngân hàng.");
        if (amountVnd <= 0)
            throw new InvalidOperationException("Số tiền giao dịch phải lớn hơn 0 ₫.");

        string paidSql;
        if (string.IsNullOrWhiteSpace(paidAt))
            paidSql = VnDateTimeHelper.NowSql();
        else if (VnDateTimeHelper.TryParseUserFilterInput(paidAt, false, out var parsed, out _))
        {
            if (paidAt.Trim().Length <= 10) parsed = parsed.Date + VnDateTimeHelper.NowVn.TimeOfDay;
            paidSql = parsed.ToString(VnDateTimeHelper.SqlFormat, CultureInfo.InvariantCulture);
        }
        else if (DateTime.TryParse(paidAt.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtIso))
        {
            if (paidAt.Trim().Length <= 10) dtIso = dtIso.Date + VnDateTimeHelper.NowVn.TimeOfDay;
            paidSql = dtIso.ToString(VnDateTimeHelper.SqlFormat, CultureInfo.InvariantCulture);
        }
        else
            paidSql = VnDateTimeHelper.NowSql();

        return Tx((c, t) =>
        {
            var cur = QueryTx(c, t, "SELECT id, payment_no, direction, COALESCE(is_locked,0), expense_category_id FROM payments WHERE id=$id AND deleted_at IS NULL",
                r => (Id: L(r, 0), No: S(r, 1), Dir: S(r, 2), Locked: L(r, 3) == 1, CatId: r.IsDBNull(4) ? (long?)null : L(r, 4)), ("$id", paymentId)).SingleOrDefault();
            if (cur.Id == 0) throw new InvalidOperationException("Không tìm thấy phiếu Sổ quỹ.");
            var isMain = IsMainDeveloper(c, t);
            if (cur.Locked && !isMain)
                throw new InvalidOperationException($"Phiếu {cur.No} đã chốt số / khóa sổ, vui lòng mở khóa trước khi chỉnh sửa.");

            var alloc = QueryTx(c, t, "SELECT id, invoice_id FROM payment_allocations WHERE payment_id=$p AND deleted_at IS NULL LIMIT 1",
                r => (Id: L(r, 0), InvId: L(r, 1)), ("$p", cur.Id)).FirstOrDefault();

            if (alloc.InvId > 0)
            {
                var invCheck = QueryTx(c, t, @"SELECT invoice_no, COALESCE(is_locked,0),
                        amount_vnd - COALESCE(offset_vnd,0) - COALESCE((SELECT SUM(allocated_vnd) FROM payment_allocations WHERE invoice_id=invoices.id AND deleted_at IS NULL AND payment_id<>$p), 0)
                    FROM invoices WHERE id=$inv AND deleted_at IS NULL",
                    r => (No: S(r, 0), Locked: L(r, 1) == 1, MaxAllowed: L(r, 2)),
                    ("$p", cur.Id), ("$inv", alloc.InvId)).SingleOrDefault();
                if (invCheck.Locked && !isMain)
                    throw new InvalidOperationException($"Chứng từ liên kết {invCheck.No} đã chốt số, cần mở khóa chứng từ trước khi sửa phiếu thu/chi.");
                if (amountVnd > invCheck.MaxAllowed)
                    throw new InvalidOperationException($"Số tiền ({amountVnd:N0} ₫) vượt số dư tối đa của chứng từ {invCheck.No} ({Math.Max(0, invCheck.MaxAllowed):N0} ₫).");

                Exec(c, t, "UPDATE payment_allocations SET allocated_vnd=$a WHERE id=$aid", ("$a", amountVnd), ("$aid", alloc.Id));
            }

            var nextCatId = categoryId is > 0 ? categoryId : cur.CatId;
            Exec(c, t, "UPDATE payments SET method=$m, amount_vnd=$a, paid_at=$paid, payee_name=$payee, notes=$notes, expense_category_id=$cat WHERE id=$id",
                ("$m", method), ("$a", amountVnd), ("$paid", paidSql), ("$payee", (partnerName ?? "").Trim()), ("$notes", (notes ?? "").Trim()), ("$cat", nextCatId), ("$id", cur.Id));

            if (alloc.InvId > 0)
                RecalculateInvoiceSettlementStatus(c, t, alloc.InvId);

            Log(c, t, "UPDATE", "PAYMENT", cur.Id, $"Cập nhật phiếu {(cur.Dir == "IN" ? "Thu" : "Chi")} {cur.No}: {amountVnd:N0} VND ({method})");
            return $"Đã cập nhật phiếu {(cur.Dir == "IN" ? "Thu" : "Chi")} {cur.No} ({amountVnd:N0} ₫) và đồng bộ Sổ quỹ / Công nợ.";
        });
    }

    public string DeleteCashBookPayment(long paymentId)
    {
        return Tx((c, t) =>
        {
            var cur = QueryTx(c, t, "SELECT id, payment_no, direction, amount_vnd, COALESCE(is_locked,0) FROM payments WHERE id=$id AND deleted_at IS NULL",
                r => (Id: L(r, 0), No: S(r, 1), Dir: S(r, 2), Amt: L(r, 3), Locked: L(r, 4) == 1), ("$id", paymentId)).SingleOrDefault();
            if (cur.Id == 0) throw new InvalidOperationException("Không tìm thấy phiếu Sổ quỹ.");
            var isMain = IsMainDeveloper(c, t);
            if (cur.Locked && !isMain)
                throw new InvalidOperationException($"Phiếu {cur.No} đã chốt số / khóa sổ, không thể xóa.");

            var linkedInvIds = QueryTx(c, t, "SELECT invoice_id FROM payment_allocations WHERE payment_id=$p AND deleted_at IS NULL", r => L(r, 0), ("$p", cur.Id));
            foreach (var invId in linkedInvIds.Distinct())
            {
                var invLocked = N(c, t, "SELECT COALESCE(is_locked,0) FROM invoices WHERE id=$i", ("$i", invId)) == 1;
                if (invLocked && !isMain)
                    throw new InvalidOperationException("Chứng từ công nợ gắn với phiếu này đã chốt số, cần mở khóa trước khi xóa phiếu thu/chi.");
            }

            var label = cur.Dir == "IN" ? "Phiếu Thu" : "Phiếu Chi";
            if (isMain)
            {
                Exec(c, t, "DELETE FROM payment_allocations WHERE payment_id=$p", ("$p", cur.Id));
                Exec(c, t, "DELETE FROM payments WHERE id=$p", ("$p", cur.Id));
                foreach (var invId in linkedInvIds.Distinct())
                    RecalculateInvoiceSettlementStatus(c, t, invId);
                return $"Đã xóa cứng {label} {cur.No} ({cur.Amt:N0} ₫) và cập nhật lại Sổ quỹ / Công nợ liên quan.";
            }
            else
            {
                var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                Exec(c, t, "UPDATE payment_allocations SET deleted_at=$now, deleted_by=$actor WHERE payment_id=$p AND deleted_at IS NULL",
                    ("$now", now), ("$actor", ActorId > 0 ? ActorId : null), ("$p", cur.Id));
                Exec(c, t, "UPDATE payments SET deleted_at=$now, deleted_by=$actor WHERE id=$p",
                    ("$now", now), ("$actor", ActorId > 0 ? ActorId : null), ("$p", cur.Id));
                foreach (var invId in linkedInvIds.Distinct())
                    RecalculateInvoiceSettlementStatus(c, t, invId);
                Log(c, t, "DELETE", "PAYMENT", cur.Id, $"Xóa mềm {label} {cur.No} ({cur.Amt:N0} VND)");
                return $"Đã xóa {label} {cur.No} ({cur.Amt:N0} ₫) khỏi Sổ quỹ và khôi phục số dư chứng từ công nợ liên quan.";
            }
        });
    }

    public string UpdateCustomInvoice(long invoiceId, long? customerId, long? supplierId, string partnerLabel, string description, long? productId, long quantity, long unitPriceVnd, string dueAt, string? issuedAt = null)
    {
        if (quantity <= 0 || unitPriceVnd <= 0)
            throw new InvalidOperationException("Số lượng và đơn giá phải lớn hơn 0.");
        if (string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException("Vui lòng nhập nội dung / diễn giải chứng từ.");

        var total = quantity * unitPriceVnd;
        string issued = VnDateTimeHelper.NowSql();
        if (!string.IsNullOrWhiteSpace(issuedAt))
        {
            if (VnDateTimeHelper.TryParseUserFilterInput(issuedAt, false, out var parsedIssued, out _))
            {
                if (issuedAt.Trim().Length <= 10) parsedIssued = parsedIssued.Date + VnDateTimeHelper.NowVn.TimeOfDay;
                issued = parsedIssued.ToString(VnDateTimeHelper.SqlFormat, CultureInfo.InvariantCulture);
            }
            else issued = issuedAt.Trim();
        }
        var due = string.IsNullOrWhiteSpace(dueAt) ? VnDateTimeHelper.TodayVn.AddDays(15).ToString("yyyy-MM-dd") : dueAt.Trim();
        var noteText = string.IsNullOrWhiteSpace(partnerLabel) ? description.Trim() : $"{partnerLabel.Trim()} — {description.Trim()}";

        return Tx((c, t) =>
        {
            var cur = QueryTx(c, t, "SELECT id, invoice_no, COALESCE(is_locked,0), COALESCE(v7_order_id,0), COALESCE(renewal_id,0) FROM invoices WHERE id=$id AND deleted_at IS NULL",
                r => (Id: L(r, 0), No: S(r, 1), Locked: L(r, 2) == 1, OrdId: L(r, 3), RenId: L(r, 4)), ("$id", invoiceId)).SingleOrDefault();
            if (cur.Id == 0) throw new InvalidOperationException("Không tìm thấy chứng từ công nợ.");
            var isMain = IsMainDeveloper(c, t);
            if (cur.Locked && !isMain)
                throw new InvalidOperationException($"Chứng từ {cur.No} đã chốt số, cần mở khóa trước khi chỉnh sửa.");

            var paidAmt = N(c, t, "SELECT COALESCE(SUM(allocated_vnd),0) FROM payment_allocations WHERE invoice_id=$i AND deleted_at IS NULL", ("$i", cur.Id));
            if (total < paidAmt)
                throw new InvalidOperationException($"Tổng tiền chứng từ mới ({total:N0} ₫) không thể nhỏ hơn số tiền đã thanh toán ({paidAmt:N0} ₫).");

            Exec(c, t, @"UPDATE invoices SET
                    customer_id=COALESCE($cust, customer_id),
                    supplier_id=COALESCE($sup, supplier_id),
                    issued_at=$issued,
                    due_at=$due,
                    amount_vnd=$amt,
                    notes=$note
                WHERE id=$id",
                ("$cust", customerId is > 0 ? customerId : null),
                ("$sup", supplierId is > 0 ? supplierId : null),
                ("$issued", issued), ("$due", due), ("$amt", total), ("$note", noteText), ("$id", cur.Id));

            var lineCount = N(c, t, "SELECT COUNT(*) FROM invoice_lines WHERE invoice_id=$i AND deleted_at IS NULL", ("$i", cur.Id));
            if (lineCount <= 1)
            {
                if (lineCount == 1)
                {
                    Exec(c, t, "UPDATE invoice_lines SET description=$d, product_id=COALESCE($p, product_id), quantity=$q, unit_price_vnd=$u WHERE invoice_id=$i AND deleted_at IS NULL",
                        ("$d", description.Trim()), ("$p", productId is > 0 ? productId : null), ("$q", quantity), ("$u", unitPriceVnd), ("$i", cur.Id));
                }
                else
                {
                    Exec(c, t, "INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES($i,$d,$p,$q,$u)",
                        ("$i", cur.Id), ("$d", description.Trim()), ("$p", productId is > 0 ? productId : null), ("$q", quantity), ("$u", unitPriceVnd));
                }
            }

            RecalculateInvoiceSettlementStatus(c, t, cur.Id);
            Log(c, t, "UPDATE", "INVOICE", cur.Id, $"Cập nhật chứng từ {cur.No}: {total:N0} VND — {noteText}");
            return $"Đã cập nhật chứng từ công nợ {cur.No} ({total:N0} ₫).";
        });
    }

    public string DeleteInvoice(long invoiceId)
    {
        return Tx((c, t) =>
        {
            var cur = QueryTx(c, t, "SELECT id, invoice_no, amount_vnd, COALESCE(is_locked,0) FROM invoices WHERE id=$id AND deleted_at IS NULL",
                r => (Id: L(r, 0), No: S(r, 1), Amt: L(r, 2), Locked: L(r, 3) == 1), ("$id", invoiceId)).SingleOrDefault();
            if (cur.Id == 0) throw new InvalidOperationException("Không tìm thấy chứng từ công nợ.");
            var isMain = IsMainDeveloper(c, t);
            if (cur.Locked && !isMain)
                throw new InvalidOperationException($"Chứng từ {cur.No} đã chốt số, cần mở khóa trước khi xóa.");

            var payIds = QueryTx(c, t, "SELECT payment_id FROM payment_allocations WHERE invoice_id=$i AND deleted_at IS NULL", r => L(r, 0), ("$i", cur.Id));

            if (isMain)
            {
                Exec(c, t, "DELETE FROM payment_allocations WHERE invoice_id=$i", ("$i", cur.Id));
                foreach (var pid in payIds.Distinct())
                {
                    if (N(c, t, "SELECT COUNT(*) FROM payment_allocations WHERE payment_id=$p", ("$p", pid)) == 0)
                        Exec(c, t, "DELETE FROM payments WHERE id=$p", ("$p", pid));
                }
                Exec(c, t, "DELETE FROM invoice_lines WHERE invoice_id=$i", ("$i", cur.Id));
                Exec(c, t, "DELETE FROM invoices WHERE id=$i", ("$i", cur.Id));
                return $"Đã xóa cứng chứng từ công nợ {cur.No} ({cur.Amt:N0} ₫) và các bút toán thanh toán gắn kèm.";
            }
            else
            {
                if (payIds.Count > 0)
                    throw new InvalidOperationException($"Chứng từ {cur.No} đã có phiếu thu/chi phân bổ. Vui lòng xóa phiếu thu/chi bên Sổ quỹ trước khi xóa chứng từ.");
                var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                Exec(c, t, "UPDATE invoice_lines SET deleted_at=$now, deleted_by=$actor WHERE invoice_id=$i AND deleted_at IS NULL",
                    ("$now", now), ("$actor", ActorId > 0 ? ActorId : null), ("$i", cur.Id));
                Exec(c, t, "UPDATE invoices SET status='VOID', deleted_at=$now, deleted_by=$actor WHERE id=$i",
                    ("$now", now), ("$actor", ActorId > 0 ? ActorId : null), ("$i", cur.Id));
                Log(c, t, "DELETE", "INVOICE", cur.Id, $"Xóa mềm chứng từ công nợ {cur.No} ({cur.Amt:N0} VND)");
                return $"Đã xóa chứng từ công nợ {cur.No} ({cur.Amt:N0} ₫).";
            }
        });
    }
}
