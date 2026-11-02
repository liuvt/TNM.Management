-- Minimal initial configuration only: NO sample inventory, transactions, orders, invoices or customers.
INSERT INTO roles(id,code,name) VALUES (1,'ADMIN','Quản trị'),(2,'MANAGER','Quản lý'),(3,'ACCOUNTANT','Kế toán'),(4,'WAREHOUSE','Kho'),(5,'CSKH','CSKH'),(6,'TECH','Kỹ thuật');
INSERT INTO app_users(id,username,display_name,is_owner) VALUES (1,'owner','Chủ sở hữu hệ thống',1);
INSERT INTO user_roles(user_id,role_id) SELECT 1,id FROM roles;
INSERT INTO supplier_groups(id,code,name) VALUES(1,'DEFAULT','Nhà cung cấp tiêu chuẩn');
INSERT INTO product_groups(id,code,name) VALUES(1,'GOODS','Hàng hóa'),(2,'SERVICE','Dịch vụ'),(3,'DEVICE','Thiết bị'),(4,'BUNDLE','Combo');
INSERT INTO customer_groups(id,code,name) VALUES(1,'RETAIL','Khách lẻ'),(2,'AGENCY','Đại lý / HTX');
INSERT INTO warehouse_areas(id,code,name) VALUES(1,'MIEN_NAM','Miền Nam');
INSERT INTO warehouses(id,code,name,area_id,province) VALUES(1,'SG','Kho Sài Gòn',1,'Miền Nam'),(2,'PQ','Kho Phú Quốc',1,'Miền Nam');
