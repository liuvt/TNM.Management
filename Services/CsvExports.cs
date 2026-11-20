using System.Text;
using TNM.Management.Models;

namespace TNM.Management.Services;

public static class CsvExports
{
    private static string Safe(string? value)
    {
        var v=value ?? "";
        // Avoid CSV formula injection when opened with spreadsheet programs.
        // Signed integer stock counts must remain numeric in Excel, including negative book balances.
        var isInteger = long.TryParse(v, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out _);
        if (!isInteger && v.Length>0 && (v[0]=='=' || v[0]=='+' || v[0]=='-' || v[0]=='@')) v="'"+v;
        return "\""+v.Replace("\"","\"\"")+"\"";
    }
    private static byte[] Encode(IEnumerable<IEnumerable<string>> rows)
    {
        var text="sep=;\r\n"+string.Join("\r\n",rows.Select(row=>string.Join(";",row.Select(Safe))))+"\r\n";
        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
    }
    public static byte[] Inventory(DemoDb db)
    {
        var rows=new List<IEnumerable<string>> { new[] { "Số IMEI", "Mã sản phẩm", "Tên thiết bị", "Trạng thái", "Nơi giữ", "Cập nhật" } };
        rows.AddRange(db.Stock().Select(x=>(IEnumerable<string>)new[] { x.Imei, x.Sku, x.Product, VnLabels.StockStatus(x.Status), x.Holder, VnDateTimeHelper.FormatDisplay(x.UpdatedAt) }));
        return Encode(rows);
    }
    public static byte[] ProductCatalog(ProductMaster master)
    {
        var rows=new List<IEnumerable<string>> { new[] {
            "Mã sản phẩm","Tên sản phẩm","Nhóm sản phẩm","Phân loại","Đơn vị tính","Quản lý IMEI","Bảo hành (tháng)","Trạng thái" } };
        rows.AddRange(master.Products(includeInactive:true).Select(x=>(IEnumerable<string>)new [] {
            x.Sku,x.Name,x.GroupName,VnLabels.ProductKind(x.Kind),x.Unit,x.UsesImei?"Có":"Không",
            x.WarrantyMonths.ToString(),x.IsActive?"Đang kinh doanh":"Ngừng kinh doanh" }));
        return Encode(rows);
    }
    public static byte[] QuantityLedger(V6Ops ops,long userId)
    {
        var rows=new List<IEnumerable<string>> { new [] {"Thời gian","Số phiếu","Mã sản phẩm","Kho","Vị trí","Số lượng +/-","Nghiệp vụ kho","Ghi chú"} };
        rows.AddRange(ops.Movements(userId,200000).Select(x=>(IEnumerable<string>)new [] {
            VnDateTimeHelper.FormatDisplay(x.When),x.Document,x.Sku,x.Warehouse,x.Location,x.Delta.ToString(),VnLabels.QuantityMovementType(x.Type),x.Note
        }));
        return Encode(rows);
    }
    public static byte[] Invoices(DemoDb db, IEnumerable<string>? visibleTypes = null, string? categoryFilter = null)
    {
        var list = db.Invoices().Where(x => visibleTypes is null || visibleTypes.Contains(x.Type));
        if (!string.IsNullOrWhiteSpace(categoryFilter) && categoryFilter != "ALL")
        {
            list = categoryFilter.ToUpperInvariant() switch
            {
                "PURCHASE" => list.Where(x => x.Type is "PURCHASE" or "SUPPLIER"),
                "SALE" => list.Where(x => x.Type == "SALE"),
                "RENEWAL" => list.Where(x => x.Type == "RENEWAL"),
                "OTHER" => list.Where(x => x.Type is "SERVICE" or "COMMISSION" || x.Type is not ("SALE" or "RENEWAL" or "PURCHASE" or "SUPPLIER")),
                _ => list.Where(x => string.Equals(x.Type, categoryFilter, StringComparison.OrdinalIgnoreCase))
            };
        }
        var rows = new List<IEnumerable<string>> { new[] {
            "Kỳ Tháng/Năm", "Số chứng từ", "Loại công nợ", "Tính chất", "Đối tác / Thụ hưởng", "Tham chiếu",
            "Chi tiết mặt hàng / Nội dung", "Phát sinh (VND)", "Đã thanh toán (VND)", "Còn nợ (VND)",
            "Trạng thái", "Ngày lập", "Đến hạn", "Ghi chú"
        } };
        foreach (var x in list)
        {
            var lineSummary = x.Lines is { Count: > 0 }
                ? string.Join(" | ", x.Lines.Select(l => $"{(string.IsNullOrWhiteSpace(l.ProductSku) ? "" : $"[{l.ProductSku}] ")}{l.Description} (SL: {l.Quantity} × {l.UnitPrice:N0})"))
                : x.Notes;
            var nature = x.IsReceivable ? "Khoản phải thu" : "Khoản phải trả";
            rows.Add(new[] {
                x.PeriodLabel, x.Code, VnLabels.InvoiceType(x.Type), nature, x.Customer, x.RefCode, lineSummary,
                x.Total.ToString(), x.Paid.ToString(), x.Balance.ToString(),
                VnLabels.InvoiceStatus(x.Status), VnDateTimeHelper.FormatDisplay(x.CreatedAt), VnDateTimeHelper.FormatDisplay(x.DueAt, false), x.Notes
            });
        }
        return Encode(rows);
    }

    public static byte[] CashBook(DemoDb db)
    {
        var summary = db.CashBookSummary();
        var rows = new List<IEnumerable<string>>
        {
            new[] { "BÁO CÁO SỔ QUỸ (THU / CHI / TỒN) — TIỀN MẶT & NGÂN HÀNG" },
            new[] { "Chỉ tiêu", "Tổng Thu", "Tổng Chi", "Tồn quỹ hiện tại (Thu - Chi)" },
            new[] { "1. Quỹ Tiền mặt", summary.CashInVnd.ToString(), summary.CashOutVnd.ToString(), summary.CashBalanceVnd.ToString() },
            new[] { "2. Quỹ Ngân hàng", summary.BankInVnd.ToString(), summary.BankOutVnd.ToString(), summary.BankBalanceVnd.ToString() },
            new[] { "TỔNG CỘNG SỔ QUỸ", summary.TotalInVnd.ToString(), summary.TotalOutVnd.ToString(), summary.TotalBalanceVnd.ToString() },
            Array.Empty<string>(),
            new[] { "Số phiếu", "Loại giao dịch", "Hạng mục Thu/Chi", "Nguồn quỹ", "Chứng từ công nợ", "Đối tác / Khách hàng / NCC / Nhân viên bán hàng", "Số tiền Thu (+)", "Số tiền Chi (-)", "Tồn quỹ sau GD", "Ngày giao dịch", "Diễn giải" }
        };

        foreach (var p in db.Payments())
        {
            var itemCat = p.Direction == "IN"
                ? "Khoản thu: Thu khách / Công nợ"
                : p.InvoiceType is "PURCHASE" or "SUPPLIER"
                    ? "Khoản chi: Nhập hàng NCC"
                    : "Khoản chi: Hoa hồng NV bán hàng / Kỹ thuật";
            rows.Add(new[]
            {
                p.PaymentNo,
                VnLabels.PaymentDirection(p.Direction),
                itemCat,
                VnLabels.PaymentMethod(p.Method),
                p.InvoiceNo,
                p.Partner,
                p.Direction == "IN" ? p.Amount.ToString() : "0",
                p.Direction == "OUT" ? p.Amount.ToString() : "0",
                p.BalanceAfterTotalVnd.ToString(),
                VnDateTimeHelper.FormatDisplay(p.PaidAt),
                p.Notes
            });
        }
        return Encode(rows);
    }
}
