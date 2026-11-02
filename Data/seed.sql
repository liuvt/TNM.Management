INSERT INTO roles(id,code,name) VALUES (1,'ADMIN','Quản trị'),(2,'MANAGER','Quản lý'),(3,'ACCOUNTANT','Kế toán'),(4,'WAREHOUSE','Kho'),(5,'CSKH','CSKH'),(6,'TECH','Kỹ thuật');
INSERT INTO employees(id,code,full_name,department,phone) VALUES
 (1,'NV001','Nguyễn Minh Anh','Kho','0901000001'),
 (2,'NV002','Trần Quốc Bảo','Kỹ thuật','0901000002'),
 (3,'NV003','Lê Thu Hà','CSKH','0901000003');
INSERT INTO app_users(id,username,display_name,employee_id,is_owner) VALUES (1,'owner','Chủ sở hữu hệ thống',1,1);
INSERT INTO user_roles(user_id,role_id) SELECT 1,id FROM roles;
INSERT INTO supplier_groups(id,code,name) VALUES (1,'NCC_TB','Nhà cung cấp thiết bị');
INSERT INTO suppliers(id,code,name,group_id,phone) VALUES (1,'NCC-01','Nhà cung cấp thiết bị mẫu',1,'0281111111');
INSERT INTO product_groups(id,code,name) VALUES
 (1,'TNM_DEVICE','Thiết bị TNM'),(2,'HOUSEHOLD','Hàng gia dụng TNM');
INSERT INTO products(id,group_id,sku,name,base_cost_vnd,renewal_fee_vnd,install_fee_vnd,technical_fee_vnd,warranty_months,is_serialized) VALUES
 (1,1,'DX00001','Thiết bị định vị GPS 4G',850000,350000,180000,60000,12,1),
 (2,1,'DX00002','Camera hành trình 2 kênh',1200000,450000,250000,100000,18,1),
 (3,2,'DX00003','Máy lọc không khí mini',650000,0,0,0,12,0);
INSERT INTO product_catalog_metadata(product_id,product_kind,unit,source_tag) VALUES
 (1,'GOODS','Cái','MANUAL'),(2,'GOODS','Bộ','MANUAL'),(3,'GOODS','Cái','MANUAL');
INSERT INTO v7_sku_sequences(prefix,last_number) VALUES ('DX',3);
INSERT INTO v7_supplier_group_prices(supplier_group_id,product_id,unit_cost_vnd,effective_from) VALUES
 (1,1,850000,'2026-01-01'),(1,2,1200000,'2026-01-01'),(1,3,650000,'2026-01-01');
INSERT INTO customer_groups(id,code,name) VALUES (1,'TTD','HTX Tân Thành Đô'),(2,'TDT','HTX Tân Đại Thành');
INSERT INTO customers(id,group_id,code,name,source_type,source_name,phone,address) VALUES
 (1,1,'KH-TTD-001','HTX Tân Thành Đô - CN1','AGENCY','HTX Tân Thành Đô','0903000001','TP. Hồ Chí Minh'),
 (2,2,'KH-TDT-002','HTX Tân Đại Thành - CN2','SALE','Sale nội bộ','0903000002','Kiên Giang');
INSERT INTO v7_group_prices(group_id,product_id,standard_price_vnd,installed_price_vnd,renewal_price_vnd,effective_from) VALUES
 (1,1,1250000,1450000,450000,'2026-01-01'),
 (2,1,1290000,1490000,490000,'2026-01-01'),
 (1,2,1790000,2090000,590000,'2026-01-01');
INSERT INTO v7_installer_product_rates(employee_id,product_id,install_rate_vnd,effective_from) VALUES
 (2,1,120000,'2026-01-01'),
 (2,2,150000,'2026-01-01');
INSERT INTO warehouse_areas(id,code,name) VALUES (1,'HCM','TP. Hồ Chí Minh'),(2,'KG','Kiên Giang');
INSERT INTO warehouses(id,code,name,area_id,province,address) VALUES
 (1,'KHO-HCM','Kho trung tâm HCM',1,'TP. Hồ Chí Minh','TP. Hồ Chí Minh'),
 (2,'KHO-PQ','Kho Phú Quốc',2,'Kiên Giang','Phú Quốc');
INSERT INTO stock_units(id,product_id,imei,supplier_id,status,warehouse_id,customer_id,employee_id) VALUES
 (1,1,'359111000000001',1,'INSTALLED',NULL,1,NULL),
 (2,1,'359111000000002',1,'INSTALLED',NULL,2,NULL),
 (3,1,'359111000000003',1,'IN_STOCK',1,NULL,NULL),
 (4,1,'359111000000004',1,'IN_STOCK',1,NULL,NULL),
 (5,1,'359111000000005',1,'IN_STOCK',2,NULL,NULL),
 (6,1,'359111000000006',1,'IN_STOCK',1,NULL,NULL),
 (7,1,'359111000000007',1,'CONSIGNED',NULL,1,NULL),
 (8,2,'869222000000001',1,'IN_STOCK',1,NULL,NULL),
 (9,2,'869222000000002',1,'TECHNICIAN',NULL,NULL,2),
 (10,2,'869222000000003',1,'WARRANTY',NULL,1,NULL);
INSERT INTO inventory_transactions(id,code,movement_type,warehouse_id,actor_user_id,notes,occurred_at) VALUES
 (1,'NK-SEED-001','RECEIVE',1,1,'Nhập kho ban đầu','2026-01-02 08:00:00');
INSERT INTO inventory_transaction_lines(transaction_id,stock_unit_id,product_id,unit_cost_snapshot_vnd)
 SELECT 1,id,product_id,CASE WHEN product_id=1 THEN 850000 ELSE 1200000 END FROM stock_units;
INSERT INTO inventory_transactions(id,code,movement_type,customer_id,actor_user_id,notes) VALUES
 (2,'XK-SEED-001','CONSIGN_OUT',1,1,'Xuất ký gửi');
INSERT INTO inventory_transaction_lines(transaction_id,stock_unit_id,product_id,unit_cost_snapshot_vnd) VALUES (2,7,1,850000);
INSERT INTO v7_orders(id,code,customer_id,seller_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd,source,status,warehouse_id,installer,installer_employee_id,installer_fee_vnd,customer_phone,sale_source,install_address,install_at,plate_no,vin_no,notes,imei,activated_at,completed_at) VALUES
 (1,'SO7-000001',1,1,1,1,1450000,850000,'INTERNAL','COMPLETED',1,'Trần Quốc Bảo',2,120000,'0903000001','HTX Tân Thành Đô','TP. Hồ Chí Minh',datetime('now','-50 days'),'51H-123.45','VIN-DEMO-01','Lắp thành công','359111000000001',datetime('now','-50 days'),datetime('now','-50 days')),
 (2,'SO7-000002',2,1,1,1,1490000,850000,'INTERNAL','COMPLETED',2,'Trần Quốc Bảo',2,120000,'0903000002','Sale nội bộ','Phú Quốc',datetime('now','-40 days'),'68H-155.19','VIN-DEMO-02','Lắp thành công','359111000000002',datetime('now','-40 days'),datetime('now','-40 days')),
 (3,'SO7-000003',1,1,2,1,2090000,1200000,'INTERNAL','PENDING',NULL,NULL,NULL,0,'0903000001','HTX Tân Thành Đô','TP. Hồ Chí Minh',datetime('now','+2 days'),'','','Đang chờ duyệt',NULL,NULL,NULL);
INSERT INTO v7_order_lines(order_id,product_id,quantity,unit_price_vnd,unit_cost_snapshot_vnd,installer_fee_vnd,imei,plate_no,vin_no) VALUES
 (1,1,1,1450000,850000,120000,'359111000000001','51H-123.45','VIN-DEMO-01'),
 (2,1,1,1490000,850000,120000,'359111000000002','68H-155.19','VIN-DEMO-02'),
 (3,2,1,2090000,1200000,0,NULL,'','');
INSERT INTO v7_stock_effects(order_id,stock_unit_id) VALUES (1,1),(2,2);
INSERT INTO activations(id,v7_order_id,stock_unit_id,customer_id,product_id,plate_no,vin_no,activated_at,expires_at) VALUES
 (1,1,1,1,1,'51H-123.45','VIN-DEMO-01',date('now','-350 days'),date('now','+15 days')),
 (2,2,2,2,1,'68H-155.19','VIN-DEMO-02',date('now','-300 days'),date('now','+80 days'));
INSERT INTO renewals(id,activation_id,source_type,renewed_at,previous_expiry,new_expiry,amount_vnd,payment_method) VALUES
 (1,2,'TNM',date('now','-45 days'),date('now','-45 days'),date('now','+80 days'),490000,'DEBT');
INSERT INTO warranty_cases(id,stock_unit_id,customer_id,received_at,sent_supplier_at,status,problem) VALUES
 (1,10,1,date('now','-4 days'),date('now','-3 days'),'SENT_SUPPLIER','Camera không lên nguồn');
INSERT INTO warranty_events(case_id,old_status,new_status,user_id,note) VALUES
 (1,NULL,'RECEIVED',1,'Đã tiếp nhận'),(1,'RECEIVED','SENT_SUPPLIER',1,'Gửi NCC');
INSERT INTO invoices(id,invoice_no,invoice_type,customer_id,v7_order_id,renewal_id,due_at,amount_vnd,status,notes) VALUES
 (1,'HD-2026-001','SALE',1,1,NULL,date('now','+30 days'),1450000,'PARTIAL','Thanh toán một phần'),
 (2,'HD-2026-002','SALE',2,2,NULL,date('now','+30 days'),1490000,'OPEN','Chưa thanh toán'),
 (3,'HD-2026-003','RENEWAL',2,NULL,1,date('now','+15 days'),490000,'OPEN','Công nợ gia hạn');
INSERT INTO invoice_lines(invoice_id,description,product_id,quantity,unit_price_vnd) VALUES
 (1,'GPS 4G bao gồm lắp đặt',1,1,1450000),
 (2,'GPS 4G bao gồm lắp đặt',1,1,1490000),
 (3,'Gia hạn GPS 4G',1,1,490000);
INSERT INTO payments(id,payment_no,direction,method,amount_vnd,notes) VALUES (1,'PT-2026-001','IN','BANK',800000,'Thu một phần đơn 1');
INSERT INTO payment_allocations(payment_id,invoice_id,allocated_vnd) VALUES (1,1,800000);
INSERT INTO audit_logs(actor_user_id,action,entity,entity_id,details) VALUES
 (1,'SEED','SYSTEM',1,'Khởi tạo dữ liệu demo TNM');
