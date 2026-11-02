import sqlite3,tempfile,pathlib,json
root=pathlib.Path('/mnt/data/TNM_V7/audit_work/TNM.Management/Data')
p=tempfile.NamedTemporaryFile(suffix='.db',delete=False);p.close()
c=sqlite3.connect(p.name)
c.execute('pragma foreign_keys=on')
for f in ['schema.sql','v6_schema.sql','v7_customer_groups.sql']:
 c.executescript((root/f).read_text())
c.create_function('tnm_actor',0,lambda:1)
# simplified stand-in for proposed C# trigger generation, equivalent SQL.
c.executescript('''
CREATE TABLE activity_events (id INTEGER PRIMARY KEY,table_name TEXT,record_key TEXT,operation TEXT,actor_user_id INTEGER,actor_username TEXT,occurred_at TEXT DEFAULT CURRENT_TIMESTAMP,before_json TEXT,after_json TEXT,source TEXT DEFAULT 'DB_TRIGGER');
CREATE TRIGGER activity_no_delete BEFORE DELETE ON activity_events BEGIN SELECT RAISE(ABORT,'AUDIT_APPEND_ONLY'); END;
CREATE TRIGGER activity_no_update BEFORE UPDATE ON activity_events BEGIN SELECT RAISE(ABORT,'AUDIT_APPEND_ONLY'); END;
''')
meta=['created_at','created_by','updated_at','updated_by','deleted_at','deleted_by']
secrets={'password_hash','security_stamp'}
def q(x):return '"'+x.replace('"','""')+'"'
def lit(x):return "'"+x.replace("'","''")+"'"
def cols(t):return [x[1] for x in c.execute(f'pragma table_info({q(t)})')]
def keys(t):
 v=sorted([(x[5],x[1]) for x in c.execute(f'pragma table_info({q(t)})') if x[5]>0]);return [i[1] for i in v] or ['rowid']
def js(alias,fields):return 'json_object('+','.join(f"{lit(f)},{lit('[REDACTED]') if f in secrets else alias+'.'+q(f)}" for f in fields)+')'
alltables=[x[0] for x in c.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name != 'activity_events'")]
for t in alltables:
 current=cols(t)
 for col in meta:
  if col not in current:c.execute(f'alter table {q(t)} add column {q(col)} '+('INTEGER' if col.endswith('_by') else 'TEXT'))
 if t=='audit_logs':continue
 if 'created_at' not in current:c.execute(f"UPDATE {q(t)} SET created_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE created_at IS NULL")
 tracked=[x for x in cols(t) if x not in meta]
 unchanged=' AND '.join(f'OLD.{q(x)} IS NEW.{q(x)}' for x in tracked) or '1'
 changed=f'NOT ({unchanged}) OR NOT (OLD.deleted_at IS NEW.deleted_at)'
 a='NULLIF(tnm_actor(),0)'; user=f'(SELECT username FROM app_users WHERE id={a})'
 newkey=js('NEW',keys(t));old=js('OLD',tracked);snap=f'(SELECT {js("snap",tracked)} FROM {q(t)} AS snap WHERE snap.rowid=NEW.rowid)'
 c.executescript(f'''
 CREATE TRIGGER {q('tnm_audit_'+t+'_insert')} AFTER INSERT ON {q(t)} BEGIN
 UPDATE {q(t)} SET created_at=COALESCE(created_at,strftime('%Y-%m-%dT%H:%M:%fZ','now')),created_by=COALESCE(created_by,{a}) WHERE rowid=NEW.rowid;
 INSERT INTO activity_events(table_name,record_key,operation,actor_user_id,actor_username,before_json,after_json) VALUES({lit(t)},{newkey},'CREATE',{a},{user},NULL,{snap});
 END;
 CREATE TRIGGER {q('tnm_audit_'+t+'_update')} AFTER UPDATE ON {q(t)} WHEN {changed} BEGIN
 UPDATE {q(t)} SET updated_at=strftime('%Y-%m-%dT%H:%M:%fZ','now'),updated_by={a},deleted_by=CASE WHEN OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL THEN {a} WHEN OLD.deleted_at IS NOT NULL AND NEW.deleted_at IS NULL THEN NULL ELSE deleted_by END WHERE rowid=NEW.rowid;
 INSERT INTO activity_events(table_name,record_key,operation,actor_user_id,actor_username,before_json,after_json) VALUES({lit(t)},{newkey},CASE WHEN OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL THEN 'DELETE' WHEN OLD.deleted_at IS NOT NULL AND NEW.deleted_at IS NULL THEN 'RESTORE' ELSE 'UPDATE' END,{a},{user},{old},{snap});
 END;
 CREATE TRIGGER {q('tnm_audit_'+t+'_no_hard_delete')} BEFORE DELETE ON {q(t)} BEGIN SELECT RAISE(ABORT,'SOFT_DELETE_ONLY'); END;
 ''')
try:
 c.execute("INSERT INTO app_users(id,username,display_name,password_hash) VALUES(1,'u1','User 1','hidden-hash')")
 c.execute("INSERT INTO product_groups(code,name) VALUES('CAMERA','Camera')")
 c.execute("INSERT INTO products(group_id,sku,name) VALUES(1,'DX00001','Camera A')")
 c.execute("UPDATE products SET name='Camera B' WHERE id=1")
 c.execute("UPDATE products SET deleted_at=CURRENT_TIMESTAMP WHERE id=1")
 c.execute("UPDATE products SET deleted_at=NULL WHERE id=1")
 c.execute("INSERT INTO roles(id,code,name) VALUES(1,'ADMIN','Admin')")
 c.execute("INSERT INTO user_roles(user_id,role_id) VALUES(1,1)")
 c.execute("UPDATE user_roles SET deleted_at=CURRENT_TIMESTAMP WHERE user_id=1")
 c.execute("INSERT INTO user_roles(user_id,role_id) VALUES(1,1) ON CONFLICT(user_id,role_id) DO UPDATE SET deleted_at=NULL,deleted_by=NULL")
 c.commit()
 out=list(c.execute("select table_name,operation,record_key,actor_user_id from activity_events where table_name in ('products','user_roles') order by id"))
 print('events:',out)
 assert [x[1] for x in out if x[0]=='products']==['CREATE','UPDATE','DELETE','RESTORE']
 assert [x[1] for x in out if x[0]=='user_roles']==['CREATE','DELETE','RESTORE']
 assert c.execute('SELECT name,created_by,updated_by,deleted_at FROM products WHERE id=1').fetchone()[:3]==('Camera B',1,1)
 assert c.execute("SELECT json_extract(after_json,'$.password_hash') FROM activity_events WHERE table_name='app_users' LIMIT 1").fetchone()[0]=='[REDACTED]'
 for sql in ['DELETE FROM products WHERE id=1','UPDATE activity_events SET operation=\'DELETE\' WHERE id=1','DELETE FROM activity_events WHERE id=1']:
  try:c.execute(sql);raise AssertionError('Did not block: '+sql)
  except sqlite3.DatabaseError as e:print('blocked:',e)
 print('AUDIT_SQLITE_SMOKE PASS', len(alltables),'tables')
except Exception:
 import traceback;traceback.print_exc();raise
finally:
 c.close()
