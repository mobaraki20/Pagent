using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Sokna.PrintAgent.Core;

internal static class Partial621FailureRecoveryTests
{
    [ModuleInitializer]
    internal static void RunBeforeMain()
        => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var dir=Path.Combine(Path.GetTempPath(),"sokna-agent-partial-621-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path=Path.Combine(dir,"queue.db");
        try
        {
            await SeedPostFailed621StateAsync(path);
            var preparation=await QueueDatabaseBootstrap.PrepareAsync(path);
            Assert(!preparation.ReinitializedLegacyEmptyDatabase,"post-failed-6.2.1 database must migrate in place");
            Assert(preparation.BackupPath is null,"post-failed-6.2.1 database must not be backup-swapped");

            var store=new LocalQueueStore(path,new PassthroughLeaseProtector());
            await store.InitializeAsync();
            Assert(await store.GetMetaAsync("schema_version")=="4","post-failed-6.2.1 database must complete v4 migration");
            Assert(await store.CountOpenAsync()==1,"open durable job must survive retry after failed 6.2.1 upgrade");

            await using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWrite}.ToString());
            await db.OpenAsync();
            Assert(await ScalarLongAsync(db,"SELECT COUNT(*) FROM local_jobs")==2,"local_jobs rows must be preserved after retry");
            Assert(await ScalarLongAsync(db,"SELECT COUNT(*) FROM report_outbox")==1,"report_outbox rows must be preserved after retry");
            Assert(await ScalarLongAsync(db,"SELECT COUNT(*) FROM attempt_outcomes")>=0,"pre-existing partial attempt_outcomes table remains valid");
            Assert(await ColumnExistsAsync(db,"report_outbox","delivery_state"),"delivery_state must be added after failed 6.2.1 retry");
            Assert(await IndexExistsAsync(db,"idx_report_delivery_state"),"delivery_state index must be created after column repair");
        }
        finally
        {
            try{Directory.Delete(dir,true);}catch{}
        }
    }

    private static async Task SeedPostFailed621StateAsync(string path)
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
        CREATE TABLE attempt_outcomes(
          attempt_id INTEGER PRIMARY KEY,
          server_job_id INTEGER NOT NULL,
          status TEXT NOT NULL,
          spooler_job_id TEXT NULL,
          retryable INTEGER NOT NULL,
          error_code TEXT NULL,
          error_message TEXT NULL,
          evidence_provenance TEXT NOT NULL,
          committed_at TEXT NOT NULL,
          FOREIGN KEY(attempt_id) REFERENCES local_jobs(attempt_id) ON DELETE RESTRICT
        );
        CREATE INDEX idx_local_jobs_job_attempt ON local_jobs(server_job_id,attempt_no,attempt_id);
        CREATE INDEX idx_local_jobs_state ON local_jobs(state,server_job_id,attempt_no);
        CREATE INDEX idx_report_outbox_pending ON report_outbox(sent_at,permanent_error,next_attempt_at,id);
        CREATE INDEX idx_report_outbox_attempt ON report_outbox(attempt_id,id);
        INSERT INTO agent_meta(key,value) VALUES('schema_version','3');
        INSERT INTO local_jobs(attempt_id,server_job_id,attempt_no,destination_key,queue_name,paper_width_mm,printable_width_mm,copies,layout_mode,payload_json,content_sha256,local_receipt_id,protected_lease_token,lease_expires_at,state,spooler_job_id,created_at,updated_at,worker_launching_at,last_error)
        VALUES(62101,201,1,'customer_receipt','MEVA TP-UN2',80,72,1,'customer','{}','0000000000000000000000000000000000000000000000000000000000000000','partial-resolved','protected','2026-09-11T03:00:00.0000000+00:00','Resolved','321','2026-09-11T01:00:00.0000000+00:00','2026-09-11T01:01:00.0000000+00:00',NULL,NULL);
        INSERT INTO local_jobs(attempt_id,server_job_id,attempt_no,destination_key,queue_name,paper_width_mm,printable_width_mm,copies,layout_mode,payload_json,content_sha256,local_receipt_id,protected_lease_token,lease_expires_at,state,spooler_job_id,created_at,updated_at,worker_launching_at,last_error)
        VALUES(62102,202,1,'prep_shared','Kitchen LAN',80,72,1,'combined','{}','0000000000000000000000000000000000000000000000000000000000000000','partial-open','protected','2026-09-11T03:00:00.0000000+00:00','Claimed',NULL,'2026-09-11T01:02:00.0000000+00:00','2026-09-11T01:02:00.0000000+00:00',NULL,NULL);
        INSERT INTO report_outbox(id,server_job_id,attempt_id,request_id,body_json,created_at,sent_at,last_error,error_count,next_attempt_at,permanent_error)
        VALUES(1,201,62101,'partial-report-1','{}','2026-09-11T01:00:30.0000000+00:00','2026-09-11T01:01:00.0000000+00:00',NULL,0,NULL,0);
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

    private static void Assert(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException("failed-6.2.1 recovery regression: "+message);
    }

    private sealed class PassthroughLeaseProtector:ILeaseTokenProtector
    {
        public string Protect(string value)=>value;
        public string Unprotect(string value)=>value;
    }
}
