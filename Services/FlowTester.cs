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
                    new Dictionary<string, string> { [Permissions.PurchaseCreate] = "ALLOW", [Permissions.InventoryReceive] = "DENY" });

                var created = auth.AllUsers().First(u => u.Username == "nv_kho_test");
                staffUserId = created.Id;
                var rights = auth.AllowedPermissions(staffUserId);
                Assert(rights.Contains(Permissions.PurchaseCreate), "Quyền ALLOW riêng (purchase.create) chưa có hiệu lực.");
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

                // Kiểm tra chống khóa/sửa Owner
                AssertThrows(() => auth.UpdateUser(staffUserId, ownerId, "Hack", false, "", [whRoleId]), "Chặn sửa tài khoản Owner");
                return $"Tạo User #{staffUserId} (nv_kho_test) & Kế toán #{accUserId} · Override ALLOW/DENY & Bảo vệ Owner hoạt động đúng.";
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
            // 4. MUA HÀNG NCC (PO), KHO SỐ LƯỢNG, ĐIỀU CHUYỂN & KIỂM KÊ SỐ LƯỢNG
            // =========================================================================
            Step("4. Kho Số lượng & PO", "Lập đơn mua PO -> Nhận hàng vào kho -> Kiểm tra tăng tồn sổ cái", () =>
            {
                var poId = ops.CreatePurchase(ownerId, supplierId, qtyProdId, wh1Id, loc1Id, 50, 150_000, "Nhập lô thẻ nhớ đợt 1");
                Assert(ops.Balances(ownerId).Where(b => b.ProductId == qtyProdId && b.WarehouseId == wh1Id).Sum(b => b.Quantity) == 0,
                    "PO ở trạng thái DRAFT không được làm tăng tồn kho.");

                ops.ReceivePurchase(ownerId, poId);
                var bal = ops.Balances(ownerId).First(b => b.ProductId == qtyProdId && b.WarehouseId == wh1Id).Quantity;
                Assert(bal == 50, $"Tồn kho sau khi nhận PO phải bằng 50, thực tế = {bal}");
                Assert(db.Invoices().Any(i => i.Type == "PURCHASE" && i.Total == 7_500_000),
                    "Sau khi nhận PO phải tự động sinh hóa đơn Công nợ Sản phẩm (PURCHASE) 7.500.000 ₫.");
                return $"PO #{poId} đã nhận hàng · Tồn kho DX00002 = {bal} · Tự động sinh công nợ NCC 7.500.000 ₫.";
            });

            Step("4. Kho Số lượng & PO", "Xuất kho, Điều chuyển liên kho & Chặn xuất âm tồn", () =>
            {
                ops.MoveQuantity(ownerId, qtyProdId, wh1Id, loc1Id, 10, incoming: false, "Xuất 10 thẻ nhớ lẻ");
                ops.TransferQuantity(ownerId, qtyProdId, wh1Id, loc1Id, wh2Id, loc2Id, 15, "Chuyển 15 cái sang Kho Thủ Đức");

                var bal1 = ops.Balances(ownerId).First(b => b.ProductId == qtyProdId && b.WarehouseId == wh1Id).Quantity;
                var bal2 = ops.Balances(ownerId).First(b => b.ProductId == qtyProdId && b.WarehouseId == wh2Id).Quantity;
                Assert(bal1 == 25 && bal2 == 15, $"Tồn sau điều chuyển sai: Kho 1={bal1}, Kho 2={bal2}");

                AssertThrows(() => ops.MoveQuantity(ownerId, qtyProdId, wh2Id, loc2Id, 999, incoming: false, "Xuất quá tồn"), "Chặn âm tồn kho số lượng");
                return $"Sau xuất & điều chuyển: Kho Q7 còn {bal1}, Kho Thủ Đức có {bal2} · Chặn xuất vượt tồn thành công.";
            });

            Step("4. Kho Số lượng & PO", "Kiểm kê Số lượng (DRAFT -> Đếm -> Duyệt điều chỉnh ADJUST 1 lần)", () =>
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
                        VALUES(last_insert_rowid(),$c,$p,'51F-777.77','2026-11-01','2027-11-01');";
                    cmd.Parameters.AddWithValue("$p", imeiProdId);
                    cmd.Parameters.AddWithValue("$c", customerId);
                    cmd.Parameters.AddWithValue("$sup", supplierId);
                    cmd.ExecuteNonQuery();
                }

                var (impPur, impSale, impRen, impSrv) = db.ReconcileAccountingData();
                Assert(impPur == 1 && impSale == 1 && impRen == 0 && impSrv == 0,
                    $"ReconcileAccountingData khi ActorId=0 phải tự động sinh 1 PURCHASE & 1 SALE cho thiết bị import chưa có chứng từ (thực tế: {impPur},{impSale},{impRen},{impSrv}).");
                var (impPur2, impSale2, impRen2, impSrv2) = db.ReconcileAccountingData();
                Assert(impPur2 == 0 && impSale2 == 0 && impRen2 == 0 && impSrv2 == 0,
                    "Gọi ReconcileAccountingData lần 2 phải trả về (0,0,0,0).");
                db.ActorId = ownerId;

                return $"Gia hạn +1 năm ({dev1After.ExpiresAt}) · Tất toán HĐ Khách ({saleInvPaid.Code}), Công Kỹ thuật ({srvInv.Code}) & Hoa hồng Sale · Đồng bộ Kế toán chuẩn.";
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
                Assert(dash.Active == 4 && dash.Revenue > 0, $"Chỉ số Dashboard không khớp: Active={dash.Active}, Revenue={dash.Revenue}");

                return $"Đã ghi {eventCount} sự kiện Audit JSON · Chặn xóa cứng thành công · Dashboard & 4 luồng CSV hoạt động chuẩn.";
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
