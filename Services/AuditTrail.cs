using System.Text;
using Microsoft.Data.Sqlite;

namespace TNM.Management.Services;

public sealed record ActivityRow(long Id,string Table,string RecordKey,string Action,string Actor,string At,string Before,string After);
public sealed record ActivitySummary(string Table,string RecordKey,long Creates,long Updates,long Deletes,string? FirstAt,string? LastAt);

/// <summary>
/// Central SQLite, transaction-consistent, append-only audit history for application tables.
/// Migration is additive and idempotent. Includes association/composite-key tables.
/// Stored UTC timestamps: ISO-8601 text. Pre-existing rows have unknown actors.
/// </summary>
public sealed class AuditTrail(IWebHostEnvironment env)
{
    private readonly string path = Path.Combine(env.ContentRootPath,"App_Data","tnm-local.db");
    private static readonly string[] Meta = ["created_at","created_by","updated_at","updated_by","deleted_at","deleted_by"];
    private static readonly HashSet<string> Secrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "password_hash","security_stamp","password_salt","token","access_token","refresh_token","secret","api_key","recovery_code","private_key"
    };
    private SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=path, ForeignKeys=true, DefaultTimeout=15, Pooling=true }.ToString());
        c.Open(); AuditRuntime.Attach(c);
        return c;
    }
    private static void Exec(SqliteConnection c,string sql)
    {
        using var q=c.CreateCommand();q.CommandText=sql;q.ExecuteNonQuery();
    }
    private static string Q(string name) => "\""+name.Replace("\"","\"\"")+"\"";
    private static string Lit(string value) => "'"+value.Replace("'","''")+"'";
    private static List<string> Columns(SqliteConnection c,string table)
    {
        using var q=c.CreateCommand();q.CommandText=$"PRAGMA table_info({Q(table)})";
        using var r=q.ExecuteReader();var result=new List<string>();while(r.Read())result.Add(r.GetString(1));return result;
    }
    private static List<string> Keys(SqliteConnection c,string table)
    {
        using var q=c.CreateCommand();q.CommandText=$"PRAGMA table_info({Q(table)})";
        using var r=q.ExecuteReader();var cols=new List<(int,string)>();while(r.Read())if(r.GetInt32(5)>0)cols.Add((r.GetInt32(5),r.GetString(1)));
        return cols.OrderBy(x=>x.Item1).Select(x=>x.Item2).ToList();
    }
    private static string Json(string alias,IEnumerable<string> cols)
    {
        var fields=cols.SelectMany(x=> new[] { Lit(x), Secrets.Contains(x)?Lit("[REDACTED]"):alias+"."+Q(x) });
        return "json_object("+string.Join(",",fields)+")";
    }
    private static string EventKey(string alias,List<string> keys)
    {
        // Record's real PK (including composite keys), rather than a non-durable rowid.
        return Json(alias,keys);
    }
    public void Initialize()
    {
        using var c=Open();
        Exec(c,"PRAGMA busy_timeout=15000;");

        using var tables=c.CreateCommand();
        tables.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('activity_events','activity_actions') ORDER BY name";
        var names=new List<string>();using(var reader=tables.ExecuteReader())while(reader.Read())names.Add(reader.GetString(0));
        foreach(var table in names)
        {
            var cols=Columns(c,table);
            foreach(var m in Meta)
            {
                if(cols.Contains(m,StringComparer.OrdinalIgnoreCase))continue;
                Exec(c,$"ALTER TABLE {Q(table)} ADD COLUMN {Q(m)} {(m.EndsWith("_by",StringComparison.Ordinal)?"INTEGER":"TEXT")}");
            }
            if(table!="audit_logs" && !cols.Contains("created_at",StringComparer.OrdinalIgnoreCase))
                Exec(c,$"UPDATE {Q(table)} SET created_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE created_at IS NULL");
            if(table.Equals("audit_logs",StringComparison.OrdinalIgnoreCase))continue;
            GenerateTriggers(c,table);
        }
        Exec(c,"""
            DROP VIEW IF EXISTS v7_product_master;
            CREATE VIEW v7_product_master AS
            SELECT p.id,p.sku,p.name,p.group_id,g.name AS group_name,
                   COALESCE(m.product_kind,'GOODS') AS product_kind,
                   COALESCE(m.unit,'Cái') AS unit,
                   p.is_serialized,p.warranty_months,p.is_active,
                   COALESCE(m.source_tag,'MANUAL') AS source_tag
            FROM products p
            JOIN product_groups g ON g.id=p.group_id
            LEFT JOIN product_catalog_metadata m ON m.product_id=p.id
            WHERE p.deleted_at IS NULL AND g.deleted_at IS NULL;
            """);
    }
    private static void GenerateTriggers(SqliteConnection c,string table)
    {
        var columns=Columns(c,table);
        var keyCols=Keys(c,table);
        if(keyCols.Count==0)keyCols.Add("rowid"); // all current application tables are rowid tables
        var safeName="tnm_audit_"+table;
        var tracked=columns.Where(x=>!Meta.Contains(x,StringComparer.OrdinalIgnoreCase)).ToList();
        var unchanged=string.Join(" AND ",tracked.Select(x=>$"OLD.{Q(x)} IS NEW.{Q(x)}"));
        if(unchanged.Length==0)unchanged="1";
        var changed=$"NOT ({unchanged}) OR NOT (OLD.deleted_at IS NEW.deleted_at)";
        var actor="NULLIF(tnm_actor(),0)";
        var username=$"(SELECT username FROM app_users WHERE id={actor})";
        var tableName=Lit(table);
        var keysNew=EventKey("NEW",keyCols);
        var keysOld=EventKey("OLD",keyCols);
        var oldJson=Json("OLD",tracked);
        var newJson=Json("NEW",tracked);
        // No credentials or sensitive hashes are persisted in the audit snapshots.
        var stampedJson=$"(SELECT {Json("snap",tracked)} FROM {Q(table)} AS snap WHERE snap.rowid=NEW.rowid)";
        // Excluding metadata-only changes prevents the stamp UPDATE from emitting duplicate events.
        Exec(c,$"DROP TRIGGER IF EXISTS {Q(safeName+"_insert")};");
        Exec(c,$"""
            CREATE TRIGGER IF NOT EXISTS {Q(safeName+"_insert")}
            AFTER INSERT ON {Q(table)}
            BEGIN
                UPDATE {Q(table)} SET created_at=COALESCE(created_at,strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    created_by=COALESCE(created_by,{actor}) WHERE rowid=NEW.rowid;
                INSERT INTO activity_events(table_name,record_key,operation,actor_user_id,actor_username,before_json,after_json)
                VALUES({tableName},{keysNew},'CREATE',{actor},{username},NULL,{stampedJson});
            END;
            """);
        Exec(c,$"DROP TRIGGER IF EXISTS {Q(safeName+"_update")};");
        Exec(c,$"""
            CREATE TRIGGER IF NOT EXISTS {Q(safeName+"_update")}
            AFTER UPDATE ON {Q(table)} WHEN {changed}
            BEGIN
                UPDATE {Q(table)} SET updated_at=strftime('%Y-%m-%dT%H:%M:%fZ','now'),updated_by={actor},
                    deleted_by=CASE WHEN OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL
                           THEN {actor} WHEN OLD.deleted_at IS NOT NULL AND NEW.deleted_at IS NULL THEN NULL
                           ELSE deleted_by END WHERE rowid=NEW.rowid;
                INSERT INTO activity_events(table_name,record_key,operation,actor_user_id,actor_username,before_json,after_json)
                VALUES({tableName},{keysNew},
                    CASE WHEN OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL THEN 'DELETE'
                         WHEN OLD.deleted_at IS NOT NULL AND NEW.deleted_at IS NULL THEN 'RESTORE' ELSE 'UPDATE' END,
                    {actor},{username},{oldJson},{stampedJson});
            END;
            """);
        // Hard deletes must never discard history. Caller must UPDATE deleted_at instead.
        Exec(c,$"""
            CREATE TRIGGER IF NOT EXISTS {Q(safeName+"_no_hard_delete")}
            BEFORE DELETE ON {Q(table)}
            BEGIN SELECT RAISE(ABORT,'SOFT_DELETE_ONLY'); END;
            """);
    }
    public void RecordAction(long? actor, string action, string resource, string details = "")
    {
        if(action.Length is < 1 or > 40 || resource.Length is < 1 or > 160)
            throw new ArgumentException("Loại hoạt động không hợp lệ.");
        using var c=Open();using var q=c.CreateCommand();
        q.CommandText="""
            INSERT INTO activity_actions(actor_user_id,actor_username,action,resource,details)
            VALUES($id,(SELECT username FROM app_users WHERE id=$id),$action,$resource,$details)
            """;
        q.Parameters.AddWithValue("$id", actor is > 0 ? (object)actor.Value : DBNull.Value);
        q.Parameters.AddWithValue("$action",action);
        q.Parameters.AddWithValue("$resource",resource);
        q.Parameters.AddWithValue("$details",details.Length>500?details[..500]:details);
        q.ExecuteNonQuery();
    }
    public List<(long Id,string Actor,string Action,string Resource,string At)> RecentActions(int limit=200)
    {
        using var c=Open();using var q=c.CreateCommand();q.CommandText="""
            SELECT id,COALESCE(actor_username,'[Không xác định]'),action,resource,occurred_at
            FROM activity_actions ORDER BY id DESC LIMIT $n
            """;
        q.Parameters.AddWithValue("$n",Math.Clamp(limit,1,200));
        using var rd=q.ExecuteReader();var rows=new List<(long,string,string,string,string)>();
        while(rd.Read())rows.Add((rd.GetInt64(0),rd.GetString(1),rd.GetString(2),rd.GetString(3),rd.GetString(4)));
        return rows;
    }

    public IReadOnlyList<string> Tables()
    {
        using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT DISTINCT table_name FROM activity_events ORDER BY table_name";
        using var r=q.ExecuteReader();var rows=new List<string>();while(r.Read())rows.Add(r.GetString(0));return rows;
    }
    public long Count(string? table=null,string? key=null,string? action=null,string? actor=null,string? from=null,string? to=null)
    {
        using var c=Open();using var q=c.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM activity_events WHERE ($t='' OR table_name=$t) AND ($k='' OR (substr($k,1,1)='{' AND record_key=$k) OR (substr($k,1,1)<>'{' AND record_key LIKE '%' || $k || '%')) AND ($a='' OR operation=$a) AND ($actor='' OR actor_username LIKE '%' || $actor || '%' OR CAST(actor_user_id AS TEXT)=$actor) AND ($from='' OR substr(occurred_at,1,10)>=$from) AND ($to='' OR substr(occurred_at,1,10)<=$to)";
        q.Parameters.AddWithValue("$t",table??"");q.Parameters.AddWithValue("$k",key??"");q.Parameters.AddWithValue("$a",action??"");
        q.Parameters.AddWithValue("$actor",actor??"");q.Parameters.AddWithValue("$from",from??"");q.Parameters.AddWithValue("$to",to??"");
        return Convert.ToInt64(q.ExecuteScalar()??0);
    }
    public List<ActivityRow> Search(string? table=null,string? key=null,string? action=null,int page=1,int pageSize=50,string? actor=null,string? from=null,string? to=null)
    {
        using var c=Open();using var q=c.CreateCommand();
        q.CommandText="""
            SELECT e.id,e.table_name,e.record_key,e.operation,
                COALESCE(e.actor_username, CASE WHEN e.actor_user_id IS NULL THEN '[Hệ thống / chưa xác định]' ELSE '#'||e.actor_user_id END),
                e.occurred_at,COALESCE(e.before_json,''),COALESCE(e.after_json,'')
            FROM activity_events e
            WHERE ($t='' OR e.table_name=$t) AND ($k='' OR (substr($k,1,1)='{' AND e.record_key=$k) OR (substr($k,1,1)<>'{' AND e.record_key LIKE '%' || $k || '%')) AND ($a='' OR e.operation=$a)
              AND ($actor='' OR e.actor_username LIKE '%' || $actor || '%' OR CAST(e.actor_user_id AS TEXT)=$actor)
              AND ($from='' OR substr(e.occurred_at,1,10)>=$from) AND ($to='' OR substr(e.occurred_at,1,10)<=$to)
            ORDER BY e.id DESC LIMIT $limit OFFSET $offset
            """;
        q.Parameters.AddWithValue("$t",table??"");q.Parameters.AddWithValue("$k",key??"");q.Parameters.AddWithValue("$a",action??"");
        q.Parameters.AddWithValue("$actor",actor??"");q.Parameters.AddWithValue("$from",from??"");q.Parameters.AddWithValue("$to",to??"");
        var clampedSize = Math.Clamp(pageSize, 1, 200);
        q.Parameters.AddWithValue("$limit", clampedSize);
        q.Parameters.AddWithValue("$offset", (Math.Max(1, page) - 1) * clampedSize);
        using var r=q.ExecuteReader();var rows=new List<ActivityRow>();while(r.Read())rows.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7)));return rows;
    }
    public ActivitySummary Summary(string table,long id)
    {
        var key="{\"id\":"+id+"}";
        using var c=Open();using var q=c.CreateCommand();
        q.CommandText="""
            SELECT COALESCE(SUM(operation='CREATE'),0),COALESCE(SUM(operation='UPDATE'),0),
                COALESCE(SUM(operation='DELETE'),0),MIN(occurred_at),MAX(occurred_at)
            FROM activity_events WHERE table_name=$t AND record_key=$key
            """;
        q.Parameters.AddWithValue("$t",table);q.Parameters.AddWithValue("$key",key);
        using var r=q.ExecuteReader();r.Read();return new(table,key,r.GetInt64(0),r.GetInt64(1),r.GetInt64(2),r.IsDBNull(3)?null:r.GetString(3),r.IsDBNull(4)?null:r.GetString(4));
    }
}
