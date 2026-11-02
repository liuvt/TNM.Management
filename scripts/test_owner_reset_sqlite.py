"""SQLite-only smoke test for owner recovery transaction.

The real password hash is created by ASP.NET PasswordHasher<Account> in C#.
This test does not claim to compile or execute the .NET application.
"""
import sqlite3
from pathlib import Path
import tempfile

root=Path(__file__).resolve().parents[1]
for seed in ['seed_empty.sql','seed.sql']:
    with tempfile.TemporaryDirectory() as td:
        db=Path(td)/'tnm-demo.db'
        c=sqlite3.connect(db)
        c.execute('PRAGMA foreign_keys=ON')
        for file in ['schema.sql','v6_schema.sql',seed]:
            c.executescript((root/'Data'/file).read_text(encoding='utf-8'))
        # Security schema, mirroring the already-existing AuthRepository.Initialize().
        c.executescript('''
        CREATE TABLE IF NOT EXISTS user_security (
          user_id INTEGER PRIMARY KEY REFERENCES app_users(id),
          security_stamp TEXT NOT NULL,
          failed_attempts INTEGER NOT NULL DEFAULT 0,
          locked_until INTEGER NOT NULL DEFAULT 0
        );
        INSERT INTO user_security(user_id,security_stamp,failed_attempts,locked_until)
          SELECT id, 'oldstamp',5,9999999999 FROM app_users WHERE is_owner=1;
        UPDATE app_users SET password_hash='oldHash' WHERE is_owner=1;
        ''')
        owner_id=c.execute('SELECT id FROM app_users WHERE is_owner=1').fetchone()[0]
        before_business={name:c.execute(f'SELECT COUNT(*) FROM {name}').fetchone()[0]
                          for name in ['products','stock_units','sales_orders','invoices']}
        c.execute('BEGIN IMMEDIATE')
        c.execute('UPDATE app_users SET password_hash=? WHERE id=? AND is_owner=1',('newHash',owner_id))
        c.execute('''INSERT OR IGNORE INTO user_security(user_id,security_stamp,failed_attempts,locked_until)
                     VALUES(?,lower(hex(randomblob(32))),0,0)''',(owner_id,))
        c.execute('''UPDATE user_security SET security_stamp=lower(hex(randomblob(32))),
                     failed_attempts=0,locked_until=0 WHERE user_id=?''',(owner_id,))
        c.execute('''INSERT INTO audit_logs(actor_user_id,action,entity,entity_id,details)
                     VALUES(NULL,'RESET_PASSWORD_OFFLINE','APP_USER',?,
                     'Owner password reset using local server console')''',(owner_id,))
        c.commit()
        assert c.execute('SELECT password_hash FROM app_users WHERE id=?',(owner_id,)).fetchone()[0]=='newHash'
        stamp, attempts, until=c.execute('SELECT security_stamp,failed_attempts,locked_until FROM user_security WHERE user_id=?',(owner_id,)).fetchone()
        assert stamp!='oldstamp' and len(stamp)==64 and attempts==0 and until==0
        assert c.execute("SELECT count(*) FROM audit_logs WHERE action='RESET_PASSWORD_OFFLINE' AND entity_id=?",(owner_id,)).fetchone()[0]==1
        after_business={name:c.execute(f'SELECT COUNT(*) FROM {name}').fetchone()[0]
                        for name in before_business}
        assert before_business==after_business
        assert c.execute('PRAGMA foreign_key_check').fetchall()==[]
        print(f'PASS {seed}: reset hash + revoke cookies + unlock + audit, business tables unchanged')
        c.close()
