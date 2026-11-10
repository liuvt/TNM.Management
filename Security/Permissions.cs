namespace TNM.Management.Security;

public sealed record PermissionDefinition(string Key, string Area, string Feature, string Label, int ExcelRow, bool Implemented);

public static class Permissions
{
    public const string DashboardView = "dashboard.view";
    public const string DashboardRevenue = "dashboard.revenue.view";
    public const string DashboardProfit = "dashboard.profit.view";
    public const string DashboardStock = "dashboard.stock.view";
    public const string DashboardDebt = "dashboard.debt.view";
    public const string DashboardOrders = "dashboard.orders.view";
    public const string DashboardDevices = "dashboard.devices.view";
    public const string DashboardRenewals = "dashboard.renewals.view";
    public const string DashboardKpi = "dashboard.kpi.view";
    public const string ProductsView = "products.view";
    public const string ProductsCreate = "products.create";
    public const string ProductsUpdate = "products.update";
    public const string ProductsExport = "products.export";
    public const string CustomersView = "customers.view";
    public const string CustomersCreate = "customers.create";
    public const string CustomersUpdate = "customers.update";
    public const string PricesView = "prices.view";
    public const string PricesUpdate = "prices.update";
    public const string InventoryView = "inventory.view";
    public const string InventoryReceive = "inventory.receive";
    public const string InventoryConsignOut = "inventory.consign_out";
    public const string InventoryConsignReturn = "inventory.consign_return";
    public const string InventoryTechOut = "inventory.tech_out";
    public const string InventoryTechReturn = "inventory.tech_return";
    public const string InventoryWarranty = "inventory.warranty";
    public const string StockCountView = "stock_count.view";
    public const string StockCountReview = "stock_count.review";
    public const string StockCountExport = "stock_count.export";
    public const string InventoryConsignCount = "inventory.consign_count.view";
    public const string InventoryLedgerView = "inventory.ledger.view";
    public const string InventoryLedgerExport = "inventory.ledger.export";
    public const string OrdersView = "orders.view";
    public const string OrdersCreate = "orders.create";
    public const string OrdersUpdate = "orders.update";
    public const string OrdersActivate = "orders.activate";
    public const string OrdersCancel = "orders.cancel";
    public const string OrdersReportSource = "orders.report_source";
    public const string OrdersReportCustomer = "orders.report_customer";
    public const string OrdersReportProduct = "orders.report_product";
    public const string DevicesView = "devices.view";
    public const string DevicesActiveView = "devices.active.view";
    public const string DevicesOverdueView = "devices.overdue.view";
    public const string DevicesDue10View = "devices.due10.view";
    public const string DevicesDue30View = "devices.due30.view";
    public const string DevicesDue60View = "devices.due60.view";
    public const string DevicesRenew = "devices.renew";
    public const string DevicesUpdate = "devices.update";
    public const string DevicesRenewalsView = "devices.renewals.view";
    public const string DevicesReportSource = "devices.report_source";
    public const string DevicesReportTime = "devices.report_time";
    public const string DevicesReportKpi = "devices.report_kpi";
    public const string FinanceView = "finance.view";
    public const string FinanceSupplierView = "finance.supplier.view";
    public const string FinanceCustomerView = "finance.customer.view";
    public const string FinanceRenewView = "finance.renewal.view";
    public const string FinanceOtherView = "finance.other.view";
    public const string FinancePay = "finance.pay";
    public const string FinanceExport = "finance.export";
    public const string ReportsView = "reports.view";
    public const string ReportsAuditView = "reports.audit.view";
    public const string ArchitectureView = "architecture.view";
    public const string UsersView = "users.view";
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string UsersPermissions = "users.permissions";

    public const string QuantityStockView = "inventory.quantity.view";
    public const string QuantityMove = "inventory.quantity.move";
    public const string WarehouseLocationManage = "inventory.location.manage";
    public const string QuantityCountView = "stock_count.quantity.view";
    public const string QuantityCountCreate = "stock_count.quantity.create";
    public const string QuantityCountApprove = "stock_count.quantity.approve";
    public const string PurchaseView = "purchase.view";
    public const string PurchaseCreate = "purchase.create";
    public const string PurchaseReceive = "purchase.receive";
    public const string WarehouseScopeManage = "users.warehouse_scope.manage";
    public const string MasterDataManage = "system.master_data.manage";

    // Giữ khóa cũ cho nâng cấp dữ liệu từ v4; không còn là quyền thực thi độc lập.
    public const string InventoryMove = "inventory.move";
    public const string ReportsExport = "reports.export";
    public static readonly PermissionDefinition[] Definitions =
    [
        new(DashboardView, "Dashboard điều hành", "Truy cập Dashboard", "Mở trang tổng quan", 68, true),
        new(DashboardRevenue, "Dashboard điều hành", "Doanh thu", "Xem tổng doanh thu", 68, true),
        new(DashboardProfit, "Dashboard điều hành", "Lợi nhuận", "Xem lợi nhuận gộp ước tính", 69, true),
        new(DashboardStock, "Dashboard điều hành", "Tồn kho", "Xem tồn và ký gửi", 70, true),
        new(DashboardDebt, "Dashboard điều hành", "Công nợ", "Xem số dư và đã thu", 71, true),
        new(DashboardOrders, "Dashboard điều hành", "Đơn hàng", "Xem lượng đơn chưa kích hoạt", 72, true),
        new(DashboardDevices, "Dashboard điều hành", "Thiết bị đang hoạt động", "Xem thiết bị đang hoạt động", 73, true),
        new(DashboardRenewals, "Dashboard điều hành", "Gia hạn", "Xem số thiết bị đến hạn 30 ngày", 74, true),
        new(DashboardKpi, "Dashboard điều hành", "KPI", "Xem bảng KPI điều hành", 75, false),
        new(ProductsView, "Khai báo thông tin", "Khai báo sản phẩm", "Danh sách, xem chi tiết sản phẩm", 3, true),
        new(ProductsCreate, "Khai báo thông tin", "Khai báo sản phẩm", "Tạo mới sản phẩm/SKU", 3, true),
        new(ProductsUpdate, "Khai báo thông tin", "Khai báo sản phẩm", "Chỉnh sửa sản phẩm/SKU", 10, true),
        new(ProductsExport, "Khai báo thông tin", "Danh sách sản phẩm", "Xuất danh mục sản phẩm CSV", 16, true),
        new(CustomersView, "Khai báo thông tin", "Nhóm khách hàng", "Xem danh sách khách hàng", 11, true),
        new(CustomersCreate, "Khai báo thông tin", "Nhóm khách hàng", "Khai báo khách hàng mới", 11, true),
        new(CustomersUpdate, "Khai báo thông tin", "Nhóm khách hàng", "Chỉnh sửa hồ sơ khách hàng", 15, true),
        new(PricesView, "Khai báo thông tin", "Bảng giá khách hàng", "Xem bảng giá theo khách hàng", 14, true),
        new(PricesUpdate, "Khai báo thông tin", "Bảng giá khách hàng", "Thiết lập/sửa giá bán, gia hạn", 14, true),
        new(InventoryView, "Quản lý kho", "Kho IMEI", "Xem kho và tra cứu IMEI", 18, true),
        new(InventoryReceive, "Quản lý kho", "Nhập kho", "Nhập kho hàng mới / IMEI", 18, true),
        new(InventoryConsignOut, "Quản lý kho", "Xuất kho khách hàng", "Xuất ký gửi khách hàng", 22, true),
        new(InventoryConsignReturn, "Quản lý kho", "Nhập kho thu hồi", "Thu hồi hàng ký gửi", 21, true),
        new(InventoryTechOut, "Quản lý kho", "Xuất kho kỹ thuật", "Xuất hàng kỹ thuật/nội bộ", 26, true),
        new(InventoryTechReturn, "Quản lý kho", "Nhập kho thu hồi", "Thu hồi từ kỹ thuật", 21, true),
        new(InventoryWarranty, "Quản lý kho", "Bảo hành/Bảo trì", "Xử lý phiếu bảo hành, bảo trì", 29, true),
        new(StockCountView, "Quản lý kho", "Kiểm kê trong kho", "Xem kiểm kho và tồn sổ", 35, false),
        new(StockCountReview, "Quản lý kho", "Kiểm kê trong kho", "Xác minh chênh lệch kiểm kho", 35, false),
        new(StockCountExport, "Quản lý kho", "Kiểm kê trong kho", "Xuất báo cáo kiểm kê CSV", 35, false),
        new(InventoryConsignCount, "Quản lý kho", "Kiểm kê hàng ký gửi", "Xem kiểm kê chi tiết hàng ký gửi", 36, false),
        new(InventoryLedgerView, "Quản lý kho", "Báo cáo xuất nhập kho", "Xem sổ kho theo IMEI và người tác động", 37, true),
        new(InventoryLedgerExport, "Quản lý kho", "Báo cáo xuất nhập kho", "Xuất báo cáo kho CSV", 37, true),
        new(OrdersView, "Đơn hàng", "Quản lý đơn hàng", "Xem danh sách đơn hàng", 38, true),
        new(OrdersCreate, "Đơn hàng", "Tạo đơn hàng", "Lập đơn hàng mới", 38, true),
        new(OrdersUpdate, "Đơn hàng", "Cập nhật trạng thái", "Thay đổi thông tin đơn hàng", 47, true),
        new(OrdersActivate, "Đơn hàng", "Cập nhật trạng thái", "Kích hoạt đơn / gắn IMEI", 47, true),
        new(OrdersCancel, "Đơn hàng", "Cập nhật trạng thái", "Hủy đơn (giữ lịch sử)", 47, true),
        new(OrdersReportSource, "Đơn hàng", "Báo cáo đơn hàng", "Báo cáo theo nguồn hàng", 50, false),
        new(OrdersReportCustomer, "Đơn hàng", "Báo cáo đơn hàng", "Báo cáo theo khách hàng", 51, false),
        new(OrdersReportProduct, "Đơn hàng", "Báo cáo đơn hàng", "Báo cáo theo mã sản phẩm", 52, false),
        new(DevicesView, "Quản lý thiết bị", "Quản lý thiết bị", "Truy cập màn hình thiết bị", 53, true),
        new(DevicesActiveView, "Quản lý thiết bị", "Đang hoạt động", "Xem thiết bị còn hạn", 53, true),
        new(DevicesOverdueView, "Quản lý thiết bị", "Quá hạn", "Xem thiết bị quá hạn", 54, true),
        new(DevicesDue10View, "Quản lý thiết bị", "Đến hạn", "Xem đến hạn trong 10 ngày", 55, true),
        new(DevicesDue30View, "Quản lý thiết bị", "Đến hạn", "Xem đến hạn trong 30 ngày", 56, true),
        new(DevicesDue60View, "Quản lý thiết bị", "Đến hạn", "Xem đến hạn trong 60 ngày", 57, true),
        new(DevicesRenew, "Quản lý thiết bị", "Gia hạn", "Gia hạn thiết bị và phát sinh công nợ", 58, true),
        new(DevicesUpdate, "Quản lý thiết bị", "Cập nhật thiết bị", "Sửa thông tin/biển số/lịch sử gia hạn thiết bị", 59, true),
        new(DevicesRenewalsView, "Quản lý thiết bị", "Lịch sử gia hạn", "Xem các lần gia hạn và số tiền", 58, true),
        new(DevicesReportSource, "Quản lý thiết bị", "Báo cáo gia hạn", "Báo cáo theo nguồn gia hạn", 60, false),
        new(DevicesReportTime, "Quản lý thiết bị", "Báo cáo gia hạn", "Báo cáo theo ngày/tháng/quý", 61, false),
        new(DevicesReportKpi, "Quản lý thiết bị", "Báo cáo gia hạn", "Báo cáo hiệu suất gia hạn", 62, false),
        new(FinanceView, "Công nợ & Thanh toán", "Sổ công nợ", "Truy cập màn hình tài chính", 63, true),
        new(FinanceSupplierView, "Công nợ & Thanh toán", "Công nợ nhà cung cấp", "Xem công nợ nhập hàng", 63, true),
        new(FinanceCustomerView, "Công nợ & Thanh toán", "Công nợ khách hàng", "Xem công nợ bán hàng", 64, true),
        new(FinanceRenewView, "Công nợ & Thanh toán", "Công nợ gia hạn", "Xem công nợ gia hạn", 65, true),
        new(FinanceOtherView, "Công nợ & Thanh toán", "Công nợ chi phí khác", "Xem công nợ kỹ thuật/hoa hồng/khác", 66, true),
        new(FinancePay, "Công nợ & Thanh toán", "Hạch toán", "Ghi thu / chi và phân bổ thanh toán", 67, true),
        new(FinanceExport, "Công nợ & Thanh toán", "Báo cáo công nợ", "Xuất công nợ được phép xem CSV", 63, true),
        new(ReportsView, "Báo cáo & Nhật ký", "Báo cáo", "Truy cập trang báo cáo", 37, true),
        new(ReportsAuditView, "Báo cáo & Nhật ký", "Nhật ký hệ thống", "Xem nhật ký thay đổi dữ liệu", 37, true),
        new(ArchitectureView, "Hệ thống", "Mô hình hệ thống", "Xem sơ đồ và mô hình dữ liệu", 1, true),
        new(UsersView, "Hệ thống", "Quản trị tài khoản", "Xem tài khoản và Role", 1, true),
        new(UsersManage, "Hệ thống", "Quản trị tài khoản", "Tạo, sửa, khóa và đặt lại mật khẩu", 1, true),
        new(RolesManage, "Hệ thống", "Quản trị vai trò", "Cấu hình bộ quyền Role", 1, true),
        new(UsersPermissions, "Hệ thống", "Phân quyền người dùng", "Cấp / từ chối từng chức năng cho User", 1, true),
        new(QuantityStockView,"Quản lý kho","Kho số lượng","Xem tồn số lượng theo kho/vị trí và sổ giao dịch",35,true),
        new(QuantityMove,"Quản lý kho","Kho số lượng","Nhập, xuất và điều chuyển số lượng",18,true),
        new(WarehouseLocationManage,"Quản lý kho","Vị trí","Tạo vị trí kho",35,true),
        new(QuantityCountView,"Quản lý kho","Kiểm kê số lượng","Xem các phiên kiểm kê số lượng",35,true),
        new(QuantityCountCreate,"Quản lý kho","Kiểm kê số lượng","Mở phiên, ghi số thực kiểm",35,true),
        new(QuantityCountApprove,"Quản lý kho","Kiểm kê số lượng","Phê duyệt và ghi điều chỉnh tồn",35,true),
        new(PurchaseView,"Mua hàng NCC","Đơn mua","Xem phiếu mua hàng và nhận hàng",18,true),
        new(PurchaseCreate,"Mua hàng NCC","Đơn mua","Tạo phiếu mua số lượng",18,true),
        new(PurchaseReceive,"Mua hàng NCC","Nhận hàng","Nhận hàng và ghi số lượng nhập kho",18,true),
        new(WarehouseScopeManage,"Hệ thống","Phân quyền kho","Giới hạn các kho User được thao tác",1,true),
        new(MasterDataManage,"Hệ thống","Danh mục nền","Tạo NCC, nhân sự, kho và vị trí chính",1,true),
    ];
    public static readonly IReadOnlyDictionary<string, PermissionDefinition> ByKey = Definitions.ToDictionary(p => p.Key, StringComparer.Ordinal);
    public static bool IsImplemented(string key) => ByKey.TryGetValue(key, out var p) && p.Implemented;
    public static readonly Dictionary<string,string> RequiredView = new(StringComparer.Ordinal)
    {
        [DashboardRevenue] = DashboardView,
        [DashboardProfit] = DashboardView,
        [DashboardStock] = DashboardView,
        [DashboardDebt] = DashboardView,
        [DashboardOrders] = DashboardView,
        [DashboardDevices] = DashboardView,
        [DashboardRenewals] = DashboardView,
        [DashboardKpi] = DashboardView,
        [ProductsCreate] = ProductsView,
        [ProductsUpdate] = ProductsView,
        [ProductsExport] = ProductsView,
        [CustomersCreate] = CustomersView,
        [CustomersUpdate] = CustomersView,
        [PricesView] = CustomersView,
        [PricesUpdate] = PricesView,
        [InventoryReceive] = InventoryView,
        [InventoryConsignOut] = InventoryView,
        [InventoryConsignReturn] = InventoryView,
        [InventoryTechOut] = InventoryView,
        [InventoryTechReturn] = InventoryView,
        [InventoryWarranty] = InventoryView,
        [InventoryLedgerView] = InventoryView,
        [InventoryLedgerExport] = InventoryView,
        [InventoryConsignCount] = InventoryView,
        [StockCountReview] = StockCountView,
        [StockCountExport] = StockCountView,
        [OrdersCreate] = OrdersView,
        [OrdersUpdate] = OrdersView,
        [OrdersActivate] = OrdersView,
        [OrdersCancel] = OrdersView,
        [OrdersReportSource] = OrdersView,
        [OrdersReportCustomer] = OrdersView,
        [OrdersReportProduct] = OrdersView,
        [DevicesActiveView] = DevicesView,
        [DevicesOverdueView] = DevicesView,
        [DevicesDue10View] = DevicesView,
        [DevicesDue30View] = DevicesView,
        [DevicesDue60View] = DevicesView,
        [DevicesRenew] = DevicesView,
        [DevicesUpdate] = DevicesView,
        [DevicesRenewalsView] = DevicesView,
        [DevicesReportSource] = DevicesView,
        [DevicesReportTime] = DevicesView,
        [DevicesReportKpi] = DevicesView,
        [FinanceCustomerView] = FinanceView,
        [FinanceRenewView] = FinanceView,
        [FinanceSupplierView] = FinanceView,
        [FinanceOtherView] = FinanceView,
        [FinancePay] = FinanceView,
        [FinanceExport] = FinanceView,
        [ReportsAuditView] = ReportsView,
        [UsersManage] = UsersView,
        [RolesManage] = UsersView,
        [UsersPermissions] = UsersView,
        [QuantityMove] = QuantityStockView,
        [WarehouseLocationManage] = QuantityStockView,
        [QuantityCountCreate] = QuantityCountView,
        [QuantityCountApprove] = QuantityCountView,
        [PurchaseCreate] = PurchaseView,
        [PurchaseReceive] = PurchaseView,
        [WarehouseScopeManage] = UsersView,
        [MasterDataManage] = UsersView,
    };
    public static void EnforceDependencies(HashSet<string> keys)
    {
        keys.RemoveWhere(key => !IsImplemented(key));
        bool changed;
        do
        {
            changed = false;
            foreach (var (action, parent) in RequiredView)
                if (keys.Contains(action) && !keys.Contains(parent)) changed |= keys.Remove(action);
        } while(changed);
    }

    public static readonly Dictionary<string, string[]> Defaults = new()
    {
        ["ADMIN"] = Definitions.Select(x => x.Key).ToArray(),
        ["MANAGER"] = [QuantityStockView, QuantityMove, WarehouseLocationManage, QuantityCountView, QuantityCountCreate, QuantityCountApprove, PurchaseView, PurchaseCreate, PurchaseReceive, CustomersUpdate, OrdersUpdate, InventoryWarranty, DashboardView, DashboardRevenue, DashboardProfit, DashboardStock, DashboardDebt, DashboardOrders, DashboardDevices, DashboardRenewals, ProductsView, ProductsCreate, ProductsUpdate, ProductsExport, StockCountView, StockCountReview, CustomersView, CustomersCreate, PricesView, PricesUpdate, InventoryView, InventoryReceive, InventoryMove, InventoryConsignOut, InventoryConsignReturn, InventoryTechOut, InventoryTechReturn, InventoryLedgerView, InventoryLedgerExport, OrdersView, OrdersCreate, OrdersActivate, OrdersCancel, DevicesView, DevicesActiveView, DevicesOverdueView, DevicesDue10View, DevicesDue30View, DevicesDue60View, DevicesRenew, DevicesRenewalsView, FinanceView, FinanceSupplierView, FinanceCustomerView, FinanceRenewView, FinanceOtherView, FinancePay, FinanceExport, ReportsView, ReportsAuditView, ReportsExport, ArchitectureView],
        ["ACCOUNTANT"] = [PurchaseView, QuantityStockView, DashboardView, DashboardRevenue, DashboardProfit, DashboardStock, DashboardDebt, DashboardOrders, DashboardDevices, DashboardRenewals, ProductsView, ProductsExport, StockCountView, CustomersView, PricesView, InventoryView, InventoryLedgerView, InventoryLedgerExport, OrdersView, DevicesView, DevicesActiveView, DevicesOverdueView, DevicesDue10View, DevicesDue30View, DevicesDue60View, DevicesRenewalsView, FinanceView, FinanceSupplierView, FinanceCustomerView, FinanceRenewView, FinanceOtherView, FinancePay, FinanceExport, ReportsView, ReportsAuditView, ReportsExport],
        ["WAREHOUSE"] = [QuantityStockView, QuantityMove, QuantityCountView, QuantityCountCreate, PurchaseView, PurchaseReceive, DashboardView, ProductsView, StockCountView, StockCountReview, InventoryView, InventoryReceive, InventoryMove, ReportsView, ReportsExport],
        ["CSKH"] = [CustomersUpdate, OrdersUpdate, DashboardView, ProductsView, CustomersView, CustomersCreate, PricesView, PricesUpdate, OrdersView, OrdersCreate, OrdersCancel, DevicesView, DevicesActiveView, DevicesOverdueView, DevicesDue10View, DevicesDue30View, DevicesDue60View, DevicesRenew, DevicesRenewalsView, ReportsView],
        ["TECH"] = [QuantityStockView, DashboardView, ProductsView, StockCountView, InventoryView, OrdersView, OrdersActivate, DevicesView, DevicesActiveView]
    };
}

