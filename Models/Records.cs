using TNM.Management.Services;

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
    string ActivatedAt, string ExpiresAt, long CustomerId, long ProductId, string VinNo = "", string OrderCode = "", string ContactName = "", long? SellerId = null, string SellerName = "",
    string DeviceTypeLabel = "", string AgencyName = "", string OwnerName = "", string OwnerPhone = "", string Status = "ACTIVE", string LastRenewedAt = "", long RenewalCount = 0, long RenewalPriceVnd = 0);
public record DeletedDeviceVm(
    long ActivationId,
    string Imei,
    string Sku,
    string DeviceTypeLabel,
    string Plate,
    string CustomerName,
    string OwnerName,
    string OwnerPhone,
    string ActivatedAt,
    string ExpiresAt,
    string DeletedAt,
    string DeletedByName,
    string DeletedSummary);
public record DeviceImportResultVm(int TotalRows, int UpdatedExisting, int InsertedNew, int SkippedInvalid, string SummaryMessage);
public record OrderSnapshotLineVm(
    string Sku,
    string SubDocOrTime,
    string ItemName,
    string DetailNote,
    long Quantity,
    long UnitPriceVnd)
{
    public long LineTotalVnd => Quantity * UnitPriceVnd;

    public static OrderSnapshotLineVm FromDescription(string? rawSku, string? rawDescription, long quantity, long unitPrice)
    {
        var sku = (rawSku ?? "").Trim();
        if (sku == "—") sku = "";
        var desc = (rawDescription ?? "").Trim();
        var subDocOrTime = "";

        if (desc.StartsWith('['))
        {
            var closeIdx = desc.IndexOf(']');
            if (closeIdx > 1)
            {
                subDocOrTime = desc[1..closeIdx].Trim();
                desc = desc[(closeIdx + 1)..].Trim();
            }
        }

        string itemName;
        string detailNote = "";

        var dashIdx = desc.IndexOf(" — ", StringComparison.Ordinal);
        if (dashIdx > 0)
        {
            itemName = desc[..dashIdx].Trim();
            detailNote = desc[(dashIdx + 3)..].Trim();
        }
        else if (desc.StartsWith("Gia hạn ", StringComparison.OrdinalIgnoreCase) && desc.Contains("(IMEI:", StringComparison.OrdinalIgnoreCase))
        {
            var parenIdx = desc.IndexOf("(IMEI:", StringComparison.OrdinalIgnoreCase);
            itemName = desc[..parenIdx].Trim().TrimEnd('·', ' ');
            detailNote = desc[parenIdx..].Trim();
        }
        else
        {
            itemName = desc;
        }

        // Bóc mã SKU ở cuối tên mặt hàng nếu trùng hoặc nếu cột SKU đang trống: VD "Sim 5G79 (30 ngày) (SP000062)"
        if (itemName.EndsWith(')'))
        {
            var lastOpen = itemName.LastIndexOf('(');
            if (lastOpen > 0)
            {
                var trailingCode = itemName[(lastOpen + 1)..^1].Trim();
                if (!string.IsNullOrWhiteSpace(trailingCode) && trailingCode.Length <= 22 && !trailingCode.Contains(' '))
                {
                    if (string.IsNullOrWhiteSpace(sku))
                        sku = trailingCode;
                    if (string.Equals(sku, trailingCode, StringComparison.OrdinalIgnoreCase))
                        itemName = itemName[..lastOpen].Trim();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(itemName))
            itemName = string.IsNullOrWhiteSpace(sku) ? "Mặt hàng / Dịch vụ" : sku;

        return new OrderSnapshotLineVm(sku, subDocOrTime, itemName, detailNote, quantity, unitPrice);
    }
}

public record InvoiceLineVm(long Id, long InvoiceId, string Description, string ProductSku, long Quantity, long UnitPrice)
{
    public long LineTotal => Quantity * UnitPrice;
    public OrderSnapshotLineVm Parsed => OrderSnapshotLineVm.FromDescription(ProductSku, Description, Quantity, UnitPrice);
}
public record PaymentRowVm(long Id, string PaymentNo, string Direction, string Method, long Amount, string InvoiceNo, string InvoiceType, string Partner, string PaidAt, string Notes = "", long BalanceAfterMethodVnd = 0, long BalanceAfterTotalVnd = 0)
{
    public DateTime? TimestampDt => VnDateTimeHelper.ParseDbTimestamp(PaidAt, false);
    public string DisplayTimestamp => VnDateTimeHelper.FormatDisplay(PaidAt, false);
    public string PeriodKey => TimestampDt.HasValue ? TimestampDt.Value.ToString("yyyy-MM") : (!string.IsNullOrWhiteSpace(PaidAt) && PaidAt.Length >= 7 && PaidAt[4] == '-' ? PaidAt[..7] : DateTime.Today.ToString("yyyy-MM"));
    public int PeriodYear => TimestampDt?.Year ?? (PeriodKey.Length >= 4 && int.TryParse(PeriodKey[..4], out var y) ? y : DateTime.Today.Year);
    public int PeriodMonth => TimestampDt?.Month ?? (PeriodKey.Length >= 7 && int.TryParse(PeriodKey[5..7], out var m) ? m : DateTime.Today.Month);
    public string PeriodLabel => $"Tháng {PeriodMonth:D2}/{PeriodYear}";
}
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
    long RemainingDebtVnd,
    string PeriodKey = "",
    string PeriodLabel = "",
    long CarryOverOldDebtVnd = 0)
{
    public long CumulativeRemainingVnd => CarryOverOldDebtVnd + RemainingDebtVnd;
    public int PeriodYear => !string.IsNullOrWhiteSpace(PeriodKey) && PeriodKey.Length >= 4 && int.TryParse(PeriodKey[..4], out var y) ? y : 0;
    public int PeriodMonth => !string.IsNullOrWhiteSpace(PeriodKey) && PeriodKey.Length >= 7 && int.TryParse(PeriodKey[5..7], out var m) ? m : 0;
}
public record InvoiceVm(long Id, string Code, string Type, string Customer, long Total, long Paid,
    string Status, string CreatedAt, string DueAt, string Notes = "", string RefCode = "", List<InvoiceLineVm>? Lines = null,
    bool IsLocked = false, string LockedAt = "", long? RenewalId = null)
{
    public long Balance => Total - Paid;
    public bool IsReceivable => Type is "SALE" or "RENEWAL";
    public DateTime? TimestampDt => VnDateTimeHelper.ParseDbTimestamp(CreatedAt, false);
    public string DisplayTimestamp => VnDateTimeHelper.FormatDisplay(CreatedAt, false);
    public string DisplayDueDate => VnDateTimeHelper.FormatDateOnly(DueAt);
    public string PeriodKey => TimestampDt.HasValue ? TimestampDt.Value.ToString("yyyy-MM") : (!string.IsNullOrWhiteSpace(CreatedAt) && CreatedAt.Length >= 7 && CreatedAt[4] == '-' ? CreatedAt[..7] : VnDateTimeHelper.TodayVn.ToString("yyyy-MM"));
    public int PeriodYear => TimestampDt?.Year ?? (PeriodKey.Length >= 4 && int.TryParse(PeriodKey[..4], out var y) ? y : VnDateTimeHelper.TodayVn.Year);
    public int PeriodMonth => TimestampDt?.Month ?? (PeriodKey.Length >= 7 && int.TryParse(PeriodKey[5..7], out var m) ? m : VnDateTimeHelper.TodayVn.Month);
    public string PeriodLabel => $"Tháng {PeriodMonth:D2}/{PeriodYear}";

    public bool MatchesRange(DateTime? from, DateTime? to)
    {
        if (!from.HasValue && !to.HasValue) return true;
        if (VnDateTimeHelper.InRange(TimestampDt, from, to)) return true;
        if ((Code.StartsWith("HD-KH", StringComparison.OrdinalIgnoreCase) || Code.StartsWith("NK-NCC", StringComparison.OrdinalIgnoreCase)) && Lines is { Count: > 1 })
        {
            return Lines.Any(l =>
            {
                var ldt = VnDateTimeHelper.TryExtractLineTimestamp(l.Description, null);
                return ldt.HasValue && VnDateTimeHelper.InRange(ldt, from, to);
            });
        }
        return false;
    }
}
public record LedgerVm(long Id, string At, string Type, string Sku, string Imei, string Target, string Actor);
public record AuditVm(long Id, string At, string Actor, string Action, string Entity, string Details);
public record RenewalVm(long Id, string Imei, string Source, string StartDate, string EndDate, long Price, string PaymentMethod,
    long? SellerId = null, string SellerName = "", string CustomerName = "", string PlateNo = "",
    long ActivationId = 0, string PreviousExpiry = "", bool IsLocked = false, string LockedAt = "", string InvoiceNo = "", long? InvoiceId = null,
    string DeviceName = "", string ProductSku = "");
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
    string OrderStatus = "COMPLETED",
    List<OrderSnapshotLineVm>? Lines = null)
{
    public long GrossProfitSnapshotVnd => RevenueSnapshotVnd - CostSnapshotVnd - InstallerFeeSnapshotVnd;
    public bool IsLocked => OrderStatus is "COMPLETED" or "ACTIVATION_BATCH";
    public DateTime? TimestampDt => VnDateTimeHelper.ParseDbTimestamp(!string.IsNullOrWhiteSpace(CompletedAt) ? CompletedAt : ActivatedAt, false);
    public string DisplayTimestamp => VnDateTimeHelper.FormatDisplay(!string.IsNullOrWhiteSpace(CompletedAt) ? CompletedAt : ActivatedAt, false);
    public string DisplayActivatedDate => !string.IsNullOrWhiteSpace(ActivatedAt) ? VnDateTimeHelper.FormatDateOnly(ActivatedAt) : DisplayTimestamp;
    public string PeriodKey => TimestampDt.HasValue ? TimestampDt.Value.ToString("yyyy-MM") : VnDateTimeHelper.TodayVn.ToString("yyyy-MM");
    public int PeriodYear => TimestampDt?.Year ?? VnDateTimeHelper.TodayVn.Year;
    public int PeriodMonth => TimestampDt?.Month ?? VnDateTimeHelper.TodayVn.Month;
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
