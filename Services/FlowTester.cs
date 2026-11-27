using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using TNM.Management.Security;

namespace TNM.Management.Services;

public sealed record FlowTestStep(int Number, string Category, string Name, bool Passed, string Detail, long DurationMs);
public sealed record FlowTestReport(bool AllPassed, int Total, int Passed, int Failed, long TotalDurationMs, List<FlowTestStep> Steps);

/// <summary>
/// Bộ kiểm thử tự động toàn diện (End-to-End Flow Tester) trên môi trường sandbox tạm thời.
/// Tuyệt đối không chạm vào database thật App_Data/tnm-local.db.
/// </summary>
public static class FlowTester
{
    private sealed class TempHostEnv : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "TNM.Management.Test";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public static FlowTestReport RunAll(string projectRoot)
    {
        var steps = new List<FlowTestStep>();
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var tempDir = Path.Combine(Path.GetTempPath(), "tnm-flow-test-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "Data"));
        Directory.CreateDirectory(Path.Combine(tempDir, "App_Data"));

        try
        {
            // Sao chép schema.sql và seed_empty.sql sang sandbox
            File.Copy(Path.Combine(projectRoot, "Data", "schema.sql"), Path.Combine(tempDir, "Data", "schema.sql"), true);
            File.Copy(Path.Combine(projectRoot, "Data", "seed_empty.sql"), Path.Combine(tempDir, "Data", "seed_empty.sql"), true);
            File.Copy(Path.Combine(projectRoot, "Data", "seed.sql"), Path.Combine(tempDir, "Data", "seed.sql"), true);

            var env = new TempHostEnv { ContentRootPath = tempDir };
            var db = new DemoDb(env);
            var v7Orders = new V7Orders(env);
            var catalog = new InputCatalog(env);
            var productMaster = new ProductMaster(env);
            var ops = new V6Ops(env);
            var auth = new AuthRepository(env);
            var audit = new AuditTrail(env);
            var deviceImporter = new DeviceImportService(env);
            var dbPath = Path.Combine(tempDir, "App_Data", "tnm-local.db");

            int stepNo = 0;
            void Step(string category, string name, Func<string> action)
            {
                stepNo++;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var detail = action();
                    sw.Stop();
                    steps.Add(new FlowTestStep(stepNo, category, name, true, detail, sw.ElapsedMilliseconds));
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    steps.Add(new FlowTestStep(stepNo, category, name, false, ex.Message, sw.ElapsedMilliseconds));
                }
            }

            static void Assert(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException(message);
            }

            static void AssertThrows(Action action, string expectedReason)
            {
                try
                {
                    action();
                }
                catch
                {
                    return;
                }
                throw new InvalidOperationException($"Mong đợi chặn lỗi ({expectedReason}) nhưng thao tác vẫn đi qua.");
            }

            long ownerId = 1, staffUserId = 0;
            long supGroupId = 0, supplierId = 0;
            long prodGroupId = 0, imeiProdId = 0, qtyProdId = 0;
            long custGroupId = 0, customerId = 0;
            long areaId = 0, wh1Id = 0, wh2Id = 0, loc1Id = 0, loc2Id = 0;
            long installerId = 0, orderInternalId = 0, orderPartnerId = 0;

            // =========================================================================
            // 1. KHỞI TẠO DATABASE, SCHEMA & AUDIT TRIGGERS
            // =========================================================================
            Step("1. Hệ thống & Schema", "Khởi tạo Database từ schema.sql & seed_empty.sql + Gắn Audit Triggers", () =>
            {
                db.Initialize();
                v7Orders.Initialize();
                catalog.Initialize();
                productMaster.Initialize();
                auth.Initialize();
                audit.Initialize();

                using var c = new SqliteConnection($"Data Source={dbPath};Foreign Keys=True");
                c.Open();
                using var fk = c.CreateCommand();
                fk.CommandText = "PRAGMA foreign_key_check;";
                using var r = fk.ExecuteReader();
                Assert(!r.Read(), "Phát hiện lỗi khóa ngoại ngay sau khi khởi tạo.");

                using var trig = c.CreateCommand();
                trig.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'tnm_audit_%'";
                var trigCount = Convert.ToInt64(trig.ExecuteScalar());
                Assert(trigCount >= 60, $"Số lượng trigger audit quá ít ({trigCount}).");
                return $"Khởi tạo sạch hoàn tất · {trigCount} SQLite Audit Triggers đang hoạt động.";
            });

            // =========================================================================
            // 2. XÁC THỰC, OWNER PROTECTION, RBAC & PHẠM VI KHO
            // =========================================================================
            Step("2. Bảo mật & Phân quyền", "Xác thực Owner mặc định, bắt buộc đổi mật khẩu & Đổi mật khẩu thành công", () =>
            {
                var acc = auth.Authenticate("owner", AuthRepository.DefaultOwnerPassword);
                Assert(acc is not null, "Không đăng nhập được bằng tài khoản owner mặc định.");
                ownerId = acc!.Id;
                Assert(auth.MustChangePassword(ownerId), "Tài khoản owner khởi tạo phải bật cờ bắt buộc đổi mật khẩu.");
                Assert(auth.AllowedPermissions(ownerId).Count == 0, "Khi chưa đổi mật khẩu khởi tạo, không được cấp quyền nghiệp vụ.");

                auth.ChangeOwnPassword(ownerId, AuthRepository.DefaultOwnerPassword, "NewOwnerPass2026");
                Assert(!auth.MustChangePassword(ownerId), "Sau khi đổi mật khẩu, cờ must_change_password phải tắt.");
                Assert(auth.AllowedPermissions(ownerId).Count > 50, "Owner sau khi đổi mật khẩu phải có toàn bộ quyền đã triển khai.");
                return $"Owner #{ownerId} xác thực & đổi mật khẩu chuẩn xác ({auth.AllowedPermissions(ownerId).Count} quyền).";
            });

            Step("2. Bảo mật & Phân quyền", "Kiểm tra Trigger bảo vệ Owner, Tạo User, Override quyền & Giới hạn kho", () =>
            {
                var roles = auth.AllRoles();
                var whRoleId = roles.First(r => r.Code == "WAREHOUSE").Id;
                auth.CreateUser(ownerId, "nv_kho_test", "Nhân viên Kho Test", "KhoPassword2026", [whRoleId],
                    new Dictionary<string, string> { [Permissions.ProductsCreate] = "ALLOW", [Permissions.InventoryReceive] = "DENY" });

                var created = auth.AllUsers().First(u => u.Username == "nv_kho_test");
                staffUserId = created.Id;
                var rights = auth.AllowedPermissions(staffUserId);
                Assert(rights.Contains(Permissions.ProductsCreate), "Quyền ALLOW riêng (products.create) chưa có hiệu lực.");
                Assert(!rights.Contains(Permissions.InventoryReceive), "Quyền DENY riêng (inventory.receive) chưa chặn được quyền Role.");

                // Kiểm tra role ACCOUNTANT có đủ 4 quyền xem phân mục công nợ
                var accRoleId = roles.First(r => r.Code == "ACCOUNTANT").Id;
                auth.CreateUser(ownerId, "ke_toan_test", "Kế toán Test", "KeToanPass2026", [accRoleId], new Dictionary<string, string>());
                var accUserId = auth.AllUsers().First(u => u.Username == "ke_toan_test").Id;
                var accRights = auth.AllowedPermissions(accUserId);
                Assert(accRights.Contains(Permissions.FinanceCustomerView) &&
                       accRights.Contains(Permissions.FinanceSupplierView) &&
                       accRights.Contains(Permissions.FinanceRenewView) &&
                       accRights.Contains(Permissions.FinanceOtherView),
                    "Vai trò ACCOUNTANT phải có đầy đủ 4 quyền xem phân mục công nợ.");

                // Kiểm tra khóa cứng quyền xem Lịch sử / Lưu vết / Log: chỉ Main, Owner, Admin được xem và không thể tùy chỉnh
                Assert(auth.CanViewSystemLogs(ownerId), "Tài khoản Owner phải có quyền xem Log hệ thống.");
                Assert(auth.CanViewRowLogs(ownerId), "Tài khoản Owner phải có quyền xem Log từng dòng trên bảng.");
                var adminRoleId = roles.First(r => r.Code == "ADMIN").Id;
                auth.CreateUser(ownerId, "admin_test", "Quản trị viên Test", "AdminPass2026", [adminRoleId]);
                var adminUserId = auth.AllUsers().First(u => u.Username == "admin_test").Id;
                Assert(auth.CanViewSystemLogs(adminUserId), "Vai trò ADMIN phải có quyền xem Log chung tổng.");
                Assert(!auth.CanViewRowLogs(adminUserId), "Vai trò ADMIN không được phép xem Log từng dòng trên bảng (chỉ Main & Owner).");
                Assert(!auth.CanViewSystemLogs(staffUserId) && !auth.CanViewSystemLogs(accUserId),
                    "Vai trò WAREHOUSE và ACCOUNTANT không được phép xem Log / Lịch sử / Lưu vết.");
                Assert(!accRights.Contains(Permissions.ReportsAuditView) && !accRights.Contains(Permissions.InventoryLedgerView) && !accRights.Contains(Permissions.DevicesRenewalsView),
                    "Vai trò ACCOUNTANT không được kế thừa các quyền Lịch sử / Lưu vết cố định.");
                AssertThrows(() => auth.SaveUserPermissions(ownerId, accUserId, new Dictionary<string, string> { [Permissions.ReportsAuditView] = "ALLOW" }),
                    "Chặn tùy chỉnh cấp riêng quyền xem Log cho cá nhân ngoài Main/Owner/Admin");
                AssertThrows(() => auth.SaveRolePermissions(ownerId, accRoleId, [Permissions.FinanceView, Permissions.ReportsAuditView]),
                    "Chặn tùy chỉnh gán quyền xem Log vào vai trò mẫu ngoài Admin");

                // Kiểm tra chống khóa/sửa Owner
                AssertThrows(() => auth.UpdateUser(staffUserId, ownerId, "Hack", false, "", [whRoleId]), "Chặn sửa tài khoản Owner");
                return $"Tạo User #{staffUserId}, Kế toán #{accUserId} & Admin #{adminUserId} · Khóa cứng quyền Xem Log (Main/Owner/Admin) & Bảo vệ Owner hoạt động đúng.";
            });

            // =========================================================================
            // 3. DANH MỤC ĐẦU VÀO V7, CẤP SKU TỰ ĐỘNG & ĐỊNH GIÁ NHÓM
            // =========================================================================
            Step("3. Danh mục & Định giá V7", "Tạo Nhóm NCC, NCC, Nhóm SP, Khu vực, Kho, Nhóm KH & Khách hàng", () =>
            {
                supGroupId = catalog.SaveGroup(ownerId, "SUPPLIER", null, "NCC_DIENTU", "Nhóm NCC Điện tử");
                supplierId = catalog.SaveSupplier(ownerId, null, "NCC001", "Công ty Thiết bị Định vị Á Châu", supGroupId, "0909111222", "0301234567");

                prodGroupId = catalog.SaveGroup(ownerId, "PRODUCT", null, "GPS_CAM", "Thiết bị Giám sát Hành trình");
                custGroupId = catalog.SaveGroup(ownerId, "CUSTOMER", null, "HTX_VIP", "Hợp tác xã Vận tải VIP");
                Assert(catalog.PeekNextCustomerCode() == "KH00010", "Mã KH dự kiến bắt đầu phải là KH00010.");
                customerId = catalog.SaveCustomer(ownerId, null, "", "HTX Vận tải Sài Gòn Xanh", custGroupId, "0908888999", "Quận 7, TP.HCM", "Sale Nguyễn Minh Anh");
                var customer2Id = catalog.SaveCustomer(ownerId, null, "", "Công ty Vận tải Miền Tây", custGroupId, "0907777888", "Cần Thơ", "Đại lý Miền Tây");
                var custs = catalog.Customers();
                var c1 = custs.First(c => c.Id == customerId);
                var c2 = custs.First(c => c.Id == customer2Id);
                Assert(c1.Code == "KH00010" && c2.Code == "KH00011", $"Mã KH tự động nhảy không đúng thứ tự: {c1.Code}, {c2.Code}");
                Assert(catalog.PeekNextCustomerCode() == "KH00012", $"Mã KH tiếp theo phải là KH00012 (thực tế: {catalog.PeekNextCustomerCode()}).");

                areaId = catalog.SaveGroup(ownerId, "AREA", null, "KV_HCM", "Khu vực TP.HCM");
                wh1Id = catalog.SaveWarehouse(ownerId, null, "KHO_Q7", "Kho Trung tâm Quận 7", areaId, "Nguyễn Văn Linh, Q7");
                wh2Id = catalog.SaveWarehouse(ownerId, null, "KHO_TD", "Kho Thủ Đức", areaId, "Xa lộ Hà Nội, Thủ Đức");

                var locs = ops.Locations(ownerId);
                loc1Id = locs.First(l => l.WarehouseId == wh1Id && l.Code == "MAIN").Id;
                loc2Id = locs.First(l => l.WarehouseId == wh2Id && l.Code == "MAIN").Id;

                installerId = catalog.SaveInstaller(ownerId, null, "KT001", "Nguyễn Kỹ Thuật", "0912333444", "Cộng tác viên lắp đặt");
                Assert(db.Suppliers().Count >= 1 && db.Technicians().Count >= 1, "Danh sách NCC và Nhân sự kỹ thuật trên DemoDb phải truy vấn hợp lệ.");
                return $"Đã tạo NCC #{supplierId}, KH #{customerId}, Nhân sự lắp #{installerId}, Kho #{wh1Id} & #{wh2Id} (tự sinh vị trí MAIN).";
            });

            Step("3. Danh mục & Định giá V7", "Tạo Sản phẩm chuẩn V7 (Tự sinh mã DX00001, DX00002) & Cập nhật mã SKU", () =>
            {
                var sku1 = productMaster.Save(ownerId, new ProductMaster.ProductDraft(null, prodGroupId, "Thiết bị GPS 4G Hợp chuẩn", "GOODS", "Bộ", true, 12));
                var sku2 = productMaster.Save(ownerId, new ProductMaster.ProductDraft(null, prodGroupId, "Thẻ nhớ Camera 64GB Công nghiệp", "GOODS", "Cái", false, 24));
                Assert(sku1 == "DX00001" && sku2 == "DX00002", $"Mã SKU tự động không đúng thứ tự: {sku1}, {sku2}");

                var allProds = productMaster.Products();
                imeiProdId = allProds.First(p => p.Sku == "DX00001").Id;
                qtyProdId = allProds.First(p => p.Sku == "DX00002").Id;

                return $"Tạo thành công {sku1} (IMEI, ID #{imeiProdId}) & {sku2} (Số lượng, ID #{qtyProdId}).";
            });

            Step("3. Danh mục & Định giá V7", "Thiết lập Bảng giá Nhập (Nhóm NCC), Giá Bán & Chiến dịch Ưu đãi (Nhóm KH)", () =>
            {
                catalog.SetSupplierGroupPrice(ownerId, supGroupId, imeiProdId, 900_000, DateTime.Today);
                catalog.SetSupplierGroupPrice(ownerId, supGroupId, qtyProdId, 150_000, DateTime.Today);

                catalog.SetCustomerGroupPrice(ownerId, custGroupId, imeiProdId, 1_500_000, 1_800_000, 500_000, DateTime.Today);
                catalog.SetCustomerGroupPrice(ownerId, custGroupId, qtyProdId, 200_000, 200_000, 0, DateTime.Today);
                catalog.SaveCampaign(ownerId, null, custGroupId, "Ưu đãi HTX 10%", 10, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(15));
                catalog.SetInstallerRate(ownerId, installerId, imeiProdId, 135_000, DateTime.Today);
                catalog.SetInstallerRate(ownerId, installerId, qtyProdId, 30_000, DateTime.Today);

                var buyQuote = catalog.QuotePurchase(supplierId, qtyProdId);
                Assert(buyQuote == 150_000, $"Giá nhập báo sai: {buyQuote}");

                var saleQuote = catalog.QuoteSale(customerId, imeiProdId, "INSTALLED");
                Assert(saleQuote.BasePrice == 1_800_000 && saleQuote.DiscountPercent == 10 && saleQuote.FinalPrice == 1_620_000,
                    $"Giá bán sau chiết khấu không đúng: Base={saleQuote.BasePrice}, Final={saleQuote.FinalPrice}");

                var installerQuote = catalog.QuoteInstallerRate(installerId, imeiProdId);
                Assert(installerQuote == 135_000, $"Đơn giá đi lắp theo SP báo sai: {installerQuote}");

                return $"Tra giá tự động chuẩn xác: Giá nhập = {buyQuote:N0} ₫ · Giá bán sau KM = {saleQuote.FinalPrice:N0} ₫ · Đơn giá đi lắp SP = {installerQuote:N0} ₫.";
            });

            // =========================================================================
            // 4. KHO SỐ LƯỢNG, ĐIỀU CHUYỂN LIÊN KHO & KIỂM KÊ TỒN KHO
            // =========================================================================
            Step("4. Kho Số lượng", "Nhập kho số lượng trực tiếp (chọn NCC & đơn giá lô) -> Kiểm tra tăng tồn & Công nợ NCC", () =>
            {
                var quoted = db.QuoteSupplierProductCost(supplierId, qtyProdId);
                Assert(quoted.UnitCostVnd == 150_000, $"QuoteSupplierProductCost phải trả về 150.000 ₫ (thực tế: {quoted.UnitCostVnd})");
                ops.MoveQuantity(ownerId, qtyProdId, wh1Id, loc1Id, 50, incoming: true, "Nhập lô thẻ nhớ đợt 1", supplierId, 150_000);
                var bal = ops.Balances(ownerId).First(b => b.ProductId == qtyProdId && b.WarehouseId == wh1Id).Quantity;
                Assert(bal == 50, $"Tồn kho sau khi nhập kho số lượng phải bằng 50, thực tế = {bal}");
                Assert(db.Invoices().Any(i => i.Type == "PURCHASE" && i.Total == 7_500_000),
                    "Sau khi nhập kho số lượng phải tự động sinh hóa đơn Công nợ Nhà cung cấp (PURCHASE) 7.500.000 ₫.");
                return $"Đã nhập kho số lượng gắn NCC #{supplierId} · Tồn kho DX00002 = {bal} · Tự động sinh công nợ NCC 7.500.000 ₫.";
            });

            Step("4. Kho Số lượng", "Xuất kho, Điều chuyển liên kho & Chặn xuất âm tồn", () =>
            {
                ops.MoveQuantity(ownerId, qtyProdId, wh1Id, loc1Id, 10, incoming: false, "Xuất 10 thẻ nhớ lẻ");
                ops.TransferQuantity(ownerId, qtyProdId, wh1Id, loc1Id, wh2Id, loc2Id, 15, "Chuyển 15 cái sang Kho Thủ Đức");

                var bal1 = ops.Balances(ownerId).First(b => b.ProductId == qtyProdId && b.WarehouseId == wh1Id).Quantity;
                var bal2 = ops.Balances(ownerId).First(b => b.ProductId == qtyProdId && b.WarehouseId == wh2Id).Quantity;
                Assert(bal1 == 25 && bal2 == 15, $"Tồn sau điều chuyển sai: Kho 1={bal1}, Kho 2={bal2}");

                AssertThrows(() => ops.MoveQuantity(ownerId, qtyProdId, wh2Id, loc2Id, 999, incoming: false, "Xuất quá tồn"), "Chặn âm tồn kho số lượng");
                return $"Sau xuất & điều chuyển: Kho Q7 còn {bal1}, Kho Thủ Đức có {bal2} · Chặn xuất vượt tồn thành công.";
            });

            Step("4. Kho Số lượng", "Kiểm kê Số lượng (DRAFT -> Đếm -> Duyệt điều chỉnh ADJUST 1 lần)", () =>
            {
                var countId = ops.StartCount(ownerId, wh1Id, loc1Id, "Kiểm kê định kỳ Q7");
                ops.SetCountLine(ownerId, countId, qtyProdId, 23); // Hao hụt 2 cái so với tồn sổ 25
                ops.ApproveCount(ownerId, countId);

                var balAfter = ops.Balances(ownerId).First(b => b.ProductId == qtyProdId && b.WarehouseId == wh1Id).Quantity;
                Assert(balAfter == 23, $"Tồn kho sau duyệt kiểm kê phải là 23, thực tế = {balAfter}");

                AssertThrows(() => ops.ApproveCount(ownerId, countId), "Chặn duyệt lại phiên kiểm kê đã APPROVED");
                return $"Phiên kiểm kê #{countId} đã duyệt bút toán ADJUST (-2) · Tồn mới = {balAfter}.";
            });

            // =========================================================================
            // 5. KHO THIẾT BỊ IMEI & BẢO HÀNH RMA
            // =========================================================================
            Step("5. Kho IMEI & Bảo hành", "Nhập lô IMEI, Luân chuyển Ký gửi / Kỹ thuật & Thu hồi về kho", () =>
            {
                db.ActorId = ownerId;
                var count = db.Receive(imeiProdId, wh1Id, "860000000000001\n860000000000002\n860000000000003");
                Assert(count == 3, "Số lượng IMEI nhập phải bằng 3.");

                var units = db.Stock();
                var u2 = units.First(u => u.Imei == "860000000000002").Id;
                var u3 = units.First(u => u.Imei == "860000000000003").Id;
                var techId = db.Technicians().First().Id;

                // Ký gửi khách hàng rồi thu hồi
                db.MoveStock(u2, "CONSIGN_OUT", customerId);
                Assert(db.Stock().First(u => u.Id == u2).Status == "CONSIGNED", "Trạng thái sau CONSIGN_OUT phải là CONSIGNED.");
                db.MoveStock(u2, "CONSIGN_RETURN", wh1Id);
                Assert(db.Stock().First(u => u.Id == u2).Status == "IN_STOCK", "Trạng thái sau CONSIGN_RETURN phải là IN_STOCK.");

                // Xuất kỹ thuật rồi thu hồi
                db.MoveStock(u3, "TECH_OUT", techId);
                Assert(db.Stock().First(u => u.Id == u3).Status == "TECHNICIAN", "Trạng thái sau TECH_OUT phải là TECHNICIAN.");
                db.MoveStock(u3, "TECH_RETURN", wh1Id);
                Assert(db.Stock().First(u => u.Id == u3).Status == "IN_STOCK", "Trạng thái sau TECH_RETURN phải là IN_STOCK.");

                return "Đã nhập 3 IMEI và kiểm tra trọn vẹn chu trình Ký gửi ↔ Kho, Kỹ thuật ↔ Kho.";
            });

            Step("5. Kho IMEI & Bảo hành", "Quy trình Bảo hành RMA: Tiếp nhận -> Gửi NCC -> Hoàn tất trả kho", () =>
            {
                var u3 = db.Stock().First(u => u.Imei == "860000000000003").Id;
                var caseId = ops.OpenWarranty(ownerId, u3, "Lỗi mất tín hiệu anten GPS", "Khách báo kiểm tra");
                Assert(db.Stock().First(u => u.Id == u3).Status == "WARRANTY", "Thiết bị mở bảo hành phải ở trạng thái WARRANTY.");

                ops.SetWarrantyStatus(ownerId, caseId, "SENT_SUPPLIER", wh1Id, supplierId, "Đã gửi hãng bảo hành");
                Assert(db.Stock().First(u => u.Id == u3).Status == "SUPPLIER", "Thiết bị gửi hãng phải ở trạng thái SUPPLIER.");

                ops.SetWarrantyStatus(ownerId, caseId, "COMPLETED", wh1Id, null, "Hãng đã đổi bo mạch, trả về kho Q7");
                Assert(db.Stock().First(u => u.Id == u3).Status == "IN_STOCK", "Thiết bị hoàn tất bảo hành phải về lại IN_STOCK.");

                return $"Ca bảo hành #{caseId} cho IMEI 860000000000003 đã hoàn tất (RECEIVED -> SENT_SUPPLIER -> COMPLETED).";
            });

            // =========================================================================
            // 6. ĐƠN HÀNG & ĐIỀU PHỐI LẮP ĐẶT V7 (INTERNAL & PARTNER)
            // =========================================================================
            Step("6. Đơn hàng & Điều phối V7", "Luồng SO Nhiều sản phẩm (INTERNAL): Tạo đơn 2 SP (2 GPS + 1 Thẻ nhớ) -> Duyệt -> Điều phối -> Báo 2 IMEI -> Hoàn tất", () =>
            {
                orderInternalId = v7Orders.CreateMulti(ownerId, customerId,
                    [new V7OrderLineDraft(imeiProdId, 2), new V7OrderLineDraft(qtyProdId, 1)],
                    "Bến xe Quận 8, TP.HCM", "Lắp 2 xe tải + 1 thẻ nhớ", cod: true,
                    customerPhone: "0908888999", installAt: "01/11/2026 09:30:00", saleSource: "Sale Nguyễn Minh Anh");
                v7Orders.Approve(ownerId, orderInternalId, wh1Id);
                v7Orders.Dispatch(ownerId, orderInternalId, partner: false, "Nguyễn Kỹ Thuật", installerEmployeeId: installerId);
                v7Orders.Report(ownerId, orderInternalId, "860000000000001, 860000000000002", "51C-888.99, 51C-999.00", "VIN88899, VIN99900");
                v7Orders.Complete(ownerId, orderInternalId);

                var orderRow = v7Orders.List().First(o => o.Id == orderInternalId);
                // Giá sau KM 10%: 2 * 1,620,000 + 1 * 180,000 = 3,420,000
                // Công lắp: 2 * 135,000 + 1 * 30,000 = 300,000
                Assert(orderRow.Status == "COMPLETED" && orderRow.Price == 3_420_000 && orderRow.Lines.Count == 3,
                    $"Đơn nhiều SP sai trạng thái, giá hoặc số dòng: {orderRow.Status}, Price={orderRow.Price}, Lines={orderRow.Lines.Count}");
                Assert(orderRow.CustomerPhone == "0908888999" && orderRow.SaleSource.Contains("Nguyễn Minh Anh") && orderRow.InstallAt == "01/11/2026 09:30:00",
                    "Thiếu thông tin SĐT KH, Nguồn Sale hoặc Thời gian lắp trên đơn hàng.");
                Assert(orderRow.InstallerEmployeeId == installerId && orderRow.InstallerFee == 300_000,
                    $"Tổng công lắp đặt của nhân sự trên đơn nhiều SP không đúng: {orderRow.InstallerFee}");
                Assert(db.Stock().First(u => u.Imei == "860000000000001").Status == "INSTALLED" &&
                       db.Stock().First(u => u.Imei == "860000000000002").Status == "INSTALLED",
                    "Cả 2 IMEI nội bộ sau khi hoàn tất đơn phải chuyển sang INSTALLED.");
                Assert(db.Devices().Any(d => d.Imei == "860000000000001" && d.Plate == "51C-888.99") &&
                       db.Devices().Any(d => d.Imei == "860000000000002" && d.Plate == "51C-999.00"),
                    "Phải tự động sinh 2 thuê bao trong bảng activations.");
                var qtyAfter = ops.Balances(ownerId).First(b => b.ProductId == qtyProdId && b.WarehouseId == wh1Id).Quantity;
                Assert(qtyAfter == 22, $"Tồn số lượng DX00002 phải tự động trừ 1 (23 -> 22), thực tế = {qtyAfter}");
                Assert(db.Invoices().Any(i => i.Type == "SALE" && i.Total == 3_420_000), "Phải tự động sinh hóa đơn phải thu SALE tổng 3.420.000 ₫.");
                Assert(db.Invoices().Any(i => i.Type == "SERVICE" && i.Total == 300_000), "Phải tự động sinh công nợ chi phí công lắp kỹ thuật (SERVICE) 300.000 ₫.");

                return $"Đơn {orderRow.Code} (3 dòng SP) hoàn tất · Trừ 2 IMEI & 1 Thẻ nhớ · Tự sinh HĐ Khách {orderRow.Price:N0} ₫ & Công nợ Kỹ thuật {orderRow.InstallerFee:N0} ₫.";
            });

            Step("6. Đơn hàng & Điều phối V7", "Luồng SO Đối tác (PARTNER): Không trừ kho nội bộ nhưng vẫn kích hoạt thuê bao & công nợ", () =>
            {
                // Kiểm tra chặn Không trừ tồn kho khi không phải Hãng lắp đặt
                AssertThrows(() => v7Orders.CreateMulti(ownerId, customerId, [new V7OrderLineDraft(imeiProdId, 1)],
                    "55 đường 30/4", "Test", false, "0886287429", "15g00 thứ 3 ( 20/10/2026)", "Seller",
                    contactName: "Lâm Hằng ( xe Đỗ Văn Hội)", durationMonths: 12, warehouseId: wh1Id,
                    partner: false, installer: "ĐV lắp đặt", deductStock: false),
                    "Chặn Không trừ tồn kho khi Đơn vị lắp đặt không phải Hãng");

                var stockBefore = db.Stock().Count(s => s.Status == "IN_STOCK");
                var summaryBefore = db.ProductStockSummaries().First(s => s.ProductId == imeiProdId).InStockQty;
                orderPartnerId = v7Orders.CreateMulti(ownerId, customerId, [new V7OrderLineDraft(imeiProdId, 1)],
                    "55 đường 30/4", "không thu tiền, khi đến địa chỉ lắp liên hệ sdt: 0886287429", cod: false,
                    customerPhone: "0886287429", installAt: "15g00 thứ 3 ( 20/10/2026)", saleSource: "HTX Nam Hi",
                    contactName: "Lâm Hằng ( xe Đỗ Văn Hội)", durationMonths: 12, warehouseId: wh1Id,
                    partner: true, installer: "Hãng lắp đặt Á Châu", deductStock: false);
                v7Orders.Report(ownerId, orderPartnerId, "869999999999999", "61C-123.45", "xe Đỗ Văn Hội");
                v7Orders.Complete(ownerId, orderPartnerId, activatedDate: "2026-10-20");

                var stockAfter = db.Stock().Count(s => s.Status == "IN_STOCK");
                var summaryAfter = db.ProductStockSummaries().First(s => s.ProductId == imeiProdId).InStockQty;
                Assert(stockBefore == stockAfter && summaryBefore == summaryAfter, "Đơn PARTNER (Không trừ tồn kho) không được trừ tồn kho nội bộ.");
                var devPartner = db.Devices().FirstOrDefault(d => d.Imei == "869999999999999");
                Assert(devPartner is not null && devPartner.ActivatedAt == "2026-10-20" && devPartner.ExpiresAt == "2027-10-20",
                    $"Đơn PARTNER phải kích hoạt đúng Ngày kích hoạt 2026-10-20 -> 2027-10-20 (thực tế: {devPartner?.ActivatedAt} -> {devPartner?.ExpiresAt}).");
                var allOrdersBeforePriceChange = v7Orders.List();
                var completedInternalBefore = allOrdersBeforePriceChange.First(o => o.Id == orderInternalId);
                Assert(completedInternalBefore.IsImmutable && completedInternalBefore.Price == 3_420_000,
                    "Đơn hoàn thành phải bật cờ IsImmutable và giữ đúng giá Snapshot 3.420.000 ₫.");

                // Kiểm tra khóa bất biến đơn hàng đã hoàn thành (COMPLETED_ORDER_IMMUTABLE) ở cả tầng C# và SQLite Trigger
                AssertThrows(() => v7Orders.UpdateOrderInfo(ownerId, orderInternalId, "Sửa trái phép", "0900000000", "Địa chỉ mới", "02/11/2026", 24, "Hack", false, wh1Id, false, "Kỹ thuật khác", installerId, true),
                    "Chặn cập nhật đơn hàng đã COMPLETED");
                AssertThrows(() => v7Orders.Cancel(ownerId, orderInternalId),
                    "Chặn hủy đơn hàng đã COMPLETED");
                AssertThrows(() =>
                {
                    using var c = new SqliteConnection($"Data Source={dbPath};Foreign Keys=True");
                    c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "UPDATE v7_orders SET price_vnd = 9999999 WHERE id = $id";
                    cmd.Parameters.AddWithValue("$id", orderInternalId);
                    cmd.ExecuteNonQuery();
                }, "SQLite Trigger trg_v7_orders_lock_completed_update");

                // Thay đổi giá sản phẩm theo thị trường trong tương lai -> Đơn hoàn thành vẫn giữ nguyên giá cố định Snapshot
                catalog.SetSupplierGroupPrice(ownerId, supGroupId, imeiProdId, 1_350_000, DateTime.Today);
                catalog.SetCustomerGroupPrice(ownerId, custGroupId, imeiProdId, 2_400_000, 2_600_000, 800_000, DateTime.Today);

                var completedInternalAfter = v7Orders.List().First(o => o.Id == orderInternalId);
                Assert(completedInternalAfter.Price == 3_420_000 && completedInternalAfter.CostSnapshot == completedInternalBefore.CostSnapshot && completedInternalAfter.GrossProfitSnapshot == completedInternalBefore.GrossProfitSnapshot,
                    $"Khi giá sản phẩm thay đổi trong tương lai, đơn hoàn thành phải giữ nguyên giá cố định Snapshot (Price={completedInternalAfter.Price}, Cost={completedInternalAfter.CostSnapshot}).");

                return $"Đơn SO7-{orderPartnerId:000000} hoàn tất · Khóa bất biến đơn COMPLETED & Bảo toàn Giá cố định Snapshot khi giá SP thị trường thay đổi.";
            });

            // =========================================================================
            // 7. QUẢN LÝ SELLER, GIA HẠN THIẾT BỊ CHỌN SELLER & THỐNG KÊ KẾ TOÁN CUỐI KỲ
            // =========================================================================
            Step("7. Gia hạn & Công nợ", "Quản lý Seller, Gia hạn thuê bao 12 tháng gắn Seller + Thống kê Kế toán Snapshot cuối kỳ", () =>
            {
                db.ActorId = ownerId;
                var newSellerId = catalog.SaveSeller(ownerId, null, "", "Seller Lê Hoàng Nam", "0918222333", "Phòng Kinh doanh B2B", 150_000, 90_000, "Chuyên HTX & Gia hạn");
                var sellerList = catalog.Sellers();
                var createdSeller = sellerList.First(s => s.Id == newSellerId);
                Assert(createdSeller.Code.StartsWith("SL"), $"Mã Seller tự sinh không hợp lệ: {createdSeller.Code}");

                var dev1 = db.Devices().First(d => d.Imei == "860000000000001");
                var oldExp = DateTime.Parse(dev1.ExpiresAt);

                // Gia hạn bằng CASH & chọn Seller Lê Hoàng Nam -> Hóa đơn RENEWAL lưu giá cố định Snapshot và ghi nhận Seller gia hạn
                db.Renew(dev1.ActivationId, "TNM", "CASH", createdSeller.Id, createdSeller.Name);
                var dev1After = db.Devices().First(d => d.ActivationId == dev1.ActivationId);
                var newExp = DateTime.Parse(dev1After.ExpiresAt);
                Assert(newExp.Year == oldExp.Year + 1, $"Hạn mới phải cộng thêm 1 năm: {dev1.ExpiresAt} -> {dev1After.ExpiresAt}");

                var latestRenewal = db.Renewals().First(r => r.Imei == "860000000000001");
                Assert(latestRenewal.SellerId == createdSeller.Id && latestRenewal.SellerName.Contains(createdSeller.Name),
                    $"Lịch sử gia hạn phải ghi nhận đúng Seller gia hạn: {latestRenewal.SellerName}");

                var renewInv = db.Invoices().First(i => i.Type == "RENEWAL");
                Assert(renewInv.Status == "PAID" && renewInv.Balance == 0 && renewInv.Total == latestRenewal.Price,
                    $"Hóa đơn gia hạn CASH phải PAID ngay với giá Snapshot {latestRenewal.Price:N0} ₫: Status={renewInv.Status}, Balance={renewInv.Balance}");

                var snapshots = db.CompletedOrderSnapshots();
                var sellerPeriodStats = db.SellerPeriodStats();
                Assert(snapshots.Count >= 2 && sellerPeriodStats.Any(s => s.SellerId == createdSeller.Id && s.RenewalCount >= 1 && s.TotalEstimatedCommissionVnd >= 90_000),
                    "Thống kê Kế toán cuối kỳ phải tổng hợp đầy đủ Đơn hàng hoàn thành Snapshot và Hoa hồng gia hạn theo Seller.");

                // Kiểm tra Admin cập nhật lịch sử gia hạn (UpdateRenewal), khóa số Kế toán (SetRenewalLock) & xóa lịch sử gia hạn (DeleteRenewal)
                var dev2 = db.Devices().First(d => d.Imei == "860000000000002");
                var dev2OrigExp = dev2.ExpiresAt;
                db.Renew(dev2.ActivationId, "HTX", "DEBT", createdSeller.Id, createdSeller.Name);
                var ren2 = db.Renewals().First(r => r.Imei == "860000000000002");
                Assert(db.SellerPeriodStats().First(s => s.SellerId == createdSeller.Id).RenewalCount == 2,
                    "Sau khi gia hạn thêm thiết bị thứ 2, số lượt gia hạn của Seller phải tăng lên 2.");

                // Cập nhật lại lịch sử gia hạn (thay đổi hạn mới + số tiền + phương thức thanh toán) khi chưa chốt số
                db.UpdateRenewal(ren2.Id, createdSeller.Id, createdSeller.Name, "B2C", ren2.StartDate, ren2.PreviousExpiry, "2029-12-31", 950_000, "CASH");
                Assert(db.Devices().First(d => d.ActivationId == dev2.ActivationId).ExpiresAt == "2029-12-31",
                    "Sau khi sửa lịch sử gia hạn, hạn hết hạn của thiết bị phải tự động cập nhật thành 2029-12-31.");
                var ren2Inv = db.Invoices().First(i => i.RenewalId == ren2.Id);
                Assert(ren2Inv.Total == 950_000 && ren2Inv.Status == "PAID",
                    $"Chứng từ Kế toán gia hạn phải tự động cập nhật theo số tiền mới 950.000 ₫ và trạng thái PAID (thực tế: {ren2Inv.Total}, {ren2Inv.Status}).");

                // Kế toán chốt số / khóa số -> chặn sửa / xóa lịch sử gia hạn và chặn đổi hạn thiết bị
                db.SetRenewalLock(ren2.Id, true);
                AssertThrows(() => db.UpdateRenewal(ren2.Id, createdSeller.Id, createdSeller.Name, "TNM", ren2.StartDate, ren2.PreviousExpiry, "2030-01-01", 500_000, "DEBT"),
                    "Chặn cập nhật lịch sử gia hạn khi Kế toán đã chốt số");
                AssertThrows(() => db.DeleteRenewal(ren2.Id),
                    "Chặn xóa lịch sử gia hạn khi Kế toán đã chốt số");
                AssertThrows(() => db.UpdateDeviceActivation(dev2.ActivationId, dev2.ActivatedAt, 12, dev2.Plate, dev2.VinNo, "2030-05-05"),
                    "Chặn cập nhật thời gian gia hạn thiết bị khi có đợt gia hạn đã chốt số");

                // Mở khóa và xóa lịch sử gia hạn -> khôi phục hạn thiết bị & xóa chứng từ kế toán + giảm hoa hồng Seller
                db.SetRenewalLock(ren2.Id, false);
                db.DeleteRenewal(ren2.Id);
                Assert(db.Devices().First(d => d.ActivationId == dev2.ActivationId).ExpiresAt == dev2OrigExp,
                    $"Sau khi xóa lịch sử gia hạn, hạn thiết bị phải khôi phục về {dev2OrigExp}.");
                Assert(!db.Invoices().Any(i => i.RenewalId == ren2.Id),
                    "Sau khi xóa lịch sử gia hạn, chứng từ kế toán của đợt gia hạn đó phải được xóa mềm.");
                Assert(db.SellerPeriodStats().First(s => s.SellerId == createdSeller.Id).RenewalCount == 1,
                    "Sau khi xóa lịch sử gia hạn, số lượt gia hạn và hoa hồng của Seller phải tự động giảm về 1.");

                // Kiểm tra nâng cấp Chuyển đổi HTX / Khách hàng trên Thiết bị (Cách 2 khi Gia hạn & Cách 1 tại Sửa thông tin):
                var retailGrpId = catalog.SaveGroup(ownerId, "CUSTOMER", null, "KH_LE_TEST", "Khách lẻ trực tiếp Test");
                catalog.SetCustomerGroupPrice(ownerId, retailGrpId, imeiProdId, 1_900_000, 2_100_000, 960_000, DateTime.Today);
                var retailCustId = catalog.SaveCustomer(ownerId, null, "", "Khách lẻ Trần Văn B", retailGrpId, "0933444555", "Q.Bình Thạnh", "Khách lẻ");
                var allTestUsers = auth.AllUsers();
                var testStaffId = allTestUsers.First(u => u.Username == "nv_kho_test").Id;
                var testAccId = allTestUsers.First(u => u.Username == "ke_toan_test").Id;
                var testAdminId = allTestUsers.First(u => u.Username == "admin_test").Id;

                Assert(db.CanTransferDeviceMethod1User(ownerId) && !db.CanTransferDeviceMethod1User(testAdminId) && !db.CanTransferDeviceMethod1User(testAccId) && !db.CanTransferDeviceMethod1User(testStaffId),
                    "Cách 1 (Sửa thông tin + đồng bộ công nợ) chỉ cho phép Owner/Main, chặn Admin/Kế toán/Nhân viên.");
                Assert(db.CanTransferDeviceOnRenewUser(ownerId) && db.CanTransferDeviceOnRenewUser(testAdminId) && db.CanTransferDeviceOnRenewUser(testAccId) && !db.CanTransferDeviceOnRenewUser(testStaffId),
                    "Cách 2 (Chuyển đơn vị khi Gia hạn) cho phép Owner/Main/Admin/Kế toán, chặn nhân viên thường.");

                // Cách 2: Nhân viên thường bị chặn chuyển đơn vị khi gia hạn; Kế toán được phép chuyển dev1 từ HTX (500k) sang Khách lẻ (960k)
                AssertThrows(() => db.Renew(dev1.ActivationId, "B2C", "DEBT", createdSeller.Id, createdSeller.Name, targetCustomerId: retailCustId, actorId: testStaffId),
                    "Chặn nhân viên thường chuyển HTX/Khách hàng khi bấm Gia hạn");
                db.Renew(dev1.ActivationId, "B2C", "DEBT", createdSeller.Id, createdSeller.Name, targetCustomerId: retailCustId, newPlateNo: "51H-999.88", actorId: testAccId);

                var dev1Transferred = db.Devices().First(d => d.ActivationId == dev1.ActivationId);
                Assert(dev1Transferred.CustomerId == retailCustId && dev1Transferred.RenewalPriceVnd == 960_000 && dev1Transferred.Plate == "51H-999.88",
                    $"Cách 2: Thiết bị phải chuyển sang Khách lẻ (960.000 ₫): Cust={dev1Transferred.CustomerId}, Price={dev1Transferred.RenewalPriceVnd}");
                var dev1Rens = db.Renewals().Where(r => r.Imei == "860000000000001").OrderBy(r => r.Id).ToList();
                Assert(dev1Rens.Count == 2 && dev1Rens[0].CustomerName.Contains("Sài Gòn Xanh") && dev1Rens[0].Price == 800_000 && dev1Rens[1].CustomerName.Contains("Trần Văn B") && dev1Rens[1].Price == 960_000,
                    $"Cách 2: Lịch sử & công nợ gia hạn cũ phải giữ nguyên ở HTX cũ (800k), chỉ đợt gia hạn mới ghi cho Khách mới (960k): [{dev1Rens[0].CustomerName}:{dev1Rens[0].Price}] vs [{dev1Rens[1].CustomerName}:{dev1Rens[1].Price}]");

                // Cách 1: Admin/Kế toán bị chặn chuyển đơn vị tại Sửa thông tin; Owner chuyển dev1 về lại HTX VIP -> cập nhật đồng bộ cả khâu sau về giá nhóm HTX (800k) & ghi log Owner
                AssertThrows(() => db.UpdateDeviceActivation(dev1.ActivationId, dev1Transferred.ActivatedAt, 12, dev1Transferred.Plate, dev1Transferred.VinNo, dev1Transferred.ExpiresAt, createdSeller.Id, "ACTIVE", "Chủ xe HTX", "0909000111", customerId, testAdminId),
                    "Chặn Admin thực hiện Cách 1 tại mục Sửa thông tin");
                AssertThrows(() => db.UpdateDeviceActivation(dev1.ActivationId, dev1Transferred.ActivatedAt, 12, dev1Transferred.Plate, dev1Transferred.VinNo, dev1Transferred.ExpiresAt, createdSeller.Id, "ACTIVE", "Chủ xe HTX", "0909000111", customerId, testAccId),
                    "Chặn Kế toán thực hiện Cách 1 tại mục Sửa thông tin");

                var auditBeforeOwnerTransfer = db.Audits().Count(a => a.Action == "TRANSFER_DEVICE");
                var syncMsg = db.UpdateDeviceActivation(dev1.ActivationId, dev1Transferred.ActivatedAt, 12, dev1Transferred.Plate, dev1Transferred.VinNo, dev1Transferred.ExpiresAt, createdSeller.Id, "ACTIVE", "Chủ xe HTX", "0909000111", customerId, ownerId);
                var dev1BackToHtx = db.Devices().First(d => d.ActivationId == dev1.ActivationId);
                Assert(dev1BackToHtx.CustomerId == customerId && dev1BackToHtx.RenewalPriceVnd == 800_000,
                    $"Cách 1: Thiết bị phải quay về HTX VIP (800.000 ₫): {syncMsg}");
                var syncedRens = db.Renewals().Where(r => r.Imei == "860000000000001").ToList();
                Assert(syncedRens.All(r => r.CustomerName.Contains("Sài Gòn Xanh") && r.Price == 800_000),
                    "Cách 1: Toàn bộ đợt gia hạn chưa khóa sổ phải đồng bộ sang đơn vị mới và tính lại theo giá nhóm KH mới (800.000 ₫).");
                var syncedRenInvs = db.Invoices().Where(i => i.Type == "RENEWAL").ToList();
                Assert(syncedRenInvs.All(i => i.Customer.Contains("Sài Gòn Xanh") && i.Total == 800_000),
                    "Cách 1: Hóa đơn công nợ gia hạn tại /finance phải đổi tên sang đơn vị mới và cập nhật theo giá nhóm KH mới (800.000 ₫).");
                Assert(db.Audits().Count(a => a.Action == "TRANSFER_DEVICE") == auditBeforeOwnerTransfer + 1,
                    "Cách 1: Khi Owner thực hiện chuyển đơn vị phải ghi nhận đầy đủ vào Audit Log.");

                // Thanh toán 1 phần rồi thanh toán hết cho hóa đơn SALE
                var saleInv = db.Invoices().First(i => i.Type == "SALE" && i.Status == "OPEN" && i.Total == 3_420_000);
                db.PayInvoice(saleInv.Id, 1_420_000, "BANK");
                var saleInvPartial = db.Invoices().First(i => i.Id == saleInv.Id);
                Assert(saleInvPartial.Status == "PARTIAL" && saleInvPartial.Balance == 2_000_000, "Sau khi trả 1 phần, trạng thái phải là PARTIAL.");

                db.PayInvoice(saleInv.Id, 2_000_000, "CASH");
                var saleInvPaid = db.Invoices().First(i => i.Id == saleInv.Id);
                Assert(saleInvPaid.Status == "PAID" && saleInvPaid.Balance == 0, "Sau khi trả đủ, trạng thái phải là PAID.");

                // Kiểm tra chi trả công nợ Kỹ thuật (SERVICE) & lập + chi Hoa hồng Sale (COMMISSION)
                var srvInv = db.Invoices().First(i => i.Type == "SERVICE" && i.Total == 300_000);
                db.PayInvoice(srvInv.Id, 300_000, "BANK");
                Assert(db.Invoices().First(i => i.Id == srvInv.Id).Status == "PAID", "Công nợ kỹ thuật sau khi chi trả phải chuyển sang PAID.");

                var commId = db.CreateCustomInvoice("COMMISSION", null, null, "Sale Nguyễn Minh Anh", "Hoa hồng đơn SO7-000001", imeiProdId, 2, 100_000, DateTime.Today.AddDays(10).ToString("yyyy-MM-dd"));
                db.PayInvoice(commId, 200_000, "BANK");
                Assert(db.Invoices().First(i => i.Id == commId).Status == "PAID", "Công nợ hoa hồng Sale sau khi chi trả phải chuyển sang PAID.");

                // Kiểm tra chi trả công nợ Nhà cung cấp (Nhập hàng) vào Sổ quỹ & kiểm tra Sổ quỹ (Thu/Chi/Tồn: Tiền mặt & Ngân hàng)
                var purInv = db.Invoices().First(i => i.Type == "PURCHASE" && i.Balance > 0);
                db.PayInvoice(purInv.Id, 1_000_000, "CASH");
                var cb = db.CashBookSummary();
                var pDebts = db.PartnerDebtSummaries();
                Assert(cb.TotalInVnd > 0 && cb.TotalOutVnd >= 1_500_000 && cb.SupplierPaidVnd >= 1_000_000 && cb.TotalBalanceVnd == cb.TotalInVnd - cb.TotalOutVnd && cb.CashBalanceVnd == cb.CashInVnd - cb.CashOutVnd && cb.BankBalanceVnd == cb.BankInVnd - cb.BankOutVnd,
                    "Sổ quỹ phải tính đúng Khoản Thu, Khoản Chi (Nhập hàng NCC & Chi khác) và Tồn quỹ Tiền mặt / Ngân hàng.");
                Assert(pDebts.Any(d => d.CategoryCode == "SUPPLIER") && pDebts.Any(d => d.CategoryCode == "CUSTOMER") && pDebts.Any(d => d.CategoryCode == "SELLER"),
                    "Báo cáo Tổng hợp Công nợ phải bao gồm đủ 3 nhóm: Nhà cung cấp, Khách hàng và Sell.");
                Assert(db.Payments().Count >= 6, "Sổ phiếu thu/chi phải ghi nhận đầy đủ các giao dịch PT (IN) và PC (OUT).");

                // Kiểm tra luồng Quản lý Thu/Chi: tùy biến tạo Khoản Thu (IN) & Khoản Chi (OUT) rồi lập Phiếu Thu / Phiếu Chi chọn đúng khoản
                var customInCatId = db.SaveExpenseCategory(null, null, "Thu đặt cọc hợp đồng B2B", "1. Thu bán hàng & Lắp đặt", "CUSTOMER", "Thu tiền đặt cọc", 25, true, "IN");
                var customOutCatId = db.SaveExpenseCategory(null, null, "Chi phí vận chuyển chành xe", "2. Bán hàng & Lắp đặt", "FREE", "Chi gửi hàng chành xe", 25, true, "OUT");
                Assert(db.ExpenseCategories(false, "IN").Any(c => c.Id == customInCatId && c.IsIncome), "Khoản Thu vừa tạo phải xuất hiện trong danh sách chọn Khoản Thu (IN).");
                Assert(db.ExpenseCategories(false, "OUT").Any(c => c.Id == customOutCatId && !c.IsIncome), "Khoản Chi vừa tạo phải xuất hiện trong danh sách chọn Khoản Chi (OUT).");

                var ptVoucherId = db.SaveExpenseVoucher(null, customInCatId, "BANK", 500_000, null, "HTX Ánh Dương", null, customerId, null, null, null, null, "UNC-001", "Nhận đặt cọc lô 5 thiết bị", "IN");
                var pcVoucherId = db.SaveExpenseVoucher(null, customOutCatId, "CASH", 120_000, null, "Chành xe Phương Trang", null, null, null, null, null, null, "BILL-88", "Gửi hàng đi tỉnh", "OUT");
                var allVouchers = db.ExpenseVouchers();
                Assert(allVouchers.Any(v => v.Id == ptVoucherId && v.IsIncome && v.PaymentNo.StartsWith("PT-") && v.CategoryId == customInCatId), "Phiếu Thu lập từ Khoản Thu tùy biến phải mang mã PT- và gắn đúng CategoryId.");
                Assert(allVouchers.Any(v => v.Id == pcVoucherId && !v.IsIncome && v.PaymentNo.StartsWith("PC-") && v.CategoryId == customOutCatId), "Phiếu Chi lập từ Khoản Chi tùy biến phải mang mã PC- và gắn đúng CategoryId.");

                // Kiểm tra tính bất biến / chống trùng lặp (idempotent) của ReconcileAccountingData & tự động đồng bộ dữ liệu import khi ActorId = 0
                db.ActorId = 0;
                var (recPur, recSale, recRen, recSrv) = db.ReconcileAccountingData();
                Assert(recPur == 0 && recSale == 0 && recRen == 0 && recSrv == 0,
                    $"ReconcileAccountingData phải chống trùng lặp tuyệt đối khi dữ liệu đã có đủ chứng từ (thực tế: {recPur},{recSale},{recRen},{recSrv}).");

                using (var c = new SqliteConnection($"Data Source={dbPath};Foreign Keys=True"))
                {
                    c.Open();
                    AuditRuntime.Attach(c, () => 0);
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO stock_units(product_id,imei,status,customer_id,supplier_id) VALUES($p,'868888888888888','INSTALLED',$c,$sup);
                        INSERT INTO activations(stock_unit_id,customer_id,product_id,plate_no,activated_at,expires_at)
                        VALUES(last_insert_rowid(),$c,$p,'51F-777.77','2026-11-01','2027-11-01');
                        INSERT INTO renewals(activation_id,source_type,renewed_at,previous_expiry,new_expiry,amount_vnd,payment_method)
                        VALUES(last_insert_rowid(),'B2C','2026-11-02','2027-11-01','2028-11-01',850000,'DEBT');";
                    cmd.Parameters.AddWithValue("$p", imeiProdId);
                    cmd.Parameters.AddWithValue("$c", customerId);
                    cmd.Parameters.AddWithValue("$sup", supplierId);
                    cmd.ExecuteNonQuery();
                }

                var (impPur, impSale, impRen, impSrv) = db.ReconcileAccountingData();
                Assert(impRen == 1,
                    $"ReconcileAccountingData khi ActorId=0 phải tự động sinh 1 RENEWAL cho đợt gia hạn chưa có chứng từ (thực tế: {impPur},{impSale},{impRen},{impSrv}).");
                var (impPur2, impSale2, impRen2, impSrv2) = db.ReconcileAccountingData();
                Assert(impPur2 == 0 && impSale2 == 0 && impRen2 == 0 && impSrv2 == 0,
                    "Gọi ReconcileAccountingData lần 2 phải trả về (0,0,0,0).");
                db.ActorId = ownerId;

                // Kiểm tra nâng cấp 2 Trụ cột (Thiết bị ↔ Đơn hàng 3 chế độ) + Giá tổng theo thời hạn & Đơn giá linh hoạt:
                // (A) Khai báo sẵn xe chưa có IMEI bên Thiết bị -> chưa tính vào Active
                var activeBeforePre = db.Dashboard().Active;
                db.CreateManualDeviceActivation(customerId, imeiProdId, "", "51K-111.22", "Chủ xe Khai báo trước", "0901222333",
                    DateTime.Today.ToString("yyyy-MM-dd"), DateTime.Today.AddYears(1).ToString("yyyy-MM-dd"), createdSeller.Id, "XANHGPS", "PENDING_INSTALL", "VIN-PRE-01");
                var preDev = db.Devices().First(d => d.Plate == "51K-111.22");
                Assert(preDev.IsPreDeclared && !preDev.HasInstallOrder && db.Dashboard().Active == activeBeforePre,
                    "Xe khai báo sẵn chờ lắp đặt phải có IsPreDeclared=true, chưa có đơn lắp và không đếm sớm vào Active.");

                // (B) 1/ Đơn lắp đặt gói 24 tháng liên kết xe khai báo sẵn -> tính đúng Giá có lắp 24T (12T + 1 năm GH 800k) & nâng cấp thẳng hồ sơ xe
                var price12M = v7Orders.PreviewUnitPrice(customerId, imeiProdId, includeInstallPrice: true, durationMonths: 12);
                var price24M = v7Orders.PreviewUnitPrice(customerId, imeiProdId, includeInstallPrice: true, durationMonths: 24);
                Assert(price24M == price12M + 800_000,
                    $"Giá lắp đặt gói 24 tháng phải bằng Giá lắp 12T ({price12M:N0}) + 1 năm gia hạn (800,000) = {price12M + 800_000:N0} (thực tế: {price24M:N0}).");

                var preInstallOrdId = v7Orders.CreateMulti(
                    ownerId, customerId,
                    [new V7OrderLineDraft(imeiProdId, 1, CustomUnitPrice: null, TargetActivationId: preDev.ActivationId, Imei: "860000000000003", PlateNo: "51K-111.22", VinNo: "VIN-PRE-01")],
                    "Bến xe Miền Đông", "Lắp xe đã khai báo sẵn gói 2 năm", false, "0901222333", null, createdSeller.Name, "Chủ xe Khai báo trước",
                    durationMonths: 24, warehouseId: wh1Id, partner: false, installer: "Nguyễn Kỹ Thuật", installerEmployeeId: installerId,
                    deductStock: true, sellerRecordId: createdSeller.Id, noInstallFee: false, orderKind: "INSTALL", completeImmediately: true);
                var preDevAfter = db.Devices().First(d => d.ActivationId == preDev.ActivationId);
                Assert(!preDevAfter.IsPreDeclared && preDevAfter.HasInstallOrder && preDevAfter.Imei == "860000000000003" && db.Devices().Count(d => d.Plate == "51K-111.22") == 1,
                    $"Sau khi hoàn thành Đơn lắp đặt SO7-{preInstallOrdId:000000}, xe khai báo sẵn phải chuyển sang ACTIVE gắn IMEI 860000000000003 mà không bị nhân đôi dòng.");

                // (C) 2/ Đơn bán thương mại (TM7-...) hoàn thành ngay 1 bước với Đơn giá chỉnh linh hoạt & 0 ₫ công lắp
                var commOrdId = v7Orders.CreateMulti(
                    ownerId, customerId,
                    [new V7OrderLineDraft(qtyProdId, 2, CustomUnitPrice: 165_000)],
                    "Giao sỉ tại văn phòng", "Đơn thương mại chỉnh giá linh hoạt", false, "0908888999", null, createdSeller.Name, "Đại diện HTX",
                    durationMonths: 12, warehouseId: wh1Id, partner: false, installer: null, installerEmployeeId: null,
                    deductStock: true, sellerRecordId: createdSeller.Id, noInstallFee: true, orderKind: "COMMERCIAL", completeImmediately: true);
                var commOrd = v7Orders.List().First(o => o.Id == commOrdId);
                Assert(commOrd.Code.StartsWith("TM7-") && commOrd.OrderKind == "COMMERCIAL" && commOrd.NoInstallFee && commOrd.InstallerFee == 0 && commOrd.Price == 330_000 && commOrd.Status == "COMPLETED",
                    $"Đơn bán thương mại phải mang mã TM7, công lắp = 0 ₫ và tổng giá = 330.000 ₫ (thực tế: {commOrd.Code}, Kind={commOrd.OrderKind}, Price={commOrd.Price}, Fee={commOrd.InstallerFee}).");

                // (D) 3/ Đơn gia hạn (GH7-...) gộp 2 xe gói ngắn hạn 3 tháng (tự tính 800k * 3/12 = 200k/xe -> tổng 400k) + Đánh mốc thời gian
                var quote3M = db.PreviewRenewalPriceForDuration(customerId, imeiProdId, 3);
                Assert(quote3M == 200_000, $"Giá gia hạn ngắn hạn 3 tháng của nhóm 800k/năm phải bằng 200.000 ₫ (thực tế: {quote3M:N0}).");
                var batchRenRes = v7Orders.CreateRenewalOrder(
                    ownerId,
                    targetCustomerId: null,
                    lines: [new V7RenewalLineDraft(dev1.ActivationId), new V7RenewalLineDraft(dev2.ActivationId)],
                    durationMonths: 3,
                    milestoneExpiryDate: null,
                    sourceType: "HTX",
                    paymentMethod: "DEBT",
                    sellerRecordId: createdSeller.Id,
                    saleSource: createdSeller.Name,
                    notes: "Gia hạn gộp 2 xe quý 1");
                Assert(batchRenRes.OrderCode.StartsWith("GH7-") && batchRenRes.RenewedCount == 2 && batchRenRes.TotalAmountVnd == 400_000,
                    $"Đơn gia hạn gộp 2 xe 3 tháng phải có mã GH7-... và tổng tiền 400.000 ₫ (thực tế: {batchRenRes.OrderCode}, Total={batchRenRes.TotalAmountVnd}).");
                Assert(db.Devices().First(d => d.ActivationId == dev1.ActivationId).LastRenewalOrderCode == batchRenRes.OrderCode,
                    "Bảng Thiết bị phải map chính xác mã Đơn gia hạn gần nhất (GH7-...).");

                var milestoneTarget = "2031-06-30";
                var msRenRes = v7Orders.CreateRenewalOrder(
                    ownerId,
                    targetCustomerId: null,
                    lines: [new V7RenewalLineDraft(preDev.ActivationId, CustomPriceVnd: 450_000)],
                    durationMonths: 12,
                    milestoneExpiryDate: milestoneTarget,
                    sourceType: "TNM",
                    paymentMethod: "CASH",
                    sellerRecordId: createdSeller.Id,
                    saleSource: createdSeller.Name);
                Assert(db.Devices().First(d => d.ActivationId == preDev.ActivationId).ExpiresAt == milestoneTarget && msRenRes.TotalAmountVnd == 450_000,
                    "Đơn gia hạn chế độ Đánh mốc thời gian + chỉnh đơn giá thực thu 450.000 ₫ phải ghi nhận đúng mốc 2031-06-30 cho thiết bị.");

                db.ActorId = 0;
                var (postPur, postSale, postRen, postSrv) = db.ReconcileAccountingData();
                Assert(postPur == 0 && postSale == 0 && postRen == 0 && postSrv == 0,
                    $"Sau khi lập Đơn lắp 24T + Đơn thương mại TM7 + Đơn gia hạn gộp GH7, ReconcileAccountingData không được sinh trùng hóa đơn (thực tế: {postPur},{postSale},{postRen},{postSrv}).");
                db.ActorId = ownerId;

                // (E) Kiểm tra luồng Hủy đơn / Xóa mềm đơn -> Hoàn trả IMEI về Kho + Tự giải phóng slot 'CHỜ-NHẬP-IMEI' + Cảnh báo trùng IMEI chuẩn xác:
                db.Receive(imeiProdId, wh1Id, "860000000000091\n860000000000092");
                // Đơn chờ nhập IMEI trừ kho trước 1 slot (sẽ mượn tạm 1 trong 2 IMEI trên)
                var anonOrdId = v7Orders.CreateMulti(ownerId, customerId, [new V7OrderLineDraft(imeiProdId, 1)],
                    "Kho Q7", "Đơn hoàn thành trước chờ bổ sung IMEI", false, "0908888999", null, createdSeller.Name, "KH Chờ IMEI",
                    durationMonths: 12, warehouseId: wh1Id, partner: false, installer: "Nguyễn Kỹ Thuật", installerEmployeeId: installerId,
                    deductStock: true, sellerRecordId: createdSeller.Id, noInstallFee: false, orderKind: "INSTALL", completeImmediately: true);
                Assert(v7Orders.List().First(o => o.Id == anonOrdId).HasMissingImei, "Đơn hoàn thành chưa nhập IMEI phải bật cờ HasMissingImei.");

                // Tạo đơn nháp báo IMEI 860000000000092 rồi HỦY ĐƠN -> IMEI phải được giải phóng sạch và nằm ở IN_STOCK
                var cancelOrdId = v7Orders.CreateMulti(ownerId, customerId, [new V7OrderLineDraft(imeiProdId, 1, Imei: "860000000000092", PlateNo: "51H-000.92")],
                    "Kho Q7", "Đơn test hủy trả kho", false, "0908888999", null, createdSeller.Name, "KH Hủy",
                    durationMonths: 12, warehouseId: wh1Id, partner: false, installer: "Nguyễn Kỹ Thuật", installerEmployeeId: installerId, deductStock: true);
                // Trong lúc cancelOrdId chưa hủy, đơn khác nhập trùng 860000000000092 phải bị cảnh báo trùng rõ mã đơn
                AssertThrows(() => v7Orders.CreateMulti(ownerId, customerId, [new V7OrderLineDraft(imeiProdId, 1, Imei: "860000000000092")],
                    "Kho Q7", "Đơn trùng", false), "Cảnh báo trùng IMEI đang chờ xử lý ở đơn khác");

                v7Orders.Cancel(ownerId, cancelOrdId);
                var cancelledOrd = v7Orders.List().First(o => o.Id == cancelOrdId);
                Assert(cancelledOrd.Status == "CANCELLED" && string.IsNullOrEmpty(cancelledOrd.Imei) && cancelledOrd.Lines.All(l => string.IsNullOrEmpty(l.Imei)),
                    "Sau khi Hủy đơn, liên kết IMEI trên đơn hủy phải được gỡ sạch.");
                Assert(db.Stock().First(s => s.Imei == "860000000000092").Status == "IN_STOCK",
                    "Sau khi Hủy đơn, thiết bị 860000000000092 phải nằm ở trạng thái IN_STOCK trong Kho.");

                // Tạo & Hoàn thành đơn mới dùng 860000000000092 (tự động chuyển slot tạm nếu bị anonOrdId giữ chỗ, không báo trùng giả!)
                var specOrdId = v7Orders.CreateMulti(ownerId, customerId, [new V7OrderLineDraft(imeiProdId, 1, Imei: "860000000000092", PlateNo: "51H-111.92")],
                    "Kho Q7", "Đơn dùng lại IMEI từ đơn hủy", false, "0908888999", null, createdSeller.Name, "KH Lắp thật",
                    durationMonths: 12, warehouseId: wh1Id, partner: false, installer: "Nguyễn Kỹ Thuật", installerEmployeeId: installerId,
                    deductStock: true, sellerRecordId: createdSeller.Id, noInstallFee: false, orderKind: "INSTALL", completeImmediately: true);
                Assert(db.Devices().Any(d => d.Imei == "860000000000092" && !d.IsPreDeclared), "Đơn dùng lại IMEI từ đơn hủy phải hoàn thành và kích hoạt thành công.");

                // Xóa mềm đơn đã hoàn thành specOrdId -> hoàn trả 860000000000092 về lại IN_STOCK, sau đó xuất lại cho đơn mới thành công (không lỗi UNIQUE v7_stock_effects)
                v7Orders.SoftDeleteOrder(ownerId, specOrdId);
                Assert(db.Stock().First(s => s.Imei == "860000000000092").Status == "IN_STOCK" && !db.Devices().Any(d => d.Imei == "860000000000092"),
                    "Sau khi Xóa mềm đơn hoàn thành, thiết bị 860000000000092 phải quay về IN_STOCK tại Kho.");

                var reuseOrdId = v7Orders.CreateMulti(ownerId, customerId, [new V7OrderLineDraft(imeiProdId, 1, Imei: "860000000000092", PlateNo: "51H-222.92")],
                    "Kho Q7", "Đơn xuất lại IMEI sau xóa mềm", false, "0908888999", null, createdSeller.Name, "KH Xuất lại",
                    durationMonths: 12, warehouseId: wh1Id, partner: false, installer: "Nguyễn Kỹ Thuật", installerEmployeeId: installerId,
                    deductStock: true, sellerRecordId: createdSeller.Id, noInstallFee: false, orderKind: "INSTALL", completeImmediately: true);
                Assert(reuseOrdId > 0 && db.Devices().Any(d => d.Imei == "860000000000092" && d.Plate == "51H-222.92"),
                    "IMEI sau khi hoàn kho từ đơn xóa mềm phải xuất lại và kích hoạt ở đơn khác trơn tru.");
                AssertThrows(() => v7Orders.CreateMulti(ownerId, customerId, [new V7OrderLineDraft(imeiProdId, 1, Imei: "860000000000092")],
                    "Kho Q7", "Trùng IMEI đã kích hoạt", false), "Cảnh báo trùng khi IMEI đang kích hoạt thực tế");

                // (F) Kiểm tra đồng bộ xuyên suốt Đơn hàng – Kho – Thiết bị – Kế toán (Chuyển đơn vị Cách 1 bởi Owner trên đơn COMPLETED, Đơn gộp GH7, Xóa mềm đơn số lượng & Chặn xóa/sửa lệch HĐ trên Kế toán):
                // F1. Xóa mềm đơn thương mại hàng số lượng (commOrdId: 2 SP qtyProdId) -> hoàn kho số lượng +2 & hủy HĐ SALE
                var qtyBeforeSoftDelComm = ops.Balances(ownerId).First(b => b.ProductId == qtyProdId && b.WarehouseId == wh1Id).Quantity;
                v7Orders.SoftDeleteOrder(ownerId, commOrdId);
                var qtyAfterSoftDelComm = ops.Balances(ownerId).First(b => b.ProductId == qtyProdId && b.WarehouseId == wh1Id).Quantity;
                Assert(qtyAfterSoftDelComm == qtyBeforeSoftDelComm + 2,
                    $"Xóa mềm đơn thương mại hàng số lượng phải hoàn trả đúng +2 SP về Kho (trước={qtyBeforeSoftDelComm}, sau={qtyAfterSoftDelComm}).");

                // F2. Sửa & Xóa 1 xe trong Đơn gia hạn gộp 2 xe (batchRenRes: GH7-...) -> đồng bộ ngược GH7 + Hóa đơn gộp RENEWAL
                var batchRenDev2 = db.Renewals().First(r => r.ActivationId == dev2.ActivationId && r.Price == 200_000);
                db.UpdateRenewal(batchRenDev2.Id, createdSeller.Id, createdSeller.Name, "HTX", batchRenDev2.StartDate[..10], batchRenDev2.PreviousExpiry, batchRenDev2.EndDate, 250_000, "DEBT");
                var ghOrdAfterEdit = v7Orders.List().First(o => o.Id == batchRenRes.OrderId);
                var ghInvAfterEdit = db.Invoices().First(i => i.RefCode == batchRenRes.OrderCode && i.Type == "RENEWAL");
                Assert(ghOrdAfterEdit.Price == 450_000 && ghInvAfterEdit.Total == 450_000,
                    $"Sửa 1 dòng gia hạn trong đơn gộp GH7 phải đồng bộ Tổng đơn GH7 ({ghOrdAfterEdit.Price:N0}) = Hóa đơn RENEWAL ({ghInvAfterEdit.Total:N0}) = 450.000 ₫.");

                db.DeleteRenewal(batchRenDev2.Id, ownerId);
                var ghOrdAfterDel1 = v7Orders.List().First(o => o.Id == batchRenRes.OrderId);
                var ghInvAfterDel1 = db.Invoices().First(i => i.RefCode == batchRenRes.OrderCode && i.Type == "RENEWAL");
                var ghRemQty = ghOrdAfterDel1.Lines.Sum(l => l.Quantity);
                Assert(ghRemQty == 1 && ghOrdAfterDel1.Price == 200_000 && ghInvAfterDel1.Total == 200_000,
                    $"Xóa 1 xe khỏi đơn gộp GH7 phải giảm Đơn GH7 về 1 xe / 200.000 ₫ và Hóa đơn RENEWAL về 200.000 ₫ (thực tế: Qty={ghRemQty}, Ord={ghOrdAfterDel1.Price}, Inv={ghInvAfterDel1.Total}).");

                // F3. Owner thực hiện Cách 1 (UpdateDeviceActivation đổi đơn vị) trực tiếp trên đơn COMPLETED (reuseOrdId)
                var custGroup2Id = catalog.SaveGroup(ownerId, "CUSTOMER", null, "DL_VIP_SYNC", "Nhóm HTX VIP Test Đồng Bộ");
                catalog.SetCustomerGroupPrice(ownerId, custGroup2Id, imeiProdId, 1_800_000, 1_950_000, 650_000, DateTime.Today.AddDays(-10));
                var cust2Id = catalog.SaveCustomer(ownerId, null, "", "HTX Đồng Bộ Mới", custGroup2Id, "0907777888", "TP.HCM", "Sale Đồng Bộ");
                var reuseDev = db.Devices().First(d => d.Imei == "860000000000092");
                db.UpdateDeviceActivation(reuseDev.ActivationId, reuseDev.ActivatedAt, 12, reuseDev.Plate, reuseDev.VinNo, reuseDev.ExpiresAt,
                    createdSeller.Id, "ACTIVE", "Chủ xe HTX Đồng Bộ", "0907777888", cust2Id, ownerId);
                var reuseOrdAfterTransfer = v7Orders.List().First(o => o.Id == reuseOrdId);
                var reuseSaleInv = db.Invoices().First(i => i.RefCode == reuseOrdAfterTransfer.Code && i.Type == "SALE");
                Assert(reuseOrdAfterTransfer.CustomerId == cust2Id && reuseOrdAfterTransfer.Price == 1_950_000 && reuseSaleInv.Total == 1_950_000,
                    $"Owner chuyển đơn vị Cách 1 trên đơn COMPLETED phải đồng bộ Đơn hàng ({reuseOrdAfterTransfer.Price:N0}) = Hóa đơn SALE ({reuseSaleInv.Total:N0}) = 1.950.000 ₫ theo nhóm giá mới.");

                // F4. Chặn xóa lẻ hoặc sửa lệch tiền chứng từ trên Kế toán khi Đơn hàng gốc đang COMPLETED
                AssertThrows(() => db.DeleteInvoice(reuseSaleInv.Id), "Chặn xóa lẻ chứng từ SALE khi đơn hàng gốc đang hiệu lực");
                AssertThrows(() => db.UpdateCustomInvoice(reuseSaleInv.Id, cust2Id, null, "", "Sửa lệch tiền", imeiProdId, 1, 999_000, reuseSaleInv.DueAt),
                    "Chặn sửa lệch tổng tiền chứng từ SALE khỏi Đơn hàng gốc");

                // F5. Xóa mềm 1 thiết bị trong đơn gộp 2 thiết bị COMPLETED -> đồng bộ giảm trừ Đơn hàng + HĐ SALE + HĐ SERVICE + Hoàn 1 IMEI về IN_STOCK
                db.Receive(imeiProdId, wh1Id, "860000000000093\n860000000000094");
                var multiSyncOrdId = v7Orders.CreateMulti(ownerId, cust2Id,
                    [
                        new V7OrderLineDraft(imeiProdId, 1, Imei: "860000000000093", PlateNo: "51H-000.93"),
                        new V7OrderLineDraft(imeiProdId, 1, Imei: "860000000000094", PlateNo: "51H-000.94")
                    ],
                    "Kho Q7", "Đơn gộp 2 xe test xóa mềm 1 xe", false, "0907777888", null, createdSeller.Name, "HTX Đồng Bộ Mới",
                    durationMonths: 12, warehouseId: wh1Id, partner: false, installer: "Nguyễn Kỹ Thuật", installerEmployeeId: installerId,
                    deductStock: true, sellerRecordId: createdSeller.Id, noInstallFee: false, orderKind: "INSTALL", completeImmediately: true);
                var multiOrdCode = v7Orders.List().First(o => o.Id == multiSyncOrdId).Code;
                var act94 = db.Devices().First(d => d.Imei == "860000000000094");
                db.SoftDeleteDeviceActivation(act94.ActivationId, ownerId);

                var multiOrdAfterDel = v7Orders.List().First(o => o.Id == multiSyncOrdId);
                var multiRemQty = multiOrdAfterDel.Lines.Sum(l => l.Quantity);
                var multiSaleInv = db.Invoices().First(i => i.RefCode == multiOrdCode && i.Type == "SALE");
                var multiSrvInv = db.Invoices().First(i => i.RefCode == multiOrdCode && i.Type == "SERVICE");
                Assert(multiRemQty == 1 && multiOrdAfterDel.Price == 1_950_000 && multiSaleInv.Total == 1_950_000 &&
                       multiOrdAfterDel.InstallerFee == multiSrvInv.Total &&
                       db.Stock().First(s => s.Imei == "860000000000094").Status == "IN_STOCK",
                    $"Xóa mềm 1 xe trong đơn gộp 2 xe phải đồng bộ Đơn hàng (Qty={multiRemQty}, Price={multiOrdAfterDel.Price}, Fee={multiOrdAfterDel.InstallerFee}) = HĐ SALE ({multiSaleInv.Total}) + HĐ Kỹ thuật ({multiSrvInv.Total}) & hoàn 860000000000094 về IN_STOCK.");

                // F6. Kiểm tra xuất kho chéo mã Nasa (SP000019 trong kho -> đơn đặt mã Nasa khác ID) không vướng Trigger validate_inventory_product / validate_activation_product
                var nasaSkuA = productMaster.Save(ownerId, new ProductMaster.ProductDraft(null, prodGroupId, "Nasa GPS 4G nano", "GOODS", "Bộ", true, 12, CustomSku: "SP000019"));
                var nasaProdAId = productMaster.Products().First(p => p.Sku == nasaSkuA).Id;
                db.Receive(nasaProdAId, wh1Id, "1200009999");
                var nasaOrdId = v7Orders.CreateMulti(ownerId, cust2Id,
                    [new V7OrderLineDraft(imeiProdId, 1, CustomUnitPrice: 850_000)],
                    "Kho Q7", "Test Nasa chéo mã hoàn thành", false, "0907777888", null, createdSeller.Name, "HTX Nasa",
                    durationMonths: 12, warehouseId: wh1Id, partner: false, installer: "Nguyễn Kỹ Thuật", installerEmployeeId: installerId,
                    deductStock: true, sellerRecordId: createdSeller.Id, noInstallFee: false, orderKind: "INSTALL", completeImmediately: false);
                v7Orders.Report(ownerId, nasaOrdId, "1200009999", "51H-999.99", "Xe Nasa chéo mã");
                v7Orders.Complete(ownerId, nasaOrdId);
                Assert(db.Devices().Any(d => d.Imei == "1200009999" && d.Plate == "51H-999.99"), "Hoàn tất đơn chéo mã IMEI không vướng Trigger validate_inventory_product / validate_activation_product.");
                v7Orders.SoftDeleteOrder(ownerId, nasaOrdId);
                Assert(db.Stock().First(s => s.Imei == "1200009999").Status == "IN_STOCK", "Hoàn kho IMEI Nasa sau xóa mềm chuẩn xác.");

                // F7. Nhân viên thường (staffUserId) được phép cập nhật Thời gian gia hạn (Ngày hết hạn) & thông tin thiết bị, có ghi Log chi tiết Trước -> Sau
                Assert(auth.AllowedPermissions(staffUserId).Contains(Permissions.DevicesUpdate), "Nhân viên phải có quyền cập nhật hạn & thông tin thiết bị (devices.update).");
                var staffTargetDev = db.Devices().First(d => d.Imei == "860000000000092");
                var oldExpBeforeStaff = staffTargetDev.ExpiresAt;
                var newExpByStaff = DateTime.Parse(oldExpBeforeStaff).AddMonths(3).ToString("yyyy-MM-dd");
                db.UpdateDeviceActivation(
                    staffTargetDev.ActivationId,
                    staffTargetDev.ActivatedAt,
                    12,
                    "51H-888.92",
                    "SK-NV-UPDATE",
                    newExpByStaff,
                    createdSeller.Id,
                    "ACTIVE",
                    "Chủ xe NV Cập nhật",
                    "0908889999",
                    null,
                    staffUserId);
                var afterStaffDev = db.Devices().First(d => d.ActivationId == staffTargetDev.ActivationId);
                Assert(afterStaffDev.ExpiresAt == newExpByStaff && afterStaffDev.Plate == "51H-888.92" && afterStaffDev.OwnerName == "Chủ xe NV Cập nhật",
                    "Nhân viên cập nhật Ngày hết hạn & thông tin thiết bị chưa được lưu đúng.");

                // Chặn nhân viên thường đổi Đơn vị / HTX theo Cách 1
                AssertThrows(() => db.UpdateDeviceActivation(staffTargetDev.ActivationId, afterStaffDev.ActivatedAt, 12, afterStaffDev.Plate, afterStaffDev.VinNo, afterStaffDev.ExpiresAt,
                    createdSeller.Id, "ACTIVE", afterStaffDev.OwnerName, afterStaffDev.OwnerPhone, customerId, staffUserId),
                    "Chặn nhân viên thường chuyển đổi đơn vị quản lý theo Cách 1");

                // Kiểm chứng bắt buộc ghi Log đầy đủ vào cả audit_logs (Trước -> Sau + Tên nhân viên) và activity_events (JSON Trigger)
                using (var logConn = new SqliteConnection($"Data Source={dbPath};Foreign Keys=True"))
                {
                    logConn.Open();
                    using var logCmd = logConn.CreateCommand();
                    logCmd.CommandText = "SELECT COALESCE(details,'') FROM audit_logs WHERE action='UPDATE_ACTIVATION' AND entity='ACTIVATION' AND entity_id=$id AND actor_user_id=$u ORDER BY id DESC LIMIT 1";
                    logCmd.Parameters.AddWithValue("$id", staffTargetDev.ActivationId);
                    logCmd.Parameters.AddWithValue("$u", staffUserId);
                    var logDetails = Convert.ToString(logCmd.ExecuteScalar()) ?? "";
                    Assert(logDetails.Contains($"{oldExpBeforeStaff} → {newExpByStaff}") && logDetails.Contains("nv_kho_test") && logDetails.Contains("51H-888.92"),
                        $"Nhật ký audit_logs chưa ghi đầy đủ thông tin Trước → Sau của nhân viên: '{logDetails}'");

                    using var actEvtCmd = logConn.CreateCommand();
                    actEvtCmd.CommandText = "SELECT COUNT(*) FROM activity_events WHERE table_name='activations' AND record_id=$id AND actor_user_id=$u AND action='UPDATE'";
                    actEvtCmd.Parameters.AddWithValue("$id", staffTargetDev.ActivationId.ToString());
                    actEvtCmd.Parameters.AddWithValue("$u", staffUserId);
                    var evtCnt = Convert.ToInt64(actEvtCmd.ExecuteScalar());
                    Assert(evtCnt >= 1, "Bảng activity_events chưa lưu vết JSON UPDATE khi nhân viên cập nhật thiết bị.");
                }

                db.ActorId = 0;
                var (finPur, finSale, finRen, finSrv) = db.ReconcileAccountingData();
                Assert(finPur == 0 && finSale == 0 && finRen == 0 && finSrv == 0,
                    $"Đối soát tổng thể cuối cùng ReconcileAccountingData phải khớp tuyệt đối 0 lệch (thực tế: {finPur},{finSale},{finRen},{finSrv}).");
                db.ActorId = ownerId;

                return $"Gia hạn +1 năm ({dev1After.ExpiresAt}) · Đồng bộ tuyệt đối Đơn hàng (SO7/TM7/GH7) ↔ Kho ↔ Thiết bị ↔ Kế toán (0 sai lệch).";
            });

            // =========================================================================
            // 8. KIỂM TOÁN TỰ ĐỘNG, CHỐNG XÓA CỨNG & TOÀN VẸN DỮ LIỆU
            // =========================================================================
            Step("8. Kiểm toán & Toàn vẹn DB", "Kiểm tra Chống xóa cứng (SOFT_DELETE_ONLY), Nhật ký JSON & Xuất CSV", () =>
            {
                // Chặn xóa cứng
                AssertThrows(() =>
                {
                    using var c = new SqliteConnection($"Data Source={dbPath};Foreign Keys=True");
                    c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "DELETE FROM customers WHERE id=$id";
                    cmd.Parameters.AddWithValue("$id", customerId);
                    cmd.ExecuteNonQuery();
                }, "Trigger chặn xóa cứng SOFT_DELETE_ONLY");

                var eventCount = audit.Count();
                Assert(eventCount > 25, $"Bảng activity_events phải ghi nhận đầy đủ các sự kiện (hiện có {eventCount}).");

                var invCsv = CsvExports.Inventory(db);
                var prodCsv = CsvExports.ProductCatalog(productMaster);
                var finCsv = CsvExports.Invoices(db);
                var qtyCsv = CsvExports.QuantityLedger(ops, ownerId);
                Assert(invCsv.Length > 50 && prodCsv.Length > 50 && finCsv.Length > 50 && qtyCsv.Length > 50, "Dữ liệu xuất CSV không được rỗng.");

                var dash = db.Dashboard();
                var profit = db.ApproxGrossProfit();
                Assert(dash.Active == 7 && dash.Revenue > 0, $"Chỉ số Dashboard không khớp: Active={dash.Active}, Revenue={dash.Revenue}");

                return $"Đã ghi {eventCount} sự kiện Audit JSON · Chặn xóa cứng thành công · Dashboard & 4 luồng CSV hoạt động chuẩn.";
            });

            // =========================================================================
            // 9. KIỂM ĐỊNH RÀNG BUỘC & IMPORT THIẾT BỊ CHUẨN 12 CỘT (.XLSX / .CSV)
            // =========================================================================
            Step("9. Import Thiết bị 12 cột", "Kiểm tra Chặn lệch cột/sai IMEI, Khôi phục biển số E+, Chống đảo ngày, Bảo vệ Kho & GH7", () =>
            {
                // 9A. Chặn file sai cấu trúc tiêu đề 12 cột (chống lặp lại lỗi lệch cột 'BIỂNSỐ')
                var badHeaderCsv = "STT,Biển số,Tài khoản,Tên thiết bị,IMEI\n1,50E16157,50E16157,NASA4G,1800008444";
                var badHeaderRes = deviceImporter.ImportFromCsvText(badHeaderCsv, dryRun: false, actorId: ownerId);
                Assert(!badHeaderRes.Committed && badHeaderRes.RejectedErrorCount > 0, "Phải chặn file không đúng 12 cột tiêu đề chuẩn.");

                // 9B. Chặn dòng sai định dạng IMEI ('BIỂNSỐ') hoặc sai Tên Thiết bị ('UNKNOWN_GPS')
                var badRowCsv = "STT,Biển số xe/Số khung,Số EMEI Thiết bị,Tên Thiết bị,Tên Đại lý,Tên Khách hàng,SĐT Khách hàng,Trạng thái thiết bị,Ngày kích hoạt,Ngày hết hạn,Ngày gia hạn gần nhất (nếu có),Số lần gia hạn\n" +
                                "1,50E16157,BIỂNSỐ,XANHGPS,HTX Tân Đại Thành,Nguyễn A,0909123456,Hoạt động,20/10/2026,20/10/2027,0,0\n" +
                                "2,51H12345,861385079999111,UNKNOWN_GPS,HTX Tân Đại Thành,Nguyễn B,0909123456,Hoạt động,20/10/2026,20/10/2027,0,0";
                var badRowRes = deviceImporter.ImportFromCsvText(badRowCsv, dryRun: false, actorId: ownerId);
                Assert(!badRowRes.Committed && badRowRes.RejectedErrorCount == 2, $"Phải chặn đúng 2 dòng sai IMEI/Tên TB (thực tế: {badRowRes.RejectedErrorCount}).");

                // 9C. Kiểm tra bảo vệ IN_STOCK tồn kho, bảo vệ hạn GH7 ERP, khôi phục biển số 6.8E+259 -> 68E00258, gỡ dấu phẩy IMEI & chống đảo tháng/ngày
                var inStockUnit = db.Stock().First(s => s.Status == "IN_STOCK");
                var activeRen = db.Renewals().First();
                var renewedDev = db.Devices().First(d => d.ActivationId == activeRen.ActivationId);
                var expectedProtectedExp = renewedDev.ExpiresAt;

                var validXlsxBytes = ExcelExporter.BuildWorkbook(
                    "Update",
                    [
                        "STT", "Biển số xe/Số khung", "Số EMEI Thiết bị", "Tên Thiết bị", "Tên Đại lý",
                        "Tên Khách hàng", "SĐT Khách hàng", "Trạng thái thiết bị", "Ngày kích hoạt",
                        "Ngày hết hạn", "Ngày gia hạn gần nhất (nếu có)", "Số lần gia hạn"
                    ],
                    [
                        // Dòng 1: Mới hoàn toàn, biển số bị lỗi khoa học 6.8E+259 -> 68E00258, IMEI có dấu phẩy, SĐT mất số 0 đầu (dùng NASAGPS khớp SP000019/DX00002 trong sandbox)
                        [1, "6.8E+259", "12,509,999,888", "NASAGPS", renewedDev.Customer, "Trần Văn C", "912345678", "Hoạt động", "27/10/2026", "27/10/2027", "0", 0],
                        // Dòng 2: Trùng IMEI đang IN_STOCK trong Kho -> Phải giữ nguyên IN_STOCK (ProtectedInStockCount = 1)
                        [2, "51F99887", inStockUnit.Imei, "NASAGPS", renewedDev.Customer, "Lê Văn D", "#REF!", "Hoạt động", "25/10/2026", "25/10/2027", "0", 0],
                        // Dòng 3: Trùng thiết bị đã có đơn gia hạn GH7 trong ERP nhưng file đưa hạn cũ thấp hơn -> Phải bảo vệ hạn GH7
                        [3, renewedDev.Plate, renewedDev.Imei, "NASAGPS", renewedDev.Customer, "Chủ xe GH", "", "Hoạt động", "15/10/2026", "15/10/2027", "0", 0]
                    ]);

                using var ms = new MemoryStream(validXlsxBytes);
                var dryRes = deviceImporter.ImportFromStream(ms, "test-import.xlsx", dryRun: true, actorId: ownerId);
                Assert(dryRes.DryRun && !dryRes.Committed && dryRes.RejectedErrorCount == 0 && dryRes.InsertedNewCount == 1 && dryRes.ProtectedInStockCount == 1,
                    $"Dry-run không khớp kỳ vọng: Err={dryRes.RejectedErrorCount}, Ins={dryRes.InsertedNewCount}, Prot={dryRes.ProtectedInStockCount}");

                ms.Position = 0;
                var commitRes = deviceImporter.ImportFromStream(ms, "test-import.xlsx", dryRun: false, actorId: ownerId);
                Assert(commitRes.Committed && commitRes.InsertedNewCount == 1 && commitRes.ProtectedInStockCount == 1, "Commit import phải thành công.");

                var allDevs = db.Devices();
                var importedNew = allDevs.FirstOrDefault(d => d.Imei == "12509999888");
                Assert(importedNew is not null && importedNew.Plate == "68E00258" && importedNew.OwnerPhone == "0912345678",
                    $"Khôi phục biển số E+ / IMEI dấu phẩy / SĐT chưa chuẩn: Plate={importedNew?.Plate}, Phone={importedNew?.OwnerPhone}");

                Assert(db.Stock().First(s => s.Imei == inStockUnit.Imei).Status == "IN_STOCK", "IMEI đang tồn kho phải được giữ nguyên IN_STOCK.");
                Assert(allDevs.First(d => d.Imei == renewedDev.Imei).ExpiresAt == expectedProtectedExp, "Không được hạ ngày hết hạn thấp hơn phiếu gia hạn GH7 trong ERP.");

                db.ActorId = 0;
                var (pDiff, sDiff, rDiff, srvDiff) = db.ReconcileAccountingData();
                Assert(pDiff == 0 && sDiff == 0 && rDiff == 0 && srvDiff == 0, "Import thiết bị tuyệt đối không được sinh công nợ / hóa đơn ảo.");
                db.ActorId = ownerId;

                return $"Chặn chuẩn file lệch cột & IMEI lỗi · Khôi phục '6.8E+259'→'68E00258' · Bảo vệ IN_STOCK & hạn GH7 · 0 công nợ ảo.";
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore cleanup lock */ }
        }

        totalSw.Stop();
        var passed = steps.Count(s => s.Passed);
        var failed = steps.Count - passed;
        return new FlowTestReport(failed == 0, steps.Count, passed, failed, totalSw.ElapsedMilliseconds, steps);
    }
}
