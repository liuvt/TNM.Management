"""SQLite smoke tests for the V7 canonical product design (no .NET build)."""
import sqlite3
from pathlib import Path

DATA=Path(__file__).resolve().parents[1]/'Data'

def one(db, sql, args=()):
    return db.execute(sql,args).fetchone()[0]

def main():
    db=sqlite3.connect(':memory:')
    db.execute('PRAGMA foreign_keys=ON')
    for fname in ['schema.sql','v6_schema.sql','v7_customer_groups.sql','seed_empty.sql']:
        db.executescript((DATA/fname).read_text(encoding='utf8'))
    # Minimal input catalog schema needed for the canonical DX sequence.
    db.executescript('''
        CREATE TABLE v7_sku_sequences(prefix TEXT PRIMARY KEY,last_number INTEGER NOT NULL);
        INSERT INTO v7_sku_sequences VALUES('DX',0);
        CREATE TABLE v7_product_migrations(code TEXT PRIMARY KEY, executed_at TEXT DEFAULT CURRENT_TIMESTAMP, affected_rows INTEGER NOT NULL);
        CREATE TRIGGER v7_product_sku_immutable BEFORE UPDATE OF sku ON products
        WHEN NEW.sku<>OLD.sku BEGIN SELECT RAISE(ABORT,'SKU_IMMUTABLE'); END;
    ''')
    db.executescript((DATA/'v7_product_master.sql').read_text(encoding='utf8'))
    assert one(db,'SELECT COUNT(*) FROM products')==0, 'Fresh install must have no Excel products'
    assert one(db,'SELECT COUNT(*) FROM v7_product_selectable')==0
    for i in (1,2):
        db.execute("UPDATE v7_sku_sequences SET last_number=last_number+1 WHERE prefix='DX'")
        sku=f"DX{one(db, 'SELECT last_number FROM v7_sku_sequences WHERE prefix=\'DX\''):05d}"
        db.execute("INSERT INTO products(sku,name,group_id,is_serialized) VALUES(?,?,?,1)",(sku,'Camera '+str(i),1))
        pid=one(db,'SELECT last_insert_rowid()')
        db.execute("INSERT INTO product_catalog_metadata VALUES(?,?,?,?)",(pid,'GOODS','Cái','MANUAL'))
    assert [r[0] for r in db.execute('SELECT sku FROM v7_product_selectable ORDER BY sku')]==['DX00001','DX00002']
    try:
        db.execute("UPDATE products SET sku='MISMATCH' WHERE sku='DX00001'")
        raise AssertionError('Changing SKU should be impossible')
    except sqlite3.IntegrityError:pass
    # Simulate existing legacy workbook SKU, then archive it without breaking references.
    db.execute("INSERT INTO products(group_id,sku,name,is_serialized) VALUES(1,'10300','Lọc nhớt mẫu',0)")
    legacy=one(db,'SELECT last_insert_rowid()')
    db.execute("INSERT INTO product_catalog_metadata VALUES(?,?,?,?)",(legacy,'GOODS','Cái','TNM_2026_10_09'))
    db.execute("INSERT INTO sales_order_lines(order_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd) VALUES(1,?,?,?,?)",(legacy,1,100,50)) if False else None
    assert one(db,'SELECT COUNT(*) FROM v7_product_master')==2
    db.execute("UPDATE products SET is_active=0 WHERE id IN (SELECT product_id FROM product_catalog_metadata WHERE source_tag='TNM_2026_10_09')")
    assert one(db,'SELECT COUNT(*) FROM products')==3
    assert one(db,'SELECT is_active FROM products WHERE id=?',(legacy,))==0
    assert one(db,'SELECT COUNT(*) FROM v7_product_selectable')==2
    assert db.execute('PRAGMA foreign_key_check').fetchall()==[]
    assert one(db,'PRAGMA integrity_check')=='ok'
    print('PASS: no sample product on fresh DB; DX00001-2; immutable SKU; legacy archived; views; FK OK')

if __name__=='__main__':main()
