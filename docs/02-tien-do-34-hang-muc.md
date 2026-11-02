# TNM v6 — Đối chiếu 34 hạng mục backlog

Nguồn: `TNM-Danh-gia-nang-cap-v5-len-v6.xlsx` và `PHÂN HỆ HỆ THỐNG - TNM (update ver2).xlsx`.

**Trạng thái 'Có cơ bản'**: đã có thao tác demo chính, chưa đủ chức năng production. **'Một phần'**: chỉ có UI, bảng hoặc khả năng liên quan; không đồng nghĩa đã hoàn thành nghiệp vụ. **'Chưa'**: không có luồng tương ứng.

| Mã | Hạng mục | Ưu tiên | Tiến độ v6 | Nhận xét |
|---|---|---|---|---|
| SP01 | Giá nhập theo bậc sản lượng | P1 | **Một phần** | Bảng giá theo bậc từ v5, chưa UI áp dụng. |
| SP02 | Phí dịch vụ kỹ thuật, lắp đặt, chi phí khác | P1 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| SP03 | Chỉnh sửa hồ sơ khách hàng và người liên hệ | P1 | **Có cơ bản** | Sửa tên, điện thoại, địa chỉ; chưa danh sách người liên hệ. |
| SP04 | Giá khách theo thời điểm và loại giá | P1 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| SP05 | Quản lý combo theo định mức thành phần | P2 | **Chưa** | BOM bảng dữ liệu mới, chưa UI/trừ tồn. |
| K01 | Sổ kho hàng hóa theo số lượng không IMEI | P0 | **Có cơ bản** | Sổ SKU/kho/vị trí, cộng/trừ; không âm tồn và bất biến. |
| K02 | Nhập IMEI hàng loạt từ Excel có kiểm tra trước | P1 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| K03 | Quản lý vị trí kho SG/PQ và SG-BH-VT | P0 | **Có cơ bản** | Kho SG/PQ + vị trí MAIN; tạo vị trí tùy chỉnh. |
| K04 | Phiếu kho nguồn hàng, nhân sự, chứng từ | P1 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| K05 | Xuất khách ký gửi / thanh toán / công nợ | P1 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| K06 | Đối soát kiểm kê, duyệt chênh lệch, ghi phiếu điều chỉnh | P0 | **Có cơ bản** | Phiên DRAFT → phê duyệt; ADJUST và chống duyệt lại. |
| K07 | Kiểm kê và báo cáo hàng ký gửi theo KH/SKU/IMEI | P1 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| K08 | Bảo hành: tiếp nhận → gửi NCC → hoàn tất / hủy | P1 | **Có cơ bản** | Nhận, gửi NCC, hoàn tất hoặc hủy, nhật ký; chưa đính kèm. |
| K09 | Tra cứu sổ kho theo SKU, KH, IMEI, ngày, người thao tác | P1 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| DH01 | Chọn nguồn hàng TNM hoặc NCC | P0 | **Có cơ bản** | Nguồn TNM/NCC chọn được, lưu NCC. |
| DH02 | Phân công kỹ thuật viên, ghi chú, lịch lắp | P0 | **Có cơ bản** | Kỹ thuật viên và lịch lắp chọn được. |
| DH03 | Sửa thông tin đơn hàng chưa kích hoạt | P1 | **Có cơ bản** | Chỉ sửa DRAFT/SCHEDULED; lịch sử JSON. |
| DH04 | Đơn nhiều sản phẩm, số lượng, giá bán | P1 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| DH05 | Ngày kích hoạt/hết hạn và chọn hình thức thanh toán | P1 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| DH06 | Báo cáo đơn theo nguồn/KH/SKU và từ-đến ngày | P2 | **Chưa** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| TB01 | Sửa thông tin thiết bị, biển số, số khung | P1 | **Chưa** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| TB02 | Tìm theo biển số, KH, điện thoại để gia hạn | P1 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| TB03 | Cảnh báo hạn và danh sách CSKH xử lý | P2 | **Một phần** | Có bảng followup_tasks; chưa xử lý scheduler. |
| TB04 | Gia hạn theo thời hạn/chính sách nhiều kỳ | P1 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| TB05 | Báo cáo nguồn, thời gian, hiệu suất gia hạn | P2 | **Chưa** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| TC01 | Công nợ nhà cung cấp từ nghiệp vụ mua hàng | P1 | **Một phần** | PO và nhận hàng số lượng; chưa sinh công nợ NCC tự động. |
| TC02 | Chi phí kỹ thuật, hoa hồng Sale và phải trả | P1 | **Chưa** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| TC03 | Hạch toán, đối soát, đối trừ và bảng kê Excel | P0 | **Một phần** | Thanh toán và phân bổ CSV có từ v5; chưa import ngân hàng/XLSX chuẩn. |
| TC04 | Tuổi nợ, quá hạn, lịch thu và phân tích theo KH | P2 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| BC01 | KPI theo mục tiêu, bộ lọc kỳ và drill-down | P2 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| BC02 | Phương pháp tính doanh thu/lợi nhuận kế toán | P1 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
| QT01 | Phân quyền theo kho/chi nhánh/khách hàng | P0 | **Một phần** | Scope kho trên module v6; chặn module cũ. Chưa scope khách hàng. |
| QT02 | Migration có phiên bản, sao lưu/khôi phục, kiểm thử tự động | P0 | **Một phần** | Khởi tạo DB version 6, test SQL; chưa engine migration/backup tự động. |
| QT03 | Phân trang server, lọc SQL, hạn chế tải toàn bộ dữ liệu | P2 | **Một phần** | Xem backlog chi tiết; chưa bảo đảm trọn quy trình. |
