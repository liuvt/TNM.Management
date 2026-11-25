using System.Text.RegularExpressions;

namespace TNM.Management.Services;

/// <summary>
/// Bộ từ điển Việt hóa chuẩn nghiệp vụ ngành Thiết bị Định vị GPS / Camera Nghị định 10,
/// Quản trị Kho vận, Điều phối Lắp đặt và Kế toán Công nợ — dùng thống nhất trên toàn giao diện & xuất Excel.
/// </summary>
public static class VnLabels
{
    public static string StockStatus(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "IN_STOCK" => "Trong kho khả dụng",
        "CONSIGNED" => "Đang ký gửi đại lý",
        "TECHNICIAN" => "Kỹ thuật đang giữ",
        "INSTALLED" => "Đã lắp đặt trên xe",
        "WARRANTY" => "Đang bảo hành",
        "SUPPLIER" => "Đã gửi hãng bảo hành",
        "" => "—",
        _ => code!
    };

    public static string ImeiMovementType(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "RECEIVE" => "Nhập kho thiết bị",
        "SALE_OUT" => "Xuất lắp đặt đơn hàng",
        "CONSIGN_OUT" => "Xuất ký gửi đại lý",
        "CONSIGN_RETURN" => "Thu hồi ký gửi về kho",
        "TECH_OUT" => "Giao kỹ thuật đi lắp",
        "TECH_RETURN" => "Kỹ thuật hoàn trả kho",
        "WARRANTY_OUT" => "Tiếp nhận / Gửi bảo hành",
        "WARRANTY_RETURN" => "Hoàn trả sau bảo hành",
        "" => "—",
        _ => code!
    };

    public static string QuantityMovementType(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "OPENING" => "Tồn kho đầu kỳ",
        "RECEIVE" => "Nhập kho hàng hóa",
        "PURCHASE_RECEIVE" => "Nhập mua hàng NCC",
        "ISSUE" => "Xuất kho hàng hóa",
        "TRANSFER_IN" => "Nhận điều chuyển kho",
        "TRANSFER_OUT" => "Xuất điều chuyển kho",
        "ADJUST" => "Điều chỉnh kiểm kê",
        "" => "—",
        _ => code!
    };

    public static string OrderStatus(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "PENDING" => "Chờ điều phối",
        "APPROVED" => "Đã duyệt kho",
        "DISPATCHED" => "Đang đi lắp đặt",
        "REPORTED" => "Đã báo số IMEI",
        "COMPLETED" => "Đã hoàn thành",
        "CANCELLED" => "Đã hủy đơn",
        "ACTIVATION_BATCH" => "Bảng kê / Lô thiết bị",
        "" => "—",
        _ => code!
    };

    public static string PurchaseOrderStatus(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "DRAFT" => "Chờ nhận hàng",
        "APPROVED" => "Đã duyệt đơn mua",
        "RECEIVED" => "Đã nhập kho",
        "CANCELLED" => "Đã hủy phiếu",
        "" => "—",
        _ => code!
    };

    public static string StockCountStatus(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "DRAFT" => "Đang kiểm đếm (Chờ duyệt)",
        "APPROVED" => "Đã chốt kiểm kê",
        "CANCELLED" => "Đã hủy phiên",
        "" => "—",
        _ => code!
    };

    public static string WarrantyStatus(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "RECEIVED" => "Đã tiếp nhận bảo hành",
        "SENT_SUPPLIER" => "Đang gửi hãng / NCC",
        "COMPLETED" => "Đã hoàn tất bảo hành",
        "CANCELLED" => "Đã hủy phiếu bảo hành",
        "" => "—",
        _ => code!
    };

    public static string DeviceStatus(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "ACTIVE" => "Đang hoạt động",
        "DISABLED" or "LOCKED" => "Đã khóa thiết bị",
        "EXPIRED" => "Đã hết hạn",
        "" => "Đang hoạt động",
        _ => code!
    };

    public static string ProductKind(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "GOODS" => "Hàng hóa / Thiết bị",
        "SERVICE" => "Dịch vụ / Gia hạn",
        "BUNDLE" => "Bộ sản phẩm trọn gói",
        "" => "Hàng hóa / Thiết bị",
        _ => code!
    };

    public static string RenewalSource(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "TNM" => "Trường Nhật Minh",
        "NCC" => "Nhà cung cấp",
        "HTX" => "Hợp tác xã / Đại lý",
        "B2C" => "Khách lẻ trực tiếp",
        "" => "—",
        _ => code!
    };

    public static string InvoiceType(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "PURCHASE" or "SUPPLIER" => "1. Công nợ Nhà cung cấp (Nhập hàng)",
        "SALE" => "2a. Công nợ Khách hàng (Đơn lắp)",
        "RENEWAL" => "2b. Công nợ Khách hàng (Gia hạn)",
        "COMMISSION" => "3a. Công nợ NV bán hàng (Hoa hồng)",
        "SERVICE" => "3b. Công nợ Kỹ thuật lắp đặt",
        "" => "—",
        _ => code!
    };

    public static string InvoiceStatus(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "OPEN" => "Chưa thanh toán",
        "PARTIAL" => "Thanh toán 1 phần",
        "PAID" => "Đã tất toán",
        "VOID" => "Đã hủy chứng từ",
        "" => "—",
        _ => code!
    };

    public static string PaymentDirection(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "IN" => "Khoản Thu (Phiếu Thu)",
        "OUT" => "Khoản Chi (Phiếu Chi)",
        "" => "—",
        _ => code!
    };

    public static string PaymentMethod(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "CASH" => "Tiền mặt",
        "BANK" => "Ngân hàng (Chuyển khoản)",
        "DEBT" => "Ghi công nợ",
        "" => "—",
        _ => code!
    };

    public static string RoleName(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "OWNER" => "Chủ sở hữu",
        "ADMIN" => "Quản trị viên",
        "MANAGER" => "Quản lý điều hành",
        "ACCOUNTANT" => "Kế toán công nợ",
        "WAREHOUSE" => "Thủ kho",
        "CSKH" => "Chăm sóc khách hàng",
        "TECH" or "TECHNICIAN" => "Kỹ thuật lắp đặt",
        "" => "Thành viên",
        _ => code!
    };

    public static string WarehouseScope(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "ALL" => "Toàn bộ các kho",
        "SELECTED" => "Theo kho được phân công",
        "" => "Toàn bộ các kho",
        _ => code!
    };

    public static string TableName(string? table) => (table ?? "").Trim().ToLowerInvariant() switch
    {
        "products" => "Danh mục Sản phẩm & Thiết bị",
        "product_groups" => "Nhóm sản phẩm",
        "product_catalog_metadata" => "Thuộc tính phân loại sản phẩm",
        "supplier_groups" => "Nhóm Nhà cung cấp",
        "suppliers" => "Danh mục Nhà cung cấp",
        "customer_groups" => "Nhóm Khách hàng / Hợp tác xã",
        "customers" => "Danh mục Khách hàng & Đại lý",
        "v7_sellers" => "Danh mục Nhân viên bán hàng",
        "employees" => "Danh mục Nhân sự & Đơn vị lắp đặt",
        "v7_installer_product_rates" => "Định mức công lắp đặt theo SP",
        "v7_supplier_group_prices" => "Bảng giá nhập Nhà cung cấp",
        "v7_group_prices" => "Bảng giá bán & gia hạn Khách hàng",
        "v7_group_campaigns" => "Chương trình chiết khấu ưu đãi",
        "v7_sku_sequences" => "Bộ đếm mã sản phẩm tự động",
        "warehouse_areas" => "Khu vực kho",
        "warehouses" => "Danh mục Kho hàng",
        "warehouse_locations" => "Vị trí lưu kho",
        "user_warehouse_scopes" => "Phân quyền phạm vi kho",
        "stock_units" => "Thiết bị quản lý theo IMEI",
        "inventory_transactions" => "Phiếu xuất / nhập kho IMEI",
        "inventory_transaction_lines" => "Chi tiết dòng phiếu kho IMEI",
        "quantity_movements" => "Sổ xuất / nhập kho số lượng",
        "quantity_counts" => "Phiên kiểm kê tồn kho",
        "quantity_count_lines" => "Chi tiết kiểm đếm tồn kho",
        "purchase_orders" => "Đơn mua hàng Nhà cung cấp",
        "purchase_order_lines" => "Chi tiết mặt hàng đơn mua NCC",
        "goods_receipts" => "Phiếu nhập kho đơn mua NCC",
        "v7_orders" => "Đơn hàng & Điều phối lắp đặt",
        "v7_order_lines" => "Chi tiết mặt hàng đơn lắp đặt",
        "v7_order_events" => "Lịch sử trạng thái đơn lắp đặt",
        "v7_stock_effects" => "Đối chiếu xuất kho theo đơn lắp",
        "activations" => "Danh sách Thiết bị đã kích hoạt",
        "renewals" => "Lịch sử Gia hạn thuê bao thiết bị",
        "warranty_cases" => "Hồ sơ Bảo hành thiết bị",
        "warranty_events" => "Tiến trình xử lý bảo hành",
        "invoices" => "Chứng từ Công nợ Kế toán",
        "invoice_lines" => "Chi tiết hạng mục chứng từ công nợ",
        "payments" => "Phiếu Thu / Phiếu Chi Sổ quỹ",
        "payment_allocations" => "Phân bổ thanh toán chứng từ",
        "expense_categories" => "Danh mục Loại Khoản Thu / Khoản Chi",
        "app_users" => "Tài khoản người dùng",
        "roles" => "Vai trò phân quyền mẫu",
        "user_roles" => "Gán vai trò cho tài khoản",
        "role_permissions" => "Quyền hạn theo vai trò mẫu",
        "user_permission_overrides" => "Quyền cấp riêng theo tài khoản",
        "user_scope_settings" => "Thiết lập giới hạn kho",
        "user_security" => "Bảo mật phiên đăng nhập",
        "role_defaults_initialized" => "Khởi tạo vai trò mặc định",
        "" => "Toàn bộ phân hệ",
        _ => table!
    };

    public static string OperationName(string? op) => (op ?? "").Trim().ToUpperInvariant() switch
    {
        "CREATE" => "Thêm mới",
        "UPDATE" => "Cập nhật",
        "DELETE" => "Xóa dữ liệu",
        "RESTORE" => "Khôi phục",
        "" => "—",
        _ => op!
    };

    public static string AuditAction(string? action) => (action ?? "").Trim().ToUpperInvariant() switch
    {
        "CREATE" => "Thêm mới",
        "UPDATE" => "Cập nhật",
        "DELETE" => "Xóa bỏ",
        "DISABLE" => "Ngừng kích hoạt",
        "RECEIVE" => "Nhập kho thiết bị",
        "SALE_OUT" => "Xuất kho lắp đặt",
        "CONSIGN_OUT" => "Xuất ký gửi đại lý",
        "CONSIGN_RETURN" => "Thu hồi hàng ký gửi",
        "TECH_OUT" => "Giao kỹ thuật đi lắp",
        "TECH_RETURN" => "Kỹ thuật hoàn trả kho",
        "RENEW" => "Gia hạn thuê bao",
        "UPDATE_ACTIVATION" => "Cập nhật thông tin kích hoạt",
        "IMPORT_DEVICES" => "Nhập danh sách thiết bị",
        "UPDATE_RENEWAL" => "Cập nhật đợt gia hạn",
        "DELETE_RENEWAL" => "Xóa đợt gia hạn",
        "LOCK_RENEWAL" => "Chốt số đợt gia hạn",
        "UNLOCK_RENEWAL" => "Mở khóa đợt gia hạn",
        "CREATE_EXPENSE" => "Lập phiếu khoản chi",
        "UPDATE_EXPENSE" => "Cập nhật khoản chi",
        "DELETE_EXPENSE" => "Xóa khoản chi",
        "LOCK_EXPENSE" => "Chốt số khoản chi",
        "UNLOCK_EXPENSE" => "Mở khóa khoản chi",
        "SAVE_EXPENSE_CATEGORY" => "Cập nhật loại khoản chi",
        "LOCK_INVOICE" => "Chốt số chứng từ",
        "UNLOCK_INVOICE" => "Mở khóa chứng từ",
        "LOCK_BATCH" => "Chốt số hàng loạt",
        "UNLOCK_BATCH" => "Mở khóa hàng loạt",
        "SETTLE_LOCK_BATCH" => "Tất toán & chốt số về 0",
        "CASHBOOK" => "Ghi phiếu Sổ quỹ",
        "PAYMENT" => "Thu / Chi công nợ",
        "RECONCILE" => "Đồng bộ đối soát kế toán",
        "QTY_OPENING" => "Khai báo tồn đầu kỳ",
        "QTY_RECEIVE" => "Nhập kho số lượng",
        "QTY_ISSUE" => "Xuất kho số lượng",
        "QTY_TRANSFER_IN" => "Nhận chuyển kho",
        "QTY_TRANSFER_OUT" => "Xuất chuyển kho",
        "QTY_ADJUST" => "Điều chỉnh kiểm kê",
        "QTY_PURCHASE_RECEIVE" => "Nhận hàng đơn mua NCC",
        "COUNT_EDIT" => "Cập nhật số kiểm đếm",
        "COUNT_APPROVE" => "Phê duyệt chốt kiểm kê",
        "PO_CREATE" => "Tạo đơn mua hàng NCC",
        "PO_RECEIVE" => "Nhận hàng đơn mua NCC",
        "WARRANTY_OPEN" => "Tiếp nhận bảo hành",
        "WARRANTY_SENT_SUPPLIER" => "Gửi hãng bảo hành",
        "WARRANTY_COMPLETED" => "Hoàn tất bảo hành",
        "WARRANTY_CANCELLED" => "Hủy phiếu bảo hành",
        "WARRANTY_EDIT" => "Cập nhật phiếu bảo hành",
        "WARRANTY_DELETE" => "Xóa phiếu bảo hành",
        "WAREHOUSE_SCOPE" => "Cập nhật phạm vi kho",
        "SET_PRICE" => "Thiết lập bảng giá",
        "UPDATE_PRICE" => "Cập nhật bảng giá",
        "DELETE_PRICE" => "Xóa dòng bảng giá",
        "SET_INSTALLER_RATE" => "Thiết lập định mức công lắp",
        "UPDATE_INSTALLER_RATE" => "Cập nhật định mức công lắp",
        "CHANGE_PASSWORD" => "Đổi mật khẩu cá nhân",
        "RESET_PASSWORD_OFFLINE" => "Đặt lại mật khẩu quản trị",
        "UPDATE_PERMISSIONS" => "Cập nhật phân quyền",
        "SEED" => "Khởi tạo dữ liệu nền",
        "" => "—",
        _ => action!
    };

    public static string AuditEntity(string? entity) => (entity ?? "").Trim().ToUpperInvariant() switch
    {
        "PRODUCT" => "Sản phẩm & Thiết bị",
        "PRODUCT_GROUPS" => "Nhóm sản phẩm",
        "INVENTORY_TX" => "Phiếu kho thiết bị",
        "STOCK_UNIT" => "Thiết bị IMEI",
        "ACTIVATION" => "Thuê bao kích hoạt",
        "RENEWAL" => "Gia hạn thiết bị",
        "INVOICE" => "Chứng từ công nợ",
        "PAYMENT" => "Phiếu thu / chi Sổ quỹ",
        "FINANCE" => "Kế toán & Công nợ",
        "WAREHOUSE" => "Kho hàng",
        "WAREHOUSE_AREAS" => "Khu vực kho",
        "WAREHOUSE_LOCATION" => "Vị trí trong kho",
        "COUNT" => "Phiên kiểm kê",
        "PURCHASE_ORDER" => "Đơn mua hàng NCC",
        "WARRANTY_CASE" => "Hồ sơ bảo hành",
        "APP_USER" => "Tài khoản người dùng",
        "ROLE" => "Vai trò phân quyền",
        "SUPPLIER" => "Nhà cung cấp",
        "SUPPLIER_GROUP" or "SUPPLIER_GROUPS" => "Nhóm Nhà cung cấp",
        "SUPPLIER_GROUP_PRICE" or "BUY" => "Bảng giá nhập NCC",
        "CUSTOMER" => "Khách hàng / Đại lý",
        "CUSTOMER_GROUP" or "CUSTOMER_GROUPS" => "Nhóm Khách hàng",
        "CUSTOMER_GROUP_PRICE" or "SALE" => "Bảng giá bán Khách hàng",
        "CAMPAIGN" => "Chương trình ưu đãi",
        "SELLER" => "Nhân viên bán hàng",
        "EMPLOYEE" or "INSTALLER" => "Nhân sự / Đơn vị lắp đặt",
        "INSTALLER_RATE" => "Định mức công lắp đặt",
        "SYSTEM" => "Hệ thống",
        "" => "—",
        _ => entity!
    };

    public static string ActivityAction(string? action) => (action ?? "").Trim().ToUpperInvariant() switch
    {
        "LOGIN" => "Đăng nhập thành công",
        "LOGIN_FAILED" => "Đăng nhập thất bại",
        "LOGOUT" => "Đăng xuất hệ thống",
        "EXPORT" => "Xuất dữ liệu báo cáo",
        "" => "—",
        _ => AuditAction(action)
    };

    public static string ActivityResource(string? resource)
    {
        var r = (resource ?? "").Trim();
        return r.ToLowerInvariant() switch
        {
            "/account/login" => "Trang đăng nhập hệ thống",
            "/account/logout" => "Đăng xuất phiên làm việc",
            "products" => "Danh mục Sản phẩm & Thiết bị",
            "catalog/suppliers" => "Danh mục Nhà cung cấp",
            "catalog/customers" => "Danh mục Khách hàng",
            "catalog/sellers" => "Danh mục Nhân viên bán hàng",
            "catalog/installers" => "Danh mục Người lắp đặt",
            "catalog/prices" => "Bảng giá & Chương trình ưu đãi",
            "catalog/warehouses" => "Danh mục Kho hàng",
            _ => r.StartsWith("catalog/", StringComparison.OrdinalIgnoreCase)
                ? "Danh mục " + TableName(r[8..])
                : TableName(r)
        };
    }

    private static readonly (string Pattern, string Replacement)[] TokenReplacements =
    [
        (@"\bIN_STOCK\b", "Trong kho khả dụng"),
        (@"\bINSTALLED\b", "Đã lắp đặt trên xe"),
        (@"\bCONSIGNED\b", "Đang ký gửi đại lý"),
        (@"\bTECHNICIAN\b", "Kỹ thuật đang giữ"),
        (@"\bWARRANTY\b", "Đang bảo hành"),
        (@"\bSENT_SUPPLIER\b", "Đang gửi hãng"),
        (@"\bPURCHASE_RECEIVE\b", "Nhập mua hàng NCC"),
        (@"\bCONSIGN_OUT\b", "Xuất ký gửi"),
        (@"\bCONSIGN_RETURN\b", "Thu hồi ký gửi"),
        (@"\bTECH_OUT\b", "Giao kỹ thuật"),
        (@"\bTECH_RETURN\b", "Kỹ thuật trả kho"),
        (@"\bSALE_OUT\b", "Xuất lắp đặt"),
        (@"\bACTIVATION_BATCH\b", "Bảng kê / Lô thiết bị"),
        (@"\bPENDING\b", "Chờ điều phối"),
        (@"\bDISPATCHED\b", "Đang đi lắp đặt"),
        (@"\bREPORTED\b", "Đã báo số IMEI"),
        (@"\bCOMPLETED\b", "Đã hoàn tất"),
        (@"\bCANCELLED\b", "Đã hủy"),
        (@"\bAPPROVED\b", "Đã duyệt"),
        (@"\bRECEIVED\b", "Đã tiếp nhận"),
        (@"\bDRAFT\b", "Chờ xử lý"),
        (@"\bPARTIAL\b", "Thanh toán 1 phần"),
        (@"\bPAID\b", "Đã tất toán"),
        (@"\bOPEN\b", "Chưa thanh toán"),
        (@"\bVOID\b", "Đã hủy"),
        (@"\bCASH\b", "Tiền mặt"),
        (@"\bBANK\b", "Ngân hàng"),
        (@"\bDEBT\b", "Ghi công nợ"),
        (@"\bPURCHASE\b", "Nhập hàng NCC"),
        (@"\bRENEWAL\b", "Gia hạn"),
        (@"\bCOMMISSION\b", "Hoa hồng"),
        (@"\bSERVICE\b", "Dịch vụ / Kỹ thuật"),
        (@"\bSALE\b", "Bán hàng / Đơn lắp"),
        (@"\bACTIVE\b", "Đang hoạt động"),
        (@"\bDISABLED\b", "Đã khóa"),
        (@"\bEXPIRED\b", "Hết hạn"),
        (@"\bGOODS\b", "Hàng hóa"),
        (@"\bBUNDLE\b", "Bộ sản phẩm"),
        (@"\bALLOW\b", "Cho phép"),
        (@"\bDENY\b", "Từ chối"),
        (@"\bINHERIT\b", "Theo vai trò"),
        (@"\bTRANSFER\b", "Điều chuyển kho"),
        (@"\bstock_units\b", "Thiết bị quản lý theo IMEI"),
        (@"\binventory_transactions\b", "Phiếu xuất nhập kho IMEI"),
        (@"\bquantity_balances\b", "Tồn kho số lượng"),
        (@"\bquantity_movements\b", "Sổ giao dịch số lượng"),
        (@"\bstock_take_sessions\b", "Phiên kiểm kê tồn kho"),
        (@"\bstock_take_lines\b", "Chi tiết kiểm đếm"),
        (@"\bpurchase_orders\b", "Đơn mua hàng NCC"),
        (@"\bv7_orders\b", "Đơn hàng lắp đặt"),
        (@"\bv7_order_lines\b", "Chi tiết mặt hàng đơn lắp"),
        (@"\bv7_stock_effects\b", "Đối chiếu xuất kho"),
        (@"\bv7_sellers\b", "Danh mục NV bán hàng"),
        (@"\bv7_installer_product_rates\b", "Định mức công lắp đặt"),
        (@"\bv7_supplier_group_prices\b", "Bảng giá nhập NCC"),
        (@"\bv7_group_prices\b", "Bảng giá khách hàng"),
        (@"\bv7_group_campaigns\b", "Chương trình ưu đãi"),
        (@"\bsupplier_groups\b", "Nhóm Nhà cung cấp"),
        (@"\bsuppliers\b", "Danh mục Nhà cung cấp"),
        (@"\bcustomer_groups\b", "Nhóm Khách hàng"),
        (@"\bcustomers\b", "Danh mục Khách hàng"),
        (@"\bproduct_groups\b", "Nhóm sản phẩm"),
        (@"\bproducts\b", "Danh mục Sản phẩm"),
        (@"\bwarehouse_areas\b", "Khu vực kho"),
        (@"\bwarehouses\b", "Danh mục Kho"),
        (@"\bemployees\b", "Danh mục Người lắp đặt"),
        (@"\bactivations\b", "Thuê bao kích hoạt"),
        (@"\brenewals\b", "Lịch sử gia hạn"),
        (@"\bwarranties\b", "Hồ sơ bảo hành"),
        (@"\binvoices\b", "Chứng từ công nợ"),
        (@"\bpayments\b", "Phiếu Sổ quỹ"),
        (@"\bpayment_allocations\b", "Phân bổ thanh toán"),
        (@"\baccounting_locks\b", "Khóa sổ kế toán"),
        (@"\bactivity_events\b", "Nhật ký hoạt động"),
        (@"\bapp_users\b", "Tài khoản người dùng"),
        (@"\buser_roles\b", "Phân vai trò tài khoản"),
        (@"\brole_permissions\b", "Quyền theo vai trò"),
        (@"\buser_permission_overrides\b", "Quyền riêng cá nhân"),
        (@"\buser_warehouse_scopes\b", "Phạm vi kho tài khoản"),
        (@"\bDialog\b", "Hộp thoại"),
        (@"\bSKU\b", "mã hàng"),
        (@"\bRMA\b", "Bảo hành"),
        (@"\bRBAC\b", "Phân quyền vai trò"),
        (@"Audit Trail", "Truy vết kiểm toán"),
        (@"Soft Delete", "Xóa an toàn"),
        (@"Historical Price Snapshot", "Chốt giá lịch sử"),
        (@"\(Standard\)", ""),
        (@"\(Installed\)", ""),
        (@"\(Renewal\)", ""),
        (@"\(IN\)", ""),
        (@"\(OUT\)", ""),
        (@"\bSeller\b", "NV bán hàng"),
        (@"\bProduct\b", "Mã SP #")
    ];

    public static string LocalizeText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        var s = raw;
        foreach (var (pattern, rep) in TokenReplacements)
            s = Regex.Replace(s, pattern, rep);
        return s;
    }
}
