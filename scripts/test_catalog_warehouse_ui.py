"""Lightweight SQLite contract checks for the TNM v6 master-data / stock opening flow.
This does not compile Blazor or execute C#; use dotnet build and browser tests separately.
"""
import sqlite3
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
con=sqlite3.connect(':memory:')
con.execute('PRAGMA foreign_keys=ON')
for sql in ('schema.sql','v6_schema.sql','seed_empty.sql','catalog_2026_10_09.sql'):
    con.executescript((ROOT/'Data'/sql).read_text(encoding='utf-8-sig'))
con.execute("INSERT OR IGNORE INTO warehouse_locations(warehouse_id,code,name) SELECT id,'MAIN','Vị trí chính' FROM warehouses")
assert con.execute('SELECT COUNT(*) FROM products').fetchone()[0]==119
assert con.execute('SELECT COUNT(*) FROM stock_count_lines').fetchone()[0]==133
assert con.execute('SELECT COUNT(*) FROM app_users WHERE is_owner=1').fetchone()[0]==1
con.execute("INSERT INTO suppliers(code,name,phone,tax_code) VALUES('NCC-T','Nhà cung cấp test','','TAX')")
con.execute("UPDATE suppliers SET name='Đối tác đã chỉnh' WHERE code='NCC-T'")
assert con.execute("SELECT name FROM suppliers WHERE code='NCC-T'").fetchone()[0]=='Đối tác đã chỉnh'
con.execute("INSERT INTO warehouses(code,name,address) VALUES('HN-T','Kho test','Địa chỉ test')")
wid=con.execute('SELECT last_insert_rowid()').fetchone()[0]
con.execute("INSERT INTO warehouse_locations(warehouse_id,code,name) VALUES (?,'MAIN','Vị trí chính')",(wid,))
con.execute("INSERT INTO warehouse_locations(warehouse_id,code,name) VALUES (?,'K-A','Kệ A')",(wid,))
main=con.execute("SELECT id FROM warehouse_locations WHERE warehouse_id=? AND code='MAIN'",(wid,)).fetchone()[0]
ke=con.execute("SELECT id FROM warehouse_locations WHERE warehouse_id=? AND code='K-A'",(wid,)).fetchone()[0]
con.execute("INSERT INTO products(group_id,sku,name,base_cost_vnd,renewal_fee_vnd,install_fee_vnd,warranty_months,is_serialized) VALUES(1,'TEST-MULTI','Hàng tồn nhiều vị trí',0,0,0,0,0)")
pid=con.execute('SELECT last_insert_rowid()').fetchone()[0]
con.execute("INSERT INTO product_catalog_metadata(product_id,product_kind,unit,source_tag) VALUES (?, 'GOODS','Cái','MANUAL')",(pid,))
for seq,loc,qty in [(1,main,10),(2,ke,25)]:
    con.execute("INSERT INTO quantity_movements(document_no,product_id,warehouse_id,location_id,delta_qty,movement_type,note,actor_user_id) VALUES (?,?,?,?,?,'OPENING','Khởi tạo tồn đầu',1)",(f'TEST-{seq}',pid,wid,loc,qty))
assert con.execute('SELECT SUM(delta_qty) FROM quantity_movements WHERE product_id=?',(pid,)).fetchone()[0]==35
# DB-level FK prevents warehouse/location mismatch
other=con.execute("SELECT id FROM warehouse_locations WHERE warehouse_id<>? LIMIT 1",(wid,)).fetchone()[0]
try:
    con.execute("INSERT INTO quantity_movements(document_no,product_id,warehouse_id,location_id,delta_qty,movement_type) VALUES('BAD-LOC',?,?,?,1,'OPENING')",(pid,wid,other))
    raise AssertionError('Cross-warehouse location unexpectedly accepted')
except sqlite3.IntegrityError: pass
assert con.execute('PRAGMA foreign_key_check').fetchall()==[]
assert con.execute('SELECT COUNT(*) FROM stock_count_lines').fetchone()[0]==133
print('PASS SQLite: 119 SKU, 133 kiểm kê, tạo NCC/kho/vị trí, phân bổ 35 đơn vị, FK không sai, Owner giữ nguyên')
