"""Generate a detailed schema dictionary from canonical Data/schema.sql (Python stdlib only)."""
import sqlite3
from pathlib import Path
root=Path(__file__).resolve().parent.parent
sql=(root/'Data/schema.sql').read_text(encoding='utf-8')
conn=sqlite3.connect(':memory:')
conn.executescript(sql)
info={
'employees':('Quản trị','Nhân viên bán hàng, kho, kỹ thuật, CSKH và mã nhân sự'),
'app_users':('Quản trị','Tài khoản ứng dụng; password_hash phải tạo bởi Identity khi production'),
'roles':('Quản trị','Danh mục vai trò: quản trị, quản lý, kế toán, kho, CSKH, kỹ thuật'),
'user_roles':('Quản trị','Liên kết M:N giữa tài khoản và vai trò'),
'audit_logs':('Quản trị','Bằng chứng hành động, đối tượng, thời điểm, người thực hiện'),
'product_groups':('Danh mục','Nhóm Thiết bị TNM, Hàng gia dụng TNM'),
'products':('Danh mục','SKU, thuộc tính hàng, giá vốn, phí và thời hạn bảo hành'),
'product_cost_tiers':('Danh mục','Các bậc giá nhập có điều kiện theo số lượng và thời gian'),
'customer_groups':('Danh mục','Nhóm HTX Tân Thành Đô, HTX Tân Đại Thành...'),
'customers':('Danh mục','Khách hàng, nguồn Sale/Đại lý và thông tin liên hệ'),
'customer_prices':('Danh mục','Giá riêng theo khách hàng–SKU–ngày hiệu lực, cả 2 giá bán và phí gia hạn'),
'suppliers':('Danh mục','Đơn vị cung cấp nguồn hàng hoặc xử lý bảo hành'),
'warehouses':('Kho','Danh sách vị trí kho vật lý'),
'stock_units':('Kho','Mỗi IMEI duy nhất = một đơn vị thiết bị và một nơi giữ hiện tại'),
'inventory_transactions':('Kho','Phiếu nhập/xuất/thu hồi/chuyển ký gửi/kỹ thuật theo thời gian'),
'inventory_transaction_lines':('Kho','IMEI và giá vốn tại mỗi lần chuyển dịch'),
'inventory_counts':('Kho','Phiếu kiểm kê theo kho, người kiểm và trạng thái duyệt'),
'inventory_count_lines':('Kho','IMEI quét thực tế và kết quả có/không tại lần kiểm kê'),
'sales_orders':('Đơn hàng','Thông tin đầu đơn, khách hàng, nguồn hàng, kỹ thuật và lịch lắp'),
'sales_order_lines':('Đơn hàng','Từng SKU, số lượng, giá bán/giá vốn đã khóa tại thời điểm đơn'),
'activations':('Thiết bị','Gắn IMEI với dòng đơn, biển số/số khung, hạn dịch vụ'),
'renewals':('Thiết bị','Lịch sử gia hạn bất biến theo từng lượt, hạn trước và sau'),
'warranty_cases':('Thiết bị','Vụ việc bảo hành, NCC nhận, trạng thái xử lý'),
'warranty_events':('Thiết bị','Lịch sử thay đổi trạng thái từng vụ bảo hành'),
'invoices':('Tài chính','Chứng từ công nợ nhập hàng, bán hàng, gia hạn, dịch vụ, hoa hồng'),
'invoice_lines':('Tài chính','Dòng chứng từ và đơn giá snapshot'),
'payments':('Tài chính','Phiếu thu / phiếu chi theo phương thức, số tiền, ngày'),
'payment_allocations':('Tài chính','Phân bổ số tiền của phiếu thu/chi vào từng hóa đơn'),
'expense_charges':('Tài chính','Chi phí kỹ thuật, hoa hồng hoặc khoản chi khác')
}
explain={
'imei':'IMEI/serial duy nhất, lưu dưới dạng TEXT để giữ số 0 đầu',
'status':'Trạng thái nghiệp vụ có CHECK enum theo bảng',
'product_id':'Khóa ngoại đến sản phẩm (SKU)',
'customer_id':'Khóa ngoại đến khách hàng / đơn vị giữ',
'group_id':'Nhóm phân loại nghiệp vụ',
'warehouse_id':'Kho đích hoặc kho đang giữ',
'employee_id':'Nhân viên/kỹ thuật liên quan',
'actor_user_id':'Tài khoản ghi nhận hành động',
'order_line_id':'Dòng đơn được kích hoạt (UNIQUE)',
'stock_unit_id':'Thiết bị IMEI; ở activation có UNIQUE',
'activation_id':'Bản kích hoạt gốc của các lần gia hạn',
'amount_vnd':'Giá trị tiền VND nguyên không âm',
'allocated_vnd':'Khoản thanh toán được phân bổ (VND nguyên)',
'base_cost_vnd':'Giá nhập chính của SKU, có thể có mức giá theo bậc',
'unit_cost_snapshot_vnd':'Đơn giá vốn được chụp thời điểm giao dịch; không tự đổi theo bảng giá',
'unit_price_vnd':'Đơn giá bán/chứng từ được khóa khi phát sinh',
'expires_at':'Ngày hết hạn dịch vụ hiện tại',
'previous_expiry':'Ngày hết hạn trước lần gia hạn',
'new_expiry':'Ngày hết hạn sau lần gia hạn',
'min_quantity':'Ngưỡng số lượng để áp bậc chiết khấu',
'effective_from':'Ngày bắt đầu hiệu lực bảng giá',
'effective_to':'Ngày hết hiệu lực nếu có',
'source_type':'Nguồn khách hàng, đơn hàng hoặc gia hạn tùy bảng',
'payment_method':'Ghi nợ hoặc thanh toán ngay',
'movement_type':'Loại di chuyển kho (RECEIVE, CONSIGN_OUT,...)',
'invoice_type':'Loại chứng từ SALE/RENEWAL/PURCHASE/SERVICE/COMMISSION',
'order_no':'Mã đơn duy nhất',
'invoice_no':'Mã hóa đơn duy nhất',
'payment_no':'Mã phiếu thu/chi duy nhất',
'code':'Mã nghiệp vụ duy nhất trong bảng',
'sku':'Mã sản phẩm duy nhất',
'is_serialized':'1 quản lý theo serial, 0 quản lý số lượng (sẽ bổ sung ledger số lượng)',
'username':'Tên đăng nhập duy nhất',
'password_hash':'Chưa tạo mật khẩu demo; production bắt buộc Identity + hash chuẩn'
}
out=['# Từ điển dữ liệu TNM — 29 bảng SQLite','',
 'Nguồn tự động: `Data/schema.sql`. Bảng mô tả tất cả cột, kiểu dữ liệu, khóa chính/khóa ngoại, NULL, giá trị mặc định và ý nghĩa. Đây là **schema demo**, không phải cấu trúc vật lý SQL Server cuối cùng.','',
 '- **PK**: khóa chính; **FK**: khóa ngoại; **UK**: khóa duy nhất khi có.','- `INTEGER` tiền = VND nguyên. `TEXT` ngày = ISO `yyyy-MM-dd` hoặc `yyyy-MM-dd HH:mm:ss` để so sánh/lọc.','- Mọi FK dùng SQLite `PRAGMA foreign_keys=ON`. `NULL` ở cột tham chiếu nghĩa là mối quan hệ tùy chọn.','']
rows=conn.execute("select name from sqlite_master where type='table' and name not like 'sqlite_%' order by name").fetchall()
print('Table count',len(rows))
for ix,(table,) in enumerate(rows,1):
 group,desc=info[table]
 out += [f'## {ix:02}. `{table}` — {desc}','',f'**Nhóm:** {group}.','',
 '| Cột | Kiểu SQLite | Khóa / ràng buộc | NULL? | Mặc định | Ý nghĩa / lưu ý |',
 '|---|---|---|---|---|---|']
 cols=conn.execute(f'PRAGMA table_info("{table}")').fetchall()
 fks=conn.execute(f'PRAGMA foreign_key_list("{table}")').fetchall()
 fkmap={row[3]:f'{row[2]}.{row[4]}' for row in fks}
 idx=[]
 for i in conn.execute(f'PRAGMA index_list("{table}")').fetchall():
  if i[2]:
   idxcols=[r[2] for r in conn.execute(f'PRAGMA index_info("{i[1]}")').fetchall()]
   idx.append(idxcols)
 for cid,name,typ,notnull,default,pk in cols:
  keys=[]
  if pk:keys.append('PK')
  if name in fkmap:keys.append('FK→'+fkmap[name])
  if any(name in k and len(k)==1 for k in idx):keys.append('UK')
  if table=='stock_units' and name=='imei':keys.append('UNIQUE')
  if table=='activations' and name in ('stock_unit_id','order_line_id'):keys.append('UNIQUE')
  note=explain.get(name,'')
  if not note:
   if name=='id': note='Khóa thay thế tự tăng'
   elif name.endswith('_at'):note='Mốc thời gian ISO'
   elif name.endswith('_date'):note='Ngày ISO'
   elif name.endswith('_vnd'):note='Số tiền VND nguyên'
   elif name.endswith('_id'):note='Khóa liên kết đến bảng nghiệp vụ'
   elif name.startswith('is_'):note='Cờ 0/1'
   elif name in ('notes','note','details','problem'):note='Ghi chú / nội dung nghiệp vụ'
   else:note='Thông tin chi tiết'
  out.append(f'| `{name}` | `{typ}` | {", ".join(keys) or "—"} | {"Không" if (notnull or pk) else "Có"} | `{str(default)}` | {note} |')
 out += ['','**Quan hệ FK:** '+(', '.join(f'`{x}` → `{ref}`' for x,ref in fkmap.items()) if fkmap else 'Không có (bảng danh mục gốc).'), '']
 s=conn.execute('SELECT sql FROM sqlite_master WHERE type="table" AND name=?',(table,)).fetchone()[0]
 checks=s.upper().count('CHECK(')
 if checks:out += [f'**Ràng buộc CHECK:** {checks} nhóm quy tắc được khai báo trong `Data/schema.sql` (xem script để biết đầy đủ enum/điều kiện).','']
out+=['## Nhóm chỉ mục và kiểm soát','',
 '| Bảng | Index | Mục đích |','|---|---|---|',
 '| stock_units | `ix_stock_units_product_status`, `ix_stock_units_customer_status` | Tồn theo SKU, nơi giữ |',
 '| sales_orders | `ix_orders_customer_status` | Danh sách đơn theo KH / trạng thái |',
 '| activations | `ix_activations_expiry` | Sắp hết hạn, quá hạn |',
 '| inventory_transactions | `ix_trans_at`, `ix_trans_customer` | Sổ kho theo kỳ / KH |',
 '| renewals | `ix_renewals_activation_at` | Lịch sử gia hạn |',
 '| invoices | `ix_invoices_type_status` | Công nợ theo nhóm nghiệp vụ |',
 '| audit_logs | `ix_audit_entity` | Nhật ký theo đối tượng |', '',
 '## Lưu ý phần mở rộng production','',
 '1. Hàng `products.is_serialized=0` chưa có sổ quantity balance hoàn chỉnh; bổ sung `stock_lots`, `stock_movements_qty` hoặc ledger chung trước khi dùng hàng gia dụng.','2. `activations.order_line_id` UNIQUE và MVP chỉ triển khai 1 thiết bị/đơn. Đơn nhiều chiếc cần allocation `order_line_units` cho phép 1 dòng -> nhiều lần activation.','3. Bảng giá version trong demo khóa `(customer_id, product_id, effective_from)` ở granularity **ngày**; để sửa nhiều lần mỗi ngày và tra cứu lịch sử, dùng timestamp/version ID kèm quy tắc khoảng hiệu lực không chồng lấn.','4. `payments` cần nâng cấp đối tượng nộp/nhận, đơn vị tiền, ngân hàng, chứng từ ngân hàng, trạng thái đối soát; áp CHECK/trigger hoặc service chống phân bổ vượt số tiền payment.','5. Tạo luồng tài liệu hợp lệ cho bảo hành từ khách hàng → NCC → hoàn trả; `stock_units` hiện là trạng thái hiện tại, `warranty_events` là timeline.','6. Thêm chính sách retention, actor from login, kiểm soát truy cập từng hàng/công ty, idempotency key chống gửi form lặp, approval và tính toàn vẹn audit log.','']
(root/'docs/04-tu-dien-bang.md').write_text('\n'.join(out),encoding='utf-8')
print('Written',root/'docs/04-tu-dien-bang.md','total lines',len(out))
