"""Checks relational rules used by V7 InputCatalog. This does not compile or run C#."""
import sqlite3
from pathlib import Path
p=Path(__file__).resolve().parents[1]
c=sqlite3.connect(':memory:');c.execute('PRAGMA foreign_keys=ON')
c.executescript((p/'Data/schema.sql').read_text())
c.executescript((p/'Data/v6_schema.sql').read_text())
c.executescript((p/'Data/v7_customer_groups.sql').read_text())
c.executescript('''
CREATE TABLE supplier_groups(id INTEGER PRIMARY KEY,code TEXT UNIQUE NOT NULL,name TEXT NOT NULL);
ALTER TABLE suppliers ADD COLUMN group_id INTEGER REFERENCES supplier_groups(id);
CREATE TABLE v7_supplier_group_prices (id INTEGER PRIMARY KEY,supplier_group_id INTEGER NOT NULL REFERENCES supplier_groups(id),product_id INTEGER NOT NULL REFERENCES products(id),unit_cost_vnd INTEGER NOT NULL CHECK(unit_cost_vnd>=0),effective_from TEXT NOT NULL, effective_to TEXT, UNIQUE(supplier_group_id,product_id,effective_from));
CREATE TABLE warehouse_areas(id INTEGER PRIMARY KEY,code TEXT UNIQUE NOT NULL,name TEXT NOT NULL);
ALTER TABLE warehouses ADD COLUMN area_id INTEGER REFERENCES warehouse_areas(id);
CREATE TABLE v7_sku_sequences(prefix TEXT PRIMARY KEY,last_number INTEGER NOT NULL);
CREATE TRIGGER v7_customer_group_required_insert BEFORE INSERT ON customers WHEN NEW.group_id IS NULL BEGIN SELECT RAISE(ABORT,'Group required'); END;
CREATE TRIGGER v7_customer_group_required_update BEFORE UPDATE OF group_id ON customers WHEN NEW.group_id IS NULL BEGIN SELECT RAISE(ABORT,'Group required'); END;
''')
c.execute("INSERT INTO product_groups(code,name) VALUES('CAMERA','Camera')")
gid=c.execute('SELECT last_insert_rowid()').fetchone()[0]
c.execute("INSERT INTO products(group_id,sku,name) VALUES(?, 'DX00001','Camera 01')",(gid,))
c.execute("INSERT INTO supplier_groups(code,name) VALUES('NCC_A','Đối tác A')")
c.execute("INSERT INTO suppliers(code,name,group_id) VALUES('NCC01','NCC 01',1)")
c.execute("INSERT INTO warehouse_areas(code,name) VALUES('MB','Miền Bắc')")
c.execute("INSERT INTO warehouses(code,name,area_id) VALUES('HN1','Kho 1',1),('HN2','Kho 2',1)")
assert c.execute('SELECT COUNT(*) FROM warehouses WHERE area_id=1').fetchone()[0]==2
c.execute("INSERT INTO customer_groups(code,name) VALUES('RETAIL','Khách lẻ'),('AGENCY','Đại lý')")
c.execute("INSERT INTO customers(code,name,group_id) VALUES('KH01','Khách 01',1),('KH02','Khách 02',2)")
for sql in ["INSERT INTO customers(code,name,group_id) VALUES('BAD','Thiếu nhóm',NULL)","UPDATE customers SET group_id=NULL WHERE code='KH01'"]:
 try:c.execute(sql);raise AssertionError('Null group accepted')
 except sqlite3.IntegrityError: pass
for group,price in [(1,1500000),(2,1250000)]:
 c.execute("INSERT INTO v7_group_prices(group_id,product_id,standard_price_vnd,installed_price_vnd,renewal_price_vnd,effective_from) VALUES(?,?,?,0,0,'2026-10-01')",(group,1,price))
c.execute("INSERT INTO v7_group_campaigns(group_id,name,discount_percent,start_date,end_date) VALUES(1,'Camera October',10,'2026-10-01','2026-10-31')")
quotes=[]
for customer in (1,2):
 base=c.execute("SELECT p.standard_price_vnd FROM v7_group_prices p JOIN customers c ON c.group_id=p.group_id WHERE c.id=? AND p.product_id=1 AND p.effective_from<='2026-10-28'",(customer,)).fetchone()[0]
 discount=c.execute("SELECT COALESCE(MAX(x.discount_percent),0) FROM v7_group_campaigns x JOIN customers c ON c.group_id=x.group_id WHERE c.id=? AND x.is_active=1 AND x.start_date<='2026-10-28' AND x.end_date>='2026-10-28'",(customer,)).fetchone()[0]
 quotes.append(base*(100-discount)//100)
assert quotes==[1350000,1250000], quotes
c.execute("INSERT INTO v7_supplier_group_prices(supplier_group_id,product_id,unit_cost_vnd,effective_from) VALUES(1,1,1000000,'2026-10-01')")
assert c.execute('SELECT unit_cost_vnd FROM v7_supplier_group_prices WHERE supplier_group_id=1 AND product_id=1').fetchone()[0]==1000000
try:c.execute("DELETE FROM product_groups WHERE id=1");raise AssertionError('FK group deletion accepted')
except sqlite3.IntegrityError: pass
c.execute("INSERT INTO v7_sku_sequences(prefix,last_number) VALUES('DX',1)")
for sku in ('DX00002','DX00003'):
 c.execute("UPDATE v7_sku_sequences SET last_number=last_number+1 WHERE prefix='DX'")
 assigned='DX'+str(c.execute("SELECT last_number FROM v7_sku_sequences WHERE prefix='DX'").fetchone()[0]).zfill(5)
 assert assigned==sku
 c.execute('INSERT INTO products(group_id,sku,name) VALUES(?,?,?)',(gid,assigned,'Mới'))
assert c.execute("SELECT COUNT(*) FROM products WHERE sku LIKE 'DX%'").fetchone()[0]==3
assert c.execute('PRAGMA foreign_key_check').fetchall()==[]
print('PASS V7 input: region/warehouses, one customer group, group pricing, campaigns, supplier prices, FK protection, DX counter')
