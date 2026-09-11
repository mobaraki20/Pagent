using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Sokna.PrintAgent.Core;

internal static class MigrationHotfixTests
{
    [ModuleInitializer]
    internal static void RunBeforeMain()
        => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var dir=Path.Combine(Path.GetTempPath(),"sokna-agent-v620-upgrade-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path=Path.Combine(dir,"queue.db");
        try
        {
            await CreateExactV620SchemaAsync(path);

            var preparation=await QueueDatabaseBootstrap.PrepareAsync(path);
            Assert(!preparation.ReinitializedLegacyEmptyDatabase,"compatible v6.2.0 database must migrate in place");
            Assert(preparation.BackupPath is null,"compatible v6.2.0 database must not be backup-swapped");
            Assert(File.Exists(path),"queue.db must remain at its original path");
            Assert(Directory.GetFiles(dir,"queue.db.legacy-empty-*.bak").Length==0,"v6.2.0 durable database must never be treated as disposable legacy data");

            // This is the exact call that failed on the real 6.2.0 -> 6.2.1 upgrade with
            // SQLite Error 1: no such column: delivery_state.
            var store=new LocalQueueStore(path,new PassthroughLeaseProtector());
            await store.InitializeAsync();
            Assert(await store.GetMetaAsync("schema_version")=="4","schema must advance to v4 only after initialization succeeds");
            Assert(await store.CountOpenAsync()==1,"open v6.2.0 durable job must survive migration");

            // Prove the migration is idempotent across a second service start.
            var restarted=new LocalQueueStore(path,new PassthroughLeaseProtector());
            await restarted.InitializeAsync();
            Assert(await restarted.GetMetaAsync("schema_version")=="4","second initialization must remain v4");
            Assert(await restarted.CountOpenAsync()==1,"second initialization must preserve the same open job");

            await using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWrite}.ToString());
            await db.OpenAsync();
            foreach(var column in new[]{"delivery_state","last_http_status","last_error_code","updated_at","agent_version","server_scope"})
                Assert(await ColumnExistsAsync(db,"report_outbox",column),$"report_outbox.{column} must exist after v3 -> v4 migration");
            Assert(await ColumnExistsAsync(db,"local_jobs","server_scope"),"local_jobs.server_scope must exist after v3 -> v4 migration");
            Assert(await IndexExistsAsync(db,"idx_report_delivery_state"),"delivery-state index must only be created after its column exists");
            Assert(await IndexExistsAsync(db,"idx_report_server_scope"),"server-scope index must only be created after its columns exist");
            Assert(await ScalarLongAsync(db,"SELECT COUNT(*) FROM local_jobs")==2,"all v6.2.0 local_jobs rows must be preserved");
            Assert(await ScalarLongAsync(db,"SELECT COUNT(*) FROM report_outbox")==1,"v6.2.0 report_outbox row must be preserved");
            Assert(await ScalarStringAsync(db,"SELECT delivery_state FROM report_outbox WHERE id=1")=="Delivered","already-sent v6.2.0 report must normalize to Delivered");
            Assert(await ScalarStringAsync(db,"SELECT server_scope FROM report_outbox WHERE id=1")=="legacy-unbound","legacy report must receive the non-destructive default server scope");
        }
        finally
        {
            try{Directory.Delete(dir,true);}catch{}
        }
    }

    private static async Task CreateExactV620SchemaAsync(string path)
    {
        await using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate}.ToString());
        await db.OpenAsync();
        await using var command=db.CreateCommand();
        command.CommandText="""
        PRAGMA journal_mode=WAL;
        CREATE TABLE agent_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE local_jobs(
          attempt_id INTEGER PRIMARY KEY,server_job_id INTEGER NOT NULL,attempt_no INTEGER NOT NULL,destination_key TEXT NOT NULL,queue_name TEXT NOT NULL,
          paper_width_mm REAL NOT NULL,printable_width_mm REAL NOT NULL,copies INTEGER NOT NULL,layout_mode TEXT NOT NULL,payload_json TEXT NOT NULL,content_sha256 TEXT NOT NULL,
          local_receipt_id TEXT NOT NULL UNIQUE,protected_lease_token TEXT NOT NULL,lease_expires_at TEXT NOT NULL,state TEXT NOT NULL,spooler_job_id TEXT NULL,
          created_at TEXT NOT NULL,updated_at TEXT NOT NULL,worker_launching_at TEXT NULL,last_error TEXT NULL
        );
        CREATE TABLE report_outbox(
          id INTEGER PRIMARY KEY AUTOINCREMENT,server_job_id INTEGER NOT NULL,attempt_id INTEGER NOT NULL,request_id TEXT NOT NULL UNIQUE,body_json TEXT NOT NULL,
          created_at TEXT NOT NULL,sent_at TEXT NULL,last_error TEXT NULL,error_count INTEGER NOT NULL DEFAULT 0,next_attempt_at TEXT NULL,permanent_error INTEGER NOT NULL DEFAULT 0,
          FOREIGN KEY(attempt_id) REFERENCES local_jobs(attempt_id) ON DELETE RESTRICT
        );
        CREATE INDEX idx_local_jobs_job_attempt ON local_jobs(server_job_id,attempt_no,attempt_id);
        CREATE INDEX idx_local_jobs_state ON local_jobs(state,server_job_id,attempt_no);
        CREATE INDEX idx_report_outbox_pending ON report_outbox(sent_at,permanent_error,next_attempt_at,id);
        CREATE INDEX idx_report_outbox_attempt ON report_outbox(attempt_id,id);
        INSERT INTO agent_meta(key,value) VALUES('schema_version','3');
        INSERT INTO local_jobs(attempt_id,server_job_id,attempt_no,destination_key,queue_name,paper_width_mm,printable_width_mm,copies,layout_mode,payload_json,content_sha256,local_receipt_id,protected_lease_token,lease_expires_at,state,spooler_job_id,created_at,updated_at,worker_launching_at,last_error)
        VALUES(62001,101,1,'customer_receipt','MEVA TP-UN2',80,72,1,'customer','{}','0000000000000000000000000000000000000000000000000000000000000000','legacy-receipt-resolved','protected','2026-09-11T03:00:00.0000000+00:00','Resolved','123','2026-09-11T01:00:00.0000000+00:00','2026-09-11T01:01:00.0000000+00:00',NULL,NULL);
        INSERT INTO local_jobs(attempt_id,server_job_id,attempt_no,destination_key,queue_name,paper_width_mm,printable_width_mm,copies,layout_mode,payload_json,content_sha256,local_receipt_id,protected_lease_token,lease_expires_at,state,spooler_job_id,created_at,updated_at,worker_launching_at,last_error)
        VALUES(62002,102,1,'prep_shared','Kitchen LAN',80,72,1,'combined','{}','0000000000000000000000000000000000000000000000000000000000000000','legacy-receipt-open','protected','2026-09-11T03:00:00.0000000+00:00','Claimed',NULL,'2026-09-11T01:02:00.0000000+00:00','2026-09-11T01:02:00.0000000+00:00',NULL,NULL);
        INSERT INTO report_outbox(id,server_job_id,attempt_id,request_id,body_json,created_at,sent_at,last_error,error_count,next_attempt_at,permanent_error)
        VALUES(1,101,62001,'legacy-report-1','{}','2026-09-11T01:00:30.0000000+00:00','2026-09-11T01:01:00.0000000+00:00',NULL,0,NULL,0);
        """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection db,string table,string column)
    {
        await using var command=db.CreateCommand();
        command.CommandText=$"SELECT EXISTS(SELECT 1 FROM pragma_table_info('{table}') WHERE name=$name)";
        command.Parameters.AddWithValue("$name",column);
        return Convert.ToInt32(await command.ExecuteScalarAsync())==1;
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection db,string name)
    {
        await using var command=db.CreateCommand();
        command.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='index' AND name=$name)";
        command.Parameters.AddWithValue("$name",name);
        return Convert.ToInt32(await command.ExecuteScalarAsync())==1;
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection db,string sql)
    {
        await using var command=db.CreateCommand();
        command.CommandText=sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ScalarStringAsync(SqliteConnection db,string sql)
    {
        await using var command=db.CreateCommand();
        command.CommandText=sql;
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static void Assert(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException("6.2.0 -> 6.2.2 migration regression: "+message);
    }

    private sealed class PassthroughLeaseProtector:ILeaseTokenProtector
    {
        public string Protect(string value)=>value;
        public string Unprotect(string value)=>value;
    }
}
