# TNM.Management v6 — khởi tạo sản phẩm và kiểm kho từ Excel

**Nguồn:** `Sản phẩm - Kiểm kho TNM ngày 09 10(1).xlsx` (Kho SG, Kho PQ), ngày kiểm kê **09/10/2026**.

## Dữ liệu

| Mục | Giá trị |
| --- | ---: |
| SKU duy nhất | 119 |
| Hàng hóa / Dịch vụ / Combo | 74 / 31 / 14 |
| Dòng kiểm kho SG / PQ | 119 / 14 |
| SG chưa ghi số thực kiểm (`NULL`) | 99 |
| SG có tồn sổ âm | 13 |
| SG đã có thực kiểm | 20 dòng, tổng 230 đơn vị |
| PQ đã có thực kiểm | 14 dòng, tổng 367 đơn vị |

## Ánh xạ SQLite

| Excel | SQLite | Ghi chú |
| --- | --- | --- |
| Loại hàng | `product_catalog_metadata.product_kind`, `product_groups` | GOODS / SERVICE / BUNDLE |
| Mã hàng | `products.sku` | khóa duy nhất toàn hệ thống, chuẩn hóa bỏ khoảng trắng thừa |
| Tên hàng | `products.name` | giữ tên thực tế |
| Tồn kho | `stock_count_lines.book_qty` | giữ nguyên số âm |
| Thực tế | `stock_count_lines.actual_qty` | ô trắng `NULL`, không phải 0 |
| SG / BH / VT | `stock_count_lines.sg_qty / bh_qty / vt_qty` | vị trí nội bộ của kho SG, không cộng thêm |
| Ghi chú Excel (cột J sheet Kho SG) | `stock_count_lines.notes` | giữ nguyên, gồm lưu ý bảo hành |
| Sheet và dòng | `source_sheet`, `source_row` | phục vụ truy ngược nguồn |
| SG / PQ | `warehouses`, `warehouse_locations`, `stock_count_lines.warehouse_code` | tạo kho và vị trí SG/BH/VT |
| Ngày nguồn | `stock_count_batches.count_date` | 2026-10-09 |

**Kiểm tra điểm khác biệt:** mã `SP000005` tồn sổ 5, thực kiểm 4, cột SG là 5 và có ghi chú *Đang gửi bảo hành 1 cái*. Không tự thay đổi bất kỳ giá trị nào.

## Khi nào chạy init

- **Database mới:** chạy ứng dụng bình thường, dù `TNM_SEED_DEMO=0`. Tạo owner + danh mục Excel + ảnh chụp kiểm kê.
- **Database v6 đã tồn tại:** sao lưu SQLite trước, update source và khởi động lại. Nếu còn thiếu snapshot, tự nhập những dòng còn thiếu; không xóa IMEI, đơn hàng, quyền User hoặc ghi chú đối soát.
- **Database demo:** vẫn thêm dữ liệu minh họa cũ nếu bật `TNM_SEED_DEMO=1`; dữ liệu Excel luôn được nhập độc lập.
- Các lệnh SQL import chạy trong `BEGIN IMMEDIATE ... COMMIT` và dùng `INSERT OR IGNORE` để tránh trùng.

## Không tự tạo tồn kho vận hành

Tồn sổ và thực kiểm là số liệu lịch sử tại ngày **09/10/2026**, **không phải tồn hiện tại**. 99 mã SG không có số thực kiểm, 13 mã sổ âm; mã `SP000005` còn có chênh lệch phân bổ SG với thực kiểm. Do đó init **không tạo** `quantity_movements`, `stock_units`, IMEI hay phiếu nhập mở đầu. Phải đối soát và chốt tồn thực tế trước khi hạch toán tồn đầu.

## Xác nhận sau khi chạy

Đăng nhập Owner rồi mở `/products` và `/stock-count`. Trên Ubuntu dùng `sqlite3` khi đã dừng dịch vụ để kiểm tra:

```sql
SELECT COUNT(*) FROM product_catalog_metadata WHERE source_tag='TNM_2026_10_09'; -- 119
SELECT warehouse_code,COUNT(*) FROM stock_count_lines GROUP BY warehouse_code; -- SG:119, PQ:14
SELECT COUNT(*) FROM stock_count_lines WHERE warehouse_code='SG' AND actual_qty IS NULL; -- 99
SELECT COUNT(*) FROM stock_count_lines WHERE warehouse_code='SG' AND book_qty<0; -- 13
PRAGMA foreign_key_check;
PRAGMA integrity_check;
```

Kiểm tra mã `SP000005`:

```sql
SELECT p.sku,l.warehouse_code,l.book_qty,l.actual_qty,l.sg_qty,l.notes
FROM products p JOIN stock_count_lines l ON l.product_id=p.id
WHERE p.sku='SP000005' AND l.warehouse_code='SG';
```

Test offline: `python scripts/test_tnm_catalog_seed.py` và `python scripts/test_v6_sqlite.py`.

**Bảo mật:** Người dùng bị giới hạn kho chỉ xem các kho được cấp trên trang kiểm kê. Xuất CSV kiểm kê được từ chối với user bị giới hạn kho ở bản demo hiện tại.
