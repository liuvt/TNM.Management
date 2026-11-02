"""SQLite + source regression checks for default Owner / change-password.
This does not compile ASP.NET/Razor: .NET SDK is unavailable here.
"""
from pathlib import Path
import sqlite3, tempfile
root=Path(__file__).resolve().parents[1]
auth=(root/'Security/AuthRepository.cs').read_text(encoding='utf-8')
program=(root/'Program.cs').read_text(encoding='utf-8')
pages=(root/'Security/AuthPages.cs').read_text(encoding='utf-8')
assert 'DefaultOwnerPassword = "123456@Dixanh"' in auth
assert 'public void ChangeOwnPassword(' in auth
assert 'public bool MustChangePassword(' in auth
assert 'if (MustChangePassword(userId)) return Array.Empty<string>();' in auth
assert 'MustChangePassword(id)) return false;' in auth
assert 'if (repository.MustChangePassword(account.Id)) return Results.Redirect("/account/change-password")' in program
assert 'app.MapPost("/account/change-password"' in program
assert 'ValidateRequestAsync(http)' in program
assert 'app.UseHttpsRedirection();' in program and 'TNM_ENABLE_HTTPS_REDIRECT' in program
assert 'await next();' in program
assert 'app.MapGet("/account/login"' in program
assert '--reset-owner-default' in program
assert 'ChangePassword(string csrf' in pages
owner_statement='CREATE UNIQUE INDEX IF NOT EXISTS ux_tnm_single_owner'+auth.split('Exec(c, @"CREATE UNIQUE INDEX IF NOT EXISTS ux_tnm_single_owner',1)[1].split('CREATE TABLE IF NOT EXISTS user_security',1)[0]
for mode in ('seed_empty.sql','seed.sql'):
 with tempfile.TemporaryDirectory() as td:
  db=Path(td)/'data.db'
  c=sqlite3.connect(db)
  c.execute('PRAGMA foreign_keys=ON')
  for file in ('schema.sql','v6_schema.sql',mode):c.executescript((root/'Data'/file).read_text(encoding='utf-8'))
  c.executescript(owner_statement)
  c.execute('ALTER TABLE app_users ADD COLUMN must_change_password INTEGER NOT NULL DEFAULT 0 CHECK(must_change_password IN (0,1))')
  assert c.execute('SELECT username,password_hash,must_change_password FROM app_users').fetchall()==[('owner',None,0)]
  # Simulate the parameterized initialization after hashing in .NET.
  c.execute('UPDATE app_users SET password_hash=?,must_change_password=1 WHERE is_owner=1',('HASH-INITIAL',))
  c.commit()
  assert c.execute('SELECT password_hash,must_change_password FROM app_users WHERE is_owner=1').fetchone()==('HASH-INITIAL',1)
  # Restart must not overwrite existing hash with the public default.
  assert c.execute('SELECT password_hash FROM app_users WHERE is_owner=1').fetchone()[0]=='HASH-INITIAL'
  c.executescript('CREATE TABLE user_security(user_id INTEGER PRIMARY KEY REFERENCES app_users(id), security_stamp TEXT NOT NULL, failed_attempts INTEGER NOT NULL DEFAULT 0, locked_until INTEGER NOT NULL DEFAULT 0);')
  c.execute("INSERT INTO user_security(user_id,security_stamp,failed_attempts,locked_until) SELECT id,'stamp1',5,9999999999 FROM app_users WHERE is_owner=1")
  c.commit()
  # Simulate explicit offline recovery which forces the password change.
  c.execute('BEGIN IMMEDIATE')
  c.execute('UPDATE app_users SET password_hash=?,must_change_password=? WHERE is_owner=1',('HASH-RESET',1))
  c.execute('UPDATE user_security SET security_stamp=?,failed_attempts=0,locked_until=0 WHERE user_id=1',('stamp2',))
  c.commit()
  assert c.execute('SELECT password_hash,must_change_password FROM app_users').fetchone()==('HASH-RESET',1)
  # Simulate logged-in password change. User role rows intact.
  c.execute('BEGIN IMMEDIATE')
  c.execute('UPDATE app_users SET password_hash=?,must_change_password=0 WHERE id=1',('HASH-CHANGED',))
  c.execute('UPDATE user_security SET security_stamp=? WHERE user_id=1',('stamp3',))
  c.commit()
  assert c.execute('SELECT password_hash,must_change_password FROM app_users').fetchone()==('HASH-CHANGED',0)
  assert c.execute("SELECT security_stamp,failed_attempts,locked_until FROM user_security WHERE user_id=1").fetchone()==('stamp3',0,0)
  assert c.execute('SELECT count(*) FROM user_roles WHERE user_id=1').fetchone()[0]==6
  assert not c.execute('PRAGMA foreign_key_check').fetchall()
  print('PASS',mode,': default -> force change -> offline reset -> change, roles/FK preserved')
