# Kiến trúc và quan hệ database TNM v6

## Luồng màn hình

```mermaid
flowchart LR
U[User] --> A[Cookie login - Role/User permissions]
A --> DM[Danh mục nền]
A --> P[SKU / Khách / Bảng giá]
A --> K[Kho số lượng & IMEI]
A --> PO[Mua hàng NCC]
A --> O[Đơn hàng / Kỹ thuật]
A --> D[Thiết bị / Bảo hành]
A --> F[Hóa đơn / Thu chi]
A --> QU[Kho được phép xem]
K --> CT[Kiểm kê - Duyệt chênh lệch]
PO --> K
O --> D --> F
```

## Quan hệ chính

```mermaid
erDiagram
    warehouses ||--o{ warehouse_locations : contains
    products ||--o{ quantity_movements : moved
    warehouses ||--o{ quantity_movements : location
    warehouse_locations ||--o{ quantity_movements : tracks
    warehouses ||--o{ quantity_counts : counts
    quantity_counts ||--o{ quantity_count_lines : contains
    products ||--o{ quantity_count_lines : counted
    suppliers ||--o{ purchase_orders : supplies
    purchase_orders ||--|{ purchase_order_lines : includes
    purchase_orders ||--o| goods_receipts : received
    sales_orders ||--o{ order_change_history : audited
    app_users ||--o{ user_warehouse_scopes : assigned
    warehouses ||--o{ user_warehouse_scopes : scopes
    stock_units ||--o{ warranty_cases : serviced
    warranty_cases ||--o{ warranty_events : events
```

## Ảnh chụp và sổ phát sinh

- `stock_count_batches` / `stock_count_lines`: ảnh chụp nguồn Excel, không sinh tồn vật lý.
- `quantity_movements`: sổ tồn số lượng **đang vận hành**, khóa SKU + kho + vị trí.
- `stock_units`: theo từng IMEI, không cộng trùng với `quantity_movements`.
- `quantity_counts` / `quantity_count_lines`: phiên kiểm kê vận hành thực tế, duyệt sinh bút toán ADJUST.
- `audit_logs`: ghi người tác động các tác vụ v6.
- `schema_versions`: đánh dấu khởi tạo schema lần đầu ở v6. Chưa phải engine migrations tự động.

## Phân quyền

Cơ chế User + Role + Allow/Deny riêng. Kho SELECTED: cho phép trang `/quantity-stock`, `/quantity-counts`, `/purchases` với query/mutation lọc phía server, chặn các trang kho/phụ thuộc kho cũ không có data scope. ADMIN luôn xem được toàn bộ. Không có phân quyền theo khách hàng/chi nhánh trên toàn ứng dụng trong bản này.
