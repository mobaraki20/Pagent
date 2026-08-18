using Microsoft.Data.Sqlite;
namespace Sokna.PrintAgent.Core;

public sealed class LocalQueueStore
{
    private readonly string _connectionString;
    private readonly ILeaseTokenProtector _leaseProtector;
    public LocalQueueStore(string databasePath,ILeaseTokenProtector? leaseProtector=null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _leaseProtector=leaseProtector ?? (OperatingSystem.IsWindows() ? new DpapiLeaseTokenProtector() : throw new PlatformNotSupportedException("DPAPI lease protection requires Windows; tests must inject ILeaseTokenProtector."));
        _connectionString=new SqliteConnectionStringBuilder{DataSource=databasePath,Mode=SqliteOpenMode.ReadWriteCreate,Cache=SqliteCacheMode.Shared,Pooling=true}.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await ExecAsync(db,"PRAGMA journal_mode=WAL;",ct);
        const string sql="""
        CREATE TABLE IF NOT EXISTS agent_meta(
          key TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS local_jobs(
          attempt_id INTEGER PRIMARY KEY,
          server_job_id INTEGER NOT NULL,
          attempt_no INTEGER NOT NULL,
          destination_key TEXT NOT NULL,
          queue_name TEXT NOT NULL,
          paper_width_mm REAL NOT NULL,
          printable_width_mm REAL NOT NULL,
          copies INTEGER NOT NULL,
          layout_mode TEXT NOT NULL,
          payload_json TEXT NOT NULL,
          content_sha256 TEXT NOT NULL,
          local_receipt_id TEXT NOT NULL UNIQUE,
          protected_lease_token TEXT NOT NULL,
          lease_expires_at TEXT NOT NULL,
          state TEXT NOT NULL,
          spooler_job_id TEXT NULL,
          created_at TEXT NOT NULL,
          updated_at TEXT NOT NULL,
          worker_launching_at TEXT NULL,
          last_error TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS report_outbox(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          server_job_id INTEGER NOT NULL,
          attempt_id INTEGER NOT NULL,
          request_id TEXT NOT NULL UNIQUE,
          body_json TEXT NOT NULL,
          created_at TEXT NOT NULL,
          sent_at TEXT NULL,
          last_error TEXT NULL,
          FOREIGN KEY(attempt_id) REFERENCES local_jobs(attempt_id) ON DELETE RESTRICT
        );
        CREATE INDEX IF NOT EXISTS idx_local_jobs_job_attempt ON local_jobs(server_job_id,attempt_no,attempt_id);
        CREATE INDEX IF NOT EXISTS idx_local_jobs_state ON local_jobs(state,server_job_id,attempt_no);
        CREATE INDEX IF NOT EXISTS idx_report_outbox_pending ON report_outbox(sent_at,id);
        CREATE INDEX IF NOT EXISTS idx_report_outbox_attempt ON report_outbox(attempt_id,id);
        INSERT INTO agent_meta(key,value) VALUES('schema_version','2') ON CONFLICT(key) DO UPDATE SET value=excluded.value;
        """;
        await ExecAsync(db,sql,ct);
        await VerifySchemaAsync(db,ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var db=new SqliteConnection(_connectionString);
        await db.OpenAsync(ct);
        // Durability settings that are connection-scoped must be applied on every connection.
        await ExecAsync(db,"PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;",ct);
        return db;
    }

    private static async Task ExecAsync(SqliteConnection db,string sql,CancellationToken ct)
    {await using var c=db.CreateCommand();c.CommandText=sql;await c.ExecuteNonQueryAsync(ct);}

    private static async Task VerifySchemaAsync(SqliteConnection db,CancellationToken ct)
    {
        await using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT name,pk FROM pragma_table_info('local_jobs') WHERE name IN ('attempt_id','server_job_id') ORDER BY name";
        await using var r=await cmd.ExecuteReaderAsync(ct);var pk=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        while(await r.ReadAsync(ct))pk[r.GetString(0)]=r.GetInt64(1);
        if(!pk.TryGetValue("attempt_id",out var attemptPk)||attemptPk!=1)
            throw new InvalidDataException("SQLite local queue schema قدیمی/ناسازگار است؛ attempt_id باید کلید اصلی باشد. قبل از نصب Production از ابزار migration نسخه Agent استفاده کنید.");
    }

    public async Task<LocalJob> PersistReservedAsync(ClaimItem item,string proposedLocalReceiptId,CancellationToken ct=default)
    {
        if(!string.Equals(CryptoUtil.Sha256Hex(item.Job.PayloadJson),item.Job.ContentSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("content_sha256 با Payload دریافتی تطابق ندارد.");
        await using var db=await OpenAsync(ct);await using var tx=(SqliteTransaction)await db.BeginTransactionAsync(ct);
        await using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;cmd.CommandText="""
            INSERT INTO local_jobs(attempt_id,server_job_id,attempt_no,destination_key,queue_name,paper_width_mm,printable_width_mm,copies,layout_mode,payload_json,content_sha256,local_receipt_id,protected_lease_token,lease_expires_at,state,created_at,updated_at)
            VALUES($attempt,$job,$no,$dest,$queue,$paper,$printable,$copies,$layout,$payload,$sha,$receipt,$lease,$lease_expires,'Reserved',$now,$now)
            ON CONFLICT(attempt_id) DO NOTHING;
            """;
            cmd.Parameters.AddWithValue("$attempt",item.Attempt.Id);cmd.Parameters.AddWithValue("$job",item.Job.Id);cmd.Parameters.AddWithValue("$no",item.Attempt.AttemptNo);
            cmd.Parameters.AddWithValue("$dest",item.Destination.DestinationKey);cmd.Parameters.AddWithValue("$queue",item.Destination.WindowsQueueName);cmd.Parameters.AddWithValue("$paper",item.Destination.PaperWidthMm);cmd.Parameters.AddWithValue("$printable",item.Destination.PrintableWidthMm);cmd.Parameters.AddWithValue("$copies",item.Destination.Copies);cmd.Parameters.AddWithValue("$layout",item.Destination.LayoutMode);cmd.Parameters.AddWithValue("$payload",item.Job.PayloadJson);
            cmd.Parameters.AddWithValue("$sha",item.Job.ContentSha256);cmd.Parameters.AddWithValue("$receipt",proposedLocalReceiptId);cmd.Parameters.AddWithValue("$lease",_leaseProtector.Protect(item.Attempt.LeaseToken));cmd.Parameters.AddWithValue("$lease_expires",DateTimeOffset.Parse(item.Attempt.LeaseExpiresAt).ToString("O"));cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        LocalJob? row;
        await using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;cmd.CommandText="SELECT * FROM local_jobs WHERE attempt_id=$attempt";cmd.Parameters.AddWithValue("$attempt",item.Attempt.Id);
            await using var r=await cmd.ExecuteReaderAsync(ct);row=await r.ReadAsync(ct)?Read(r):null;
        }
        if(row is null)throw new InvalidOperationException("Attempt پس از ذخیره محلی پیدا نشد.");
        if(row.ServerJobId!=item.Job.Id || !string.Equals(row.ContentSha256,item.Job.ContentSha256,StringComparison.OrdinalIgnoreCase) || !string.Equals(row.PayloadJson,item.Job.PayloadJson,StringComparison.Ordinal))
            throw new InvalidDataException("Claim تکراری با داده متفاوت برای همان attempt_id دریافت شد.");
        await tx.CommitAsync(ct);return row;
    }

    public async Task SetStateAsync(long attemptId,LocalJobState state,string? spoolerJobId=null,string? error=null,bool markWorkerLaunching=false,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);await using var cmd=db.CreateCommand();cmd.CommandText="UPDATE local_jobs SET state=$state,spooler_job_id=COALESCE($spooler,spooler_job_id),last_error=$error,updated_at=$now,worker_launching_at=CASE WHEN $launch=1 THEN $now ELSE worker_launching_at END WHERE attempt_id=$attempt";
        cmd.Parameters.AddWithValue("$state",state.ToString());cmd.Parameters.AddWithValue("$spooler",(object?)spoolerJobId??DBNull.Value);cmd.Parameters.AddWithValue("$error",(object?)error??DBNull.Value);cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$launch",markWorkerLaunching?1:0);cmd.Parameters.AddWithValue("$attempt",attemptId);
        if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("Local attempt پیدا نشد.");
    }

    public async Task<LocalJob?> GetByAttemptAsync(long attemptId,CancellationToken ct=default)
    {await using var db=await OpenAsync(ct);await using var cmd=db.CreateCommand();cmd.CommandText="SELECT * FROM local_jobs WHERE attempt_id=$attempt";cmd.Parameters.AddWithValue("$attempt",attemptId);await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?Read(r):null;}

    public async Task<List<LocalJob>> GetRecoverableAsync(CancellationToken ct=default)
    {await using var db=await OpenAsync(ct);await using var cmd=db.CreateCommand();cmd.CommandText="SELECT * FROM local_jobs WHERE state NOT IN ('Resolved') ORDER BY server_job_id,attempt_no,attempt_id";await using var r=await cmd.ExecuteReaderAsync(ct);var rows=new List<LocalJob>();while(await r.ReadAsync(ct))rows.Add(Read(r));return rows;}

    public async Task<LocalJob?> GetNextClaimedAsync(CancellationToken ct=default)
    {await using var db=await OpenAsync(ct);await using var cmd=db.CreateCommand();cmd.CommandText="SELECT * FROM local_jobs WHERE state='Claimed' ORDER BY server_job_id,attempt_no,attempt_id LIMIT 1";await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?Read(r):null;}

    public async Task<int> CountOpenAsync(CancellationToken ct=default)
    {await using var db=await OpenAsync(ct);await using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM local_jobs WHERE state NOT IN ('Resolved')";return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));}
    public async Task<int> CountAmbiguousAsync(CancellationToken ct=default)
    {await using var db=await OpenAsync(ct);await using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM local_jobs WHERE state IN ('Unknown','RecoveryHold')";return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));}

    public async Task<bool> HasPendingReportAsync(long attemptId,CancellationToken ct=default)
    {await using var db=await OpenAsync(ct);await using var cmd=db.CreateCommand();cmd.CommandText="SELECT EXISTS(SELECT 1 FROM report_outbox WHERE attempt_id=$attempt AND sent_at IS NULL)";cmd.Parameters.AddWithValue("$attempt",attemptId);return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))==1;}

    public async Task EnqueueReportAsync(long jobId,long attemptId,string requestId,string bodyJson,CancellationToken ct=default)
    {await using var db=await OpenAsync(ct);await using var cmd=db.CreateCommand();cmd.CommandText="INSERT OR IGNORE INTO report_outbox(server_job_id,attempt_id,request_id,body_json,created_at) VALUES($job,$attempt,$request,$body,$now)";cmd.Parameters.AddWithValue("$job",jobId);cmd.Parameters.AddWithValue("$attempt",attemptId);cmd.Parameters.AddWithValue("$request",requestId);cmd.Parameters.AddWithValue("$body",bodyJson);cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));await cmd.ExecuteNonQueryAsync(ct);}

    public async Task<List<(long Id,long JobId,long AttemptId,string RequestId,string BodyJson)>> PendingReportsAsync(int limit=20,CancellationToken ct=default)
    {await using var db=await OpenAsync(ct);await using var cmd=db.CreateCommand();cmd.CommandText="SELECT id,server_job_id,attempt_id,request_id,body_json FROM report_outbox WHERE sent_at IS NULL ORDER BY id LIMIT $limit";cmd.Parameters.AddWithValue("$limit",limit);await using var r=await cmd.ExecuteReaderAsync(ct);var rows=new List<(long,long,long,string,string)>();while(await r.ReadAsync(ct))rows.Add((r.GetInt64(0),r.GetInt64(1),r.GetInt64(2),r.GetString(3),r.GetString(4)));return rows;}

    public async Task MarkReportSentAsync(long id,long attemptId,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);await using var tx=(SqliteTransaction)await db.BeginTransactionAsync(ct);
        await using(var c=db.CreateCommand()){c.Transaction=tx;c.CommandText="UPDATE report_outbox SET sent_at=$now,last_error=NULL WHERE id=$id AND attempt_id=$attempt";c.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));c.Parameters.AddWithValue("$id",id);c.Parameters.AddWithValue("$attempt",attemptId);if(await c.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("Report outbox row پیدا نشد.");}
        await using(var c=db.CreateCommand()){c.Transaction=tx;c.CommandText="UPDATE local_jobs SET state='Resolved',updated_at=$now WHERE attempt_id=$attempt";c.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));c.Parameters.AddWithValue("$attempt",attemptId);await c.ExecuteNonQueryAsync(ct);}
        await tx.CommitAsync(ct);
    }

    public async Task MarkReportErrorAsync(long id,string message,CancellationToken ct=default)
    {await using var db=await OpenAsync(ct);await using var cmd=db.CreateCommand();cmd.CommandText="UPDATE report_outbox SET last_error=$err WHERE id=$id";cmd.Parameters.AddWithValue("$err",message.Length>500?message[..500]:message);cmd.Parameters.AddWithValue("$id",id);await cmd.ExecuteNonQueryAsync(ct);}

    private static LocalJob Read(SqliteDataReader r)=>new(
        r.GetInt64(r.GetOrdinal("server_job_id")),r.GetInt64(r.GetOrdinal("attempt_id")),r.GetInt32(r.GetOrdinal("attempt_no")),r.GetString(r.GetOrdinal("destination_key")),r.GetString(r.GetOrdinal("queue_name")),r.GetDouble(r.GetOrdinal("paper_width_mm")),r.GetDouble(r.GetOrdinal("printable_width_mm")),r.GetInt32(r.GetOrdinal("copies")),r.GetString(r.GetOrdinal("layout_mode")),r.GetString(r.GetOrdinal("payload_json")),r.GetString(r.GetOrdinal("content_sha256")),r.GetString(r.GetOrdinal("local_receipt_id")),r.GetString(r.GetOrdinal("protected_lease_token")),DateTimeOffset.Parse(r.GetString(r.GetOrdinal("lease_expires_at"))),Enum.Parse<LocalJobState>(r.GetString(r.GetOrdinal("state"))),r.IsDBNull(r.GetOrdinal("spooler_job_id"))?null:r.GetString(r.GetOrdinal("spooler_job_id")),DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at"))),DateTimeOffset.Parse(r.GetString(r.GetOrdinal("updated_at"))),r.IsDBNull(r.GetOrdinal("worker_launching_at"))?null:DateTimeOffset.Parse(r.GetString(r.GetOrdinal("worker_launching_at"))),r.IsDBNull(r.GetOrdinal("last_error"))?null:r.GetString(r.GetOrdinal("last_error")));
}

