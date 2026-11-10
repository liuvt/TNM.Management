PRAGMA foreign_keys = ON;

-- =============================================================================
-- 1. QUẢN TRỊ TÀI KHOẢN, VAI TRÒ & PHẠM VI KHO
-- =============================================================================
CREATE TABLE IF NOT EXISTS employees(
  id INTEGER PRIMARY KEY,
  code TEXT NOT NULL UNIQUE,
  full_name TEXT NOT NULL,
  department TEXT NOT NULL,
  phone TEXT,
  is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS app_users(
  id INTEGER PRIMARY KEY,
  username TEXT NOT NULL UNIQUE,
  display_name TEXT NOT NULL,
  password_hash TEXT,
  employee_id INTEGER REFERENCES employees(id),
  is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1)),
  is_owner INTEGER NOT NULL DEFAULT 0 CHECK(is_owner IN (0,1)),
  must_change_password INTEGER NOT NULL DEFAULT 0 CHECK(must_change_password IN (0,1))
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_users_username_nocase ON app_users(username COLLATE NOCASE);
CREATE UNIQUE INDEX IF NOT EXISTS ux_tnm_single_owner ON app_users(is_owner) WHERE is_owner=1;

CREATE TABLE IF NOT EXISTS roles(
  id INTEGER PRIMARY KEY,
  code TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS user_roles(
  user_id INTEGER NOT NULL REFERENCES app_users(id),
  role_id INTEGER NOT NULL REFERENCES roles(id),
  PRIMARY KEY(user_id, role_id)
);

CREATE TABLE IF NOT EXISTS user_security(
  user_id INTEGER PRIMARY KEY REFERENCES app_users(id),
  security_stamp TEXT NOT NULL,
  failed_attempts INTEGER NOT NULL DEFAULT 0,
  locked_until INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS role_permissions(
  role_id INTEGER NOT NULL REFERENCES roles(id),
  permission_key TEXT NOT NULL,
  PRIMARY KEY(role_id, permission_key)
);

CREATE TABLE IF NOT EXISTS role_defaults_initialized(
  role_id INTEGER PRIMARY KEY REFERENCES roles(id)
);

CREATE TABLE IF NOT EXISTS user_permission_overrides(
  user_id INTEGER NOT NULL REFERENCES app_users(id) ON DELETE CASCADE,
  permission_key TEXT NOT NULL,
  effect TEXT NOT NULL CHECK(effect IN ('ALLOW','DENY')),
  PRIMARY KEY(user_id, permission_key)
);
CREATE INDEX IF NOT EXISTS ix_user_permission_overrides_effect ON user_permission_overrides(permission_key, effect);

CREATE TABLE IF NOT EXISTS user_scope_settings(
  user_id INTEGER PRIMARY KEY REFERENCES app_users(id) ON DELETE CASCADE,
  warehouse_mode TEXT NOT NULL DEFAULT 'ALL' CHECK(warehouse_mode IN ('ALL','SELECTED'))
);

-- =============================================================================
-- 2. DANH MỤC ĐẦU VÀO DÙNG CHUNG & BẢNG GIÁ V7
-- =============================================================================
CREATE TABLE IF NOT EXISTS supplier_groups(
  id INTEGER PRIMARY KEY,
  code TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS suppliers(
  id INTEGER PRIMARY KEY,
  code TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  group_id INTEGER REFERENCES supplier_groups(id),
  phone TEXT,
  tax_code TEXT
);

CREATE TABLE IF NOT EXISTS product_groups(
  id INTEGER PRIMARY KEY,
  code TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS products(
  id INTEGER PRIMARY KEY,
  group_id INTEGER NOT NULL REFERENCES product_groups(id),
  sku TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  base_cost_vnd INTEGER NOT NULL DEFAULT 0 CHECK(base_cost_vnd>=0),
  renewal_fee_vnd INTEGER NOT NULL DEFAULT 0 CHECK(renewal_fee_vnd>=0),
  install_fee_vnd INTEGER NOT NULL DEFAULT 0 CHECK(install_fee_vnd>=0),
  technical_fee_vnd INTEGER NOT NULL DEFAULT 0 CHECK(technical_fee_vnd>=0),
  other_fee_vnd INTEGER NOT NULL DEFAULT 0 CHECK(other_fee_vnd>=0),
  warranty_months INTEGER NOT NULL DEFAULT 12 CHECK(warranty_months>=0),
  is_serialized INTEGER NOT NULL DEFAULT 1 CHECK(is_serialized IN (0,1)),
  is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1))
);
CREATE INDEX IF NOT EXISTS ix_products_group_active ON products(group_id, is_active);

CREATE TABLE IF NOT EXISTS product_catalog_metadata(
  product_id INTEGER PRIMARY KEY REFERENCES products(id) ON DELETE CASCADE,
  product_kind TEXT NOT NULL CHECK(product_kind IN ('GOODS','SERVICE','BUNDLE')),
  unit TEXT NOT NULL DEFAULT 'Cái',
  source_tag TEXT NOT NULL DEFAULT 'MANUAL'
);

CREATE TABLE IF NOT EXISTS v7_sku_sequences(
  prefix TEXT PRIMARY KEY,
  last_number INTEGER NOT NULL CHECK(last_number>=0)
);

CREATE TABLE IF NOT EXISTS v7_sellers(
  id INTEGER PRIMARY KEY,
  code TEXT NOT NULL UNIQUE,
  full_name TEXT NOT NULL,
  phone TEXT NOT NULL DEFAULT '',
  department TEXT NOT NULL DEFAULT 'Kinh doanh / Sale',
  order_commission_vnd INTEGER NOT NULL DEFAULT 0 CHECK(order_commission_vnd>=0),
  renewal_commission_vnd INTEGER NOT NULL DEFAULT 0 CHECK(renewal_commission_vnd>=0),
  notes TEXT NOT NULL DEFAULT '',
  is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS customer_groups(
  id INTEGER PRIMARY KEY,
  code TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS customers(
  id INTEGER PRIMARY KEY,
  group_id INTEGER NOT NULL REFERENCES customer_groups(id),
  code TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  representative_name TEXT NOT NULL DEFAULT '',
  seller_id INTEGER REFERENCES v7_sellers(id),
  source_type TEXT NOT NULL DEFAULT 'SALE' CHECK(source_type IN ('SALE','AGENCY','DIRECT')),
  source_name TEXT NOT NULL DEFAULT '',
  phone TEXT NOT NULL DEFAULT '',
  address TEXT NOT NULL DEFAULT '',
  is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS warehouse_areas(
  id INTEGER PRIMARY KEY,
  code TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS warehouses(
  id INTEGER PRIMARY KEY,
  code TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  area_id INTEGER REFERENCES warehouse_areas(id),
  province TEXT NOT NULL DEFAULT '',
  address TEXT
);
CREATE INDEX IF NOT EXISTS ix_input_warehouse_area ON warehouses(area_id);

CREATE TABLE IF NOT EXISTS warehouse_locations(
  id INTEGER PRIMARY KEY,
  warehouse_id INTEGER NOT NULL REFERENCES warehouses(id),
  code TEXT NOT NULL,
  name TEXT NOT NULL,
  is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1)),
  UNIQUE(warehouse_id, code),
  UNIQUE(id, warehouse_id)
);

CREATE TABLE IF NOT EXISTS user_warehouse_scopes(
  user_id INTEGER NOT NULL REFERENCES app_users(id) ON DELETE CASCADE,
  warehouse_id INTEGER NOT NULL REFERENCES warehouses(id),
  PRIMARY KEY(user_id, warehouse_id)
);

CREATE TABLE IF NOT EXISTS v7_supplier_group_prices(
  id INTEGER PRIMARY KEY,
  supplier_group_id INTEGER NOT NULL REFERENCES supplier_groups(id),
  product_id INTEGER NOT NULL REFERENCES products(id),
  unit_cost_vnd INTEGER NOT NULL CHECK(unit_cost_vnd>=0),
  effective_from TEXT NOT NULL DEFAULT (date('now')),
  effective_to TEXT,
  CHECK(effective_to IS NULL OR effective_to>=effective_from),
  UNIQUE(supplier_group_id, product_id, effective_from)
);
CREATE INDEX IF NOT EXISTS ix_supplier_prices ON v7_supplier_group_prices(supplier_group_id, product_id, effective_from);

CREATE TABLE IF NOT EXISTS v7_group_prices(
  id INTEGER PRIMARY KEY,
  group_id INTEGER NOT NULL REFERENCES customer_groups(id),
  product_id INTEGER NOT NULL REFERENCES products(id),
  standard_price_vnd INTEGER NOT NULL CHECK(standard_price_vnd>=0),
  installed_price_vnd INTEGER NOT NULL CHECK(installed_price_vnd>=0),
  renewal_price_vnd INTEGER NOT NULL CHECK(renewal_price_vnd>=0),
  effective_from TEXT NOT NULL DEFAULT (date('now')),
  effective_to TEXT,
  CHECK(effective_to IS NULL OR effective_to>=effective_from),
  UNIQUE(group_id, product_id, effective_from)
);
CREATE INDEX IF NOT EXISTS ix_v7_group_prices ON v7_group_prices(group_id, product_id, effective_from);

CREATE TABLE IF NOT EXISTS v7_group_campaigns(
  id INTEGER PRIMARY KEY,
  group_id INTEGER NOT NULL REFERENCES customer_groups(id),
  name TEXT NOT NULL,
  description TEXT NOT NULL DEFAULT '',
  discount_percent INTEGER NOT NULL DEFAULT 0 CHECK(discount_percent BETWEEN 0 AND 100),
  start_date TEXT NOT NULL,
  end_date TEXT NOT NULL,
  is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1)),
  CHECK(end_date>=start_date)
);
CREATE INDEX IF NOT EXISTS ix_v7_campaigns ON v7_group_campaigns(group_id, start_date, end_date);

CREATE TABLE IF NOT EXISTS v7_installer_product_rates(
  id INTEGER PRIMARY KEY,
  employee_id INTEGER NOT NULL REFERENCES employees(id),
  product_id INTEGER NOT NULL REFERENCES products(id),
  install_rate_vnd INTEGER NOT NULL DEFAULT 0 CHECK(install_rate_vnd>=0),
  effective_from TEXT NOT NULL DEFAULT (date('now')),
  UNIQUE(employee_id, product_id)
);
CREATE INDEX IF NOT EXISTS ix_installer_rates ON v7_installer_product_rates(employee_id, product_id);

-- =============================================================================
-- 3. TỒN KHO SỐ LƯỢNG, MUA HÀNG NCC (PO) & KIỂM KÊ SỐ LƯỢNG
-- =============================================================================
CREATE TABLE IF NOT EXISTS quantity_movements(
  id INTEGER PRIMARY KEY,
  document_no TEXT NOT NULL UNIQUE,
  product_id INTEGER NOT NULL REFERENCES products(id),
  warehouse_id INTEGER NOT NULL REFERENCES warehouses(id),
  location_id INTEGER NOT NULL,
  delta_qty INTEGER NOT NULL CHECK(delta_qty<>0),
  movement_type TEXT NOT NULL CHECK(movement_type IN ('OPENING','RECEIVE','ISSUE','TRANSFER_IN','TRANSFER_OUT','ADJUST','PURCHASE_RECEIVE')),
  ref_type TEXT,
  ref_id INTEGER,
  note TEXT NOT NULL DEFAULT '',
  actor_user_id INTEGER REFERENCES app_users(id),
  occurred_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
  FOREIGN KEY(location_id, warehouse_id) REFERENCES warehouse_locations(id, warehouse_id)
);
CREATE INDEX IF NOT EXISTS idx_qmov_balance ON quantity_movements(product_id, warehouse_id, location_id);
CREATE INDEX IF NOT EXISTS idx_qmov_time ON quantity_movements(occurred_at);

CREATE TABLE IF NOT EXISTS quantity_counts(
  id INTEGER PRIMARY KEY,
  count_no TEXT NOT NULL UNIQUE,
  warehouse_id INTEGER NOT NULL REFERENCES warehouses(id),
  location_id INTEGER NOT NULL,
  status TEXT NOT NULL DEFAULT 'DRAFT' CHECK(status IN ('DRAFT','APPROVED','CANCELLED')),
  note TEXT NOT NULL DEFAULT '',
  created_by INTEGER REFERENCES app_users(id),
  approved_by INTEGER REFERENCES app_users(id),
  created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
  approved_at TEXT,
  FOREIGN KEY(location_id, warehouse_id) REFERENCES warehouse_locations(id, warehouse_id)
);

CREATE TABLE IF NOT EXISTS quantity_count_lines(
  id INTEGER PRIMARY KEY,
  count_id INTEGER NOT NULL REFERENCES quantity_counts(id) ON DELETE CASCADE,
  product_id INTEGER NOT NULL REFERENCES products(id),
  book_qty INTEGER NOT NULL CHECK(book_qty>=0),
  counted_qty INTEGER NOT NULL CHECK(counted_qty>=0),
  UNIQUE(count_id, product_id)
);

CREATE TABLE IF NOT EXISTS purchase_orders(
  id INTEGER PRIMARY KEY,
  po_no TEXT NOT NULL UNIQUE,
  supplier_id INTEGER NOT NULL REFERENCES suppliers(id),
  warehouse_id INTEGER NOT NULL REFERENCES warehouses(id),
  location_id INTEGER NOT NULL,
  status TEXT NOT NULL DEFAULT 'DRAFT' CHECK(status IN ('DRAFT','RECEIVED','CANCELLED')),
  note TEXT NOT NULL DEFAULT '',
  created_by INTEGER REFERENCES app_users(id),
  created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
  received_at TEXT,
  FOREIGN KEY(location_id, warehouse_id) REFERENCES warehouse_locations(id, warehouse_id)
);

CREATE TABLE IF NOT EXISTS purchase_order_lines(
  id INTEGER PRIMARY KEY,
  po_id INTEGER NOT NULL REFERENCES purchase_orders(id) ON DELETE CASCADE,
  product_id INTEGER NOT NULL REFERENCES products(id),
  quantity INTEGER NOT NULL CHECK(quantity>0),
  unit_cost_vnd INTEGER NOT NULL CHECK(unit_cost_vnd>=0)
);

CREATE TABLE IF NOT EXISTS goods_receipts(
  id INTEGER PRIMARY KEY,
  receipt_no TEXT NOT NULL UNIQUE,
  po_id INTEGER NOT NULL UNIQUE REFERENCES purchase_orders(id),
  received_by INTEGER REFERENCES app_users(id),
  received_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- =============================================================================
-- 4. TỒN KHO IMEI & BẢO HÀNH RMA
-- =============================================================================
CREATE TABLE IF NOT EXISTS stock_units(
  id INTEGER PRIMARY KEY,
  product_id INTEGER NOT NULL REFERENCES products(id),
  imei TEXT NOT NULL UNIQUE,
  supplier_id INTEGER REFERENCES suppliers(id),
  status TEXT NOT NULL CHECK(status IN ('IN_STOCK','CONSIGNED','TECHNICIAN','INSTALLED','WARRANTY','SUPPLIER')),
  warehouse_id INTEGER REFERENCES warehouses(id),
  customer_id INTEGER REFERENCES customers(id),
  employee_id INTEGER REFERENCES employees(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  updated_at TEXT NOT NULL DEFAULT (datetime('now')),
  CHECK(
    (status='IN_STOCK' AND warehouse_id IS NOT NULL AND customer_id IS NULL AND employee_id IS NULL) OR
    (status='CONSIGNED' AND customer_id IS NOT NULL AND warehouse_id IS NULL AND employee_id IS NULL) OR
    (status='TECHNICIAN' AND employee_id IS NOT NULL AND warehouse_id IS NULL AND customer_id IS NULL) OR
    (status IN ('INSTALLED','WARRANTY','SUPPLIER') AND warehouse_id IS NULL AND employee_id IS NULL)
  )
);
CREATE INDEX IF NOT EXISTS ix_stock_units_product_status ON stock_units(product_id, status);
CREATE INDEX IF NOT EXISTS ix_stock_units_customer_status ON stock_units(customer_id, status);

CREATE TABLE IF NOT EXISTS inventory_transactions(
  id INTEGER PRIMARY KEY,
  code TEXT NOT NULL UNIQUE,
  movement_type TEXT NOT NULL CHECK(movement_type IN ('RECEIVE','CONSIGN_OUT','CONSIGN_RETURN','TECH_OUT','TECH_RETURN','SALE_OUT','WARRANTY_OUT','WARRANTY_RETURN')),
  warehouse_id INTEGER REFERENCES warehouses(id),
  customer_id INTEGER REFERENCES customers(id),
  employee_id INTEGER REFERENCES employees(id),
  notes TEXT NOT NULL DEFAULT '',
  actor_user_id INTEGER REFERENCES app_users(id),
  occurred_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_trans_at ON inventory_transactions(occurred_at);
CREATE INDEX IF NOT EXISTS ix_trans_customer ON inventory_transactions(customer_id);

CREATE TABLE IF NOT EXISTS inventory_transaction_lines(
  id INTEGER PRIMARY KEY,
  transaction_id INTEGER NOT NULL REFERENCES inventory_transactions(id),
  stock_unit_id INTEGER NOT NULL REFERENCES stock_units(id),
  product_id INTEGER NOT NULL REFERENCES products(id),
  unit_cost_snapshot_vnd INTEGER NOT NULL DEFAULT 0 CHECK(unit_cost_snapshot_vnd>=0),
  UNIQUE(transaction_id, stock_unit_id)
);

CREATE TABLE IF NOT EXISTS warranty_cases(
  id INTEGER PRIMARY KEY,
  stock_unit_id INTEGER NOT NULL REFERENCES stock_units(id),
  customer_id INTEGER REFERENCES customers(id),
  received_at TEXT NOT NULL,
  sent_supplier_at TEXT,
  completed_at TEXT,
  status TEXT NOT NULL CHECK(status IN ('RECEIVED','SENT_SUPPLIER','COMPLETED','CANCELLED')),
  problem TEXT NOT NULL DEFAULT '',
  note TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS warranty_events(
  id INTEGER PRIMARY KEY,
  case_id INTEGER NOT NULL REFERENCES warranty_cases(id),
  old_status TEXT,
  new_status TEXT NOT NULL,
  changed_at TEXT NOT NULL DEFAULT (datetime('now')),
  user_id INTEGER REFERENCES app_users(id),
  note TEXT NOT NULL DEFAULT ''
);

-- =============================================================================
-- 5. ĐƠN HÀNG & ĐIỀU PHỐI LẮP ĐẶT V7, KÍCH HOẠT & GIA HẠN THUÊ BAO
-- =============================================================================
CREATE TABLE IF NOT EXISTS v7_orders(
  id INTEGER PRIMARY KEY,
  code TEXT UNIQUE NOT NULL,
  customer_id INTEGER NOT NULL REFERENCES customers(id),
  seller_id INTEGER NOT NULL REFERENCES app_users(id),
  seller_record_id INTEGER REFERENCES v7_sellers(id),
  product_id INTEGER NOT NULL REFERENCES products(id),
  quantity INTEGER NOT NULL DEFAULT 1 CHECK(quantity>0),
  unit_price_vnd INTEGER NOT NULL DEFAULT 0 CHECK(unit_price_vnd>=0),
  unit_cost_snapshot_vnd INTEGER NOT NULL DEFAULT 0 CHECK(unit_cost_snapshot_vnd>=0),
  source TEXT NOT NULL DEFAULT 'INTERNAL' CHECK(source IN ('INTERNAL','PARTNER')),
  status TEXT NOT NULL DEFAULT 'PENDING' CHECK(status IN ('PENDING','APPROVED','DISPATCHED','REPORTED','COMPLETED','CANCELLED')),
  warehouse_id INTEGER REFERENCES warehouses(id),
  installer TEXT,
  installer_employee_id INTEGER REFERENCES employees(id),
  installer_fee_vnd INTEGER NOT NULL DEFAULT 0 CHECK(installer_fee_vnd>=0),
  contact_name TEXT NOT NULL DEFAULT '',
  customer_phone TEXT NOT NULL DEFAULT '',
  sale_source TEXT NOT NULL DEFAULT '',
  duration_months INTEGER NOT NULL DEFAULT 12 CHECK(duration_months>0),
  deduct_stock INTEGER NOT NULL DEFAULT 1 CHECK(deduct_stock IN (0,1)),
  collect_on_delivery INTEGER NOT NULL DEFAULT 0,
  install_address TEXT NOT NULL DEFAULT '',
  install_at TEXT,
  plate_no TEXT NOT NULL DEFAULT '',
  vin_no TEXT NOT NULL DEFAULT '',
  notes TEXT NOT NULL DEFAULT '',
  imei TEXT,
  activated_at TEXT,
  created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
  completed_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_v7_order_status ON v7_orders(status);
CREATE INDEX IF NOT EXISTS ix_v7_order_customer_status ON v7_orders(customer_id, status);

CREATE TABLE IF NOT EXISTS v7_order_lines(
  id INTEGER PRIMARY KEY,
  order_id INTEGER NOT NULL REFERENCES v7_orders(id) ON DELETE CASCADE,
  product_id INTEGER NOT NULL REFERENCES products(id),
  quantity INTEGER NOT NULL DEFAULT 1 CHECK(quantity>0),
  unit_price_vnd INTEGER NOT NULL DEFAULT 0 CHECK(unit_price_vnd>=0),
  unit_cost_snapshot_vnd INTEGER NOT NULL DEFAULT 0 CHECK(unit_cost_snapshot_vnd>=0),
  installer_fee_vnd INTEGER NOT NULL DEFAULT 0 CHECK(installer_fee_vnd>=0),
  imei TEXT,
  plate_no TEXT NOT NULL DEFAULT '',
  vin_no TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ix_v7_order_lines_order ON v7_order_lines(order_id);

CREATE TABLE IF NOT EXISTS v7_order_events(
  id INTEGER PRIMARY KEY,
  order_id INTEGER NOT NULL REFERENCES v7_orders(id),
  actor_id INTEGER NOT NULL REFERENCES app_users(id),
  event TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS v7_stock_effects(
  id INTEGER PRIMARY KEY,
  order_id INTEGER NOT NULL REFERENCES v7_orders(id),
  stock_unit_id INTEGER NOT NULL UNIQUE REFERENCES stock_units(id),
  created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS activations(
  id INTEGER PRIMARY KEY,
  v7_order_id INTEGER REFERENCES v7_orders(id),
  stock_unit_id INTEGER NOT NULL UNIQUE REFERENCES stock_units(id),
  customer_id INTEGER NOT NULL REFERENCES customers(id),
  product_id INTEGER NOT NULL REFERENCES products(id),
  plate_no TEXT NOT NULL DEFAULT '',
  vin_no TEXT NOT NULL DEFAULT '',
  activated_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'ACTIVE' CHECK(status IN ('ACTIVE','EXPIRED','DISABLED'))
);
CREATE INDEX IF NOT EXISTS ix_activations_expiry ON activations(expires_at, status);

CREATE TABLE IF NOT EXISTS renewals(
  id INTEGER PRIMARY KEY,
  activation_id INTEGER NOT NULL REFERENCES activations(id),
  seller_id INTEGER REFERENCES v7_sellers(id),
  seller_name TEXT NOT NULL DEFAULT '',
  source_type TEXT NOT NULL CHECK(source_type IN ('NCC','TNM','HTX','B2C')),
  renewed_at TEXT NOT NULL,
  previous_expiry TEXT NOT NULL,
  new_expiry TEXT NOT NULL,
  amount_vnd INTEGER NOT NULL CHECK(amount_vnd>=0),
  payment_method TEXT NOT NULL CHECK(payment_method IN ('DEBT','CASH')),
  is_locked INTEGER NOT NULL DEFAULT 0 CHECK(is_locked IN (0,1)),
  locked_at TEXT,
  locked_by INTEGER REFERENCES app_users(id),
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_renewals_activation_at ON renewals(activation_id, renewed_at);

-- =============================================================================
-- 6. CÔNG NỢ, HÓA ĐƠN & THANH TOÁN
-- =============================================================================
CREATE TABLE IF NOT EXISTS invoices(
  id INTEGER PRIMARY KEY,
  invoice_no TEXT NOT NULL UNIQUE,
  invoice_type TEXT NOT NULL CHECK(invoice_type IN ('SALE','RENEWAL','PURCHASE','SERVICE','COMMISSION')),
  customer_id INTEGER REFERENCES customers(id),
  supplier_id INTEGER REFERENCES suppliers(id),
  v7_order_id INTEGER REFERENCES v7_orders(id),
  renewal_id INTEGER REFERENCES renewals(id),
  issued_at TEXT NOT NULL DEFAULT (date('now')),
  due_at TEXT NOT NULL,
  amount_vnd INTEGER NOT NULL CHECK(amount_vnd>=0),
  status TEXT NOT NULL DEFAULT 'OPEN' CHECK(status IN ('OPEN','PARTIAL','PAID','VOID')),
  notes TEXT NOT NULL DEFAULT '',
  is_locked INTEGER NOT NULL DEFAULT 0 CHECK(is_locked IN (0,1)),
  locked_at TEXT,
  locked_by INTEGER REFERENCES app_users(id)
);
CREATE INDEX IF NOT EXISTS ix_invoices_type_status ON invoices(invoice_type, status);

CREATE TABLE IF NOT EXISTS invoice_lines(
  id INTEGER PRIMARY KEY,
  invoice_id INTEGER NOT NULL REFERENCES invoices(id),
  description TEXT NOT NULL,
  product_id INTEGER REFERENCES products(id),
  quantity INTEGER NOT NULL CHECK(quantity>0),
  unit_price_vnd INTEGER NOT NULL CHECK(unit_price_vnd>=0)
);

CREATE TABLE IF NOT EXISTS payments(
  id INTEGER PRIMARY KEY,
  payment_no TEXT NOT NULL UNIQUE,
  direction TEXT NOT NULL CHECK(direction IN ('IN','OUT')),
  paid_at TEXT NOT NULL DEFAULT (date('now')),
  method TEXT NOT NULL CHECK(method IN ('CASH','BANK')),
  amount_vnd INTEGER NOT NULL CHECK(amount_vnd>0),
  notes TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS payment_allocations(
  id INTEGER PRIMARY KEY,
  payment_id INTEGER NOT NULL REFERENCES payments(id),
  invoice_id INTEGER NOT NULL REFERENCES invoices(id),
  allocated_vnd INTEGER NOT NULL CHECK(allocated_vnd>0),
  UNIQUE(payment_id, invoice_id)
);

-- =============================================================================
-- 7. NHẬT KÝ HỆ THỐNG & KIỂM TOÁN (AUDIT TRAIL)
-- =============================================================================
CREATE TABLE IF NOT EXISTS audit_logs(
  id INTEGER PRIMARY KEY,
  occurred_at TEXT NOT NULL DEFAULT (datetime('now','localtime')),
  actor_user_id INTEGER REFERENCES app_users(id),
  action TEXT NOT NULL,
  entity TEXT NOT NULL,
  entity_id INTEGER,
  details TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ix_audit_entity ON audit_logs(entity, entity_id, occurred_at);

CREATE TABLE IF NOT EXISTS activity_events(
  id INTEGER PRIMARY KEY,
  table_name TEXT NOT NULL,
  record_key TEXT NOT NULL,
  operation TEXT NOT NULL CHECK(operation IN ('CREATE','UPDATE','DELETE','RESTORE')),
  actor_user_id INTEGER,
  actor_username TEXT,
  occurred_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  before_json TEXT,
  after_json TEXT,
  source TEXT NOT NULL DEFAULT 'DB_TRIGGER'
);
CREATE INDEX IF NOT EXISTS ix_activity_target ON activity_events(table_name, record_key, id DESC);
CREATE INDEX IF NOT EXISTS ix_activity_actor ON activity_events(actor_user_id, id DESC);
CREATE INDEX IF NOT EXISTS ix_activity_time ON activity_events(occurred_at, id DESC);

CREATE TABLE IF NOT EXISTS activity_actions(
  id INTEGER PRIMARY KEY,
  actor_user_id INTEGER,
  actor_username TEXT,
  action TEXT NOT NULL,
  resource TEXT NOT NULL,
  details TEXT NOT NULL DEFAULT '',
  occurred_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_activity_actions_at ON activity_actions(id DESC);

-- =============================================================================
-- 8. VIEWS & TRIGGERS BẢO VỆ TOÀN VẸN DỮ LIỆU
-- =============================================================================
CREATE VIEW IF NOT EXISTS v7_product_master AS
SELECT p.id, p.sku, p.name, p.group_id, g.name AS group_name,
       COALESCE(m.product_kind,'GOODS') AS product_kind,
       COALESCE(m.unit,'Cái') AS unit,
       p.is_serialized, p.warranty_months, p.is_active,
       COALESCE(m.source_tag,'MANUAL') AS source_tag
FROM products p
JOIN product_groups g ON g.id = p.group_id
LEFT JOIN product_catalog_metadata m ON m.product_id = p.id;

-- Bảo vệ tài khoản Owner
CREATE TRIGGER IF NOT EXISTS tnm_owner_protect_account
BEFORE UPDATE OF username, is_owner, is_active ON app_users
WHEN OLD.is_owner=1 AND (NEW.is_owner<>1 OR NEW.is_active<>1 OR NEW.username<>OLD.username)
BEGIN SELECT RAISE(ABORT,'Owner account cannot be deactivated or reassigned'); END;

CREATE TRIGGER IF NOT EXISTS tnm_owner_no_delete
BEFORE DELETE ON app_users WHEN OLD.is_owner=1
BEGIN SELECT RAISE(ABORT,'Owner account cannot be deleted'); END;

CREATE TRIGGER IF NOT EXISTS tnm_owner_no_remove_role
BEFORE DELETE ON user_roles
WHEN EXISTS(SELECT 1 FROM app_users WHERE id=OLD.user_id AND is_owner=1)
BEGIN SELECT RAISE(ABORT,'Cannot remove a role from Owner'); END;

CREATE TRIGGER IF NOT EXISTS tnm_owner_no_change_role
BEFORE UPDATE OF user_id, role_id ON user_roles
WHEN EXISTS(SELECT 1 FROM app_users WHERE id=OLD.user_id AND is_owner=1)
BEGIN SELECT RAISE(ABORT,'Cannot change an Owner role'); END;

CREATE TRIGGER IF NOT EXISTS tnm_owner_auto_new_role
AFTER INSERT ON roles
BEGIN INSERT OR IGNORE INTO user_roles(user_id, role_id)
  SELECT id, NEW.id FROM app_users WHERE is_owner=1; END;

-- Cho phép cập nhật mã SKU sản phẩm (hoặc tự sinh DX00001 nếu không nhập)
DROP TRIGGER IF EXISTS v7_product_sku_immutable;

CREATE TRIGGER IF NOT EXISTS v7_customer_group_required_insert
BEFORE INSERT ON customers
WHEN NEW.group_id IS NULL
BEGIN SELECT RAISE(ABORT,'Khách hàng bắt buộc thuộc một nhóm'); END;

CREATE TRIGGER IF NOT EXISTS v7_customer_group_required_update
BEFORE UPDATE OF group_id ON customers
WHEN NEW.group_id IS NULL
BEGIN SELECT RAISE(ABORT,'Khách hàng bắt buộc thuộc một nhóm'); END;

-- Bảo vệ khớp sản phẩm của IMEI
CREATE TRIGGER IF NOT EXISTS validate_inventory_product
BEFORE INSERT ON inventory_transaction_lines
BEGIN
  SELECT CASE WHEN (SELECT product_id FROM stock_units WHERE id=NEW.stock_unit_id)!=NEW.product_id
    THEN RAISE(ABORT,'Product mismatched for IMEI') END;
END;

CREATE TRIGGER IF NOT EXISTS validate_activation_product
BEFORE INSERT ON activations
BEGIN
  SELECT CASE WHEN (SELECT product_id FROM stock_units WHERE id=NEW.stock_unit_id)!=NEW.product_id
    THEN RAISE(ABORT,'IMEI does not match ordered product') END;
END;

-- Bảo vệ sổ cái số lượng bất biến và không âm tồn
CREATE TRIGGER IF NOT EXISTS trg_quantity_no_negative
BEFORE INSERT ON quantity_movements
WHEN NEW.delta_qty<0
BEGIN
  SELECT CASE WHEN COALESCE((SELECT SUM(delta_qty) FROM quantity_movements
      WHERE product_id=NEW.product_id AND warehouse_id=NEW.warehouse_id
        AND location_id=NEW.location_id),0)+NEW.delta_qty<0
      THEN RAISE(ABORT,'INSUFFICIENT_QUANTITY_STOCK') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_quantity_immutable_update
BEFORE UPDATE OF document_no, product_id, warehouse_id, location_id, delta_qty, movement_type, ref_type, ref_id, note, actor_user_id, occurred_at ON quantity_movements
BEGIN SELECT RAISE(ABORT,'APPEND_ONLY_QUANTITY_LEDGER'); END;

CREATE TRIGGER IF NOT EXISTS trg_quantity_immutable_delete
BEFORE DELETE ON quantity_movements
BEGIN SELECT RAISE(ABORT,'APPEND_ONLY_QUANTITY_LEDGER'); END;

-- Bảo vệ các bảng Audit chỉ thêm (Append-Only)
CREATE TRIGGER IF NOT EXISTS activity_actions_no_update BEFORE UPDATE ON activity_actions
BEGIN SELECT RAISE(ABORT,'AUDIT_APPEND_ONLY'); END;

CREATE TRIGGER IF NOT EXISTS activity_actions_no_delete BEFORE DELETE ON activity_actions
BEGIN SELECT RAISE(ABORT,'AUDIT_APPEND_ONLY'); END;

CREATE TRIGGER IF NOT EXISTS activity_no_update BEFORE UPDATE ON activity_events
BEGIN SELECT RAISE(ABORT,'AUDIT_APPEND_ONLY'); END;

CREATE TRIGGER IF NOT EXISTS activity_no_delete BEFORE DELETE ON activity_events
BEGIN SELECT RAISE(ABORT,'AUDIT_APPEND_ONLY'); END;

CREATE TRIGGER IF NOT EXISTS legacy_audit_no_update BEFORE UPDATE ON audit_logs
BEGIN SELECT RAISE(ABORT,'AUDIT_APPEND_ONLY'); END;

CREATE TRIGGER IF NOT EXISTS legacy_audit_no_delete BEFORE DELETE ON audit_logs
BEGIN SELECT RAISE(ABORT,'AUDIT_APPEND_ONLY'); END;

-- Khóa cố định đơn hàng đã hoàn thành (COMPLETED) & bảo vệ giá snapshot cố định
CREATE TRIGGER IF NOT EXISTS trg_v7_orders_lock_completed_update
BEFORE UPDATE OF code, customer_id, seller_id, seller_record_id, product_id, quantity, unit_price_vnd, unit_cost_snapshot_vnd, source, status, warehouse_id, installer, installer_employee_id, installer_fee_vnd, contact_name, customer_phone, sale_source, duration_months, deduct_stock, collect_on_delivery, install_address, install_at, plate_no, vin_no, notes, imei, activated_at, completed_at ON v7_orders
WHEN OLD.status = 'COMPLETED'
BEGIN
  SELECT RAISE(ABORT,'COMPLETED_ORDER_IMMUTABLE: Đơn hàng đã hoàn thành và khóa snapshot giá cố định, không thể thay đổi.');
END;

CREATE TRIGGER IF NOT EXISTS trg_v7_order_lines_lock_completed_update
BEFORE UPDATE OF order_id, product_id, quantity, unit_price_vnd, unit_cost_snapshot_vnd, installer_fee_vnd, imei, plate_no, vin_no ON v7_order_lines
WHEN (SELECT status FROM v7_orders WHERE id = OLD.order_id) = 'COMPLETED'
BEGIN
  SELECT RAISE(ABORT,'COMPLETED_ORDER_LINE_IMMUTABLE: Dòng sản phẩm của đơn hàng đã hoàn thành và khóa snapshot giá cố định, không thể thay đổi.');
END;

CREATE TRIGGER IF NOT EXISTS trg_v7_order_lines_lock_completed_insert
BEFORE INSERT ON v7_order_lines
WHEN (SELECT status FROM v7_orders WHERE id = NEW.order_id) = 'COMPLETED'
BEGIN
  SELECT RAISE(ABORT,'COMPLETED_ORDER_LINE_IMMUTABLE: Đơn hàng đã hoàn thành, không thể thêm dòng sản phẩm mới.');
END;
