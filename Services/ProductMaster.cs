using Microsoft.Data.Sqlite;

namespace TNM.Management.Services;

/// <summary>
/// V7 canonical product master. PO/SO/IMEI and group price lists reference products.id.
/// </summary>
public sealed class ProductMaster(IWebHostEnvironment environment)
{
    public sealed record ProductRow(long Id, string Sku, string Name, long GroupId, string GroupName,
        string Kind, string Unit, bool UsesImei, int WarrantyMonths, bool IsActive, long StockInHand = 0);
    public sealed record GroupRow(long Id, string Code, string Name);
    public sealed record ProductDraft(long? Id, long GroupId, string Name, string Kind, string Unit, bool UsesImei, int WarrantyMonths, string? CustomSku = null);
    private readonly string path = Path.Combine(environment.ContentRootPath, "App_Data", "tnm-local.db");

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, ForeignKeys = true, Pooling = true, DefaultTimeout = 15
        }.ToString());
        connection.Open(); AuditRuntime.Attach(connection);
        using var pragma = connection.CreateCommand(); pragma.CommandText = "PRAGMA busy_timeout=15000;"; pragma.ExecuteNonQuery();
        return connection;
    }
    private static SqliteCommand Sql(SqliteConnection c, string text, params (string Key, object? Value)[] p)
    {
        var cmd = c.CreateCommand(); cmd.CommandText = text;
        foreach (var (key, val) in p) cmd.Parameters.AddWithValue(key, val ?? DBNull.Value);
        return cmd;
    }
    private static void Execute(SqliteConnection c, string text, params (string Key, object? Value)[] p)
    { using var q = Sql(c, text, p); q.ExecuteNonQuery(); }
    private static long Count(SqliteConnection c, string text, params (string Key, object? Value)[] p)
    { using var q = Sql(c, text, p); return Convert.ToInt64(q.ExecuteScalar() ?? 0L); }
    private T Write<T>(long actor, Func<SqliteConnection, T> operation)
    { using var scope = AuditRuntime.As(actor); return Write(operation); }
    private T Write<T>(Func<SqliteConnection, T> operation)
    {
        using var c = Open(); Execute(c, "BEGIN IMMEDIATE;");
        try { var result = operation(c); Execute(c, "COMMIT;"); return result; }
        catch { Execute(c, "ROLLBACK;"); throw; }
    }

    public void Initialize()
    {
        // Schema and triggers are created centrally in Data/schema.sql.
    }

    public List<GroupRow> Groups()
    {
        using var c = Open(); using var q = Sql(c, "SELECT id,code,name FROM product_groups WHERE deleted_at IS NULL ORDER BY name");
        using var r = q.ExecuteReader(); var rows = new List<GroupRow>();
        while (r.Read()) rows.Add(new GroupRow(r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return rows;
    }

    public List<ProductRow> Products(bool includeInactive = false)
    {
        using var c = Open(); using var q = Sql(c, """
            SELECT id,sku,name,group_id,group_name,product_kind,unit,is_serialized,warranty_months,is_active,
                   CASE
                     WHEN product_kind <> 'GOODS' THEN 0
                     WHEN is_serialized = 1 THEN
                       (SELECT COUNT(*) FROM stock_units u WHERE u.product_id=v7_product_master.id AND u.status='IN_STOCK' AND u.deleted_at IS NULL)
                       + COALESCE((SELECT SUM(qm.delta_qty) FROM quantity_movements qm WHERE qm.product_id=v7_product_master.id), 0)
                     ELSE
                       COALESCE((SELECT SUM(qm.delta_qty) FROM quantity_movements qm WHERE qm.product_id=v7_product_master.id), 0)
                   END AS stock_in_hand
            FROM v7_product_master
            WHERE ($all=1 OR is_active=1)
            ORDER BY id DESC;
            """, ("$all", includeInactive ? 1 : 0));
        using var r = q.ExecuteReader(); var rows = new List<ProductRow>();
        while (r.Read()) rows.Add(new ProductRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
            r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7) == 1, (int)r.GetInt64(8), r.GetInt64(9) == 1, r.GetInt64(10)));
        return rows;
    }

    private static void Validate(ProductDraft d)
    {
        if (d.GroupId <= 0 || string.IsNullOrWhiteSpace(d.Name) || d.Name.Trim().Length > 180 ||
           d.Kind is not ("GOODS" or "SERVICE" or "BUNDLE") ||
           string.IsNullOrWhiteSpace(d.Unit) || d.Unit.Trim().Length > 30 ||
           d.WarrantyMonths < 0 || d.WarrantyMonths > 240 || (d.Kind != "GOODS" && d.UsesImei))
            throw new InvalidOperationException("Nhóm, tên, loại, đơn vị tính hoặc bảo hành không hợp lệ.");
    }

    public string Save(long actor, ProductDraft d)
    {
        Validate(d);
        return Write(actor, c =>
        {
            if (Count(c, "SELECT COUNT(*) FROM product_groups WHERE id=$id AND deleted_at IS NULL", ("$id", d.GroupId)) != 1)
                throw new InvalidOperationException("Nhóm sản phẩm không tồn tại.");
            var newProduct = !d.Id.HasValue;
            long id; string sku;
            if (newProduct)
            {
                sku = string.IsNullOrWhiteSpace(d.CustomSku) ? InputCatalog.NextSku(c) : d.CustomSku.Trim();
                if (Count(c, "SELECT COUNT(*) FROM products WHERE sku=$sku AND deleted_at IS NULL", ("$sku", sku)) > 0)
                    throw new InvalidOperationException($"Mã sản phẩm '{sku}' đã tồn tại.");
                Execute(c, """
                    INSERT INTO products(group_id,sku,name,warranty_months,is_serialized,is_active)
                    VALUES($group,$sku,$name,$warranty,$imei,1)
                    """, ("$group", d.GroupId), ("$sku", sku), ("$name", d.Name.Trim()),
                    ("$warranty", d.WarrantyMonths), ("$imei", d.UsesImei ? 1 : 0));
                id = Count(c, "SELECT last_insert_rowid()");
            }
            else
            {
                id = d.Id!.Value;
                using var q = Sql(c, """
                    SELECT p.sku,p.is_serialized,COALESCE(m.product_kind,'GOODS')
                    FROM products p LEFT JOIN product_catalog_metadata m ON m.product_id=p.id
                    WHERE p.id=$id AND p.is_active=1 AND p.deleted_at IS NULL
                    """, ("$id", id));
                using var r = q.ExecuteReader();
                if (!r.Read()) throw new InvalidOperationException("Không tìm thấy sản phẩm còn hoạt động.");
                var oldSku = r.GetString(0); var oldImei = r.GetInt64(1) == 1; var oldKind = r.GetString(2); r.Close();
                sku = string.IsNullOrWhiteSpace(d.CustomSku) ? oldSku : d.CustomSku.Trim();
                if (!string.Equals(sku, oldSku, StringComparison.OrdinalIgnoreCase) &&
                    Count(c, "SELECT COUNT(*) FROM products WHERE sku=$sku AND id<>$id AND deleted_at IS NULL", ("$sku", sku), ("$id", id)) > 0)
                {
                    throw new InvalidOperationException($"Mã sản phẩm '{sku}' đã tồn tại.");
                }
                if (oldImei != d.UsesImei || oldKind != d.Kind)
                {
                    var inUse = Count(c, "SELECT COUNT(*) FROM stock_units WHERE product_id=$id", ("$id", id)) +
                        Count(c, "SELECT COUNT(*) FROM quantity_movements WHERE product_id=$id", ("$id", id)) +
                        Count(c, "SELECT COUNT(*) FROM v7_orders WHERE product_id=$id", ("$id", id)) +
                        Count(c, "SELECT COUNT(*) FROM purchase_order_lines WHERE product_id=$id", ("$id", id));
                    if (inUse > 0) throw new InvalidOperationException("Sản phẩm đã phát sinh giao dịch; không thể đổi loại hàng hoặc IMEI.");
                }
                Execute(c, """
                    UPDATE products SET sku=$sku,group_id=$group,name=$name,warranty_months=$warranty,is_serialized=$imei
                    WHERE id=$id
                    """, ("$sku", sku), ("$group", d.GroupId), ("$name", d.Name.Trim()), ("$warranty", d.WarrantyMonths),
                    ("$imei", d.UsesImei ? 1 : 0), ("$id", id));
            }
            Execute(c, """
                INSERT INTO product_catalog_metadata(product_id,product_kind,unit,source_tag)
                VALUES($id,$kind,$unit,'MANUAL')
                ON CONFLICT(product_id) DO UPDATE SET product_kind=excluded.product_kind,unit=excluded.unit
                """, ("$id", id), ("$kind", d.Kind), ("$unit", d.Unit.Trim()));
            Execute(c, "INSERT INTO audit_logs(actor_user_id,action,entity,entity_id,details) VALUES($actor,$action,'PRODUCT',$id,$sku)",
                ("$actor", actor), ("$action", newProduct ? "CREATE" : "UPDATE"), ("$id", id), ("$sku", sku));
            return sku;
        });
    }

    public void Disable(long actor, long productId)
    {
        Write(actor, c =>
        {
            if (Count(c, "SELECT COUNT(*) FROM products WHERE id=$id AND is_active=1 AND deleted_at IS NULL", ("$id", productId)) == 0)
                throw new InvalidOperationException("Sản phẩm không tồn tại hoặc đã ngừng sử dụng.");
            if (Count(c, "SELECT COUNT(*) FROM stock_units WHERE product_id=$id AND status IN ('IN_STOCK','CONSIGNED','TECHNICIAN')", ("$id", productId)) > 0 ||
               Count(c, "SELECT COUNT(*) FROM (SELECT warehouse_id,SUM(delta_qty) AS qty FROM quantity_movements WHERE product_id=$id GROUP BY warehouse_id HAVING qty<>0)", ("$id", productId)) > 0)
                throw new InvalidOperationException("Sản phẩm còn tồn kho, cần xử lý tồn trước khi ngừng sử dụng.");
            Execute(c, "UPDATE products SET is_active=0 WHERE id=$id", ("$id", productId));
            Execute(c, "INSERT INTO audit_logs(actor_user_id,action,entity,entity_id,details) VALUES($actor,'DISABLE','PRODUCT',$id,'Ngừng kinh doanh')",
                ("$actor", actor), ("$id", productId));
            return 0;
        });
    }

    private static bool IsMainDeveloper(SqliteConnection c, long actor) =>
        actor > 0 && Count(c, "SELECT COUNT(*) FROM app_users WHERE id=$u AND LOWER(username)='vanlt_it'", ("$u", actor)) > 0;

    public void SoftDelete(long actor, long productId)
    {
        Write(actor, c =>
        {
            if (Count(c, "SELECT COUNT(*) FROM products WHERE id=$id AND deleted_at IS NULL", ("$id", productId)) != 1)
                throw new InvalidOperationException("Sản phẩm không tồn tại hoặc đã xóa.");
            if (Count(c, "SELECT COUNT(*) FROM stock_units WHERE product_id=$id AND status IN ('IN_STOCK','CONSIGNED','TECHNICIAN')", ("$id", productId)) > 0 ||
               Count(c, "SELECT COUNT(*) FROM (SELECT warehouse_id,SUM(delta_qty) AS qty FROM quantity_movements WHERE product_id=$id GROUP BY warehouse_id HAVING qty<>0)", ("$id", productId)) > 0)
                throw new InvalidOperationException("Sản phẩm còn tồn kho, không thể xóa. Hãy xử lý tồn trước.");
            if (IsMainDeveloper(c, actor))
            {
                try
                {
                    Execute(c, "DELETE FROM v7_group_prices WHERE product_id=$id", ("$id", productId));
                    Execute(c, "DELETE FROM v7_supplier_group_prices WHERE product_id=$id", ("$id", productId));
                    Execute(c, "DELETE FROM v7_installer_product_rates WHERE product_id=$id", ("$id", productId));
                    Execute(c, "DELETE FROM product_catalog_metadata WHERE product_id=$id", ("$id", productId));
                    Execute(c, "DELETE FROM products WHERE id=$id", ("$id", productId));
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    throw new InvalidOperationException("Sản phẩm đang có dữ liệu phát sinh liên kết (Đơn hàng, IMEI, Phiếu kho...), vui lòng xóa cứng các chứng từ liên quan trước.");
                }
            }
            else
            {
                Execute(c, "UPDATE products SET deleted_at=CURRENT_TIMESTAMP,is_active=0 WHERE id=$id AND deleted_at IS NULL", ("$id", productId));
            }
            return 0;
        });
    }
}
