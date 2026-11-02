#!/usr/bin/env python3
"""Kiểm tra SELECT danh sách sản phẩm InputCatalog trỏ đúng view có is_active."""
import sqlite3
from pathlib import Path
source=Path(__file__).resolve().parents[1]
cs=(source/'Services'/'InputCatalog.cs').read_text(encoding='utf-8')
sql='SELECT id,sku,name,group_id,group_name,is_active FROM v7_product_master WHERE is_active=1 ORDER BY id DESC'
assert sql in cs, 'InputCatalog.Products vẫn còn dùng SELECT sai schema'
con=sqlite3.connect(':memory:')
con.executescript("""CREATE TABLE products (id INTEGER PRIMARY KEY, sku TEXT, name TEXT, group_id INTEGER, is_serialized INTEGER, warranty_months INTEGER, is_active INTEGER);
CREATE TABLE product_groups (id INTEGER PRIMARY KEY, name TEXT);
CREATE TABLE product_catalog_metadata (product_id INTEGER PRIMARY KEY, product_kind TEXT, unit TEXT, source_tag TEXT);
INSERT INTO product_groups VALUES(1,'Camera');
INSERT INTO products VALUES(1,'DX00001','Camera AI',1,1,12,1);
INSERT INTO products VALUES(2,'X-OLD','Legacy',1,0,0,0);
""")
con.executescript((source/'Data'/'v7_product_master.sql').read_text(encoding='utf-8'))
rows=con.execute(sql).fetchall()
assert len(rows)==1 and rows[0][1]=='DX00001', rows
print('PASS: InputCatalog.Products query matches v7_product_master; only active rows returned')
