"""Schema-level smoke tests; no dotnet SDK required. SQLite business invariants are checked independently."""
import sqlite3,tempfile,pathlib
D=pathlib.Path(__file__).resolve().parents[1]/'Data'

def new_db(demo=False):
    c=sqlite3.connect(':memory:')
    c.execute('PRAGMA foreign_keys=ON')
    for f in ('schema.sql','v6_schema.sql', 'seed.sql' if demo else 'seed_empty.sql'):
        c.executescript((D/f).read_text(encoding='utf8'))
    c.execute("INSERT OR IGNORE INTO warehouse_locations(warehouse_id,code,name) SELECT id,'MAIN','Vị trí chính' FROM warehouses")
    if demo:
        c.executescript((D/'v6_demo.sql').read_text(encoding='utf8'))
    c.executescript((D/'catalog_2026_10_09.sql').read_text(encoding='utf8'))
    assert c.execute('pragma integrity_check').fetchone()[0]=='ok'
    assert c.execute('pragma foreign_key_check').fetchall()==[]
    return c

def count(c,table):return c.execute('select count(*) from '+table).fetchone()[0]

def tests():
    c=new_db(False)
    assert count(c,'app_users')==1 and count(c,'roles')==6
    for table in ('customers','quantity_movements','sales_orders','invoices','purchase_orders'):
        assert count(c,table)==0,(table,count(c,table))
    assert count(c,'warehouse_locations')==5
    assert count(c,'products')==119
    assert count(c,'stock_count_lines')==133
    assert count(c,'schema_versions')==1
    tables=count(c,'sqlite_master')
    location_id=c.execute("SELECT id FROM warehouse_locations WHERE warehouse_id=1").fetchone()[0]
    # A single item can only have one stock balance per SKU / warehouse / location.
    c.execute("INSERT INTO products(group_id,sku,name,is_serialized) VALUES(1,'GOODS-01','Hàng không IMEI',0)")
    pid=c.execute("SELECT id FROM products WHERE sku='GOODS-01'").fetchone()[0]
    c.execute(f"INSERT INTO quantity_movements(document_no,product_id,warehouse_id,location_id,delta_qty,movement_type) VALUES('OPEN001',{pid},1,{location_id},100,'OPENING')")
    assert c.execute('SELECT SUM(delta_qty) FROM quantity_movements WHERE warehouse_id=1').fetchone()[0]==100
    c.execute(f"INSERT INTO quantity_movements(document_no,product_id,warehouse_id,location_id,delta_qty,movement_type) VALUES('OUT001',{pid},1,{location_id},-20,'ISSUE')")
    assert c.execute('SELECT SUM(delta_qty) FROM quantity_movements').fetchone()[0]==80
    c.execute(f"INSERT INTO quantity_counts(count_no,warehouse_id,location_id) VALUES('KK001',1,{location_id})")
    c.execute(f"INSERT INTO quantity_count_lines(count_id,product_id,book_qty,counted_qty) VALUES(1,{pid},80,75)")
    c.execute(f"INSERT INTO quantity_movements(document_no,product_id,warehouse_id,location_id,delta_qty,movement_type,ref_type,ref_id) VALUES('ADJ001',{pid},1,{location_id},-5,'ADJUST','COUNT',1)")
    c.execute("UPDATE quantity_counts SET status='APPROVED' WHERE id=1")
    assert c.execute('SELECT SUM(delta_qty) FROM quantity_movements').fetchone()[0]==75
    try:
        c.execute(f"INSERT INTO quantity_movements(document_no,product_id,warehouse_id,location_id,delta_qty,movement_type) VALUES('ADJ001',{pid},1,{location_id},1,'RECEIVE')")
        raise AssertionError('unique document_no must be enforced')
    except sqlite3.IntegrityError:pass
    try:
        c.execute(f"INSERT INTO quantity_movements(document_no,product_id,warehouse_id,location_id,delta_qty,movement_type) VALUES('BAD-W',{pid},1,999,1,'RECEIVE')")
        raise AssertionError('foreign key must be enforced')
    except sqlite3.IntegrityError:pass
    assert c.execute('pragma foreign_key_check').fetchall()==[]
    # Trigger-level invariants (cannot be bypassed by raw SQL or duplicate approval).
    try:
        c.execute(f"INSERT INTO quantity_movements(document_no,product_id,warehouse_id,location_id,delta_qty,movement_type) VALUES('NEG001',{pid},1,{location_id},-1000,'ISSUE')")
        raise AssertionError('negative stock must be rejected')
    except sqlite3.IntegrityError: pass
    for sql in ("UPDATE quantity_movements SET delta_qty=200 WHERE document_no='OPEN001'", "DELETE FROM quantity_movements WHERE document_no='OPEN001'"):
        try: c.execute(sql); raise AssertionError('append-only ledger must reject mutation')
        except sqlite3.IntegrityError: pass
    d=new_db(True)
    assert count(d,'products')>=119
    assert count(d,'stock_count_lines')==133
    assert count(d,'quantity_movements')==1
    assert d.execute('SELECT SUM(delta_qty) FROM quantity_movements').fetchone()[0]==100
    assert d.execute('pragma foreign_key_check').fetchall()==[]
    print('PASS: fresh DB with TNM catalog+snapshot; no sample orders; quantity ledger, constraints, foreign keys')
    print('PASS: demo option, 119+ products, 133 count snapshot lines, 100 opening quantity')
    print('SQLITE TABLE COUNT',c.execute("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").fetchone()[0])
if __name__=='__main__':tests()
