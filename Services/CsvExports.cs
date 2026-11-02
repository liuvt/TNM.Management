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
        var rows=new List<IEnumerable<string>> { new[] { "IMEI", "SKU", "Tên thiết bị", "Trạng thái", "Nơi giữ", "Cập nhật" } };
        rows.AddRange(db.Stock().Select(x=>(IEnumerable<string>)new[] { x.Imei, x.Sku, x.Product, x.Status, x.Holder, x.UpdatedAt }));
        return Encode(rows);
    }
    public static byte[] ProductCatalog(ProductMaster master)
    {
        var rows=new List<IEnumerable<string>> { new[] {
            "Mã sản phẩm","Tên sản phẩm","Nhóm sản phẩm","Loại","Đơn vị tính","IMEI","Bảo hành (tháng)","Trạng thái" } };
        rows.AddRange(master.Products(includeInactive:true).Select(x=>(IEnumerable<string>)new [] {
            x.Sku,x.Name,x.GroupName,x.Kind,x.Unit,x.UsesImei?"Có":"Không",
            x.WarrantyMonths.ToString(),x.IsActive?"Đang kinh doanh":"Ngừng kinh doanh" }));
        return Encode(rows);
    }
    public static byte[] QuantityLedger(V6Ops ops,long userId)
    {
        var rows=new List<IEnumerable<string>> { new [] {"Ngày","Số phiếu","SKU","Kho","Vị trí","Số lượng +/-","Loại","Ghi chú"} };
        rows.AddRange(ops.Movements(userId,200000).Select(x=>(IEnumerable<string>)new [] {
            x.When,x.Document,x.Sku,x.Warehouse,x.Location,x.Delta.ToString(),x.Type,x.Note
        }));
        return Encode(rows);
    }
    public static byte[] Invoices(DemoDb db, IEnumerable<string>? visibleTypes = null)
    {
        var rows=new List<IEnumerable<string>> { new[] { "Số hóa đơn", "Loại", "Đối tác", "Phát sinh VND", "Đã thanh toán VND", "Còn nợ VND", "Trạng thái", "Đến hạn" } };
        rows.AddRange(db.Invoices().Where(x=>visibleTypes is null || visibleTypes.Contains(x.Type)).Select(x=>(IEnumerable<string>)new[] { x.Code, x.Type, x.Customer, x.Total.ToString(), x.Paid.ToString(), x.Balance.ToString(), x.Status, x.DueAt }));
        return Encode(rows);
    }
}
