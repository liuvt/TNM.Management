namespace TNM.Management.Models;

public record DashboardVm(long Products, long Customers, long Stock, long Consigned, long Active, long Due30,
    long OpenOrders, long Unpaid, long Revenue, long Received, long RenewalRevenue);
public record ProductVm(long Id, string Sku, string Name, string Group, long Cost, long Renewal,
    long Installation, int WarrantyMonths, bool IsSerialized);
public record CustomerVm(long Id, string Code, string Name, string Group, string Source, string Phone, string Address = "", string RepresentativeName = "")
{
    public string B2BDisplayName => string.IsNullOrWhiteSpace(RepresentativeName) ? Name : $"{Name} (ĐD: {RepresentativeName})";
}
public record StockVm(long Id, string Imei, string Sku, string Product, string Status, string Holder, string UpdatedAt, long ProductId);
public record DeviceVm(long ActivationId, string Imei, string Sku, string Customer, string Plate,
    string ActivatedAt, string ExpiresAt, long CustomerId, long ProductId);
public record InvoiceLineVm(long Id, long InvoiceId, string Description, string ProductSku, long Quantity, long UnitPrice)
{
    public long LineTotal => Quantity * UnitPrice;
}
public record PaymentRowVm(long Id, string PaymentNo, string Direction, string Method, long Amount, string InvoiceNo, string InvoiceType, string Partner, string PaidAt);
public record InvoiceVm(long Id, string Code, string Type, string Customer, long Total, long Paid,
    string Status, string CreatedAt, string DueAt, string Notes = "", string RefCode = "", List<InvoiceLineVm>? Lines = null)
{
    public long Balance => Total - Paid;
    public bool IsReceivable => Type is "SALE" or "RENEWAL";
}
public record LedgerVm(long Id, string At, string Type, string Sku, string Imei, string Target, string Actor);
public record AuditVm(long Id, string At, string Actor, string Action, string Entity, string Details);
public record RenewalVm(long Id, string Imei, string Source, string StartDate, string EndDate, long Price, string PaymentMethod);
