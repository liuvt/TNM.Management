"""Regression: TNM 09/10/2026 product + SG/PQ stocktake seed, no live balances."""
from pathlib import Path
import sqlite3

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'Data'
CATALOG = (DATA / 'catalog_2026_10_09.sql').read_text(encoding='utf-8')


def setup(demo=False):
    conn = sqlite3.connect(':memory:')
    conn.execute('PRAGMA foreign_keys=ON')
    for file in ('schema.sql', 'v6_schema.sql', 'seed.sql' if demo else 'seed_empty.sql'):
        conn.executescript((DATA / file).read_text(encoding='utf-8'))
    conn.execute("INSERT OR IGNORE INTO warehouse_locations(warehouse_id,code,name) SELECT id,'MAIN','Vị trí chính' FROM warehouses")
    if demo:
        conn.executescript((DATA / 'v6_demo.sql').read_text(encoding='utf-8'))
    conn.executescript(CATALOG)
    return conn


def one(c, sql):
    return c.execute(sql).fetchone()[0]


def audit(c, demo=False):
    assert one(c,"SELECT count(*) FROM product_catalog_metadata WHERE source_tag='TNM_2026_10_09'")==119
    assert one(c,"SELECT count(*) FROM stock_count_lines")==133
    assert one(c,"SELECT count(*) FROM stock_count_lines WHERE warehouse_code='SG'")==119
    assert one(c,"SELECT count(*) FROM stock_count_lines WHERE warehouse_code='PQ'")==14
    assert one(c,"SELECT count(*) FROM stock_count_lines WHERE warehouse_code='SG' AND actual_qty IS NULL")==99
    assert one(c,"SELECT count(*) FROM stock_count_lines WHERE warehouse_code='SG' AND book_qty<0")==13
    assert one(c,"SELECT COALESCE(SUM(actual_qty),0) FROM stock_count_lines WHERE warehouse_code='SG'")==230
    assert one(c,"SELECT COALESCE(SUM(actual_qty),0) FROM stock_count_lines WHERE warehouse_code='PQ'")==367
    assert one(c,"SELECT COUNT(*) FROM warehouses WHERE code IN ('SG','PQ')")==2
    assert one(c,"SELECT COUNT(*) FROM warehouse_locations l JOIN warehouses w ON w.id=l.warehouse_id WHERE w.code='SG' AND l.code IN ('MAIN','SG','BH','VT')")==4
    assert one(c,"SELECT COUNT(*) FROM quantity_movements")== (1 if demo else 0)
    if not demo: assert one(c,"SELECT COUNT(*) FROM stock_units")==0
    assert c.execute('PRAGMA foreign_key_check').fetchall()==[]
    assert c.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
    data=c.execute("""SELECT s.book_qty,s.actual_qty,s.sg_qty,s.notes
      FROM stock_count_lines s JOIN products p ON p.id=s.product_id
      WHERE s.warehouse_code='SG' AND p.sku='SP000005'""").fetchone()
    assert data==(5,4,5,'Đang gửi bảo hành 1 cái'),data
    return True


def test():
    for demo in (False, True):
        con=setup(demo)
        audit(con,demo)
        con.execute("""INSERT INTO stock_count_reviews(line_id,review_status,review_note)
            SELECT l.id,'VERIFIED','Giữ nguyên đánh giá' FROM stock_count_lines l
            JOIN products p ON p.id=l.product_id WHERE l.warehouse_code='SG' AND p.sku='SP000005'""")
        con.commit()
        before=one(con,'SELECT COUNT(*) FROM products')
        con.executescript(CATALOG)  # repeat import
        audit(con,demo)
        assert one(con,'SELECT COUNT(*) FROM products')==before
        assert con.execute('SELECT review_status,review_note FROM stock_count_reviews').fetchone()==('VERIFIED','Giữ nguyên đánh giá')
        print(f'PASS demo={demo}: 119 SKU, 133 stocktake rows, idempotent, scopes, FK')
        con.close()


if __name__=='__main__':
    test()
