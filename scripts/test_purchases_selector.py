"""Independent SQLite smoke test for supplier/PO selection (not a Blazor build test)."""
import sqlite3
from pathlib import Path

DATA = Path(__file__).resolve().parents[1] / 'Data'


def test_purchase_flow():
    db = sqlite3.connect(':memory:')
    db.execute('PRAGMA foreign_keys=ON')
    for name in ('schema.sql', 'v6_schema.sql', 'seed_empty.sql'):
        db.executescript((DATA / name).read_text(encoding='utf-8'))
    # Empty fresh DB has no supplier: the UI must guide the user to create one.
    assert db.execute('SELECT COUNT(*) FROM suppliers').fetchone()[0] == 0
    # PO selection must only show active, non-serialized GOODS, not services/combos.
    eligible_query = """
        SELECT p.id,p.sku FROM products p
        LEFT JOIN product_catalog_metadata m ON m.product_id=p.id
        WHERE p.is_active=1 AND p.is_serialized=0
        AND COALESCE(m.product_kind,'GOODS')='GOODS'
    """
    eligible = db.execute(eligible_query).fetchall()
    assert len(eligible) == 0, len(eligible)
    # PO must use master products that an operator has explicitly created.
    db.execute("INSERT INTO products(group_id,sku,name,is_serialized) VALUES(1,'DX00001','Camera thử',0)")
    db.execute("INSERT INTO product_catalog_metadata(product_id,product_kind,unit,source_tag) VALUES((SELECT id FROM products WHERE sku='DX00001'),'GOODS','Cái','MANUAL')")
    eligible = db.execute(eligible_query).fetchall()
    assert len(eligible) == 1, eligible

    db.execute("INSERT INTO suppliers(code,name,phone) VALUES('NCC-T01','NCC thử nghiệm','0900000000')")
    sid = db.execute("SELECT id FROM suppliers WHERE code='NCC-T01'").fetchone()[0]
    wid = db.execute("SELECT id FROM warehouses WHERE code='SG'").fetchone()[0]
    db.execute("INSERT OR IGNORE INTO warehouse_locations(warehouse_id,code,name) VALUES(?, 'MAIN','Mặc định')", (wid,))
    lid = db.execute("SELECT id FROM warehouse_locations WHERE warehouse_id=? AND code='MAIN'", (wid,)).fetchone()[0]
    pid = eligible[0][0]

    db.commit()
    db.execute('BEGIN IMMEDIATE')
    db.execute("""INSERT INTO purchase_orders(po_no,supplier_id,warehouse_id,location_id,note,created_by)
      VALUES('PO-SELECT-01',?,?,?,?,1)""", (sid,wid,lid,'Kiểm thử NCC'))
    poid = db.execute("SELECT id FROM purchase_orders WHERE po_no='PO-SELECT-01'").fetchone()[0]
    db.execute("INSERT INTO purchase_order_lines(po_id,product_id,quantity,unit_cost_vnd) VALUES(?,?,?,?)", (poid,pid,5,120000))
    db.execute('COMMIT')
    assert db.execute('SELECT status FROM purchase_orders WHERE id=?',(poid,)).fetchone()[0]=='DRAFT'
    # Simulate receiving all units once, as the app's ReceivePurchase does.
    db.execute('BEGIN IMMEDIATE')
    db.execute("""INSERT INTO quantity_movements(document_no,product_id,warehouse_id,location_id,delta_qty,
      movement_type,ref_type,ref_id,note,actor_user_id) VALUES('Q-SELECT-01',?,?,?,5,'PURCHASE_RECEIVE','PURCHASE',?,'Nhận PO',1)""", (pid,wid,lid,poid))
    db.execute("INSERT INTO goods_receipts(receipt_no,po_id,received_by) VALUES('GR-SELECT-01',?,1)",(poid,))
    db.execute("UPDATE purchase_orders SET status='RECEIVED' WHERE id=?", (poid,))
    db.execute('COMMIT')
    assert db.execute('SELECT SUM(delta_qty) FROM quantity_movements WHERE product_id=? AND warehouse_id=? AND location_id=?',(pid,wid,lid)).fetchone()[0]==5
    assert db.execute('SELECT COUNT(*) FROM goods_receipts WHERE po_id=?',(poid,)).fetchone()[0]==1
    assert db.execute('PRAGMA foreign_key_check').fetchall()==[]
    assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
    print('PASS: empty supplier catalog identified, 1 explicitly created GOODS, create NCC -> PO -> receive 5 -> stock 5; FK OK')

if __name__ == '__main__':
    test_purchase_flow()
