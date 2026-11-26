using Microsoft.Data.Sqlite;

namespace TNM.Management.Services;

/// <summary>
/// Bộ định giá phân cấp chuẩn thống nhất cho Đơn hàng (SO) và Đơn gia hạn (Renewal):
/// 1. Nhóm con / Nhóm riêng của HTX (customers.sub_group_id)
/// 2. Nhóm cha của nhóm con (sub_group.parent_id) -> Nhóm chính (customers.group_id) -> Nhóm cha của nhóm chính (main_group.parent_id)
/// 3. Giá sản phẩm khai báo gốc tại Danh mục Sản phẩm (products: standard_price_vnd / install_fee_vnd / renewal_fee_vnd)
/// Tuyệt đối KHÔNG lấy chéo giá của nhóm khách hàng khác khi nhóm hiện tại và nhóm cha không khai báo.
/// </summary>
public static class GroupPriceResolver
{
    public sealed record ResolvedPrice(
        long BasePriceVnd,
        int DiscountPercent,
        long FinalPriceVnd,
        long? MatchedGroupId,
        long? CampaignId,
        string? CampaignName,
        string SourceTier);

    private static string[] GetPairedRenewalSkus(string productSku) =>
        (productSku ?? "").Trim().ToUpperInvariant() switch
        {
            "SP000001" => ["DX00019", "DX00025", "DX00017"],
            "SP000005" => ["DX00020"],
            "SP000006" => ["DX00021"],
            "SP000019" or "DX00002" or "DX00026" => ["DX00022"],
            "SP000012" or "CAM4CH" => ["DX00018"],
            _ => []
        };

    public static List<long> GetCustomerGroupChain(SqliteConnection c, SqliteTransaction? tx, long customerId)
    {
        var chain = new List<long>(4);
        using var cmd = Cmd(c, tx, @"
            SELECT c.sub_group_id,
                   sg.parent_id AS sub_parent_id,
                   c.group_id,
                   mg.parent_id AS main_parent_id
            FROM customers c
            LEFT JOIN customer_groups sg ON sg.id = c.sub_group_id AND sg.deleted_at IS NULL
            LEFT JOIN customer_groups mg ON mg.id = c.group_id AND mg.deleted_at IS NULL
            WHERE c.id = $c AND c.deleted_at IS NULL",
            ("$c", customerId));
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return chain;

        void AddIfValid(int ordinal)
        {
            if (r.IsDBNull(ordinal)) return;
            var gid = r.GetInt64(ordinal);
            if (gid > 0 && !chain.Contains(gid))
                chain.Add(gid);
        }

        // Thứ tự nghiêm ngặt: Nhóm con HTX -> Nhóm cha của nhóm con -> Nhóm chính -> Nhóm cha của nhóm chính
        AddIfValid(0);
        AddIfValid(1);
        AddIfValid(2);
        AddIfValid(3);
        return chain;
    }

    public static ResolvedPrice ResolveOrderPrice(
        SqliteConnection c,
        SqliteTransaction? tx,
        long customerId,
        long productId,
        string priceMode = "INSTALLED",
        DateTime? atVn = null)
    {
        var day = (atVn ?? VnDateTimeHelper.TodayVn).ToString("yyyy-MM-dd");
        var chain = GetCustomerGroupChain(c, tx, customerId);

        long basePrice = 0;
        long? matchedGroup = null;
        string tier = "PRODUCT_DECLARED";

        for (int idx = 0; idx < chain.Count; idx++)
        {
            var gid = chain[idx];
            using var q = Cmd(c, tx, @"
                SELECT standard_price_vnd, installed_price_vnd, renewal_price_vnd
                FROM v7_group_prices
                WHERE group_id = $g
                  AND product_id = $p
                  AND deleted_at IS NULL
                  AND effective_from <= $d
                  AND (effective_to IS NULL OR effective_to >= $d)
                ORDER BY effective_from DESC, id DESC
                LIMIT 1",
                ("$g", gid), ("$p", productId), ("$d", day));
            using var r = q.ExecuteReader();
            if (r.Read())
            {
                var std = r.GetInt64(0);
                var inst = r.GetInt64(1);
                var ren = r.GetInt64(2);
                var candidate = priceMode == "STANDARD"
                    ? (std > 0 ? std : inst > 0 ? inst : ren)
                    : priceMode == "RENEWAL"
                        ? (ren > 0 ? ren : std > 0 ? std : inst)
                        : (inst > 0 ? inst : std > 0 ? std : ren);
                if (candidate > 0)
                {
                    basePrice = candidate;
                    matchedGroup = gid;
                    tier = idx == 0 ? "CHILD_GROUP" : "PARENT_GROUP";
                    break;
                }
            }
        }

        // Nếu cả Nhóm con lẫn Nhóm cha đều không có -> lấy Giá sản phẩm khai báo trên bảng products
        if (basePrice <= 0)
        {
            basePrice = LookupDeclaredProductOrderPrice(c, tx, productId, priceMode);
            tier = "PRODUCT_DECLARED";
        }

        // Kiểm tra chiến dịch chiết khấu theo đúng phân cấp nhóm của khách hàng
        int discount = 0;
        long? campId = null;
        string? campName = null;
        foreach (var gid in chain)
        {
            using var cp = Cmd(c, tx, @"
                SELECT id, name, discount_percent
                FROM v7_group_campaigns
                WHERE group_id = $g
                  AND is_active = 1
                  AND deleted_at IS NULL
                  AND start_date <= $d
                  AND end_date >= $d
                ORDER BY discount_percent DESC, id ASC
                LIMIT 1",
                ("$g", gid), ("$d", day));
            using var cr = cp.ExecuteReader();
            if (cr.Read())
            {
                campId = cr.GetInt64(0);
                campName = cr.GetString(1);
                discount = cr.GetInt32(2);
                break;
            }
        }

        var finalPrice = (long)((decimal)basePrice * (100 - discount) / 100m);
        return new ResolvedPrice(basePrice, discount, finalPrice, matchedGroup, campId, campName, tier);
    }

    public static long RoundToThousand(decimal raw)
    {
        if (raw <= 0m) return 0;
        return (long)(Math.Round(raw / 1000m, MidpointRounding.AwayFromZero) * 1000m);
    }

    public static decimal ComputeEffectiveRenewalMonths(int durationMonths, DateTime? baseExpiry, DateTime? customTargetExpiry)
    {
        if (customTargetExpiry.HasValue && baseExpiry.HasValue)
        {
            var start = baseExpiry.Value.Date;
            var end = customTargetExpiry.Value.Date;
            if (end <= start) return 1m;
            var exactMonths = (end.Year - start.Year) * 12 + (end.Month - start.Month);
            if (exactMonths > 0 && (end.Day == start.Day || (start.Day > 28 && end.Day == DateTime.DaysInMonth(end.Year, end.Month))))
                return exactMonths;
            var days = (decimal)(end - start).TotalDays;
            return Math.Max(0.5m, days / 30.4375m);
        }
        return durationMonths > 0 ? durationMonths : 12m;
    }

    /// <summary>
    /// Tính chuẩn đơn giá bán theo thời hạn cho Đơn lắp đặt (hỗ trợ gói nhiều năm 24T/36T hoặc 6T) và Đơn thương mại (STANDARD).
    /// </summary>
    public static long ResolveOrderUnitPriceForDuration(
        SqliteConnection c,
        SqliteTransaction? tx,
        long customerId,
        long productId,
        string priceMode = "INSTALLED",
        long durationMonths = 12,
        DateTime? atVn = null)
    {
        var baseQuote = ResolveOrderPrice(c, tx, customerId, productId, priceMode, atVn);
        if (priceMode == "STANDARD" || durationMonths <= 0 || durationMonths == 12)
            return baseQuote.FinalPriceVnd;

        var isSubDevice = Scalar(c, tx, "SELECT CASE WHEN is_serialized=1 OR renewal_fee_vnd>0 THEN 1 ELSE 0 END FROM products WHERE id=$p", ("$p", productId)) == 1;
        if (!isSubDevice)
            return baseQuote.FinalPriceVnd;

        var annualRenewal = ResolveRenewalPrice(c, tx, customerId, productId, atVn);
        if (annualRenewal <= 0)
            return baseQuote.FinalPriceVnd;

        if (durationMonths > 12)
        {
            var extraMonths = durationMonths - 12;
            var extraFee = RoundToThousand(annualRenewal * extraMonths / 12m);
            return baseQuote.FinalPriceVnd + extraFee;
        }
        else // Ví dụ gói 6 tháng khi lắp đặt: lấy giá thương mại + tỷ lệ phí thuê bao (hoặc giảm trừ nửa năm phí gia hạn)
        {
            var stdQuote = ResolveOrderPrice(c, tx, customerId, productId, "STANDARD", atVn).FinalPriceVnd;
            var reducedFee = RoundToThousand(annualRenewal * (12 - durationMonths) / 12m);
            var candidate = baseQuote.FinalPriceVnd - reducedFee;
            return Math.Max(stdQuote > 0 ? stdQuote : baseQuote.FinalPriceVnd / 2, candidate);
        }
    }

    /// <summary>
    /// Tính chuẩn giá gia hạn theo gói tháng (1T / 3T / 6T / 12T / 24T) hoặc đánh mốc thời gian cụ thể.
    /// </summary>
    public static long ResolveRenewalPriceForDuration(
        SqliteConnection c,
        SqliteTransaction? tx,
        long customerId,
        long productId,
        int durationMonths = 12,
        DateTime? baseExpiry = null,
        DateTime? customTargetExpiry = null,
        DateTime? atVn = null)
    {
        var annual = ResolveRenewalPrice(c, tx, customerId, productId, atVn);
        if (annual <= 0) return 0;

        var effMonths = ComputeEffectiveRenewalMonths(durationMonths, baseExpiry, customTargetExpiry);
        if (effMonths == 12m) return annual;
        if (effMonths == 24m) return annual * 2;
        if (effMonths == 36m) return annual * 3;
        return Math.Max(1000, RoundToThousand(annual * effMonths / 12m));
    }

    /// <summary>
    /// Tra cứu giá vốn dịch vụ gia hạn trả hãng theo Nhóm KH / Dòng thiết bị và thời hạn gia hạn.
    /// </summary>
    public static long ResolveRenewalCostForDuration(
        SqliteConnection c,
        SqliteTransaction? tx,
        long customerId,
        long productId,
        int durationMonths = 12,
        DateTime? baseExpiry = null,
        DateTime? customTargetExpiry = null,
        DateTime? atVn = null)
    {
        var day = (atVn ?? VnDateTimeHelper.TodayVn).ToString("yyyy-MM-dd");
        var chain = GetCustomerGroupChain(c, tx, customerId);
        string sku = "";
        using (var pq = Cmd(c, tx, "SELECT COALESCE(sku,'') FROM products WHERE id=$p", ("$p", productId)))
        {
            sku = pq.ExecuteScalar()?.ToString() ?? "";
        }
        var pairedSkus = GetPairedRenewalSkus(sku);
        if (pairedSkus.Length == 0) return 0;

        long annualCost = 0;
        foreach (var gid in chain)
        {
            foreach (var pSku in pairedSkus)
            {
                using var q = Cmd(c, tx, @"
                    SELECT COALESCE(p2.base_cost_vnd, 0)
                    FROM v7_group_prices gp
                    JOIN products p2 ON p2.id = gp.product_id
                    WHERE gp.group_id = $g
                      AND LOWER(p2.sku) = LOWER($sku)
                      AND p2.deleted_at IS NULL AND gp.deleted_at IS NULL
                      AND gp.effective_from <= $d AND (gp.effective_to IS NULL OR gp.effective_to >= $d)
                    ORDER BY gp.effective_from DESC, gp.id DESC LIMIT 1",
                    ("$g", gid), ("$sku", pSku), ("$d", day));
                var found = q.ExecuteScalar();
                if (found is not null && Convert.ToInt64(found) > 0)
                {
                    annualCost = Convert.ToInt64(found);
                    break;
                }
            }
            if (annualCost > 0) break;
        }

        if (annualCost <= 0)
        {
            foreach (var pSku in pairedSkus)
            {
                var fallback = Scalar(c, tx, "SELECT COALESCE(base_cost_vnd,0) FROM products WHERE LOWER(sku)=LOWER($sku) AND deleted_at IS NULL LIMIT 1", ("$sku", pSku));
                if (fallback > 0)
                {
                    annualCost = fallback;
                    break;
                }
            }
        }

        if (annualCost <= 0) return 0;
        var effMonths = ComputeEffectiveRenewalMonths(durationMonths, baseExpiry, customTargetExpiry);
        if (effMonths == 12m) return annualCost;
        return RoundToThousand(annualCost * effMonths / 12m);
    }

    public static long ResolveRenewalPrice(
        SqliteConnection c,
        SqliteTransaction? tx,
        long customerId,
        long productId,
        DateTime? atVn = null)
    {
        var day = (atVn ?? VnDateTimeHelper.TodayVn).ToString("yyyy-MM-dd");
        var chain = GetCustomerGroupChain(c, tx, customerId);

        string sku = "";
        long declaredRenewal = 0;
        string kind = "GOODS";
        using (var pq = Cmd(c, tx, @"
            SELECT p.sku, p.renewal_fee_vnd, COALESCE(m.product_kind, 'GOODS')
            FROM products p
            LEFT JOIN product_catalog_metadata m ON m.product_id = p.id
            WHERE p.id = $p", ("$p", productId)))
        using (var pr = pq.ExecuteReader())
        {
            if (pr.Read())
            {
                sku = pr.IsDBNull(0) ? "" : pr.GetString(0);
                declaredRenewal = pr.IsDBNull(1) ? 0 : pr.GetInt64(1);
                kind = pr.IsDBNull(2) ? "GOODS" : pr.GetString(2);
            }
        }

        var pairedSkus = GetPairedRenewalSkus(sku);

        foreach (var gid in chain)
        {
            // 1. Kiểm tra trực tiếp theo product_id của thiết bị tại bậc nhóm hiện tại
            using (var q = Cmd(c, tx, @"
                SELECT renewal_price_vnd, installed_price_vnd, standard_price_vnd
                FROM v7_group_prices
                WHERE group_id = $g
                  AND product_id = $p
                  AND deleted_at IS NULL
                  AND effective_from <= $d
                  AND (effective_to IS NULL OR effective_to >= $d)
                ORDER BY effective_from DESC, id DESC
                LIMIT 1",
                ("$g", gid), ("$p", productId), ("$d", day)))
            using (var r = q.ExecuteReader())
            {
                if (r.Read())
                {
                    var ren = r.GetInt64(0);
                    var inst = r.GetInt64(1);
                    var std = r.GetInt64(2);
                    var direct = ren > 0 ? ren : (kind == "SERVICE" ? (inst > 0 ? inst : std) : 0);
                    if (direct > 0)
                        return direct;
                }
            }

            // 2. Trong CÙNG bậc nhóm hiện tại: kiểm tra gói dịch vụ gia hạn tương ứng (VD: SP000001 <-> DX00019 / DX00025 / DX00017)
            foreach (var pSku in pairedSkus)
            {
                using var q2 = Cmd(c, tx, @"
                    SELECT gp.renewal_price_vnd, gp.installed_price_vnd, gp.standard_price_vnd
                    FROM v7_group_prices gp
                    JOIN products p2 ON p2.id = gp.product_id
                    LEFT JOIN product_catalog_metadata m2 ON m2.product_id = p2.id
                    WHERE gp.group_id = $g
                      AND LOWER(p2.sku) = LOWER($sku)
                      AND p2.deleted_at IS NULL
                      AND gp.deleted_at IS NULL
                      AND (COALESCE(m2.product_kind, 'GOODS') = 'SERVICE' OR LOWER(p2.name) LIKE 'gia hạn%')
                      AND gp.effective_from <= $d
                      AND (gp.effective_to IS NULL OR gp.effective_to >= $d)
                    ORDER BY gp.effective_from DESC, gp.id DESC
                    LIMIT 1",
                    ("$g", gid), ("$sku", pSku), ("$d", day));
                using var r2 = q2.ExecuteReader();
                if (r2.Read())
                {
                    var ren = r2.GetInt64(0);
                    var inst = r2.GetInt64(1);
                    var std = r2.GetInt64(2);
                    var pairedPrice = ren > 0 ? ren : inst > 0 ? inst : std;
                    if (pairedPrice > 0)
                        return pairedPrice;
                }
            }
        }

        // 3. Nhóm con & Nhóm cha đều không có -> trả về đúng Giá gia hạn khai báo trên Danh mục Sản phẩm
        return declaredRenewal;
    }

    public static long LookupDeclaredProductOrderPrice(SqliteConnection c, SqliteTransaction? tx, long productId, string priceMode = "INSTALLED")
    {
        var hasStdCol = false;
        using (var ti = Cmd(c, tx, "PRAGMA table_info(products)"))
        using (var tr = ti.ExecuteReader())
        {
            while (tr.Read())
            {
                if (string.Equals(tr.GetString(1), "standard_price_vnd", StringComparison.OrdinalIgnoreCase))
                {
                    hasStdCol = true;
                    break;
                }
            }
        }

        var sql = hasStdCol
            ? @"SELECT COALESCE(p.standard_price_vnd,0), COALESCE(p.install_fee_vnd,0), COALESCE(p.renewal_fee_vnd,0), COALESCE(p.base_cost_vnd,0), COALESCE(m.product_kind,'GOODS')
                FROM products p LEFT JOIN product_catalog_metadata m ON m.product_id=p.id WHERE p.id=$p"
            : @"SELECT 0, COALESCE(p.install_fee_vnd,0), COALESCE(p.renewal_fee_vnd,0), COALESCE(p.base_cost_vnd,0), COALESCE(m.product_kind,'GOODS')
                FROM products p LEFT JOIN product_catalog_metadata m ON m.product_id=p.id WHERE p.id=$p";

        using var cmd = Cmd(c, tx, sql, ("$p", productId));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return 0;

        var stdPrice = r.GetInt64(0);
        var installFee = r.GetInt64(1);
        var renewalFee = r.GetInt64(2);
        var baseCost = r.GetInt64(3);
        var kind = r.GetString(4);

        if (kind == "SERVICE")
            return stdPrice > 0 ? stdPrice : renewalFee > 0 ? renewalFee : installFee;

        if (stdPrice > 0)
            return priceMode == "STANDARD" ? stdPrice : stdPrice + installFee;

        var supCost = Scalar(c, tx, @"
            SELECT COALESCE(
                (SELECT gp.unit_cost_vnd FROM v7_supplier_group_prices gp
                 WHERE gp.product_id=$p AND gp.deleted_at IS NULL AND gp.unit_cost_vnd>0
                 ORDER BY gp.effective_from DESC, gp.id DESC LIMIT 1),
                $bc, 0)", ("$p", productId), ("$bc", baseCost));
        return priceMode == "STANDARD" ? supCost : supCost + installFee;
    }

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    private static long Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(c, tx, sql, args);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
    }
}
