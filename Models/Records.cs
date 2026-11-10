namespace TNM.Management.Models;

public record DashboardVm(long Products, long Customers, long Stock, long Consigned, long Active, long Due30,
    long OpenOrders, long Unpaid, long Revenue, long Received, long RenewalRevenue);
public record ProductVm(long Id, string Sku, string Name, string Group, long Cost, long Renewal,
    long Installation, int WarrantyMonths, bool IsSerialized, long StockInHand = 0, string Kind = "GOODS");
public record CustomerVm(long Id, string Code, string Name, string Group, string Source, string Phone, string Address = "", string RepresentativeName = "", long? SellerId = null)
{
    public string B2BDisplayName => string.IsNullOrWhiteSpace(RepresentativeName) ? Name : $"{Name} (ĐD: {RepresentativeName})";
}
public record StockVm(long Id, string Imei, string Sku, string Product, string Status, string Holder, string UpdatedAt, long ProductId, long? WarehouseId = null);
public record ProductStockSummaryVm(long ProductId, string Sku, string ProductName, bool IsSerialized, string Kind, long InStockQty, long ConsignedOrTechQty, long InstalledQty);
public record DeviceVm(long ActivationId, string Imei, string Sku, string Customer, string Plate,
    string ActivatedAt, string ExpiresAt, long CustomerId, long ProductId, string VinNo = "", string OrderCode = "", string ContactName = "", long? SellerId = null, string SellerName = "");
public record InvoiceLineVm(long Id, long InvoiceId, string Description, string ProductSku, long Quantity, long UnitPrice)
{
    public long LineTotal => Quantity * UnitPrice;
}
public record PaymentRowVm(long Id, string PaymentNo, string Direction, string Method, long Amount, string InvoiceNo, string InvoiceType, string Partner, string PaidAt, string Notes = "", long BalanceAfterMethodVnd = 0, long BalanceAfterTotalVnd = 0);
public record CashBookSummaryVm(
    long TotalInVnd,
    long TotalOutVnd,
    long TotalBalanceVnd,
    long CashInVnd,
    long CashOutVnd,
    long CashBalanceVnd,
    long BankInVnd,
    long BankOutVnd,
    long BankBalanceVnd,
    long CustomerReceivedVnd,
    long SupplierPaidVnd,
    long OtherPaidVnd);
public record PartnerDebtSummaryVm(
    string CategoryCode,
    string CategoryLabel,
    string PartnerCode,
    string PartnerName,
    string SubInfo,
    long InvoiceCount,
    long TotalIncurredVnd,
    long TotalPaidVnd,
    long RemainingDebtVnd);
public record InvoiceVm(long Id, string Code, string Type, string Customer, long Total, long Paid,
    string Status, string CreatedAt, string DueAt, string Notes = "", string RefCode = "", List<InvoiceLineVm>? Lines = null)
{
    public long Balance => Total - Paid;
    public bool IsReceivable => Type is "SALE" or "RENEWAL";
}
public record LedgerVm(long Id, string At, string Type, string Sku, string Imei, string Target, string Actor);
public record AuditVm(long Id, string At, string Actor, string Action, string Entity, string Details);
public record RenewalVm(long Id, string Imei, string Source, string StartDate, string EndDate, long Price, string PaymentMethod, long? SellerId = null, string SellerName = "", string CustomerName = "", string PlateNo = "");
public record SellerChoiceVm(long Id, string Code, string Name, string Phone, string Department, long OrderCommissionVnd, long RenewalCommissionVnd)
{
    public string DisplayLabel => $"{Code} — {Name}";
}
public record CompletedOrderSnapshotVm(
    long OrderId,
    string OrderCode,
    string CompletedAt,
    string ActivatedAt,
    string CustomerCode,
    string CustomerName,
    string ContactName,
    long? SellerId,
    string SellerDisplay,
    string ProductSummary,
    long TotalQty,
    long RevenueSnapshotVnd,
    long CostSnapshotVnd,
    long InstallerFeeSnapshotVnd,
    string InstallerDisplay,
    bool DeductStock,
    string InvoiceNo,
    string InvoiceStatus,
    string OrderStatus = "COMPLETED")
{
    public long GrossProfitSnapshotVnd => RevenueSnapshotVnd - CostSnapshotVnd - InstallerFeeSnapshotVnd;
    public bool IsLocked => OrderStatus is "COMPLETED" or "ACTIVATION_BATCH";
}
public record SellerPeriodStatVm(
    long SellerId,
    string SellerCode,
    string SellerName,
    string Phone,
    string Department,
    long OrderCommissionRateVnd,
    long RenewalCommissionRateVnd,
    long CompletedOrderCount,
    long CompletedDeviceQty,
    long OrderRevenueSnapshotVnd,
    long OrderCostSnapshotVnd,
    long OrderInstallerFeeSnapshotVnd,
    long RenewalCount,
    long RenewalRevenueSnapshotVnd,
    long InvoicedCommissionVnd)
{
    public long OrderGrossProfitVnd => OrderRevenueSnapshotVnd - OrderCostSnapshotVnd - OrderInstallerFeeSnapshotVnd;
    public long TotalRevenueVnd => OrderRevenueSnapshotVnd + RenewalRevenueSnapshotVnd;
    public long EstimatedOrderCommissionVnd => CompletedDeviceQty * OrderCommissionRateVnd;
    public long EstimatedRenewalCommissionVnd => RenewalCount * RenewalCommissionRateVnd;
    public long TotalEstimatedCommissionVnd => EstimatedOrderCommissionVnd + EstimatedRenewalCommissionVnd;
}

public sealed class PagerState
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    public int PageCount(int totalCount) => Math.Max(1, (Math.Max(0, totalCount) + PageSize - 1) / Math.Max(1, PageSize));
    public int CurrentPage(int totalCount) => Math.Clamp(Page, 1, PageCount(totalCount));
    public int StartIndex(int totalCount) => (CurrentPage(totalCount) - 1) * PageSize;
    public void Reset() => Page = 1;

    public List<(int Stt, T Item)> Slice<T>(IEnumerable<T> source, out int totalCount)
    {
        var list = source as IReadOnlyList<T> ?? source.ToList();
        totalCount = list.Count;
        var start = StartIndex(totalCount);
        var count = Math.Min(PageSize, Math.Max(0, totalCount - start));
        var result = new List<(int Stt, T Item)>(count);
        for (var i = 0; i < count; i++)
            result.Add((start + i + 1, list[start + i]));
        return result;
    }
}
