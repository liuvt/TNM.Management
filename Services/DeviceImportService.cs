using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace TNM.Management.Services;

public sealed record DeviceImportRowError(
    string SheetName,
    int RowNumber,
    string RawImei,
    string RawPlate,
    string Reason);

public sealed record DeviceImportRowNote(
    string SheetName,
    int RowNumber,
    string Imei,
    string Plate,
    string Category,
    string Detail);

public sealed record DeviceImportPreviewItem(
    string SheetName,
    int RowNumber,
    string Action, // INSERT_NEW, LINK_COMPLETED_ORDER, UPDATE_EXISTING, UNCHANGED, SKIP_IN_STOCK
    string Imei,
    string PlateNo,
    string VinNo,
    string DeviceType,
    string ProductSku,
    string AgencyRaw,
    string ResolvedCustomerName,
    string OwnerName,
    string OwnerPhone,
    string ActivatedAt,
    string ExpiresAt,
    string LastRenewedAt,
    long RenewalCount,
    string Note);

public sealed record DeviceImportReport(
    bool DryRun,
    bool Committed,
    int SheetsRead,
    int TotalRowsRead,
    int ValidUniqueDevices,
    int DuplicateRowsMerged,
    int AutoCleanedCount,
    int InsertedNewCount,
    int LinkedCompletedOrderCount,
    int UpdatedExistingCount,
    int UnchangedExistingCount,
    int ProtectedInStockCount,
    int NewCustomersCreatedCount,
    int CleanedLegacyCorruptedCount,
    int RejectedErrorCount,
    string SummaryMessage,
    List<DeviceImportRowError> Errors,
    List<DeviceImportRowNote> WarningsAndFixes,
    List<DeviceImportPreviewItem> PreviewItems);

/// <summary>
/// Dịch vụ Kiểm định Ràng buộc Đầu vào &amp; Import Hồ sơ Thiết bị chuẩn 12 cột (.xlsx / .csv)
/// Đảm bảo đồng nhất 100% thông luồng: Thiết bị ↔ Đơn hàng (SO7/TM7/GH7) ↔ Kho (IN_STOCK/INSTALLED) ↔ Kế toán (/finance).
/// </summary>
public sealed class DeviceImportService
{
    private readonly string _dbPath;

    private static readonly HashSet<string> JunkTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "0", "#REF!", "#VALUE!", "#N/A", "#NAME?", "#NUM!", "NAN", "NULL", "NONE", "N/A", "-"
    };

    private static readonly Regex ValidImeiRegex = new(@"^[A-Z0-9\-]{6,32}$", RegexOptions.Compiled);
    private static readonly Regex ScientificPlateRegex = new(@"^(\d(?:\.\d+)?)[eE]\+(\d+)$", RegexOptions.Compiled);
    private static readonly Regex PureChassisDigitsRegex = new(@"^\d{3,8}$", RegexOptions.Compiled);
    private static readonly Regex LooksLikePlateRegex = new(@"^\d{2}[A-Z]{1,2}\d{4,6}[A-Z]?$", RegexOptions.Compiled);

    public DeviceImportService(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "tnm-local.db");
    }

    public DeviceImportService(string dbPath)
    {
        _dbPath = dbPath;
    }

    private SqliteConnection Open(long actorId)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            ForeignKeys = true,
            DefaultTimeout = 15,
            Pooling = true
        }.ToString());
        c.Open();
        AuditRuntime.Attach(c, () => actorId);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=15000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private sealed class RawCell
    {
        public string Value { get; init; } = "";
        public string CellType { get; init; } = "s"; // "s"=string, ""=number/OADate, "e"=error
    }

    private sealed class RawSheet
    {
        public string Name { get; init; } = "";
        public int HeaderRowNumber { get; set; } = 1;
        public List<string> HeaderCells { get; set; } = [];
        public List<(int RowNumber, RawCell[] Cells)> DataRows { get; } = [];
    }

    private sealed class NormalizedDeviceRow
    {
        public string SheetName { get; set; } = "";
        public int RowNumber { get; set; }
        public string Imei { get; set; } = "";
        public string PlateNo { get; set; } = "";
        public string VinNo { get; set; } = "";
        public string DeviceTypeLabel { get; set; } = "";
        public string PrimarySku { get; set; } = "";
        public string AgencyRaw { get; set; } = "";
        public string OwnerName { get; set; } = "";
        public string OwnerPhone { get; set; } = "";
        public string RawStatus { get; set; } = "Hoạt động";
        public DateTime ActivatedDate { get; set; }
        public DateTime ExpiresDate { get; set; }
        public DateTime? LastRenewedDate { get; set; }
        public long RenewalCount { get; set; }
    }

    public DeviceImportReport ImportFromFile(string filePath, bool dryRun, long actorId = 0)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Không tìm thấy file import: {filePath}");
        using var fs = File.OpenRead(filePath);
        return ImportFromStream(fs, Path.GetFileName(filePath), dryRun, actorId);
    }

    public DeviceImportReport ImportFromCsvText(string csvText, bool dryRun, long actorId = 0)
    {
        var bytes = Encoding.UTF8.GetBytes(csvText ?? "");
        using var ms = new MemoryStream(bytes);
        return ImportFromStream(ms, "paste-input.csv", dryRun, actorId);
    }

    public DeviceImportReport ImportFromStream(Stream stream, string fileName, bool dryRun, long actorId = 0)
    {
        var errors = new List<DeviceImportRowError>();
        var notes = new List<DeviceImportRowNote>();

        using var mem = new MemoryStream();
        stream.CopyTo(mem);
        mem.Position = 0;

        bool isXlsx = IsZipArchive(mem) || fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
        mem.Position = 0;

        List<RawSheet> sheets;
        try
        {
            sheets = isXlsx ? ReadXlsxSheets(mem, errors) : ReadCsvSheets(mem, errors);
        }
        catch (Exception ex)
        {
            errors.Add(new DeviceImportRowError(fileName, 1, "", "", $"Không thể đọc cấu trúc file ({ex.Message}). Vui lòng sử dụng file .xlsx hoặc .csv chuẩn UTF-8 12 cột."));
            return EmptyFailedReport(dryRun, errors, notes);
        }

        if (sheets.Count == 0)
        {
            if (errors.Count == 0)
                errors.Add(new DeviceImportRowError(fileName, 1, "", "", "File không có dữ liệu hoặc không tìm thấy dòng tiêu đề 12 cột chuẩn."));
            return EmptyFailedReport(dryRun, errors, notes);
        }

        // Bước 1: Kiểm tra nghiêm ngặt cấu trúc Header 12 cột trên tất cả các sheet có dữ liệu
        foreach (var sh in sheets)
        {
            ValidateHeader12Columns(sh, errors);
        }
        if (errors.Count > 0)
        {
            return EmptyFailedReport(dryRun, errors, notes, sheets.Count, sheets.Sum(s => s.DataRows.Count));
        }

        // Bước 2: Phân tích dữ liệu từng dòng, tự động khôi phục lỗi định dạng Excel & gom trùng IMEI
        int totalRowsRead = 0;
        int duplicateRowsMerged = 0;
        int autoCleanedCount = 0;
        var mergedByImei = new Dictionary<string, NormalizedDeviceRow>(StringComparer.OrdinalIgnoreCase);
        var orderedImeis = new List<string>();

        foreach (var sh in sheets)
        {
            // Phát hiện cột ngày bị Excel US-locale (MM/dd/yyyy) đảo tháng/ngày trên toàn sheet
            bool swapActCol = DetectColumnUsLocaleSwap(sh.DataRows, colIdx: 8);
            bool swapExpCol = DetectColumnUsLocaleSwap(sh.DataRows, colIdx: 9);
            bool swapRenCol = DetectColumnUsLocaleSwap(sh.DataRows, colIdx: 10);

            foreach (var (rowNum, cells) in sh.DataRows)
            {
                totalRowsRead++;
                var rawPlateCell = cells[1].Value?.Trim() ?? "";
                var rawImeiCell = cells[2].Value?.Trim() ?? "";

                // Chống lặp dòng tiêu đề giữa chừng
                if (IsRepeatedHeaderRow(cells))
                {
                    notes.Add(new DeviceImportRowNote(sh.Name, rowNum, rawImeiCell, rawPlateCell, "SKIP_HEADER", "Bỏ qua dòng tiêu đề lặp lại giữa bảng."));
                    continue;
                }

                // Ràng buộc 2: Kiểm định Mã IMEI
                var normImei = NormalizeImei(rawImeiCell, out var imeiCleanedComma);
                if (string.IsNullOrWhiteSpace(normImei) || JunkTokens.Contains(normImei))
                {
                    errors.Add(new DeviceImportRowError(sh.Name, rowNum, rawImeiCell, rawPlateCell, "Thiếu Số IMEI Thiết bị (hoặc giá trị rỗng/#REF!/0)."));
                    continue;
                }
                if (!ValidImeiRegex.IsMatch(normImei))
                {
                    errors.Add(new DeviceImportRowError(sh.Name, rowNum, rawImeiCell, rawPlateCell, $"Mã IMEI '{rawImeiCell}' sai định dạng chuẩn (chỉ chấp nhận chữ cái ASCII không dấu, chữ số, dấu '-' và độ dài 6..32 ký tự)."));
                    continue;
                }
                if (imeiCleanedComma)
                {
                    autoCleanedCount++;
                    notes.Add(new DeviceImportRowNote(sh.Name, rowNum, normImei, rawPlateCell, "CLEAN_IMEI", $"Tự động gỡ dấu phẩy phân cách hàng nghìn trong IMEI: '{rawImeiCell}' → '{normImei}'."));
                }

                // Ràng buộc 4: Kiểm định Danh mục Tên Thiết bị
                var rawDevType = CleanCellText(cells[3].Value);
                if (!TryMapDeviceType(rawDevType, out var canonicalDevLabel, out var primarySku))
                {
                    errors.Add(new DeviceImportRowError(sh.Name, rowNum, normImei, rawPlateCell, $"Tên Thiết bị '{cells[3].Value}' không nằm trong danh mục cho phép (XANHGPS, NASAGPS, TDCGPS, AICAM, mGPS, CAM4CH)."));
                    continue;
                }

                // Ràng buộc 3: Chuẩn hóa Biển số xe / Số khung & Khôi phục biển số E bị lỗi khoa học Excel
                var (cleanPlate, cleanVin, plateFixedMsg) = NormalizePlateAndVin(cells[1]);
                if (!string.IsNullOrWhiteSpace(plateFixedMsg))
                {
                    autoCleanedCount++;
                    notes.Add(new DeviceImportRowNote(sh.Name, rowNum, normImei, cleanPlate, "RECOVER_PLATE", plateFixedMsg));
                }

                // Ràng buộc 5: Làm sạch Đại lý, Chủ xe, SĐT
                var agencyRaw = CleanCellText(cells[4].Value, out var agencyWasJunk);
                var ownerName = CleanCellText(cells[5].Value, out var ownerWasJunk);
                var (ownerPhone, phoneFixedMsg) = NormalizePhone(cells[6].Value);
                if (agencyWasJunk || ownerWasJunk || !string.IsNullOrWhiteSpace(phoneFixedMsg))
                {
                    autoCleanedCount++;
                    if (!string.IsNullOrWhiteSpace(phoneFixedMsg))
                        notes.Add(new DeviceImportRowNote(sh.Name, rowNum, normImei, cleanPlate, "CLEAN_PHONE", phoneFixedMsg));
                }

                var rawStatus = CleanCellText(cells[7].Value);
                if (!string.IsNullOrWhiteSpace(rawStatus) && !IsAllowedStatusText(rawStatus))
                {
                    errors.Add(new DeviceImportRowError(sh.Name, rowNum, normImei, cleanPlate, $"Trạng thái thiết bị '{rawStatus}' không hợp lệ (chỉ chấp nhận: Hoạt động / Khoá)."));
                    continue;
                }

                // Ràng buộc 6: Kiểm định Ngày kích hoạt, Ngày hết hạn, Ngày gia hạn gần nhất & Số lần gia hạn
                if (!TryParseImportDate(cells[8], swapActCol, out var actDate, out var actSwapped))
                {
                    errors.Add(new DeviceImportRowError(sh.Name, rowNum, normImei, cleanPlate, $"Ngày kích hoạt '{cells[8].Value}' không hợp lệ (yêu cầu dd/MM/yyyy trong khoảng năm 2018..2036)."));
                    continue;
                }
                if (!TryParseImportDate(cells[9], swapExpCol, out var expDate, out var expSwapped))
                {
                    errors.Add(new DeviceImportRowError(sh.Name, rowNum, normImei, cleanPlate, $"Ngày hết hạn '{cells[9].Value}' không hợp lệ (yêu cầu dd/MM/yyyy trong khoảng năm 2018..2036)."));
                    continue;
                }

                // Nếu Ngày kích hoạt > Ngày hết hạn do 1 trong 2 ô bị đảo tháng/ngày cục bộ, thử khôi phục có kiểm chứng
                if (actDate > expDate)
                {
                    if (!actSwapped && actDate.Day <= 12 && TrySwapMonthDay(actDate, out var altAct) && altAct <= expDate)
                    {
                        actDate = altAct;
                        actSwapped = true;
                    }
                    else if (!expSwapped && expDate.Day <= 12 && TrySwapMonthDay(expDate, out var altExp) && actDate <= altExp)
                    {
                        expDate = altExp;
                        expSwapped = true;
                    }
                }
                if (actDate > expDate)
                {
                    notes.Add(new DeviceImportRowNote(sh.Name, rowNum, normImei, cleanPlate, "WARN_EXP_BEFORE_ACT",
                        $"Cảnh báo: Ngày hết hạn ({expDate:dd/MM/yyyy}) sớm hơn Ngày kích hoạt ({actDate:dd/MM/yyyy}) (thường do đổi thiết bị bảo hành giữ hạn cũ hoặc hạ hạn khóa thuê bao) — Hệ thống vẫn ghi nhận ở trạng thái Quá hạn để lập Đơn gia hạn."));
                }

                DateTime? lastRenDate = null;
                bool renSwapped = false;
                var rawLastRen = cells[10].Value?.Trim() ?? "";
                if (!JunkTokens.Contains(rawLastRen))
                {
                    if (!TryParseImportDate(cells[10], swapRenCol, out var parsedRen, out renSwapped))
                    {
                        errors.Add(new DeviceImportRowError(sh.Name, rowNum, normImei, cleanPlate, $"Ngày gia hạn gần nhất '{rawLastRen}' không hợp lệ."));
                        continue;
                    }
                    // Phát hiện đảo tháng/ngày cục bộ ở cột Ngày gia hạn gần nhất (VD: XANHGPS 08/11/2026 bị Excel hiểu thành 2026-08-11 trong khi Hạn là 08/11/2027)
                    if (!renSwapped && IsPureIntegerCell(cells[10]) && parsedRen.Day <= 12 && parsedRen.Day != parsedRen.Month
                        && TrySwapMonthDay(parsedRen, out var altRen)
                        && altRen.Day == expDate.Day && altRen.Month == expDate.Month
                        && altRen <= expDate)
                    {
                        parsedRen = altRen;
                        renSwapped = true;
                    }
                    lastRenDate = parsedRen;
                }

                if (actSwapped || expSwapped || renSwapped)
                {
                    autoCleanedCount++;
                    notes.Add(new DeviceImportRowNote(sh.Name, rowNum, normImei, cleanPlate, "UNSWAP_DATE",
                        $"Tự động chuẩn hóa ngày bị Excel US-locale đảo tháng/ngày → Kích hoạt: {actDate:yyyy-MM-dd}, Hết hạn: {expDate:yyyy-MM-dd}" + (lastRenDate.HasValue ? $", GH gần nhất: {lastRenDate:yyyy-MM-dd}" : "")));
                }

                long renCount = 0;
                var rawRenCnt = CleanCellText(cells[11].Value);
                if (!string.IsNullOrWhiteSpace(rawRenCnt))
                {
                    if (!long.TryParse(rawRenCnt, NumberStyles.Integer, CultureInfo.InvariantCulture, out renCount) || renCount < 0 || renCount > 100)
                    {
                        errors.Add(new DeviceImportRowError(sh.Name, rowNum, normImei, cleanPlate, $"Số lần gia hạn '{cells[11].Value}' không hợp lệ (phải là số nguyên từ 0..100)."));
                        continue;
                    }
                }
                if (lastRenDate.HasValue && renCount == 0)
                    renCount = 1;

                var candidate = new NormalizedDeviceRow
                {
                    SheetName = sh.Name,
                    RowNumber = rowNum,
                    Imei = normImei,
                    PlateNo = cleanPlate,
                    VinNo = cleanVin,
                    DeviceTypeLabel = canonicalDevLabel,
                    PrimarySku = primarySku,
                    AgencyRaw = agencyRaw,
                    OwnerName = ownerName,
                    OwnerPhone = ownerPhone,
                    RawStatus = string.IsNullOrWhiteSpace(rawStatus) ? "Hoạt động" : rawStatus,
                    ActivatedDate = actDate,
                    ExpiresDate = expDate,
                    LastRenewedDate = lastRenDate,
                    RenewalCount = renCount
                };

                if (mergedByImei.TryGetValue(normImei, out var prev))
                {
                    duplicateRowsMerged++;
                    MergeDuplicateRow(prev, candidate, notes);
                }
                else
                {
                    mergedByImei[normImei] = candidate;
                    orderedImeis.Add(normImei);
                }
            }
        }

        // Nếu có bất kỳ dòng vi phạm ràng buộc cứng nào, dừng ghi DB và trả báo cáo lỗi chi tiết để người dùng kiểm tra
        if (errors.Count > 0)
        {
            return new DeviceImportReport(
                DryRun: dryRun,
                Committed: false,
                SheetsRead: sheets.Count,
                TotalRowsRead: totalRowsRead,
                ValidUniqueDevices: mergedByImei.Count,
                DuplicateRowsMerged: duplicateRowsMerged,
                AutoCleanedCount: autoCleanedCount,
                InsertedNewCount: 0,
                LinkedCompletedOrderCount: 0,
                UpdatedExistingCount: 0,
                UnchangedExistingCount: 0,
                ProtectedInStockCount: 0,
                NewCustomersCreatedCount: 0,
                CleanedLegacyCorruptedCount: 0,
                RejectedErrorCount: errors.Count,
                SummaryMessage: $"Phát hiện {errors.Count} dòng vi phạm ràng buộc dữ liệu đầu vào. Hệ thống đã chặn ghi để bảo vệ dữ liệu — vui lòng xem danh sách lỗi chi tiết bên dưới.",
                Errors: errors,
                WarningsAndFixes: notes,
                PreviewItems: []);
        }

        // Bước 3: Đối soát & Thực thi đồng bộ vào DB theo đúng thông luồng Thiết bị ↔ Đơn hàng ↔ Kho ↔ Kế toán
        using var conn = Open(actorId);
        EnsureActivationSchemaColumns(conn);
        var effActor = actorId > 0
            ? actorId
            : ScalarLong(conn, null, "SELECT COALESCE((SELECT id FROM app_users WHERE is_owner=1 AND is_active=1 ORDER BY id LIMIT 1), (SELECT id FROM app_users ORDER BY id LIMIT 1), 1)");

        using var actorScope = AuditRuntime.As(effActor);
        using var syncScope = AuditRuntime.AllowCompletedOrderSync();
        using var tx = conn.BeginTransaction();

        int insertedNew = 0;
        int linkedCompletedOrder = 0;
        int updatedExisting = 0;
        int unchangedExisting = 0;
        int protectedInStock = 0;
        int newCustomersCreated = 0;
        int cleanedLegacyCorrupted = 0;
        var previewItems = new List<DeviceImportPreviewItem>(orderedImeis.Count);
        var touchedCompletedOrders = new HashSet<long>();

        // Dọn dẹp an toàn các bản ghi rác bị lệch cột từ lần import không ràng buộc trước đây (VD: IMEI='BIỂNSỐ' hoặc Biển số nhảy vào cột IMEI trong khi IMEI thật nằm ở cột Chủ xe)
        cleanedLegacyCorrupted = CleanupLegacyColumnShiftedActivations(conn, tx, mergedByImei, effActor, notes);

        // Nạp danh mục Sản phẩm, Nhóm KH, Khách hàng hiện hành
        var productsBySku = LoadProductsBySku(conn, tx);
        var customerResolver = new CustomerAliasResolver(conn, tx);

        // Tập hợp các thiết bị đã từng bị khóa thủ công hoặc chuyển đơn vị thủ công trong ERP để bảo toàn quyết định quản trị
        var manuallyLockedActIds = LoadAuditEntityIds(conn, tx, "LOCK_DEVICE", "ACTIVATION");
        var manuallyTransferredActIds = LoadAuditEntityIds(conn, tx, "TRANSFER_DEVICE", "ACTIVATION");

        foreach (var imei in orderedImeis)
        {
            var row = mergedByImei[imei];
            if (!productsBySku.TryGetValue(row.PrimarySku, out var targetProd))
            {
                errors.Add(new DeviceImportRowError(row.SheetName, row.RowNumber, row.Imei, row.PlateNo, $"Không tìm thấy mã sản phẩm '{row.PrimarySku}' cho dòng thiết bị '{row.DeviceTypeLabel}' trong danh mục Sản phẩm."));
                continue;
            }

            var (resolvedCustId, resolvedCustName, custWasCreated, fallbackOwnerFromAgency) = customerResolver.ResolveOrCreate(row.AgencyRaw);
            if (custWasCreated) newCustomersCreated++;
            var effOwnerName = !string.IsNullOrWhiteSpace(row.OwnerName) ? row.OwnerName : fallbackOwnerFromAgency;

            var actStr = row.ActivatedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var expStr = row.ExpiresDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var lastRenStr = row.LastRenewedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

            // Kiểm tra xem IMEI đã có trên bảng activations (Thiết bị đang quản lý) chưa
            var existingAct = FindActiveActivationByImei(conn, tx, row.Imei);
            if (existingAct.ActId > 0)
            {
                // Bảo vệ Lịch sử Gia hạn ERP (Ràng buộc 7C): Không bao giờ hạ hạn/số lần gia hạn thấp hơn phiếu GH7 trong ERP
                var erpRen = GetErpRenewalStats(conn, tx, existingAct.ActId);
                var finalExpStr = MaxIsoDate(expStr, erpRen.MaxNewExpiry);
                var finalLastRenStr = MaxIsoDate(
                    !string.IsNullOrWhiteSpace(lastRenStr) ? lastRenStr : existingAct.LastRenewedAt,
                    erpRen.MaxRenewedAt);
                var finalRenCount = Math.Max(Math.Max(row.RenewalCount, existingAct.RenewalCount), erpRen.Count);

                // Bảo toàn số 0 ở đầu số khung (VD: DB đang có '000331' còn Excel làm mất số 0 thành '331')
                var finalPlate = row.PlateNo;
                var finalVin = row.VinNo;
                if (!string.IsNullOrWhiteSpace(existingAct.PlateNo) && !string.IsNullOrWhiteSpace(finalPlate)
                    && PureChassisDigitsRegex.IsMatch(existingAct.PlateNo) && PureChassisDigitsRegex.IsMatch(finalPlate)
                    && existingAct.PlateNo.TrimStart('0') == finalPlate.TrimStart('0')
                    && existingAct.PlateNo.Length > finalPlate.Length)
                {
                    finalPlate = existingAct.PlateNo;
                }
                else if (!string.IsNullOrWhiteSpace(existingAct.PlateNo)
                    && PureChassisDigitsRegex.IsMatch(existingAct.PlateNo)
                    && !PureChassisDigitsRegex.IsMatch(finalPlate)
                    && !string.IsNullOrWhiteSpace(finalPlate))
                {
                    // Xe trước đây lưu tạm số khung 6 số ở cột Biển số ('846096'), nay đã bấm biển số chính thức ('50F23226') -> giữ số khung cũ vào vin_no!
                    if (string.IsNullOrWhiteSpace(finalVin))
                        finalVin = existingAct.PlateNo;
                }
                if (string.IsNullOrWhiteSpace(finalPlate))
                    finalPlate = existingAct.PlateNo;
                if (string.IsNullOrWhiteSpace(finalVin))
                    finalVin = existingAct.VinNo;

                var finalOwner = !string.IsNullOrWhiteSpace(effOwnerName) ? effOwnerName : existingAct.OwnerName;
                var finalPhone = !string.IsNullOrWhiteSpace(row.OwnerPhone) ? row.OwnerPhone : existingAct.OwnerPhone;

                // Bảo toàn Khách hàng nếu thiết bị đã gắn Đơn hàng ERP hoặc đã được Owner chuyển đơn vị Cách 1
                long finalCustId = resolvedCustId;
                string finalCustName = resolvedCustName;
                string finalAgencyLabel = !string.IsNullOrWhiteSpace(row.AgencyRaw) ? row.AgencyRaw : existingAct.AgencyName;
                if (existingAct.V7OrderId > 0 || manuallyTransferredActIds.Contains(existingAct.ActId))
                {
                    finalCustId = existingAct.CustId;
                    finalCustName = existingAct.CustName;
                    if (manuallyTransferredActIds.Contains(existingAct.ActId))
                        finalAgencyLabel = existingAct.CustName;
                }
                if (string.IsNullOrWhiteSpace(finalAgencyLabel))
                    finalAgencyLabel = finalCustName;

                // Bảo toàn trạng thái ACTIVE để xe quá hạn vẫn chọn lập Đơn gia hạn được (trừ khi đã khóa thủ công LOCK_DEVICE trong ERP)
                var finalStatus = manuallyLockedActIds.Contains(existingAct.ActId) ? existingAct.Status : "ACTIVE";

                bool changed =
                    finalCustId != existingAct.CustId ||
                    !string.Equals(finalPlate, existingAct.PlateNo, StringComparison.Ordinal) ||
                    !string.Equals(finalVin, existingAct.VinNo, StringComparison.Ordinal) ||
                    !string.Equals(actStr, existingAct.ActivatedAt, StringComparison.Ordinal) ||
                    !string.Equals(finalExpStr, existingAct.ExpiresAt, StringComparison.Ordinal) ||
                    !string.Equals(finalLastRenStr, existingAct.LastRenewedAt, StringComparison.Ordinal) ||
                    finalRenCount != existingAct.RenewalCount ||
                    !string.Equals(row.DeviceTypeLabel, existingAct.DeviceTypeLabel, StringComparison.Ordinal) ||
                    !string.Equals(finalAgencyLabel, existingAct.AgencyName, StringComparison.Ordinal) ||
                    !string.Equals(finalOwner, existingAct.OwnerName, StringComparison.Ordinal) ||
                    !string.Equals(finalPhone, existingAct.OwnerPhone, StringComparison.Ordinal) ||
                    !string.Equals(finalStatus, existingAct.Status, StringComparison.Ordinal);

                if (changed)
                {
                    Exec(conn, tx, @"UPDATE activations SET
                        customer_id=$c,
                        plate_no=$plate,
                        vin_no=$vin,
                        activated_at=$act,
                        expires_at=$exp,
                        status=$st,
                        device_type_label=$dlbl,
                        agency_name=$ag,
                        owner_name=$ow,
                        owner_phone=$ph,
                        last_renewed_at=$lren,
                        renewal_count=$rcnt,
                        updated_at=datetime('now'),
                        updated_by=$actor
                        WHERE id=$id",
                        ("$c", finalCustId), ("$plate", finalPlate), ("$vin", finalVin),
                        ("$act", actStr), ("$exp", finalExpStr), ("$st", finalStatus),
                        ("$dlbl", row.DeviceTypeLabel), ("$ag", finalAgencyLabel),
                        ("$ow", finalOwner), ("$ph", finalPhone),
                        ("$lren", finalLastRenStr), ("$rcnt", finalRenCount),
                        ("$actor", effActor), ("$id", existingAct.ActId));

                    if (existingAct.UnitCustId != finalCustId)
                    {
                        Exec(conn, tx, "UPDATE stock_units SET customer_id=$c, updated_at=datetime('now') WHERE id=$uid",
                            ("$c", finalCustId), ("$uid", existingAct.StockUnitId));
                    }

                    updatedExisting++;
                    previewItems.Add(new DeviceImportPreviewItem(
                        row.SheetName, row.RowNumber, "UPDATE_EXISTING", row.Imei, finalPlate, finalVin,
                        row.DeviceTypeLabel, existingAct.ProdSku, row.AgencyRaw, finalCustName,
                        finalOwner, finalPhone, actStr, finalExpStr, finalLastRenStr, finalRenCount,
                        $"Cập nhật hồ sơ thiết bị có sẵn (Biển số: {existingAct.PlateNo} → {finalPlate}, Hạn: {existingAct.ExpiresAt} → {finalExpStr})"));
                }
                else
                {
                    unchangedExisting++;
                }
                continue;
            }

            // Chưa có trên bảng activations -> Kiểm tra bảng stock_units (Kho & Đơn hàng)
            var existingUnit = FindActiveStockUnitByImei(conn, tx, row.Imei);
            if (existingUnit.UnitId > 0)
            {
                // Ràng buộc 7A: Nếu IMEI đang nằm trong Kho khả dụng (IN_STOCK / CONSIGNED / TECHNICIAN) hoặc gắn trên Đơn hàng đang chờ duyệt -> KHÔNG tự ý rút khỏi kho không qua Đơn hàng!
                var openOrder = FindOpenOrderForImei(conn, tx, row.Imei);
                if (existingUnit.Status is "IN_STOCK" or "CONSIGNED" or "TECHNICIAN" || openOrder.OrderId > 0)
                {
                    protectedInStock++;
                    string protectNote;
                    if (openOrder.OrderId > 0)
                    {
                        // Bổ sung biển số vào dòng đơn chờ hoàn thành nếu đơn chưa có biển số
                        if (!string.IsNullOrWhiteSpace(row.PlateNo) && string.IsNullOrWhiteSpace(openOrder.LinePlate))
                        {
                            Exec(conn, tx, "UPDATE v7_order_lines SET plate_no=$pl WHERE id=$lid", ("$pl", row.PlateNo), ("$lid", openOrder.LineId));
                            Exec(conn, tx, "UPDATE v7_orders SET plate_no=CASE WHEN COALESCE(plate_no,'')='' THEN $pl ELSE plate_no END WHERE id=$oid", ("$pl", row.PlateNo), ("$oid", openOrder.OrderId));
                        }
                        protectNote = $"Giữ nguyên tồn kho chờ bấm Duyệt hoàn thành trên đơn {openOrder.OrderCode} ({openOrder.OrderStatus} · KH: {openOrder.CustName}).";
                    }
                    else
                    {
                        protectNote = $"IMEI đang nằm trong Tồn kho khả dụng ({existingUnit.Status}) nhưng chưa lập Đơn xuất bán — Giữ nguyên tồn kho để tạo Đơn lắp đặt / Bán thương mại đúng luồng Kho → Đơn hàng → Kế toán.";
                    }

                    notes.Add(new DeviceImportRowNote(row.SheetName, row.RowNumber, row.Imei, row.PlateNo, "PROTECT_IN_STOCK", protectNote));
                    previewItems.Add(new DeviceImportPreviewItem(
                        row.SheetName, row.RowNumber, "SKIP_IN_STOCK", row.Imei, row.PlateNo, row.VinNo,
                        row.DeviceTypeLabel, existingUnit.ProdSku, row.AgencyRaw,
                        openOrder.OrderId > 0 ? openOrder.CustName : resolvedCustName,
                        effOwnerName, row.OwnerPhone, actStr, expStr, lastRenStr, row.RenewalCount, protectNote));
                    continue;
                }

                // Ràng buộc 7B: IMEI đang ở trạng thái INSTALLED do đã xuất bán trên Đơn hoàn thành (CHỜ-NHẬP-IMEI hoặc Đơn bán thương mại) nhưng chưa có dòng activations!
                var compOrd = FindCompletedOrderForStockUnit(conn, tx, existingUnit.UnitId);
                long linkCustId = compOrd.OrderId > 0 ? compOrd.CustId : (existingUnit.CustId > 0 ? existingUnit.CustId : resolvedCustId);
                string linkCustName = compOrd.OrderId > 0 ? compOrd.CustName : (!string.IsNullOrWhiteSpace(existingUnit.CustName) ? existingUnit.CustName : resolvedCustName);
                long linkProdId = existingUnit.ProdId; // Khớp 100% trigger validate_activation_product!
                string linkAgencyName = compOrd.OrderId > 0 ? linkCustName : (!string.IsNullOrWhiteSpace(row.AgencyRaw) ? row.AgencyRaw : linkCustName);
                var linkVin = ResolveVinForInsert(row.VinNo, effOwnerName, row.OwnerPhone, row.PlateNo, row.Imei);

                Exec(conn, tx, @"INSERT INTO activations(
                        stock_unit_id,customer_id,product_id,v7_order_id,seller_id,plate_no,vin_no,
                        activated_at,expires_at,is_pre_declared,status,device_type_label,agency_name,
                        owner_name,owner_phone,last_renewed_at,renewal_count,created_at,updated_at)
                    VALUES($u,$c,$p,$ord,$sel,$plate,$vin,$act,$exp,0,'ACTIVE',$dlbl,$ag,$ow,$ph,$lren,$rcnt,datetime('now'),datetime('now'))",
                    ("$u", existingUnit.UnitId), ("$c", linkCustId), ("$p", linkProdId),
                    ("$ord", compOrd.OrderId > 0 ? compOrd.OrderId : null),
                    ("$sel", compOrd.SellerRecordId > 0 ? compOrd.SellerRecordId : null),
                    ("$plate", row.PlateNo), ("$vin", linkVin),
                    ("$act", actStr), ("$exp", expStr), ("$dlbl", row.DeviceTypeLabel),
                    ("$ag", linkAgencyName), ("$ow", effOwnerName), ("$ph", row.OwnerPhone),
                    ("$lren", lastRenStr), ("$rcnt", row.RenewalCount));
                var newLinkedActId = ScalarLong(conn, tx, "SELECT last_insert_rowid()");

                if (compOrd.LineId > 0)
                {
                    Exec(conn, tx, @"UPDATE v7_order_lines
                        SET imei=$imei,
                            plate_no=CASE WHEN $pl<>'' THEN $pl ELSE plate_no END,
                            vin_no=CASE WHEN $vn<>'' THEN $vn ELSE vin_no END,
                            activation_id=$aid
                        WHERE id=$lid",
                        ("$imei", row.Imei), ("$pl", row.PlateNo), ("$vn", row.VinNo),
                        ("$aid", newLinkedActId), ("$lid", compOrd.LineId));
                    touchedCompletedOrders.Add(compOrd.OrderId);
                }

                linkedCompletedOrder++;
                var linkMsg = compOrd.OrderId > 0
                    ? $"Tự động khớp vào Đơn đã hoàn thành {compOrd.OrderCode} ({linkCustName}) — Không trừ kho trùng, không sinh hóa đơn trùng."
                    : $"Kích hoạt hồ sơ xe từ thiết bị đã xuất kho ({linkCustName}).";
                notes.Add(new DeviceImportRowNote(row.SheetName, row.RowNumber, row.Imei, row.PlateNo, "LINK_COMPLETED_ORDER", linkMsg));
                previewItems.Add(new DeviceImportPreviewItem(
                    row.SheetName, row.RowNumber, "LINK_COMPLETED_ORDER", row.Imei, row.PlateNo, linkVin,
                    row.DeviceTypeLabel, existingUnit.ProdSku, row.AgencyRaw, linkCustName,
                    effOwnerName, row.OwnerPhone, actStr, expStr, lastRenStr, row.RenewalCount, linkMsg));
                continue;
            }

            // Ràng buộc 7D: IMEI hoàn toàn mới (hồ sơ thiết bị khách hàng đang sử dụng ngoài kho hiện tại)
            // -> Giải phóng hậu tố các dòng stock_units đã xóa mềm trùng IMEI (nếu có), tạo stock_units(status='INSTALLED', supplier_id=NULL) + activations(v7_order_id=NULL)
            Exec(conn, tx, "UPDATE stock_units SET imei = imei || '#DEL#' || id WHERE UPPER(TRIM(imei))=UPPER($imei) AND deleted_at IS NOT NULL",
                ("$imei", row.Imei));

            Exec(conn, tx, @"INSERT INTO stock_units(product_id,imei,status,warehouse_id,supplier_id,customer_id,employee_id,created_at,updated_at)
                VALUES($p,$imei,'INSTALLED',NULL,NULL,$c,NULL,datetime('now'),datetime('now'))",
                ("$p", targetProd.Id), ("$imei", row.Imei), ("$c", resolvedCustId));
            var newUnitId = ScalarLong(conn, tx, "SELECT last_insert_rowid()");

            var newVin = ResolveVinForInsert(row.VinNo, effOwnerName, row.OwnerPhone, row.PlateNo, row.Imei);
            var newAgencyLabel = !string.IsNullOrWhiteSpace(row.AgencyRaw) ? row.AgencyRaw : resolvedCustName;

            Exec(conn, tx, @"INSERT INTO activations(
                    stock_unit_id,customer_id,product_id,v7_order_id,seller_id,plate_no,vin_no,
                    activated_at,expires_at,is_pre_declared,status,device_type_label,agency_name,
                    owner_name,owner_phone,last_renewed_at,renewal_count,created_at,updated_at)
                VALUES($u,$c,$p,NULL,NULL,$plate,$vin,$act,$exp,0,'ACTIVE',$dlbl,$ag,$ow,$ph,$lren,$rcnt,datetime('now'),datetime('now'))",
                ("$u", newUnitId), ("$c", resolvedCustId), ("$p", targetProd.Id),
                ("$plate", row.PlateNo), ("$vin", newVin),
                ("$act", actStr), ("$exp", expStr), ("$dlbl", row.DeviceTypeLabel),
                ("$ag", newAgencyLabel), ("$ow", effOwnerName), ("$ph", row.OwnerPhone),
                ("$lren", lastRenStr), ("$rcnt", row.RenewalCount));

            insertedNew++;
            previewItems.Add(new DeviceImportPreviewItem(
                row.SheetName, row.RowNumber, "INSERT_NEW", row.Imei, row.PlateNo, newVin,
                row.DeviceTypeLabel, targetProd.Sku, row.AgencyRaw, resolvedCustName,
                effOwnerName, row.OwnerPhone, actStr, expStr, lastRenStr, row.RenewalCount,
                "Thêm mới hồ sơ thiết bị đang hoạt động (Chưa gắn đơn lắp gốc · Không phát sinh công nợ ảo)."));
        }

        // Cập nhật lại chuỗi tóm tắt IMEI / Biển số trên header các đơn COMPLETED vừa được điền bổ sung IMEI
        foreach (var ordId in touchedCompletedOrders)
        {
            RefreshOrderHeaderImeiAndPlateSummary(conn, tx, ordId);
        }

        // Chuẩn hóa nhãn thiết bị còn trống trên các dòng kích hoạt từ đơn ERP (nếu có)
        NormalizeBlankDeviceLabelsOnExistingActivations(conn, tx);

        if (errors.Count > 0)
        {
            tx.Rollback();
            return new DeviceImportReport(
                dryRun, false, sheets.Count, totalRowsRead, mergedByImei.Count, duplicateRowsMerged, autoCleanedCount,
                0, 0, 0, 0, 0, 0, 0, errors.Count,
                $"Phát hiện {errors.Count} lỗi khi đối soát danh mục — Đã hủy giao dịch (Rollback).",
                errors, notes, previewItems);
        }

        bool committed = false;
        if (dryRun)
        {
            tx.Rollback();
        }
        else
        {
            var isMain = ScalarLong(conn, tx, "SELECT COUNT(*) FROM app_users WHERE id=$u AND LOWER(username)='vanlt_it'", ("$u", effActor)) > 0;
            if (!isMain)
            {
                var auditDetail = $"Import file '{fileName}' ({sheets.Count} sheet · {totalRowsRead} dòng): Thêm mới {insertedNew}, Khớp đơn hoàn thành {linkedCompletedOrder}, Cập nhật {updatedExisting}, Giữ nguyên {unchangedExisting}, Bảo vệ tồn kho {protectedInStock}, Tự làm sạch {autoCleanedCount}" +
                                  (cleanedLegacyCorrupted > 0 ? $", Dọn {cleanedLegacyCorrupted} dòng lệch cột cũ" : "");
                Exec(conn, tx, @"INSERT INTO audit_logs(actor_user_id,action,entity,entity_id,details)
                    VALUES($actor,'IMPORT_DEVICES','ACTIVATION',0,$detail)",
                    ("$actor", effActor), ("$detail", auditDetail));
            }
            tx.Commit();
            committed = true;
            DemoDb.NotifyDevicesChanged();
        }

        var summary = dryRun
            ? $"[KIỂM TRA TRƯỚC KHI GHI — HỢP LỆ 100%] Đọc {sheets.Count} sheet ({totalRowsRead:N0} dòng → {mergedByImei.Count:N0} IMEI duy nhất): Sẽ thêm mới {insertedNew:N0} xe, khớp vào Đơn hoàn thành {linkedCompletedOrder:N0} xe, cập nhật {updatedExisting:N0} xe, giữ nguyên {unchangedExisting:N0} xe, bảo vệ tồn kho/đơn chờ duyệt {protectedInStock:N0} xe (Tự động chuẩn hóa {autoCleanedCount:N0} ô lỗi định dạng Excel)."
            : $"[IMPORT THÀNH CÔNG] Đã đồng bộ {mergedByImei.Count:N0} thiết bị từ '{fileName}': Thêm mới {insertedNew:N0} xe · Khớp Đơn hoàn thành {linkedCompletedOrder:N0} xe · Cập nhật {updatedExisting:N0} xe · Giữ nguyên {unchangedExisting:N0} xe · Bảo vệ tồn kho/đơn chờ duyệt {protectedInStock:N0} xe" +
              (newCustomersCreated > 0 ? $" · Tạo mới {newCustomersCreated} KH/ĐL" : "") +
              (cleanedLegacyCorrupted > 0 ? $" · Dọn sạch {cleanedLegacyCorrupted} dòng lỗi lệch cột cũ" : "") + ".";

        return new DeviceImportReport(
            DryRun: dryRun,
            Committed: committed,
            SheetsRead: sheets.Count,
            TotalRowsRead: totalRowsRead,
            ValidUniqueDevices: mergedByImei.Count,
            DuplicateRowsMerged: duplicateRowsMerged,
            AutoCleanedCount: autoCleanedCount,
            InsertedNewCount: insertedNew,
            LinkedCompletedOrderCount: linkedCompletedOrder,
            UpdatedExistingCount: updatedExisting,
            UnchangedExistingCount: unchangedExisting,
            ProtectedInStockCount: protectedInStock,
            NewCustomersCreatedCount: newCustomersCreated,
            CleanedLegacyCorruptedCount: cleanedLegacyCorrupted,
            RejectedErrorCount: 0,
            SummaryMessage: summary,
            Errors: errors,
            WarningsAndFixes: notes,
            PreviewItems: previewItems);
    }

    private static DeviceImportReport EmptyFailedReport(bool dryRun, List<DeviceImportRowError> errors, List<DeviceImportRowNote> notes, int sheets = 0, int rows = 0)
        => new(dryRun, false, sheets, rows, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, errors.Count,
            $"Không thể import do vi phạm ràng buộc đầu vào ({errors.Count} lỗi).", errors, notes, []);

    private static bool IsZipArchive(Stream s)
    {
        if (s.Length < 4) return false;
        Span<byte> sig = stackalloc byte[4];
        int read = s.Read(sig);
        return read == 4 && sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04;
    }

    private static List<RawSheet> ReadXlsxSheets(Stream stream, List<DeviceImportRowError> errors)
    {
        var result = new List<RawSheet>();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        XNamespace mainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace pkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

        var sharedStrings = new List<string>();
        var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (ssEntry is not null)
        {
            using var ssStream = ssEntry.Open();
            var ssDoc = XDocument.Load(ssStream);
            foreach (var si in ssDoc.Descendants(mainNs + "si"))
            {
                var sb = new StringBuilder();
                foreach (var t in si.Descendants(mainNs + "t"))
                    sb.Append(t.Value);
                sharedStrings.Add(sb.ToString());
            }
        }

        var wbEntry = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidOperationException("Thiếu xl/workbook.xml trong file .xlsx.");
        var relEntry = zip.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidOperationException("Thiếu xl/_rels/workbook.xml.rels trong file .xlsx.");

        Dictionary<string, string> relMap;
        using (var relStream = relEntry.Open())
        {
            var relDoc = XDocument.Load(relStream);
            relMap = relDoc.Descendants(pkgRelNs + "Relationship")
                .ToDictionary(x => x.Attribute("Id")?.Value ?? "", x => x.Attribute("Target")?.Value ?? "");
        }

        using var wbStream = wbEntry.Open();
        var wbDoc = XDocument.Load(wbStream);
        foreach (var shEl in wbDoc.Descendants(mainNs + "sheet"))
        {
            var sheetName = shEl.Attribute("name")?.Value ?? "Sheet";
            var rId = shEl.Attribute(relNs + "id")?.Value ?? "";
            if (!relMap.TryGetValue(rId, out var target) || string.IsNullOrWhiteSpace(target))
                continue;

            var cleanTarget = target.TrimStart('/');
            if (!cleanTarget.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                cleanTarget = "xl/" + cleanTarget;

            var wsEntry = zip.GetEntry(cleanTarget);
            if (wsEntry is null) continue;

            using var wsStream = wsEntry.Open();
            var wsDoc = XDocument.Load(wsStream);
            var rawSheet = new RawSheet { Name = sheetName };
            bool headerLoaded = false;

            foreach (var rowEl in wsDoc.Descendants(mainNs + "row"))
            {
                int rNum = int.TryParse(rowEl.Attribute("r")?.Value, out var parsedR) ? parsedR : 0;
                var cells = new RawCell[12];
                for (int i = 0; i < 12; i++) cells[i] = new RawCell();

                foreach (var cEl in rowEl.Elements(mainNs + "c"))
                {
                    var cellRef = cEl.Attribute("r")?.Value ?? "";
                    int colIdx = CellRefToColIndex(cellRef);
                    if (colIdx < 0 || colIdx >= 12) continue;

                    var cType = cEl.Attribute("t")?.Value ?? "";
                    string val = "";
                    if (cType == "s")
                    {
                        var vText = cEl.Element(mainNs + "v")?.Value;
                        if (int.TryParse(vText, out var ssIdx) && ssIdx >= 0 && ssIdx < sharedStrings.Count)
                            val = sharedStrings[ssIdx];
                    }
                    else if (cType == "inlineStr")
                    {
                        var isEl = cEl.Element(mainNs + "is");
                        if (isEl is not null)
                            val = string.Concat(isEl.Descendants(mainNs + "t").Select(t => t.Value));
                    }
                    else
                    {
                        val = cEl.Element(mainNs + "v")?.Value ?? "";
                    }

                    cells[colIdx] = new RawCell { Value = val, CellType = cType };
                }

                if (cells.All(c => string.IsNullOrWhiteSpace(c.Value)))
                    continue;

                if (!headerLoaded)
                {
                    rawSheet.HeaderRowNumber = rNum;
                    rawSheet.HeaderCells = cells.Select(c => (c.Value ?? "").Trim()).ToList();
                    headerLoaded = true;
                }
                else
                {
                    rawSheet.DataRows.Add((rNum, cells));
                }
            }

            if (headerLoaded && rawSheet.DataRows.Count > 0)
                result.Add(rawSheet);
        }

        return result;
    }

    private static List<RawSheet> ReadCsvSheets(Stream stream, List<DeviceImportRowError> errors)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var content = reader.ReadToEnd();
        var allRecords = ParseCsvRecords(content);
        var result = new List<RawSheet>();

        RawSheet? currentSheet = null;
        int sectionIdx = 1;
        for (int i = 0; i < allRecords.Count; i++)
        {
            var (lineNo, fields) = allRecords[i];
            if (fields.All(string.IsNullOrWhiteSpace)) continue;

            var padded = new RawCell[12];
            for (int c = 0; c < 12; c++)
            {
                var v = c < fields.Count ? fields[c].Trim() : "";
                padded[c] = ClassifyCsvCell(v);
            }

            if (currentSheet is null || IsStandard12Header(padded.Select(x => x.Value).ToList()))
            {
                currentSheet = new RawSheet
                {
                    Name = sectionIdx == 1 ? "CSV-Bảng 1" : $"CSV-Bảng {sectionIdx}",
                    HeaderRowNumber = lineNo,
                    HeaderCells = padded.Select(x => x.Value).ToList()
                };
                result.Add(currentSheet);
                sectionIdx++;
            }
            else
            {
                currentSheet.DataRows.Add((lineNo, padded));
            }
        }

        return result.Where(s => s.DataRows.Count > 0 || s.HeaderCells.Count > 0).ToList();
    }

    private static RawCell ClassifyCsvCell(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return new RawCell { Value = "", CellType = "s" };
        // Nếu chuỗi dạng yyyy-MM-dd hoặc số OADate trong CSV xuất từ Excel, đánh dấu để bộ dò đảo tháng/ngày nhận diện
        if (Regex.IsMatch(v, @"^\d{4}-\d{2}-\d{2}$"))
            return new RawCell { Value = v, CellType = "iso_csv" };
        if (Regex.IsMatch(v, @"^\d{5}(\.\d+)?$"))
            return new RawCell { Value = v, CellType = "" };
        return new RawCell { Value = v, CellType = "s" };
    }

    private static List<(int LineNumber, List<string> Fields)> ParseCsvRecords(string text)
    {
        var records = new List<(int, List<string>)>();
        if (string.IsNullOrEmpty(text)) return records;

        char delimiter = DetectCsvDelimiter(text);
        var curFields = new List<string>();
        var curField = new StringBuilder();
        bool inQuotes = false;
        int lineNo = 1;
        int recordStartLine = 1;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        curField.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (ch == '\n') lineNo++;
                    curField.Append(ch);
                }
            }
            else
            {
                if (ch == '"' && curField.Length == 0)
                {
                    inQuotes = true;
                }
                else if (ch == delimiter)
                {
                    curFields.Add(curField.ToString());
                    curField.Clear();
                }
                else if (ch == '\r')
                {
                    continue;
                }
                else if (ch == '\n')
                {
                    curFields.Add(curField.ToString());
                    curField.Clear();
                    records.Add((recordStartLine, curFields));
                    curFields = [];
                    lineNo++;
                    recordStartLine = lineNo;
                }
                else
                {
                    curField.Append(ch);
                }
            }
        }
        if (curField.Length > 0 || curFields.Count > 0)
        {
            curFields.Add(curField.ToString());
            records.Add((recordStartLine, curFields));
        }
        return records;
    }

    private static char DetectCsvDelimiter(string text)
    {
        var firstLineEnd = text.IndexOf('\n');
        var firstLine = firstLineEnd >= 0 ? text[..firstLineEnd] : text;
        int tabs = firstLine.Count(c => c == '\t');
        int semis = firstLine.Count(c => c == ';');
        int commas = firstLine.Count(c => c == ',');
        if (tabs >= 10 && tabs > commas) return '\t';
        if (semis >= 10 && semis > commas) return ';';
        return ',';
    }

    private static int CellRefToColIndex(string cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return -1;
        int idx = 0;
        int letters = 0;
        foreach (char ch in cellRef)
        {
            if (ch >= 'A' && ch <= 'Z')
            {
                idx = idx * 26 + (ch - 'A' + 1);
                letters++;
            }
            else if (ch >= 'a' && ch <= 'z')
            {
                idx = idx * 26 + (ch - 'a' + 1);
                letters++;
            }
            else break;
        }
        return letters > 0 ? idx - 1 : -1;
    }

    private static string FoldAscii(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var norm = s.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (var ch in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch switch { 'đ' or 'Đ' => 'D', _ => char.ToUpperInvariant(ch) });
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static readonly (int ColIndex, string ExpectedTitle, string[] AllowedKeywords)[] HeaderSpec12 =
    [
        (0, "STT", ["STT"]),
        (1, "Biển số xe/Số khung", ["BIEN SO", "SO KHUNG"]),
        (2, "Số EMEI Thiết bị", ["EMEI", "IMEI"]),
        (3, "Tên Thiết bị", ["TEN THIET BI", "DONG THIET BI", "LOAI THIET BI"]),
        (4, "Tên Đại lý", ["DAI LY", "HTX", "DON VI"]),
        (5, "Tên Khách hàng", ["KHACH HANG", "CHU XE"]),
        (6, "SĐT Khách hàng", ["SDT", "DIEN THOAI"]),
        (7, "Trạng thái thiết bị", ["TRANG THAI"]),
        (8, "Ngày kích hoạt", ["KICH HOAT"]),
        (9, "Ngày hết hạn", ["HET HAN"]),
        (10, "Ngày gia hạn gần nhất (nếu có)", ["GIA HAN GAN NHAT", "NGAY GIA HAN"]),
        (11, "Số lần gia hạn", ["SO LAN GIA HAN"])
    ];

    private static bool IsStandard12Header(IReadOnlyList<string> headers)
    {
        if (headers.Count < 12) return false;
        foreach (var spec in HeaderSpec12)
        {
            var folded = FoldAscii(headers[spec.ColIndex]);
            if (!spec.AllowedKeywords.Any(k => folded.Contains(k, StringComparison.Ordinal)))
                return false;
        }
        return true;
    }

    private static void ValidateHeader12Columns(RawSheet sheet, List<DeviceImportRowError> errors)
    {
        if (sheet.HeaderCells.Count < 12)
        {
            errors.Add(new DeviceImportRowError(sheet.Name, sheet.HeaderRowNumber, "", "",
                $"Sheet '{sheet.Name}' thiếu cột (yêu cầu đúng 12 cột chuẩn: STT, Biển số xe/Số khung, Số EMEI Thiết bị, Tên Thiết bị, Tên Đại lý, Tên Khách hàng, SĐT Khách hàng, Trạng thái thiết bị, Ngày kích hoạt, Ngày hết hạn, Ngày gia hạn gần nhất, Số lần gia hạn)."));
            return;
        }

        foreach (var spec in HeaderSpec12)
        {
            var actual = sheet.HeaderCells[spec.ColIndex];
            var folded = FoldAscii(actual);
            if (!spec.AllowedKeywords.Any(k => folded.Contains(k, StringComparison.Ordinal)))
            {
                errors.Add(new DeviceImportRowError(sheet.Name, sheet.HeaderRowNumber, "", "",
                    $"Sai cấu trúc cột số {spec.ColIndex + 1} tại sheet '{sheet.Name}': mong đợi '{spec.ExpectedTitle}' nhưng nhận '{actual}'. Hệ thống chặn import để chống lệch cột dữ liệu."));
            }
        }
    }

    private static bool IsRepeatedHeaderRow(RawCell[] cells)
    {
        var c0 = FoldAscii(cells[0].Value);
        var c2 = FoldAscii(cells[2].Value);
        return c0 == "STT" && (c2.Contains("EMEI") || c2.Contains("IMEI"));
    }

    private static string NormalizeImei(string? raw, out bool cleanedComma)
    {
        cleanedComma = false;
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim().Trim('\uFEFF', '\u200B');
        if (s.Contains(','))
        {
            cleanedComma = true;
            s = s.Replace(",", "");
        }
        s = s.Replace(" ", "").Replace("\t", "").ToUpperInvariant();
        return s;
    }

    private static string CleanCellText(string? raw, out bool wasJunk)
    {
        wasJunk = false;
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim().Trim('\uFEFF', '\u200B');
        if (JunkTokens.Contains(s))
        {
            wasJunk = s != "" && s != "0";
            return "";
        }
        return Regex.Replace(s, @"\s+", " ");
    }

    private static string CleanCellText(string? raw) => CleanCellText(raw, out _);

    private static (string PlateNo, string VinNo, string? FixedNote) NormalizePlateAndVin(RawCell cell)
    {
        var raw = (cell.Value ?? "").Trim().Trim('\uFEFF', '\u200B');
        if (string.IsNullOrWhiteSpace(raw) || JunkTokens.Contains(raw))
            return ("", "", null);

        // Khôi phục biển số điện / vận tải chữ E bị Excel biến thành số mũ khoa học (VD: 68E00258 -> 6.8E+259 hoặc 6.70E+169 -> 67E00168)
        var sciMatch = ScientificPlateRegex.Match(raw);
        if (sciMatch.Success &&
            double.TryParse(sciMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mantissa) &&
            int.TryParse(sciMatch.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp))
        {
            int province = (int)Math.Round(mantissa * 10.0);
            int serial = exp - 1;
            if (province is >= 11 and <= 99 && serial is >= 0 and <= 99999)
            {
                var recovered = $"{province:00}E{serial:00000}";
                return (recovered, "", $"Khôi phục biển số chữ E bị Excel chuyển thành số khoa học: '{raw}' → '{recovered}'.");
            }
        }

        var cleaned = raw.TrimEnd('.', ',', ';').Trim().ToUpperInvariant();
        cleaned = Regex.Replace(cleaned, @"\s+", " ");

        // Nếu mang tiền tố số khung (SK / SH) -> điền đồng thời vào vin_no để tra cứu cả 2 chiều
        var folded = FoldAscii(cleaned);
        if (folded.StartsWith("SK") || folded.Contains(" SK"))
        {
            return (cleaned, cleaned, null);
        }

        return (cleaned, "", null);
    }

    private static (string Phone, string? FixedNote) NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", null);
        var t = raw.Trim();
        if (JunkTokens.Contains(t))
            return ("", t is "#REF!" or "NaN" ? $"Đã làm sạch ô SĐT lỗi '{t}'." : null);

        var stripped = Regex.Replace(t, @"[\s\.\-\(\)]+", "");
        if (Regex.IsMatch(stripped, @"^[35789]\d{8}$"))
        {
            var withZero = "0" + stripped;
            return (withZero, $"Bổ sung số 0 đầu SĐT bị Excel cắt: '{t}' → '{withZero}'.");
        }
        if (Regex.IsMatch(stripped, @"^\+?\d{8,15}$"))
            return (stripped, null);

        return ("", $"Bỏ qua giá trị SĐT không hợp lệ: '{t}'.");
    }

    private static bool TryMapDeviceType(string raw, out string canonicalLabel, out string primarySku)
    {
        var key = (raw ?? "").Trim().Replace(" ", "").ToUpperInvariant();
        switch (key)
        {
            case "XANHGPS":
                canonicalLabel = "XANHGPS";
                primarySku = "SP000001";
                return true;
            case "NASAGPS":
                canonicalLabel = "NASAGPS";
                primarySku = "DX00002";
                return true;
            case "TDCGPS":
                canonicalLabel = "TDCGPS";
                primarySku = "SP000005";
                return true;
            case "AICAM":
                canonicalLabel = "AICAM";
                primarySku = "SP000012";
                return true;
            case "MGPS":
                canonicalLabel = "mGPS";
                primarySku = "SP000006";
                return true;
            case "CAM4CH":
                canonicalLabel = "CAM4CH";
                primarySku = "CAM4CH";
                return true;
            default:
                canonicalLabel = "";
                primarySku = "";
                return false;
        }
    }

    private static bool IsAllowedStatusText(string raw)
    {
        var f = FoldAscii(raw);
        return f is "HOAT DONG" or "ACTIVE" or "KHOA" or "LOCKED" or "DISABLED" or "TAM KHOA";
    }

    private static bool IsPureIntegerCell(RawCell cell)
        => (cell.CellType == "" || cell.CellType == "n") && Regex.IsMatch((cell.Value ?? "").Trim(), @"^\d+$");

    private static bool DetectColumnUsLocaleSwap(List<(int RowNumber, RawCell[] Cells)> rows, int colIdx)
    {
        int slashDayGt12 = 0;
        int fracOa = 0;
        int intOrIsoTotal = 0;
        int intOrIsoDayLe12 = 0;

        foreach (var (_, cells) in rows)
        {
            var c = cells[colIdx];
            var v = (c.Value ?? "").Trim();
            if (JunkTokens.Contains(v)) continue;

            if (c.CellType == "s")
            {
                var m = Regex.Match(v, @"^(\d{1,2})/(\d{1,2})/(\d{4})$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var d) && d > 12)
                    slashDayGt12++;
            }
            else if (c.CellType == "" || c.CellType == "n")
            {
                if (v.Contains('.'))
                {
                    fracOa++;
                }
                else if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa) && oa is >= 30000 and <= 60000)
                {
                    var dt = ExcelOaToDate(oa);
                    intOrIsoTotal++;
                    if (dt.Day <= 12) intOrIsoDayLe12++;
                }
            }
            else if (c.CellType == "iso_csv")
            {
                if (DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var isoDt))
                {
                    intOrIsoTotal++;
                    if (isoDt.Day <= 12) intOrIsoDayLe12++;
                }
            }
        }

        // Đặc trưng toán học 100% khi paste chuỗi dd/MM/yyyy vào Excel định dạng Mỹ MM/dd/yyyy:
        // Các ngày > 12 bị giữ nguyên dạng chuỗi 'dd/MM/yyyy', còn 100% các ngày <= 12 bị biến thành OADate nguyên (hoặc yyyy-MM-dd khi xuất CSV) có dt.Day <= 12!
        return slashDayGt12 > 0 && fracOa == 0 && intOrIsoTotal > 0 && intOrIsoTotal == intOrIsoDayLe12;
    }

    private static DateTime ExcelOaToDate(double oa)
        => new DateTime(1899, 12, 30).AddDays(Math.Floor(oa));

    private static bool TrySwapMonthDay(DateTime dt, out DateTime swapped)
    {
        swapped = default;
        if (dt.Day is < 1 or > 12) return false;
        try
        {
            swapped = new DateTime(dt.Year, dt.Day, dt.Month);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseImportDate(RawCell cell, bool columnIsUsSwapped, out DateTime result, out bool wasUnswapped)
    {
        result = default;
        wasUnswapped = false;
        var v = (cell.Value ?? "").Trim();
        if (JunkTokens.Contains(v)) return false;

        // 1. Excel OADate numeric cell
        if ((cell.CellType == "" || cell.CellType == "n") &&
            double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa) &&
            oa is >= 30000 and <= 60000)
        {
            var dt = ExcelOaToDate(oa);
            if (columnIsUsSwapped && !v.Contains('.') && dt.Day != dt.Month && TrySwapMonthDay(dt, out var unswapped))
            {
                result = unswapped;
                wasUnswapped = true;
            }
            else
            {
                result = dt;
            }
            return result.Year is >= 2018 and <= 2036;
        }

        // 2. CSV ISO yyyy-MM-dd cell
        if (DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            if (columnIsUsSwapped && iso.Day != iso.Month && TrySwapMonthDay(iso, out var unswappedIso))
            {
                result = unswappedIso;
                wasUnswapped = true;
            }
            else
            {
                result = iso;
            }
            return result.Year is >= 2018 and <= 2036;
        }

        // 3. Chuỗi ngày Việt Nam dd/MM/yyyy
        string[] vnFormats = ["dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy HH:mm:ss", "d/M/yyyy HH:mm:ss"];
        if (DateTime.TryParseExact(v, vnFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var vnDt))
        {
            result = vnDt.Date;
            return result.Year is >= 2018 and <= 2036;
        }

        return false;
    }

    private static void MergeDuplicateRow(NormalizedDeviceRow target, NormalizedDeviceRow newer, List<DeviceImportRowNote> notes)
    {
        var oldPlate = target.PlateNo;
        // Nếu dòng mới có biển số thật (không bị mất số 0 đầu của số khung cũ), ưu tiên biển số mới; giữ số khung cũ vào vin_no
        if (!string.IsNullOrWhiteSpace(newer.PlateNo))
        {
            if (!string.IsNullOrWhiteSpace(target.PlateNo)
                && PureChassisDigitsRegex.IsMatch(target.PlateNo) && PureChassisDigitsRegex.IsMatch(newer.PlateNo)
                && target.PlateNo.TrimStart('0') == newer.PlateNo.TrimStart('0')
                && target.PlateNo.Length > newer.PlateNo.Length)
            {
                // Giữ số 0 đầu của số khung cũ (VD: '000331' thay vì '331')
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(target.PlateNo) && PureChassisDigitsRegex.IsMatch(target.PlateNo) && !PureChassisDigitsRegex.IsMatch(newer.PlateNo) && string.IsNullOrWhiteSpace(target.VinNo))
                {
                    target.VinNo = target.PlateNo;
                }
                target.PlateNo = newer.PlateNo;
            }
        }

        if (!string.IsNullOrWhiteSpace(newer.VinNo)) target.VinNo = newer.VinNo;
        if (!string.IsNullOrWhiteSpace(newer.AgencyRaw)) target.AgencyRaw = newer.AgencyRaw;
        if (!string.IsNullOrWhiteSpace(newer.OwnerName)) target.OwnerName = newer.OwnerName;
        if (!string.IsNullOrWhiteSpace(newer.OwnerPhone)) target.OwnerPhone = newer.OwnerPhone;
        target.DeviceTypeLabel = newer.DeviceTypeLabel;
        target.PrimarySku = newer.PrimarySku;
        target.ActivatedDate = newer.ActivatedDate;
        if (newer.ExpiresDate > target.ExpiresDate)
            target.ExpiresDate = newer.ExpiresDate;

        // Không để dòng xuất sau có '0' lần gia hạn xóa mất lịch sử gia hạn của dòng gốc
        if (newer.LastRenewedDate.HasValue && (!target.LastRenewedDate.HasValue || newer.LastRenewedDate.Value >= target.LastRenewedDate.Value))
            target.LastRenewedDate = newer.LastRenewedDate;
        target.RenewalCount = Math.Max(target.RenewalCount, newer.RenewalCount);

        notes.Add(new DeviceImportRowNote(newer.SheetName, newer.RowNumber, newer.Imei, target.PlateNo, "MERGE_DUPLICATE",
            $"Gộp IMEI trùng lặp giữa [{target.SheetName} dòng {target.RowNumber}] và [{newer.SheetName} dòng {newer.RowNumber}] (Biển số: {oldPlate} → {target.PlateNo})."));
        target.SheetName = newer.SheetName;
        target.RowNumber = newer.RowNumber;
    }

    private static string ResolveVinForInsert(string cleanVin, string ownerName, string ownerPhone, string cleanPlate, string normImei)
    {
        if (!string.IsNullOrWhiteSpace(cleanVin)) return cleanVin;
        if (!string.IsNullOrWhiteSpace(ownerName) && !string.Equals(ownerName, cleanPlate, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(ownerPhone) ? ownerName : $"{ownerName} · {ownerPhone}";
        return normImei;
    }

    private static string MaxIsoDate(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return b ?? "";
        if (string.IsNullOrWhiteSpace(b)) return a;
        return string.CompareOrdinal(a, b) >= 0 ? a : b;
    }

    private sealed class CustomerAliasResolver
    {
        private readonly SqliteConnection _c;
        private readonly SqliteTransaction _tx;
        private readonly Dictionary<string, (long Id, string Name)> _byFoldedName = new(StringComparer.OrdinalIgnoreCase);
        private readonly (long Id, string Name) _retailDefault;
        private readonly long _agencyGroupId;
        private readonly long _agencySubGroup2Id;
        private readonly long _b2bGroupId;

        public CustomerAliasResolver(SqliteConnection c, SqliteTransaction tx)
        {
            _c = c;
            _tx = tx;
            _agencyGroupId = ScalarLong(c, tx, "SELECT COALESCE((SELECT id FROM customer_groups WHERE code='AGENCY' AND deleted_at IS NULL LIMIT 1), 2)");
            _agencySubGroup2Id = ScalarLong(c, tx, "SELECT COALESCE((SELECT id FROM customer_groups WHERE code='DL-NHOM-2' AND deleted_at IS NULL LIMIT 1), 0)");
            _b2bGroupId = ScalarLong(c, tx, "SELECT COALESCE((SELECT id FROM customer_groups WHERE code='B2B' AND deleted_at IS NULL LIMIT 1), 3)");

            using (var cmd = _c.CreateCommand())
            {
                cmd.Transaction = _tx;
                cmd.CommandText = "SELECT id, name FROM customers WHERE deleted_at IS NULL ORDER BY id ASC";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetInt64(0);
                    var nm = r.GetString(1);
                    _byFoldedName[FoldAscii(nm)] = (id, nm);
                }
            }

            if (_byFoldedName.TryGetValue("KHACH LE TRUC TIEP", out var ret))
                _retailDefault = ret;
            else
                _retailDefault = _byFoldedName.Values.FirstOrDefault();

            RegisterCanonicalAliases();
        }

        private void RegisterCanonicalAliases()
        {
            void MapAliases(string targetFoldedCustomer, params string[] aliases)
            {
                if (!_byFoldedName.TryGetValue(targetFoldedCustomer, out var target)) return;
                foreach (var al in aliases)
                    _byFoldedName[FoldAscii(al)] = target;
            }

            MapAliases("DL CTY TRUONG NHAT MINH",
                "ĐL CTY TRƯỜNG NHẬT MINH - CẦN THƠ",
                "Công ty Trường Nhật Minh",
                "CÔNG TY TNHH THƯƠNG MẠI DỊCH VỤ TRƯỜNG NHẬT MINH",
                "MGPS TRường Nhật",
                "Trường Nhật Minh");

            MapAliases("HTX VT NAM THANG KG",
                "HTX VẬN TẢI NAM THẮNG KIÊN GIANG",
                "HTX VT NAM THẮNG KG");

            MapAliases("HTX VAN TAI NAM THANG",
                "HTX VẬN TẢI NAM THẮNG");

            MapAliases("HTX LAM MINH",
                "HTX Lâm Minh",
                "htxlamminh");

            MapAliases("HTX TAN DAI THANH",
                "ĐL HTX TÂN ĐẠI THÀNH - CẦN THƠ",
                "HTX Tân Đại Thành",
                "HTX TÂN ĐẠI THÀNH - CẦN THƠ");

            MapAliases("HTX NAM HI", "HTX Nam Hi");
            MapAliases("HOP TAC XA 586", "Hợp tác xã 586", "HTX 586");

            MapAliases("HTX GARA O TO CHINH HIEP",
                "HTX Ga Ra Ô Tô Chính Hiệp",
                "HTX Gara Ô tô Chính Hiệp",
                "HTX CHÍNH HIỆP - CẦN THƠ");

            MapAliases("HTX TAN THANH DO",
                "HTX Tân Thành Đô",
                "HTX TÂN THÀNH ĐÔ - CẦN THƠ");

            MapAliases("HTX NAM PHUONG",
                "HTX NAM PHƯƠNG",
                "HTX Vận Tải Du Lịch Nam Phương");

            MapAliases("CTY DI XANH",
                "Xe Dịch Vụ Đi Xanh",
                "Cam Dịch Vụ Đi Xanh",
                "MGPS DI XANH",
                "Cty Greenway Automotives",
                "Công Ty Cổ phần Đi Xanh",
                "Công ty CP Đi Xanh",
                "Đi Xanh");

            MapAliases("HAI VAN XE HOP DONG",
                "CHI NHÁNH CÔNG TY LIÊN DOANH VẬN CHUYỂN QUỐC TẾ HẢI VÂN",
                "Cam hợp đồng Hải Vân",
                "136");

            MapAliases("DAI LY NAM THANG CHINH HIEP", "Đại Lý Nam Thắng Chính Hiệp");

            MapAliases("DANG TIEN (HTX SAO VIET)",
                "ĐL ĐĂNG TIẾN",
                "Hợp Tác Xã Sao Việt",
                "HTX Sao Việt");

            MapAliases("HUY TIEN DUNG",
                "ĐL HUY TIẾN DŨNG",
                "Huy Tiến Dũng");

            MapAliases("KHACH LE TRUC TIEP",
                "Khách Hàng Lẻ Công Ty",
                "Nhóm Team Sales Công Ty");
        }

        public (long CustomerId, string CustomerName, bool CreatedNew, string FallbackOwnerName) ResolveOrCreate(string? rawAgency)
        {
            var clean = (rawAgency ?? "").Trim();
            if (string.IsNullOrWhiteSpace(clean) || JunkTokens.Contains(clean))
                return (_retailDefault.Id, _retailDefault.Name, false, "");

            var folded = FoldAscii(clean);
            if (_byFoldedName.TryGetValue(folded, out var hit))
                return (hit.Id, hit.Name, false, "");

            // Nếu cột Đại lý vô tình chứa biển số xe hoặc con số thuần -> chuyển về Khách lẻ trực tiếp
            if (PureChassisDigitsRegex.IsMatch(folded) || LooksLikePlateRegex.IsMatch(folded.Replace("-", "").Replace(".", "").Replace(" ", "")))
                return (_retailDefault.Id, _retailDefault.Name, false, "");

            // Kiểm tra xem có phải tên Tổ chức (HTX / Đại lý / Công ty) mới thực sự hay chỉ là tên cá nhân khách lẻ
            bool isAgency = folded.StartsWith("HTX ") || folded.Contains("HOP TAC XA") || folded.StartsWith("DL ") || folded.StartsWith("DAI LY ");
            bool isCompany = folded.Contains("CONG TY") || folded.StartsWith("CTY ") || folded.Contains("TNHH") || folded.Contains("CO PHAN") || folded.Contains("DOANH NGHIEP") || folded.Contains("GARAGE") || folded.Contains("SHOWROOM");

            if (!isAgency && !isCompany)
            {
                // Tên cá nhân khách lẻ -> gom vào Khách lẻ trực tiếp và lưu tên cá nhân làm Chủ xe dự phòng
                return (_retailDefault.Id, _retailDefault.Name, false, clean);
            }

            long grpId = isAgency ? _agencyGroupId : _b2bGroupId;
            long? subGrpId = isAgency && _agencySubGroup2Id > 0 ? _agencySubGroup2Id : null;
            var nextSeq = ScalarLong(_c, _tx, "SELECT COALESCE(MAX(id), 0) + 11 FROM customers");
            string newCode = $"KH{nextSeq:00000}";
            while (ScalarLong(_c, _tx, "SELECT COUNT(*) FROM customers WHERE UPPER(code)=UPPER($c)", ("$c", newCode)) > 0)
            {
                nextSeq++;
                newCode = $"KH{nextSeq:00000}";
            }

            Exec(_c, _tx, @"INSERT INTO customers(code,name,group_id,sub_group_id,is_active,created_at,updated_at)
                VALUES($code,$name,$grp,$sub,1,datetime('now'),datetime('now'))",
                ("$code", newCode), ("$name", clean), ("$grp", grpId), ("$sub", subGrpId));
            var newId = ScalarLong(_c, _tx, "SELECT last_insert_rowid()");
            _byFoldedName[folded] = (newId, clean);
            return (newId, clean, true, "");
        }
    }

    private static Dictionary<string, (long Id, string Sku, string Name)> LoadProductsBySku(SqliteConnection c, SqliteTransaction tx)
    {
        var map = new Dictionary<string, (long, string, string)>(StringComparer.OrdinalIgnoreCase);
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, sku, name FROM products WHERE deleted_at IS NULL ORDER BY id ASC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            var sku = r.GetString(1);
            var nm = r.GetString(2);
            map[sku] = (id, sku, nm);
        }
        // Fallback tương đương giữa DX00002 và SP000019 cho dòng NASAGPS
        if (!map.ContainsKey("DX00002") && map.TryGetValue("SP000019", out var sp19))
            map["DX00002"] = sp19;
        return map;
    }

    private static HashSet<long> LoadAuditEntityIds(SqliteConnection c, SqliteTransaction tx, string action, string entityType)
    {
        var set = new HashSet<long>();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT DISTINCT entity_id FROM audit_logs WHERE action=$a AND entity=$e AND entity_id IS NOT NULL";
        cmd.Parameters.AddWithValue("$a", action);
        cmd.Parameters.AddWithValue("$e", entityType);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!r.IsDBNull(0)) set.Add(r.GetInt64(0));
        }
        return set;
    }

    private static int CleanupLegacyColumnShiftedActivations(
        SqliteConnection c,
        SqliteTransaction tx,
        Dictionary<string, NormalizedDeviceRow> importedByImei,
        long actorId,
        List<DeviceImportRowNote> notes)
    {
        var candidates = new List<(long ActId, long UnitId, string BadImei, string BadPlate, string OwnerField)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                SELECT a.id, a.stock_unit_id, u.imei, COALESCE(a.plate_no,''), COALESCE(a.owner_name,'')
                FROM activations a
                JOIN stock_units u ON u.id = a.stock_unit_id
                WHERE a.deleted_at IS NULL
                  AND a.v7_order_id IS NULL
                  AND COALESCE(a.is_pre_declared, 0) = 0
                  AND NOT EXISTS (SELECT 1 FROM renewals r WHERE r.activation_id = a.id AND r.deleted_at IS NULL)
                  AND NOT EXISTS (SELECT 1 FROM inventory_transaction_lines tl WHERE tl.stock_unit_id = u.id AND tl.deleted_at IS NULL)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var aid = r.GetInt64(0);
                var uid = r.GetInt64(1);
                var imei = r.GetString(2).Trim();
                var plate = r.GetString(3).Trim();
                var owner = r.GetString(4).Trim();

                bool isHeaderRow = string.Equals(FoldAscii(imei), "BIENSO", StringComparison.OrdinalIgnoreCase) && string.Equals(FoldAscii(plate), "STT", StringComparison.OrdinalIgnoreCase);
                bool isColumnShifted = !importedByImei.ContainsKey(imei)
                    && !string.IsNullOrWhiteSpace(owner)
                    && importedByImei.TryGetValue(owner, out var realRow)
                    && (string.Equals(realRow.PlateNo, imei, StringComparison.OrdinalIgnoreCase) || PureChassisDigitsRegex.IsMatch(plate));

                if (isHeaderRow || isColumnShifted)
                    candidates.Add((aid, uid, imei, plate, owner));
            }
        }

        if (candidates.Count == 0) return 0;

        var nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        foreach (var item in candidates)
        {
            Exec(c, tx, "UPDATE activations SET deleted_at=$now, deleted_by=$actor WHERE id=$id",
                ("$now", nowIso), ("$actor", actorId), ("$id", item.ActId));
            Exec(c, tx, "UPDATE stock_units SET imei = imei || '#BADIMP#' || id, deleted_at=$now, deleted_by=$actor WHERE id=$uid",
                ("$now", nowIso), ("$actor", actorId), ("$uid", item.UnitId));
            notes.Add(new DeviceImportRowNote("DB-Cleanup", 0, item.BadImei, item.BadPlate, "CLEAN_LEGACY_SHIFT",
                $"Đã dọn dòng rác lệch cột từ lần import cũ: IMEI sai='{item.BadImei}', Biển số sai='{item.BadPlate}' (IMEI thực='{item.OwnerField}')."));
        }
        return candidates.Count;
    }

    private static void NormalizeBlankDeviceLabelsOnExistingActivations(SqliteConnection c, SqliteTransaction tx)
    {
        Exec(c, tx, @"
            UPDATE activations
            SET device_type_label = CASE
                    WHEN (SELECT UPPER(p.sku) FROM products p WHERE p.id = activations.product_id) = 'SP000001' THEN 'XANHGPS'
                    WHEN (SELECT UPPER(p.sku) FROM products p WHERE p.id = activations.product_id) IN ('DX00002', 'SP000019') THEN 'NASAGPS'
                    WHEN (SELECT UPPER(p.sku) FROM products p WHERE p.id = activations.product_id) = 'SP000005' THEN 'TDCGPS'
                    WHEN (SELECT UPPER(p.sku) FROM products p WHERE p.id = activations.product_id) = 'SP000012' THEN 'AICAM'
                    WHEN (SELECT UPPER(p.sku) FROM products p WHERE p.id = activations.product_id) = 'SP000006' THEN 'mGPS'
                    WHEN (SELECT UPPER(p.sku) FROM products p WHERE p.id = activations.product_id) = 'CAM4CH' THEN 'CAM4CH'
                    ELSE device_type_label END,
                agency_name = CASE
                    WHEN COALESCE(TRIM(agency_name), '') = '' THEN COALESCE((SELECT cu.name FROM customers cu WHERE cu.id = activations.customer_id), '')
                    ELSE agency_name END
            WHERE deleted_at IS NULL
              AND (COALESCE(TRIM(device_type_label), '') = '' OR COALESCE(TRIM(agency_name), '') = '')");
    }

    private static (
        long ActId, long StockUnitId, long CustId, string CustName, long UnitCustId,
        long ProdId, string ProdSku, long V7OrderId,
        string PlateNo, string VinNo, string ActivatedAt, string ExpiresAt,
        string Status, string DeviceTypeLabel, string AgencyName,
        string OwnerName, string OwnerPhone, string LastRenewedAt, long RenewalCount)
        FindActiveActivationByImei(SqliteConnection c, SqliteTransaction tx, string normImei)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT a.id, a.stock_unit_id, a.customer_id, COALESCE(cu.name,''), COALESCE(u.customer_id,0),
                   a.product_id, p.sku, COALESCE(a.v7_order_id,0),
                   COALESCE(a.plate_no,''), COALESCE(a.vin_no,''), COALESCE(a.activated_at,''), COALESCE(a.expires_at,''),
                   COALESCE(a.status,'ACTIVE'), COALESCE(a.device_type_label,''), COALESCE(a.agency_name,''),
                   COALESCE(a.owner_name,''), COALESCE(a.owner_phone,''), COALESCE(a.last_renewed_at,''), COALESCE(a.renewal_count,0)
            FROM activations a
            JOIN stock_units u ON u.id = a.stock_unit_id
            JOIN products p ON p.id = a.product_id
            LEFT JOIN customers cu ON cu.id = a.customer_id
            WHERE a.deleted_at IS NULL AND UPPER(TRIM(u.imei)) = UPPER($imei)
            ORDER BY a.id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$imei", normImei);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return default;
        return (
            r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4),
            r.GetInt64(5), r.GetString(6), r.GetInt64(7),
            r.GetString(8), r.GetString(9), r.GetString(10), r.GetString(11),
            r.GetString(12), r.GetString(13), r.GetString(14),
            r.GetString(15), r.GetString(16), r.GetString(17), r.GetInt64(18));
    }

    private static (long Count, string MaxNewExpiry, string MaxRenewedAt) GetErpRenewalStats(SqliteConnection c, SqliteTransaction tx, long activationId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT COUNT(*), COALESCE(MAX(new_expiry),''), COALESCE(MAX(substr(renewed_at,1,10)),'')
            FROM renewals
            WHERE activation_id=$a AND deleted_at IS NULL";
        cmd.Parameters.AddWithValue("$a", activationId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (0, "", "");
        return (r.GetInt64(0), r.GetString(1), r.GetString(2));
    }

    private static (long UnitId, string Status, long ProdId, string ProdSku, long CustId, string CustName)
        FindActiveStockUnitByImei(SqliteConnection c, SqliteTransaction tx, string normImei)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT u.id, u.status, u.product_id, p.sku, COALESCE(u.customer_id,0), COALESCE(cu.name,'')
            FROM stock_units u
            JOIN products p ON p.id = u.product_id
            LEFT JOIN customers cu ON cu.id = u.customer_id
            WHERE u.deleted_at IS NULL AND UPPER(TRIM(u.imei)) = UPPER($imei)
            ORDER BY u.id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$imei", normImei);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return default;
        return (r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4), r.GetString(5));
    }

    private static (long OrderId, string OrderCode, string OrderStatus, long CustId, string CustName, long LineId, string LinePlate)
        FindOpenOrderForImei(SqliteConnection c, SqliteTransaction tx, string normImei)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT o.id, o.code, o.status, o.customer_id, COALESCE(cu.name,''), l.id, COALESCE(l.plate_no,'')
            FROM v7_order_lines l
            JOIN v7_orders o ON o.id = l.order_id
            LEFT JOIN customers cu ON cu.id = o.customer_id
            WHERE l.deleted_at IS NULL AND o.deleted_at IS NULL
              AND o.status NOT IN ('COMPLETED', 'CANCELLED')
              AND COALESCE(o.order_kind, 'INSTALL') <> 'RENEWAL'
              AND UPPER(TRIM(COALESCE(l.imei,''))) = UPPER($imei)
            ORDER BY o.id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$imei", normImei);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return default;
        return (r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.GetInt64(5), r.GetString(6));
    }

    private static (long OrderId, string OrderCode, long CustId, string CustName, long SellerRecordId, long LineId)
        FindCompletedOrderForStockUnit(SqliteConnection c, SqliteTransaction tx, long stockUnitId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT o.id, o.code, o.customer_id, COALESCE(cu.name,''), COALESCE(o.seller_record_id,0), COALESCE(l.id,0)
            FROM v7_orders o
            LEFT JOIN customers cu ON cu.id = o.customer_id
            LEFT JOIN v7_order_lines l ON l.order_id = o.id AND l.stock_unit_id = $sid AND l.deleted_at IS NULL
            WHERE o.deleted_at IS NULL AND o.status = 'COMPLETED'
              AND (l.id IS NOT NULL OR EXISTS (SELECT 1 FROM v7_stock_effects e WHERE e.order_id = o.id AND e.stock_unit_id = $sid))
            ORDER BY o.id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$sid", stockUnitId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return default;
        return (r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5));
    }

    private static void RefreshOrderHeaderImeiAndPlateSummary(SqliteConnection c, SqliteTransaction tx, long orderId)
    {
        var imeis = new List<string>();
        var plates = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COALESCE(imei,''), COALESCE(plate_no,'') FROM v7_order_lines WHERE order_id=$o AND deleted_at IS NULL ORDER BY id ASC";
            cmd.Parameters.AddWithValue("$o", orderId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var im = r.GetString(0).Trim();
                var pl = r.GetString(1).Trim();
                if (!string.IsNullOrWhiteSpace(im)) imeis.Add(im);
                if (!string.IsNullOrWhiteSpace(pl) && !plates.Contains(pl, StringComparer.OrdinalIgnoreCase)) plates.Add(pl);
            }
        }
        if (imeis.Count == 0 && plates.Count == 0) return;

        Exec(c, tx, @"UPDATE v7_orders
            SET imei = CASE WHEN $im <> '' THEN $im ELSE imei END,
                plate_no = CASE WHEN $pl <> '' THEN $pl ELSE plate_no END
            WHERE id=$o",
            ("$im", string.Join(", ", imeis)),
            ("$pl", string.Join(", ", plates)),
            ("$o", orderId));
    }

    private static void EnsureActivationSchemaColumns(SqliteConnection c)
    {
        var actCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(activations)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) actCols.Add(r.GetString(1));
        }
        (string Col, string Def)[] required =
        [
            ("is_pre_declared", "INTEGER NOT NULL DEFAULT 0"),
            ("status", "TEXT NOT NULL DEFAULT 'ACTIVE'"),
            ("device_type_label", "TEXT NOT NULL DEFAULT ''"),
            ("agency_name", "TEXT NOT NULL DEFAULT ''"),
            ("owner_name", "TEXT NOT NULL DEFAULT ''"),
            ("owner_phone", "TEXT NOT NULL DEFAULT ''"),
            ("last_renewed_at", "TEXT NOT NULL DEFAULT ''"),
            ("renewal_count", "INTEGER NOT NULL DEFAULT 0"),
            ("seller_id", "INTEGER"),
            ("v7_order_id", "INTEGER")
        ];
        foreach (var (col, def) in required)
        {
            if (!actCols.Contains(col))
                Exec(c, null, $"ALTER TABLE activations ADD COLUMN {col} {def}");
        }

        var custCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd2 = c.CreateCommand())
        {
            cmd2.CommandText = "PRAGMA table_info(customers)";
            using var r2 = cmd2.ExecuteReader();
            while (r2.Read()) custCols.Add(r2.GetString(1));
        }
        if (!custCols.Contains("sub_group_id"))
            Exec(c, null, "ALTER TABLE customers ADD COLUMN sub_group_id INTEGER");
    }

    private static long ScalarLong(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
    }

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
