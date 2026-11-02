"""SQLite-only bootstrap regression checks. No .NET SDK required.

Runs the same CREATE TRIGGER statement embedded in AuthRepository.Initialize,
verifies fresh and demo owner seeds, immutable owner controls, and automatic
assignment to future roles. Password hashing is in ASP.NET; not tested here.
"""
from pathlib import Path
import sqlite3
import tempfile

root = Path(__file__).resolve().parents[1]
source = (root / "Security/AuthRepository.cs").read_text(encoding="utf-8")
marker = 'Exec(c, @"CREATE UNIQUE INDEX IF NOT EXISTS ux_tnm_single_owner'
assert source.count(marker) == 1
owner_sql = ("CREATE UNIQUE INDEX IF NOT EXISTS ux_tnm_single_owner" +
             source.split(marker, 1)[1].split("CREATE TABLE IF NOT EXISTS user_security", 1)[0])

for seed_file in ("seed_empty.sql", "seed.sql"):
    with tempfile.TemporaryDirectory() as tmp:
        con = sqlite3.connect(str(Path(tmp) / "temp.sqlite"))
        con.execute("PRAGMA foreign_keys=ON")
        for file in ("schema.sql", "v6_schema.sql", seed_file):
            con.executescript((root / "Data" / file).read_text(encoding="utf-8"))
        con.executescript(owner_sql)
        users = con.execute("SELECT id,username,is_owner,password_hash FROM app_users").fetchall()
        assert users == [(1, "owner", 1, None)], users
        codes = {x[0] for x in con.execute("""SELECT r.code FROM user_roles ur
                      JOIN roles r ON r.id=ur.role_id WHERE ur.user_id=1""")}
        assert codes == {"ADMIN", "MANAGER", "ACCOUNTANT", "WAREHOUSE", "CSKH", "TECH"}
        con.execute("INSERT INTO roles(code,name) VALUES('CUSTOM_ROLE','Tùy chỉnh')")
        assert con.execute("""SELECT count(*) FROM user_roles ur JOIN roles r ON r.id=ur.role_id
                             WHERE ur.user_id=1 AND r.code='CUSTOM_ROLE'""").fetchone()[0] == 1
        forbidden = (
            "UPDATE app_users SET is_active=0 WHERE id=1",
            "UPDATE app_users SET username='hacker' WHERE id=1",
            "UPDATE app_users SET is_owner=0 WHERE id=1",
            "DELETE FROM app_users WHERE id=1",
            "DELETE FROM user_roles WHERE user_id=1",
            "INSERT INTO app_users(username,display_name,is_owner) VALUES('owner2','Other',1)",
        )
        for sql in forbidden:
            try:
                con.execute(sql)
            except sqlite3.IntegrityError:
                pass
            else:
                raise AssertionError("Owner protection not enforced: " + sql)
        assert con.execute("PRAGMA foreign_key_check").fetchall() == []
        print(f"PASS {seed_file}: one owner, 6 default roles, auto new role, 6 protection checks, FK")
