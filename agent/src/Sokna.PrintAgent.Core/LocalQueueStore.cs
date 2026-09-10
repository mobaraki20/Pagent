using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Sokna.PrintAgent.Core;

public sealed class LocalQueueStore
{
    private const string LegacyServerScope="legacy-unbound";
    private readonly string _connectionString;
    private readonly ILeaseTokenProtector _leaseProtector;

    public LocalQueueStore(string databasePath,ILeaseTokenProtector? leaseProtector=null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _leaseProtector=leaseProtector ?? (OperatingSystem.IsWindows()
            ? new DpapiLeaseTokenProtector()
            : throw new PlatformNotSupportedException("DPAPI lease protection requires Windows; tests must inject ILeaseTokenProtector."));
        _connectionString=new SqliteConnectionStringBuilder
        {
            DataSource=databasePath,
            Mode=SqliteOpenMode.ReadWriteCreate,
            Cache=SqliteCacheMode.Shared,
            Pooling=true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await ExecAsync(db,"PRAGMA journal_mode=WAL;",ct);
        await ExecAsync(db,"""
        CREATE TABLE IF NOT EXISTS agent_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
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
          last_error TEXT NULL,
          server_scope TEXT NOT NULL DEFAULT 'legacy-unbound'
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
          error_count INTEGER NOT NULL DEFAULT 0,
          next_attempt_at TEXT NULL,
          permanent_error INTEGER NOT NULL DEFAULT 0,
          delivery_state TEXT NOT NULL DEFAULT 'Pending',
          last_http_status INTEGER NULL,
          last_error_code TEXT NULL,
          updated_at TEXT NULL,
          agent_version TEXT NOT NULL DEFAULT '6.2.0',
          server_scope TEXT NOT NULL DEFAULT 'legacy-unbound',
          FOREIGN KEY(attempt_id) REFERENCES local_jobs(attempt_id) ON DELETE RESTRICT
        );
        CREATE TABLE IF NOT EXISTS attempt_outcomes(
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
        CREATE INDEX IF NOT EXISTS idx_local_jobs_job_attempt ON local_jobs(server_job_id,attempt_no,attempt_id);
        CREATE INDEX IF NOT EXISTS idx_local_jobs_state ON local_jobs(state,server_job_id,attempt_no);
        CREATE INDEX IF NOT EXISTS idx_report_outbox_pending ON report_outbox(sent_at,permanent_error,next_attempt_at,id);
        CREATE INDEX IF NOT EXISTS idx_report_outbox_attempt ON report_outbox(attempt_id,id);
        CREATE INDEX IF NOT EXISTS idx_report_delivery_state ON report_outbox(delivery_state,next_attempt_at,id);
        CREATE INDEX IF NOT EXISTS idx_report_server_scope ON report_outbox(server_scope,delivery_state,id);
        """,ct);

        // Additive v3 -> v4 migration; old binaries ignore the new columns/table.
        await EnsureColumnAsync(db,"local_jobs","server_scope","TEXT NOT NULL DEFAULT 'legacy-unbound'",ct);
        await EnsureColumnAsync(db,"report_outbox","error_count","INTEGER NOT NULL DEFAULT 0",ct);
        await EnsureColumnAsync(db,"report_outbox","next_attempt_at","TEXT NULL",ct);
        await EnsureColumnAsync(db,"report_outbox","permanent_error","INTEGER NOT NULL DEFAULT 0",ct);
        await EnsureColumnAsync(db,"report_outbox","delivery_state","TEXT NOT NULL DEFAULT 'Pending'",ct);
        await EnsureColumnAsync(db,"report_outbox","last_http_status","INTEGER NULL",ct);
        await EnsureColumnAsync(db,"report_outbox","last_error_code","TEXT NULL",ct);
        await EnsureColumnAsync(db,"report_outbox","updated_at","TEXT NULL",ct);
        await EnsureColumnAsync(db,"report_outbox","agent_version","TEXT NOT NULL DEFAULT '6.2.0'",ct);
        await EnsureColumnAsync(db,"report_outbox","server_scope","TEXT NOT NULL DEFAULT 'legacy-unbound'",ct);

        await MigrateLegacyOutcomesAsync(db,ct);
        await SetMetaOnConnectionAsync(db,"schema_version","4",ct);
        await VerifySchemaAsync(db,ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var db=new SqliteConnection(_connectionString);
        await db.OpenAsync(ct);
        await ExecAsync(db,"PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;",ct);
        return db;
    }

    private static async Task ExecAsync(SqliteConnection db,string sql,CancellationToken ct)
    {
        await using var command=db.CreateCommand();
        command.CommandText=sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureColumnAsync(SqliteConnection db,string table,string column,string definition,CancellationToken ct)
    {
        await using var check=db.CreateCommand();
        check.CommandText=$"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$name";
        check.Parameters.AddWithValue("$name",column);
        if(Convert.ToInt32(await check.ExecuteScalarAsync(ct))==0)
        {
            await ExecAsync(db,$"ALTER TABLE {table} ADD COLUMN {column} {definition}",ct);
        }
    }

    private static async Task SetMetaOnConnectionAsync(SqliteConnection db,string key,string value,CancellationToken ct)
    {
        await using var command=db.CreateCommand();
        command.CommandText="INSERT INTO agent_meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$k",key);
        command.Parameters.AddWithValue("$v",value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task VerifySchemaAsync(SqliteConnection db,CancellationToken ct)
    {
        await using var command=db.CreateCommand();
        command.CommandText="SELECT name,pk FROM pragma_table_info('local_jobs') WHERE name IN ('attempt_id','server_job_id') ORDER BY name";
        await using var reader=await command.ExecuteReaderAsync(ct);
        var pk=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        while(await reader.ReadAsync(ct))pk[reader.GetString(0)]=reader.GetInt64(1);
        if(!pk.TryGetValue("attempt_id",out var attemptPk)||attemptPk!=1)
        {
            throw new InvalidDataException("SQLite local queue schema قدیمی/ناسازگار است؛ attempt_id باید کلید اصلی باشد.");
        }
    }

    public async Task<string?> GetMetaAsync(string key,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="SELECT value FROM agent_meta WHERE key=$key";
        command.Parameters.AddWithValue("$key",key);
        var value=await command.ExecuteScalarAsync(ct);
        return value is null or DBNull?null:Convert.ToString(value);
    }

    public async Task SetMetaAsync(string key,string value,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await SetMetaOnConnectionAsync(db,key,value,ct);
    }

    public async Task DeleteMetaAsync(string key,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="DELETE FROM agent_meta WHERE key=$key";
        command.Parameters.AddWithValue("$key",key);
        await command.ExecuteNonQueryAsync(ct);
    }

    public Task<LocalJob> PersistReservedAsync(ClaimItem item,string proposedLocalReceiptId,CancellationToken ct=default)
        => PersistReservedAsync(item,proposedLocalReceiptId,LegacyServerScope,ct);

    public async Task<LocalJob> PersistReservedAsync(ClaimItem item,string proposedLocalReceiptId,string serverScope,CancellationToken ct=default)
    {
        if(!string.Equals(CryptoUtil.Sha256Hex(item.Job.PayloadJson),item.Job.ContentSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("content_sha256 با Payload دریافتی تطابق ندارد.");
        if(!TryParseExplicitOffset(item.Attempt.LeaseExpiresAt,out var leaseExpiry))
            throw new InvalidDataException("lease_expires_at باید ISO-8601 با offset صریح باشد.");
        if(string.IsNullOrWhiteSpace(serverScope))serverScope=LegacyServerScope;

        await using var db=await OpenAsync(ct);
        await using var tx=(SqliteTransaction)await db.BeginTransactionAsync(ct);
        await using(var command=db.CreateCommand())
        {
            command.Transaction=tx;
            command.CommandText="""
            INSERT INTO local_jobs(
              attempt_id,server_job_id,attempt_no,destination_key,queue_name,paper_width_mm,printable_width_mm,copies,layout_mode,
              payload_json,content_sha256,local_receipt_id,protected_lease_token,lease_expires_at,state,created_at,updated_at,server_scope)
            VALUES($attempt,$job,$no,$dest,$queue,$paper,$printable,$copies,$layout,$payload,$sha,$receipt,$lease,$lease_expires,'Reserved',$now,$now,$scope)
            ON CONFLICT(attempt_id) DO NOTHING;
            """;
            command.Parameters.AddWithValue("$attempt",item.Attempt.Id);
            command.Parameters.AddWithValue("$job",item.Job.Id);
            command.Parameters.AddWithValue("$no",item.Attempt.AttemptNo);
            command.Parameters.AddWithValue("$dest",item.Destination.DestinationKey);
            command.Parameters.AddWithValue("$queue",item.Destination.WindowsQueueName);
            command.Parameters.AddWithValue("$paper",item.Destination.PaperWidthMm);
            command.Parameters.AddWithValue("$printable",item.Destination.PrintableWidthMm);
            command.Parameters.AddWithValue("$copies",item.Destination.Copies);
            command.Parameters.AddWithValue("$layout",item.Destination.LayoutMode);
            command.Parameters.AddWithValue("$payload",item.Job.PayloadJson);
            command.Parameters.AddWithValue("$sha",item.Job.ContentSha256);
            command.Parameters.AddWithValue("$receipt",proposedLocalReceiptId);
            command.Parameters.AddWithValue("$lease",_leaseProtector.Protect(item.Attempt.LeaseToken));
            command.Parameters.AddWithValue("$lease_expires",leaseExpiry.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$scope",serverScope);
            await command.ExecuteNonQueryAsync(ct);
        }

        LocalJob? row;
        await using(var command=db.CreateCommand())
        {
            command.Transaction=tx;
            command.CommandText="SELECT * FROM local_jobs WHERE attempt_id=$attempt";
            command.Parameters.AddWithValue("$attempt",item.Attempt.Id);
            await using var reader=await command.ExecuteReaderAsync(ct);
            row=await reader.ReadAsync(ct)?ReadJob(reader):null;
        }
        if(row is null)throw new InvalidOperationException("Attempt پس از ذخیره محلی پیدا نشد.");
        if(row.ServerJobId!=item.Job.Id||
           !string.Equals(row.ContentSha256,item.Job.ContentSha256,StringComparison.OrdinalIgnoreCase)||
           !string.Equals(row.PayloadJson,item.Job.PayloadJson,StringComparison.Ordinal)||
           !string.Equals(row.DestinationKey,item.Destination.DestinationKey,StringComparison.OrdinalIgnoreCase)||
           !string.Equals(row.QueueName,item.Destination.WindowsQueueName,StringComparison.OrdinalIgnoreCase)||
           !string.Equals(row.ServerScope,serverScope,StringComparison.Ordinal))
        {
            throw new InvalidDataException("Claim تکراری با identity/payload/destination/server متفاوت برای همان attempt_id دریافت شد.");
        }
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task SetStateAsync(long attemptId,LocalJobState state,string? spoolerJobId=null,string? error=null,bool markWorkerLaunching=false,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="""
        UPDATE local_jobs
        SET state=$state,
            spooler_job_id=COALESCE($spooler,spooler_job_id),
            last_error=$error,
            updated_at=$now,
            worker_launching_at=CASE WHEN $launch=1 THEN $now ELSE worker_launching_at END
        WHERE attempt_id=$attempt
        """;
        command.Parameters.AddWithValue("$state",state.ToString());
        command.Parameters.AddWithValue("$spooler",(object?)spoolerJobId??DBNull.Value);
        command.Parameters.AddWithValue("$error",(object?)Bound(error,500)??DBNull.Value);
        command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$launch",markWorkerLaunching?1:0);
        command.Parameters.AddWithValue("$attempt",attemptId);
        if(await command.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("Local attempt پیدا نشد.");
    }

    public async Task<LocalJob?> GetByAttemptAsync(long attemptId,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="SELECT * FROM local_jobs WHERE attempt_id=$attempt";
        command.Parameters.AddWithValue("$attempt",attemptId);
        await using var reader=await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)?ReadJob(reader):null;
    }

    public async Task<List<LocalJob>> GetRecoverableAsync(CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="SELECT * FROM local_jobs WHERE state NOT IN ('Resolved') ORDER BY server_job_id,attempt_no,attempt_id";
        await using var reader=await command.ExecuteReaderAsync(ct);
        var rows=new List<LocalJob>();
        while(await reader.ReadAsync(ct))rows.Add(ReadJob(reader));
        return rows;
    }

    public async Task<LocalJob?> GetNextClaimedAsync(CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="SELECT * FROM local_jobs WHERE state='Claimed' ORDER BY server_job_id,attempt_no,attempt_id LIMIT 1";
        await using var reader=await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)?ReadJob(reader):null;
    }

    public async Task<int> CountOpenAsync(CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="SELECT COUNT(*) FROM local_jobs WHERE state NOT IN ('Resolved')";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public async Task<int> CountAmbiguousAsync(CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="SELECT COUNT(*) FROM attempt_outcomes WHERE status IN ('Unknown','RecoveryHold')";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public async Task<AttemptOutcome?> GetOutcomeAsync(long attemptId,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="SELECT * FROM attempt_outcomes WHERE attempt_id=$attempt";
        command.Parameters.AddWithValue("$attempt",attemptId);
        await using var reader=await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)?ReadOutcome(reader):null;
    }

    public async Task<ReportOutboxRow?> GetOutboxForAttemptAsync(long attemptId,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="SELECT * FROM report_outbox WHERE attempt_id=$attempt ORDER BY id LIMIT 1";
        command.Parameters.AddWithValue("$attempt",attemptId);
        await using var reader=await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)?ReadReport(reader):null;
    }

    public async Task<ReportOutboxRow> CommitOutcomeAndReportAsync(LocalJob job,AttemptOutcomeDraft outcome,ReportRequestEnvelope report,CancellationToken ct=default)
    {
        ValidateReportEnvelope(job,outcome,report);
        var now=DateTimeOffset.UtcNow;
        await using var db=await OpenAsync(ct);
        await using var tx=(SqliteTransaction)await db.BeginTransactionAsync(ct);

        AttemptOutcome? existingOutcome=null;
        await using(var command=db.CreateCommand())
        {
            command.Transaction=tx;
            command.CommandText="SELECT * FROM attempt_outcomes WHERE attempt_id=$attempt";
            command.Parameters.AddWithValue("$attempt",job.AttemptId);
            await using var reader=await command.ExecuteReaderAsync(ct);
            if(await reader.ReadAsync(ct))existingOutcome=ReadOutcome(reader);
        }

        if(existingOutcome is not null&&!OutcomeEquivalent(existingOutcome,outcome,job.ServerJobId))
        {
            await MarkAttemptReportsReconciliationOnConnectionAsync(db,tx,job.AttemptId,"conflicting_outcome_commit",ct);
            await tx.CommitAsync(ct);
            throw new InvalidDataException("Outcome متناقض برای Attempt قبلاً ثبت شده است؛ reconciliation لازم است.");
        }

        if(existingOutcome is null)
        {
            await using var insert=db.CreateCommand();
            insert.Transaction=tx;
            insert.CommandText="""
            INSERT INTO attempt_outcomes(attempt_id,server_job_id,status,spooler_job_id,retryable,error_code,error_message,evidence_provenance,committed_at)
            VALUES($attempt,$job,$status,$spooler,$retryable,$code,$message,$provenance,$now)
            """;
            insert.Parameters.AddWithValue("$attempt",job.AttemptId);
            insert.Parameters.AddWithValue("$job",job.ServerJobId);
            insert.Parameters.AddWithValue("$status",outcome.Status.ToString());
            insert.Parameters.AddWithValue("$spooler",(object?)outcome.SpoolerJobId??DBNull.Value);
            insert.Parameters.AddWithValue("$retryable",outcome.Retryable?1:0);
            insert.Parameters.AddWithValue("$code",(object?)Bound(outcome.ErrorCode,160)??DBNull.Value);
            insert.Parameters.AddWithValue("$message",(object?)Bound(outcome.ErrorMessage,500)??DBNull.Value);
            insert.Parameters.AddWithValue("$provenance",Bound(outcome.EvidenceProvenance,200)??"unknown");
            insert.Parameters.AddWithValue("$now",now.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }

        ReportOutboxRow? existingReport=null;
        await using(var command=db.CreateCommand())
        {
            command.Transaction=tx;
            command.CommandText="SELECT * FROM report_outbox WHERE attempt_id=$attempt ORDER BY id LIMIT 1";
            command.Parameters.AddWithValue("$attempt",job.AttemptId);
            await using var reader=await command.ExecuteReaderAsync(ct);
            if(await reader.ReadAsync(ct))existingReport=ReadReport(reader);
        }

        if(existingReport is null)
        {
            var body=JsonSerializer.Serialize(report,AgentOptions.JsonOptions());
            await using var insert=db.CreateCommand();
            insert.Transaction=tx;
            insert.CommandText="""
            INSERT INTO report_outbox(
              server_job_id,attempt_id,request_id,body_json,created_at,sent_at,last_error,error_count,next_attempt_at,permanent_error,
              delivery_state,last_http_status,last_error_code,updated_at,agent_version,server_scope)
            VALUES($job,$attempt,$request,$body,$now,NULL,NULL,0,$now,0,'Pending',NULL,NULL,$now,$version,$scope)
            """;
            insert.Parameters.AddWithValue("$job",job.ServerJobId);
            insert.Parameters.AddWithValue("$attempt",job.AttemptId);
            insert.Parameters.AddWithValue("$request",report.RequestId);
            insert.Parameters.AddWithValue("$body",body);
            insert.Parameters.AddWithValue("$now",now.ToString("O"));
            insert.Parameters.AddWithValue("$version",report.AgentVersion);
            insert.Parameters.AddWithValue("$scope",job.ServerScope);
            await insert.ExecuteNonQueryAsync(ct);
            var id=db.LastInsertRowId;
            existingReport=new ReportOutboxRow(id,job.ServerJobId,job.AttemptId,report.RequestId,body,report.AgentVersion,job.ServerScope,ReportDeliveryState.Pending,0,now,now,now,null,null,null);
        }
        else
        {
            // The first committed outbox identity/body is authoritative. A recovery call may present a new generated
            // request id, but it must never replace the durable request that already exists.
            ReportRequestEnvelope persisted;
            try{persisted=JsonSerializer.Deserialize<ReportRequestEnvelope>(existingReport.BodyJson,AgentOptions.JsonOptions())??throw new JsonException("empty");}
            catch(Exception e){await MarkAttemptReportsReconciliationOnConnectionAsync(db,tx,job.AttemptId,"invalid_persisted_report_body",ct);await tx.CommitAsync(ct);throw new InvalidDataException("Report body پایدار قابل خواندن نیست.",e);}
            if(!ReportSemanticsEquivalent(persisted,report))
            {
                await MarkAttemptReportsReconciliationOnConnectionAsync(db,tx,job.AttemptId,"conflicting_report_body",ct);
                await tx.CommitAsync(ct);
                throw new InvalidDataException("Report body متناقض برای همان Outcome ثبت شده است.");
            }
        }

        var state=OutcomeToLocalState(outcome.Status);
        await using(var update=db.CreateCommand())
        {
            update.Transaction=tx;
            update.CommandText="UPDATE local_jobs SET state=$state,spooler_job_id=COALESCE($spooler,spooler_job_id),last_error=$error,updated_at=$now WHERE attempt_id=$attempt";
            update.Parameters.AddWithValue("$state",state.ToString());
            update.Parameters.AddWithValue("$spooler",(object?)outcome.SpoolerJobId??DBNull.Value);
            update.Parameters.AddWithValue("$error",(object?)Bound(outcome.ErrorMessage,500)??DBNull.Value);
            update.Parameters.AddWithValue("$now",now.ToString("O"));
            update.Parameters.AddWithValue("$attempt",job.AttemptId);
            if(await update.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("Local attempt برای Outcome پیدا نشد.");
        }
        await tx.CommitAsync(ct);
        return existingReport;
    }

    public async Task<bool> HasPendingReportAsync(long attemptId,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        // Delivery blocks/quarantine are still durable undelivered evidence and must suppress report invention.
        command.CommandText="SELECT EXISTS(SELECT 1 FROM report_outbox WHERE attempt_id=$attempt AND sent_at IS NULL AND delivery_state<>'Delivered')";
        command.Parameters.AddWithValue("$attempt",attemptId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct))==1;
    }

    // Compatibility helper used by legacy tests/tools. New production code should use CommitOutcomeAndReportAsync.
    public async Task EnqueueReportAsync(long jobId,long attemptId,string requestId,string bodyJson,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        var local=await GetByAttemptAsync(attemptId,ct)??throw new InvalidOperationException("Local attempt برای report پیدا نشد.");
        await using var command=db.CreateCommand();
        command.CommandText="""
        INSERT OR IGNORE INTO report_outbox(
          server_job_id,attempt_id,request_id,body_json,created_at,next_attempt_at,delivery_state,updated_at,agent_version,server_scope)
        VALUES($job,$attempt,$request,$body,$now,$now,'Pending',$now,$version,$scope)
        """;
        command.Parameters.AddWithValue("$job",jobId);
        command.Parameters.AddWithValue("$attempt",attemptId);
        command.Parameters.AddWithValue("$request",requestId);
        command.Parameters.AddWithValue("$body",bodyJson);
        command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$version",AgentVersionInfo.Current);
        command.Parameters.AddWithValue("$scope",local.ServerScope);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<ReportOutboxRow>> PendingReportsAsync(int limit=20,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="""
        SELECT * FROM report_outbox
        WHERE sent_at IS NULL
          AND delivery_state IN ('Pending','Backoff')
          AND (next_attempt_at IS NULL OR next_attempt_at<=$now)
        ORDER BY id
        LIMIT $limit
        """;
        command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$limit",limit);
        await using var reader=await command.ExecuteReaderAsync(ct);
        var rows=new List<ReportOutboxRow>();
        while(await reader.ReadAsync(ct))rows.Add(ReadReport(reader));
        return rows;
    }

    public async Task MarkReportSentAsync(long id,long attemptId,CancellationToken ct=default)
    {
        var now=DateTimeOffset.UtcNow;
        await using var db=await OpenAsync(ct);
        await using var tx=(SqliteTransaction)await db.BeginTransactionAsync(ct);
        await using(var update=db.CreateCommand())
        {
            update.Transaction=tx;
            update.CommandText="""
            UPDATE report_outbox
            SET sent_at=$now,last_error=NULL,last_error_code=NULL,last_http_status=NULL,
                delivery_state='Delivered',permanent_error=0,next_attempt_at=NULL,updated_at=$now
            WHERE id=$id AND attempt_id=$attempt AND sent_at IS NULL
            """;
            update.Parameters.AddWithValue("$now",now.ToString("O"));
            update.Parameters.AddWithValue("$id",id);
            update.Parameters.AddWithValue("$attempt",attemptId);
            var changed=await update.ExecuteNonQueryAsync(ct);
            if(changed is not (0 or 1))throw new InvalidOperationException("Report outbox row نامعتبر است.");
        }
        await using(var update=db.CreateCommand())
        {
            update.Transaction=tx;
            update.CommandText="UPDATE local_jobs SET state='Resolved',updated_at=$now WHERE attempt_id=$attempt";
            update.Parameters.AddWithValue("$now",now.ToString("O"));
            update.Parameters.AddWithValue("$attempt",attemptId);
            await update.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task MarkReportDeliveryAsync(long id,ReportDeliveryState state,string? message,int? httpStatus,string? errorCode,DateTimeOffset? nextAttemptAt,CancellationToken ct=default)
    {
        if(state==ReportDeliveryState.Delivered)throw new ArgumentException("Use MarkReportSentAsync for delivered reports.",nameof(state));
        var permanent=state is ReportDeliveryState.AuthBlocked or ReportDeliveryState.ReconciliationRequired;
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="""
        UPDATE report_outbox
        SET last_error=$err,
            last_error_code=$code,
            last_http_status=$http,
            error_count=error_count+1,
            permanent_error=$permanent,
            delivery_state=$state,
            next_attempt_at=$next,
            updated_at=$now
        WHERE id=$id AND sent_at IS NULL
        """;
        command.Parameters.AddWithValue("$err",(object?)Bound(message,500)??DBNull.Value);
        command.Parameters.AddWithValue("$code",(object?)Bound(errorCode,160)??DBNull.Value);
        command.Parameters.AddWithValue("$http",(object?)httpStatus??DBNull.Value);
        command.Parameters.AddWithValue("$permanent",permanent?1:0);
        command.Parameters.AddWithValue("$state",state.ToString());
        command.Parameters.AddWithValue("$next",(object?)nextAttemptAt?.ToString("O")??DBNull.Value);
        command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id",id);
        if(await command.ExecuteNonQueryAsync(ct)!=1)throw new InvalidOperationException("Report outbox row پیدا نشد یا قبلاً ACK شده است.");
    }

    // Compatibility helper: permanent=true maps to reconciliation instead of deleting report evidence.
    public Task MarkReportErrorAsync(long id,string message,bool permanent=false,CancellationToken ct=default)
        => MarkReportDeliveryAsync(id,permanent?ReportDeliveryState.ReconciliationRequired:ReportDeliveryState.Backoff,message,null,null,permanent?null:DateTimeOffset.UtcNow.AddSeconds(10),ct);

    public async Task<int> MarkServerScopeAuthBlockedAsync(string serverScope,string message,int? httpStatus,string? errorCode,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="""
        UPDATE report_outbox
        SET delivery_state='AuthBlocked',permanent_error=1,next_attempt_at=NULL,last_error=$err,last_http_status=$http,last_error_code=$code,updated_at=$now,error_count=error_count+1
        WHERE sent_at IS NULL AND server_scope=$scope AND delivery_state IN ('Pending','Backoff','AuthBlocked')
        """;
        command.Parameters.AddWithValue("$err",Bound(message,500)??"authentication blocked");
        command.Parameters.AddWithValue("$http",(object?)httpStatus??DBNull.Value);
        command.Parameters.AddWithValue("$code",(object?)Bound(errorCode,160)??DBNull.Value);
        command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$scope",serverScope);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> ResumeAuthBlockedReportsAsync(string? serverScope=null,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText=string.IsNullOrWhiteSpace(serverScope)
            ? "UPDATE report_outbox SET delivery_state='Pending',permanent_error=0,next_attempt_at=$now,last_error=NULL,last_error_code=NULL,last_http_status=NULL,updated_at=$now WHERE sent_at IS NULL AND delivery_state='AuthBlocked'"
            : "UPDATE report_outbox SET delivery_state='Pending',permanent_error=0,next_attempt_at=$now,last_error=NULL,last_error_code=NULL,last_http_status=NULL,updated_at=$now WHERE sent_at IS NULL AND delivery_state='AuthBlocked' AND server_scope=$scope";
        command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        if(!string.IsNullOrWhiteSpace(serverScope))command.Parameters.AddWithValue("$scope",serverScope);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> ResumeReconciliationReportAsync(long outboxId,CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="UPDATE report_outbox SET delivery_state='Pending',permanent_error=0,next_attempt_at=$now,last_error=NULL,last_error_code=NULL,last_http_status=NULL,updated_at=$now WHERE id=$id AND sent_at IS NULL AND delivery_state='ReconciliationRequired'";
        command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id",outboxId);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ReportStateCounts> GetReportStateCountsAsync(CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="""
        SELECT
          SUM(CASE WHEN sent_at IS NULL AND delivery_state='Pending' THEN 1 ELSE 0 END),
          SUM(CASE WHEN sent_at IS NULL AND delivery_state='Backoff' THEN 1 ELSE 0 END),
          SUM(CASE WHEN sent_at IS NULL AND delivery_state='AuthBlocked' THEN 1 ELSE 0 END),
          SUM(CASE WHEN sent_at IS NULL AND delivery_state='ReconciliationRequired' THEN 1 ELSE 0 END)
        FROM report_outbox
        """;
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))return new(0,0,0,0);
        return new(reader.IsDBNull(0)?0:reader.GetInt32(0),reader.IsDBNull(1)?0:reader.GetInt32(1),reader.IsDBNull(2)?0:reader.GetInt32(2),reader.IsDBNull(3)?0:reader.GetInt32(3));
    }

    public async Task<long?> GetOldestUndeliveredReportAgeSecondsAsync(CancellationToken ct=default)
    {
        await using var db=await OpenAsync(ct);
        await using var command=db.CreateCommand();
        command.CommandText="SELECT created_at FROM report_outbox WHERE sent_at IS NULL AND delivery_state<>'Delivered' ORDER BY created_at LIMIT 1";
        var raw=await command.ExecuteScalarAsync(ct);
        if(raw is null or DBNull)return null;
        if(!DateTimeOffset.TryParse(Convert.ToString(raw),out var created))return null;
        return Math.Max(0,(long)(DateTimeOffset.UtcNow-created).TotalSeconds);
    }

    private async Task MigrateLegacyOutcomesAsync(SqliteConnection db,CancellationToken ct)
    {
        var jobs=new List<LocalJob>();
        await using(var command=db.CreateCommand())
        {
            command.CommandText="""
            SELECT j.* FROM local_jobs j
            LEFT JOIN attempt_outcomes o ON o.attempt_id=j.attempt_id
            WHERE o.attempt_id IS NULL AND j.state<>'Resolved'
            ORDER BY j.attempt_id
            """;
            await using var reader=await command.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))jobs.Add(ReadJob(reader));
        }
        if(jobs.Count==0)
        {
            await NormalizeLegacyDeliveryStatesAsync(db,ct);
            return;
        }

        await using var tx=(SqliteTransaction)await db.BeginTransactionAsync(ct);
        foreach(var job in jobs)
        {
            var reports=new List<LegacyReport>();
            await using(var command=db.CreateCommand())
            {
                command.Transaction=tx;
                command.CommandText="SELECT id,request_id,body_json,created_at,sent_at,last_error,error_count,next_attempt_at,permanent_error,agent_version,server_scope FROM report_outbox WHERE attempt_id=$attempt ORDER BY id";
                command.Parameters.AddWithValue("$attempt",job.AttemptId);
                await using var reader=await command.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct))reports.Add(ReadLegacyReport(reader));
            }

            var parsed=reports.Select(r=>(Row:r,Parsed:TryParseLegacyOutcome(r.BodyJson))).ToList();
            var valid=parsed.Where(x=>x.Parsed is not null).ToList();
            var signatures=valid.Select(x=>LegacySignature(x.Parsed!)).Distinct(StringComparer.Ordinal).ToList();

            if(reports.Count>0 && valid.Count==reports.Count && signatures.Count==1)
            {
                var semantic=valid[0].Parsed!;
                await InsertOutcomeOnConnectionAsync(db,tx,job,semantic,"migration:legacy-report",ct);
                var canonical=reports[0];
                var envelope=new ReportRequestEnvelope(canonical.RequestId,string.IsNullOrWhiteSpace(canonical.AgentVersion)?"6.2.0":canonical.AgentVersion,4,job.AttemptId,job.LocalReceiptId,ToWireStatus(semantic.Status),semantic.SpoolerJobId,semantic.Retryable,semantic.ErrorCode,semantic.ErrorMessage);
                var delivery=LegacyDelivery(canonical);
                await UpdateLegacyReportCanonicalAsync(db,tx,canonical.Id,envelope,delivery,ct);
                foreach(var duplicate in reports.Skip(1))
                {
                    await MarkLegacyReportReconciliationAsync(db,tx,duplicate.Id,"legacy_duplicate_report_row_preserved",ct);
                }
                await UpdateJobStateOnConnectionAsync(db,tx,job.AttemptId,OutcomeToLocalState(semantic.Status),semantic.SpoolerJobId,semantic.ErrorMessage,ct);
                continue;
            }

            if(reports.Count>0)
            {
                var hold=new AttemptOutcomeDraft(PrintOutcomeStatus.RecoveryHold,job.SpoolerJobId,false,"legacy_conflicting_report_evidence","Legacy ReportPending دارای شواهد متناقض/نامعتبر است.","migration:legacy-conflict");
                await InsertOutcomeOnConnectionAsync(db,tx,job,hold,hold.EvidenceProvenance,ct);
                await UpdateJobStateOnConnectionAsync(db,tx,job.AttemptId,LocalJobState.RecoveryHold,job.SpoolerJobId,hold.ErrorMessage,ct);
                foreach(var report in reports)await MarkLegacyReportReconciliationAsync(db,tx,report.Id,"legacy_conflicting_report_evidence",ct);
                continue;
            }

            AttemptOutcomeDraft? derived=job.State switch
            {
                LocalJobState.Submitted when !string.IsNullOrWhiteSpace(job.SpoolerJobId)=>new(PrintOutcomeStatus.Submitted,job.SpoolerJobId,false,null,null,"migration:local-submitted"),
                LocalJobState.SafeFailed=>new(PrintOutcomeStatus.Failed,null,true,"legacy_safe_failed",job.LastError,"migration:local-safe-failed"),
                LocalJobState.Unknown=>new(PrintOutcomeStatus.Unknown,job.SpoolerJobId,false,"legacy_unknown",job.LastError,"migration:local-unknown"),
                LocalJobState.RecoveryHold=>new(PrintOutcomeStatus.RecoveryHold,job.SpoolerJobId,false,"legacy_recovery_hold",job.LastError,"migration:local-hold"),
                LocalJobState.ReportPending=>new(PrintOutcomeStatus.RecoveryHold,job.SpoolerJobId,false,"legacy_reportpending_missing_report","ReportPending قدیمی بدون Report body معتبر؛ submitted حدس زده نمی‌شود.","migration:reportpending-missing-report"),
                _=>null
            };
            if(derived is null)continue;
            await InsertOutcomeOnConnectionAsync(db,tx,job,derived,derived.EvidenceProvenance,ct);
            var request=new ReportRequestEnvelope(CryptoUtil.NewRequestId(),AgentVersionInfo.Current,4,job.AttemptId,job.LocalReceiptId,ToWireStatus(derived.Status),derived.SpoolerJobId,derived.Retryable,derived.ErrorCode,derived.ErrorMessage);
            await InsertReportOnConnectionAsync(db,tx,job,request,ReportDeliveryState.Pending,ct);
            await UpdateJobStateOnConnectionAsync(db,tx,job.AttemptId,OutcomeToLocalState(derived.Status),derived.SpoolerJobId,derived.ErrorMessage,ct);
        }
        await tx.CommitAsync(ct);
        await NormalizeLegacyDeliveryStatesAsync(db,ct);
    }

    private static async Task NormalizeLegacyDeliveryStatesAsync(SqliteConnection db,CancellationToken ct)
    {
        await using var command=db.CreateCommand();
        command.CommandText="""
        UPDATE report_outbox
        SET delivery_state=CASE
              WHEN sent_at IS NOT NULL THEN 'Delivered'
              WHEN permanent_error=1 AND (last_http_status=401 OR lower(COALESCE(last_error,'')) LIKE '%401%' OR lower(COALESCE(last_error_code,'')) LIKE '%auth%') THEN 'AuthBlocked'
              WHEN permanent_error=1 THEN 'ReconciliationRequired'
              WHEN next_attempt_at IS NOT NULL AND next_attempt_at>=$now THEN 'Backoff'
              ELSE 'Pending'
            END,
            updated_at=COALESCE(updated_at,created_at),
            agent_version=COALESCE(NULLIF(agent_version,''),'6.2.0'),
            server_scope=COALESCE(NULLIF(server_scope,''),'legacy-unbound')
        WHERE delivery_state IS NULL OR delivery_state='' OR (delivery_state='Pending' AND (sent_at IS NOT NULL OR permanent_error=1))
        """;
        command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertOutcomeOnConnectionAsync(SqliteConnection db,SqliteTransaction tx,LocalJob job,AttemptOutcomeDraft outcome,string provenance,CancellationToken ct)
    {
        await using var command=db.CreateCommand();
        command.Transaction=tx;
        command.CommandText="INSERT OR IGNORE INTO attempt_outcomes(attempt_id,server_job_id,status,spooler_job_id,retryable,error_code,error_message,evidence_provenance,committed_at) VALUES($attempt,$job,$status,$spooler,$retryable,$code,$message,$provenance,$now)";
        command.Parameters.AddWithValue("$attempt",job.AttemptId);
        command.Parameters.AddWithValue("$job",job.ServerJobId);
        command.Parameters.AddWithValue("$status",outcome.Status.ToString());
        command.Parameters.AddWithValue("$spooler",(object?)outcome.SpoolerJobId??DBNull.Value);
        command.Parameters.AddWithValue("$retryable",outcome.Retryable?1:0);
        command.Parameters.AddWithValue("$code",(object?)Bound(outcome.ErrorCode,160)??DBNull.Value);
        command.Parameters.AddWithValue("$message",(object?)Bound(outcome.ErrorMessage,500)??DBNull.Value);
        command.Parameters.AddWithValue("$provenance",Bound(provenance,200)??"migration");
        command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertReportOnConnectionAsync(SqliteConnection db,SqliteTransaction tx,LocalJob job,ReportRequestEnvelope request,ReportDeliveryState state,CancellationToken ct)
    {
        var now=DateTimeOffset.UtcNow.ToString("O");
        await using var command=db.CreateCommand();
        command.Transaction=tx;
        command.CommandText="INSERT INTO report_outbox(server_job_id,attempt_id,request_id,body_json,created_at,next_attempt_at,delivery_state,permanent_error,updated_at,agent_version,server_scope) VALUES($job,$attempt,$request,$body,$now,$next,$state,$permanent,$now,$version,$scope)";
        command.Parameters.AddWithValue("$job",job.ServerJobId);
        command.Parameters.AddWithValue("$attempt",job.AttemptId);
        command.Parameters.AddWithValue("$request",request.RequestId);
        command.Parameters.AddWithValue("$body",JsonSerializer.Serialize(request,AgentOptions.JsonOptions()));
        command.Parameters.AddWithValue("$now",now);
        command.Parameters.AddWithValue("$next",state is ReportDeliveryState.Pending or ReportDeliveryState.Backoff?now:DBNull.Value);
        command.Parameters.AddWithValue("$state",state.ToString());
        command.Parameters.AddWithValue("$permanent",state is ReportDeliveryState.AuthBlocked or ReportDeliveryState.ReconciliationRequired?1:0);
        command.Parameters.AddWithValue("$version",request.AgentVersion);
        command.Parameters.AddWithValue("$scope",job.ServerScope);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateLegacyReportCanonicalAsync(SqliteConnection db,SqliteTransaction tx,long id,ReportRequestEnvelope envelope,ReportDeliveryState state,CancellationToken ct)
    {
        await using var command=db.CreateCommand();
        command.Transaction=tx;
        command.CommandText="UPDATE report_outbox SET body_json=$body,delivery_state=$state,permanent_error=$permanent,updated_at=$now,agent_version=$version WHERE id=$id";
        command.Parameters.AddWithValue("$body",JsonSerializer.Serialize(envelope,AgentOptions.JsonOptions()));
        command.Parameters.AddWithValue("$state",state.ToString());
        command.Parameters.AddWithValue("$permanent",state is ReportDeliveryState.AuthBlocked or ReportDeliveryState.ReconciliationRequired?1:0);
        command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$version",envelope.AgentVersion);
        command.Parameters.AddWithValue("$id",id);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task MarkLegacyReportReconciliationAsync(SqliteConnection db,SqliteTransaction tx,long id,string code,CancellationToken ct)
    {
        await using var command=db.CreateCommand();
        command.Transaction=tx;
        command.CommandText="UPDATE report_outbox SET delivery_state='ReconciliationRequired',permanent_error=1,next_attempt_at=NULL,last_error_code=$code,last_error=$code,updated_at=$now WHERE id=$id AND sent_at IS NULL";
        command.Parameters.AddWithValue("$code",code);
        command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id",id);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task MarkAttemptReportsReconciliationOnConnectionAsync(SqliteConnection db,SqliteTransaction tx,long attemptId,string code,CancellationToken ct)
    {
        await using var command=db.CreateCommand();
        command.Transaction=tx;
        command.CommandText="UPDATE report_outbox SET delivery_state='ReconciliationRequired',permanent_error=1,next_attempt_at=NULL,last_error_code=$code,last_error=$code,updated_at=$now WHERE attempt_id=$attempt AND sent_at IS NULL";
        command.Parameters.AddWithValue("$code",code);
        command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$attempt",attemptId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateJobStateOnConnectionAsync(SqliteConnection db,SqliteTransaction tx,long attemptId,LocalJobState state,string? spooler,string? error,CancellationToken ct)
    {
        await using var command=db.CreateCommand();
        command.Transaction=tx;
        command.CommandText="UPDATE local_jobs SET state=$state,spooler_job_id=COALESCE($spooler,spooler_job_id),last_error=$error,updated_at=$now WHERE attempt_id=$attempt";
        command.Parameters.AddWithValue("$state",state.ToString());
        command.Parameters.AddWithValue("$spooler",(object?)spooler??DBNull.Value);
        command.Parameters.AddWithValue("$error",(object?)Bound(error,500)??DBNull.Value);
        command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$attempt",attemptId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static LegacyReport ReadLegacyReport(SqliteDataReader reader)
        => new(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("request_id")),
            reader.GetString(reader.GetOrdinal("body_json")),
            reader.GetString(reader.GetOrdinal("created_at")),
            reader.IsDBNull(reader.GetOrdinal("sent_at"))?null:reader.GetString(reader.GetOrdinal("sent_at")),
            reader.IsDBNull(reader.GetOrdinal("last_error"))?null:reader.GetString(reader.GetOrdinal("last_error")),
            reader.GetInt32(reader.GetOrdinal("error_count")),
            reader.IsDBNull(reader.GetOrdinal("next_attempt_at"))?null:reader.GetString(reader.GetOrdinal("next_attempt_at")),
            reader.GetInt32(reader.GetOrdinal("permanent_error"))!=0,
            reader.IsDBNull(reader.GetOrdinal("agent_version"))?"6.2.0":reader.GetString(reader.GetOrdinal("agent_version")),
            reader.IsDBNull(reader.GetOrdinal("server_scope"))?LegacyServerScope:reader.GetString(reader.GetOrdinal("server_scope")));

    private static AttemptOutcomeDraft? TryParseLegacyOutcome(string body)
    {
        try
        {
            using var doc=JsonDocument.Parse(body);
            var root=doc.RootElement;
            if(!root.TryGetProperty("status",out var statusElement)||statusElement.ValueKind!=JsonValueKind.String)return null;
            var status=ParseWireStatus(statusElement.GetString());
            if(status is null)return null;
            var spooler=root.TryGetProperty("spooler_job_id",out var sp)&&sp.ValueKind==JsonValueKind.String?sp.GetString():null;
            var retryable=root.TryGetProperty("retryable",out var rr)&&rr.ValueKind==JsonValueKind.True;
            var code=root.TryGetProperty("error_code",out var ec)&&ec.ValueKind==JsonValueKind.String?ec.GetString():null;
            var message=root.TryGetProperty("error_message",out var em)&&em.ValueKind==JsonValueKind.String?em.GetString():null;
            return new AttemptOutcomeDraft(status.Value,spooler,retryable,code,message,"migration:legacy-report");
        }
        catch(JsonException){return null;}
    }

    private static string LegacySignature(AttemptOutcomeDraft outcome)
        => string.Join("|",outcome.Status,outcome.SpoolerJobId??"",outcome.Retryable?"1":"0",outcome.ErrorCode??"");

    private static ReportDeliveryState LegacyDelivery(LegacyReport row)
    {
        if(row.SentAt is not null)return ReportDeliveryState.Delivered;
        if(row.PermanentError)
        {
            var combined=(row.LastError??"")+" ";
            if(combined.Contains("401",StringComparison.OrdinalIgnoreCase)||combined.Contains("unauthor",StringComparison.OrdinalIgnoreCase)||combined.Contains("auth",StringComparison.OrdinalIgnoreCase))return ReportDeliveryState.AuthBlocked;
            return ReportDeliveryState.ReconciliationRequired;
        }
        if(row.NextAttemptAt is not null&&DateTimeOffset.TryParse(row.NextAttemptAt,out var next)&&next>DateTimeOffset.UtcNow)return ReportDeliveryState.Backoff;
        return ReportDeliveryState.Pending;
    }

    private static bool OutcomeEquivalent(AttemptOutcome existing,AttemptOutcomeDraft incoming,long serverJobId)
        => existing.ServerJobId==serverJobId &&
           existing.Status==incoming.Status &&
           string.Equals(existing.SpoolerJobId??"",incoming.SpoolerJobId??"",StringComparison.Ordinal) &&
           existing.Retryable==incoming.Retryable &&
           string.Equals(existing.ErrorCode??"",incoming.ErrorCode??"",StringComparison.Ordinal);

    private static bool ReportSemanticsEquivalent(ReportRequestEnvelope left,ReportRequestEnvelope right)
        => left.AttemptId==right.AttemptId &&
           string.Equals(left.LocalReceiptId,right.LocalReceiptId,StringComparison.Ordinal) &&
           string.Equals(left.Status,right.Status,StringComparison.Ordinal) &&
           string.Equals(left.SpoolerJobId??"",right.SpoolerJobId??"",StringComparison.Ordinal) &&
           left.Retryable==right.Retryable &&
           string.Equals(left.ErrorCode??"",right.ErrorCode??"",StringComparison.Ordinal);

    private static void ValidateReportEnvelope(LocalJob job,AttemptOutcomeDraft outcome,ReportRequestEnvelope report)
    {
        if(report.AttemptId!=job.AttemptId)throw new InvalidDataException("Report attempt_id با Local Attempt تطابق ندارد.");
        if(!string.Equals(report.LocalReceiptId,job.LocalReceiptId,StringComparison.Ordinal))throw new InvalidDataException("Report local_receipt_id با Local Attempt تطابق ندارد.");
        if(report.ProtocolVersion!=4)throw new InvalidDataException("Report protocol_version نامعتبر است.");
        if(string.IsNullOrWhiteSpace(report.RequestId)||string.IsNullOrWhiteSpace(report.AgentVersion))throw new InvalidDataException("Report identity ناقص است.");
        if(!string.Equals(report.Status,ToWireStatus(outcome.Status),StringComparison.Ordinal))throw new InvalidDataException("Report status با Outcome تطابق ندارد.");
        if(!string.Equals(report.SpoolerJobId??"",outcome.SpoolerJobId??"",StringComparison.Ordinal))throw new InvalidDataException("Report spooler_job_id با Outcome تطابق ندارد.");
    }

    private static PrintOutcomeStatus? ParseWireStatus(string? status)=>status switch
    {
        "submitted"=>PrintOutcomeStatus.Submitted,
        "failed"=>PrintOutcomeStatus.Failed,
        "unknown"=>PrintOutcomeStatus.Unknown,
        "recovery_hold"=>PrintOutcomeStatus.RecoveryHold,
        _=>null
    };

    public static string ToWireStatus(PrintOutcomeStatus status)=>status switch
    {
        PrintOutcomeStatus.Submitted=>"submitted",
        PrintOutcomeStatus.Failed=>"failed",
        PrintOutcomeStatus.Unknown=>"unknown",
        _=>"recovery_hold"
    };

    private static LocalJobState OutcomeToLocalState(PrintOutcomeStatus status)=>status switch
    {
        PrintOutcomeStatus.Submitted=>LocalJobState.Submitted,
        PrintOutcomeStatus.Failed=>LocalJobState.SafeFailed,
        PrintOutcomeStatus.Unknown=>LocalJobState.Unknown,
        _=>LocalJobState.RecoveryHold
    };

    private static LocalJob ReadJob(SqliteDataReader reader)
        => new(
            reader.GetInt64(reader.GetOrdinal("server_job_id")),
            reader.GetInt64(reader.GetOrdinal("attempt_id")),
            reader.GetInt32(reader.GetOrdinal("attempt_no")),
            reader.GetString(reader.GetOrdinal("destination_key")),
            reader.GetString(reader.GetOrdinal("queue_name")),
            reader.GetDouble(reader.GetOrdinal("paper_width_mm")),
            reader.GetDouble(reader.GetOrdinal("printable_width_mm")),
            reader.GetInt32(reader.GetOrdinal("copies")),
            reader.GetString(reader.GetOrdinal("layout_mode")),
            reader.GetString(reader.GetOrdinal("payload_json")),
            reader.GetString(reader.GetOrdinal("content_sha256")),
            reader.GetString(reader.GetOrdinal("local_receipt_id")),
            reader.GetString(reader.GetOrdinal("protected_lease_token")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("lease_expires_at"))),
            Enum.Parse<LocalJobState>(reader.GetString(reader.GetOrdinal("state"))),
            reader.IsDBNull(reader.GetOrdinal("spooler_job_id"))?null:reader.GetString(reader.GetOrdinal("spooler_job_id")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
            reader.IsDBNull(reader.GetOrdinal("worker_launching_at"))?null:DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("worker_launching_at"))),
            reader.IsDBNull(reader.GetOrdinal("last_error"))?null:reader.GetString(reader.GetOrdinal("last_error")),
            reader.IsDBNull(reader.GetOrdinal("server_scope"))?LegacyServerScope:reader.GetString(reader.GetOrdinal("server_scope")));

    private static AttemptOutcome ReadOutcome(SqliteDataReader reader)
        => new(
            reader.GetInt64(reader.GetOrdinal("attempt_id")),
            reader.GetInt64(reader.GetOrdinal("server_job_id")),
            Enum.Parse<PrintOutcomeStatus>(reader.GetString(reader.GetOrdinal("status"))),
            reader.IsDBNull(reader.GetOrdinal("spooler_job_id"))?null:reader.GetString(reader.GetOrdinal("spooler_job_id")),
            reader.GetInt32(reader.GetOrdinal("retryable"))!=0,
            reader.IsDBNull(reader.GetOrdinal("error_code"))?null:reader.GetString(reader.GetOrdinal("error_code")),
            reader.IsDBNull(reader.GetOrdinal("error_message"))?null:reader.GetString(reader.GetOrdinal("error_message")),
            reader.GetString(reader.GetOrdinal("evidence_provenance")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("committed_at"))));

    private static ReportOutboxRow ReadReport(SqliteDataReader reader)
    {
        var created=DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at")));
        var updated=reader.IsDBNull(reader.GetOrdinal("updated_at"))?created:DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")));
        var next=reader.IsDBNull(reader.GetOrdinal("next_attempt_at"))?(DateTimeOffset?)null:DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("next_attempt_at")));
        var stateRaw=reader.IsDBNull(reader.GetOrdinal("delivery_state"))?"Pending":reader.GetString(reader.GetOrdinal("delivery_state"));
        if(!Enum.TryParse<ReportDeliveryState>(stateRaw,out var state))state=ReportDeliveryState.ReconciliationRequired;
        return new ReportOutboxRow(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetInt64(reader.GetOrdinal("server_job_id")),
            reader.GetInt64(reader.GetOrdinal("attempt_id")),
            reader.GetString(reader.GetOrdinal("request_id")),
            reader.GetString(reader.GetOrdinal("body_json")),
            reader.IsDBNull(reader.GetOrdinal("agent_version"))?"6.2.0":reader.GetString(reader.GetOrdinal("agent_version")),
            reader.IsDBNull(reader.GetOrdinal("server_scope"))?LegacyServerScope:reader.GetString(reader.GetOrdinal("server_scope")),
            state,
            reader.GetInt32(reader.GetOrdinal("error_count")),
            created,
            updated,
            next,
            reader.IsDBNull(reader.GetOrdinal("last_http_status"))?null:reader.GetInt32(reader.GetOrdinal("last_http_status")),
            reader.IsDBNull(reader.GetOrdinal("last_error_code"))?null:reader.GetString(reader.GetOrdinal("last_error_code")),
            reader.IsDBNull(reader.GetOrdinal("last_error"))?null:reader.GetString(reader.GetOrdinal("last_error")));
    }

    private static bool TryParseExplicitOffset(string value,out DateTimeOffset result)
    {
        result=default;
        if(string.IsNullOrWhiteSpace(value))return false;
        var text=value.Trim();
        var hasZulu=text.EndsWith('Z');
        var offsetIndex=Math.Max(text.LastIndexOf('+'),text.LastIndexOf('-'));
        var timeIndex=text.IndexOf('T');
        var hasOffset=hasZulu || (offsetIndex>timeIndex && offsetIndex>=0);
        return hasOffset&&DateTimeOffset.TryParse(text,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out result);
    }

    private static string? Bound(string? value,int max)
        => string.IsNullOrWhiteSpace(value)?value:value.Length<=max?value:value[..max];

    private sealed record LegacyReport(long Id,string RequestId,string BodyJson,string CreatedAt,string? SentAt,string? LastError,int ErrorCount,string? NextAttemptAt,bool PermanentError,string AgentVersion,string ServerScope);
}
