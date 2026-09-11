using Microsoft.Data.Sqlite;

namespace Sokna.PrintAgent.Core;

public sealed record QueueDatabasePreparation(bool ReinitializedLegacyEmptyDatabase,string? BackupPath);

public static class QueueDatabaseBootstrap
{
    public static async Task<QueueDatabasePreparation> PrepareAsync(string databasePath,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(databasePath))throw new ArgumentException("Database path is required.",nameof(databasePath));
        var full=Path.GetFullPath(databasePath);
        if(!File.Exists(full))return new(false,null);

        var requiresSafeReinitialize=false;
        try
        {
            var cs=new SqliteConnectionStringBuilder
            {
                DataSource=full,
                Mode=SqliteOpenMode.ReadWrite,
                Cache=SqliteCacheMode.Private,
                Pooling=false
            }.ToString();

            await using var db=new SqliteConnection(cs);
            await db.OpenAsync(ct);

            await using(var quick=db.CreateCommand())
            {
                quick.CommandText="PRAGMA quick_check(1);";
                var result=Convert.ToString(await quick.ExecuteScalarAsync(ct));
                if(!string.Equals(result,"ok",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SQLite queue.db quick_check failed: {Bound(result,240)}");
            }

            if(!await TableExistsAsync(db,"local_jobs",ct))return new(false,null);

            await using(var schema=db.CreateCommand())
            {
                schema.CommandText="SELECT name,pk FROM pragma_table_info('local_jobs') WHERE name IN ('attempt_id','server_job_id') ORDER BY name";
                await using var reader=await schema.ExecuteReaderAsync(ct);
                var keys=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
                while(await reader.ReadAsync(ct))keys[reader.GetString(0)]=reader.GetInt64(1);
                if(keys.TryGetValue("attempt_id",out var attemptPk)&&attemptPk==1)
                {
                    // 6.2.0 already uses attempt_id as the durable primary key, so this is a compatible
                    // v3 database and must be upgraded in-place. 6.2.1 accidentally tried to create
                    // indexes on delivery_state/server_scope before those columns were added, which made
                    // real 6.2.0 -> 6.2.1 upgrades stop the Windows Service. Pre-add every v4 column here
                    // inside one SQLite transaction; LocalQueueStore can then create dependent tables/
                    // indexes and perform semantic legacy-outcome migration without ever deleting rows.
                    await PrepareCompatibleV3ColumnsAsync(db,ct);
                    return new(false,null);
                }
            }

            var localRows=await CountRowsAsync(db,"local_jobs",ct);
            var outboxRows=await TableExistsAsync(db,"report_outbox",ct)?await CountRowsAsync(db,"report_outbox",ct):0;
            if(localRows!=0||outboxRows!=0)
                throw new InvalidDataException($"SQLite local queue schema is legacy/incompatible and contains durable data (local_jobs={localRows}, report_outbox={outboxRows}). Automatic reset is blocked to prevent duplicate printing. Preserve queue.db and run an explicit migration/reconciliation procedure.");

            requiresSafeReinitialize=true;
        }
        catch(SqliteException e)
        {
            throw new InvalidDataException($"SQLite queue.db could not be validated: {Bound(e.Message,300)}",e);
        }

        if(!requiresSafeReinitialize)return new(false,null);

        var stamp=DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        var backup=full+$".legacy-empty-{stamp}-{Guid.NewGuid():N}.bak";
        MoveIfExists(full,backup);
        MoveIfExists(full+"-wal",backup+"-wal");
        MoveIfExists(full+"-shm",backup+"-shm");
        return new(true,backup);
    }

    private static async Task PrepareCompatibleV3ColumnsAsync(SqliteConnection db,CancellationToken ct)
    {
        await using var tx=(SqliteTransaction)await db.BeginTransactionAsync(ct);
        try
        {
            await EnsureColumnAsync(db,tx,"local_jobs","server_scope","TEXT NOT NULL DEFAULT 'legacy-unbound'",ct);

            if(await TableExistsAsync(db,tx,"report_outbox",ct))
            {
                await EnsureColumnAsync(db,tx,"report_outbox","error_count","INTEGER NOT NULL DEFAULT 0",ct);
                await EnsureColumnAsync(db,tx,"report_outbox","next_attempt_at","TEXT NULL",ct);
                await EnsureColumnAsync(db,tx,"report_outbox","permanent_error","INTEGER NOT NULL DEFAULT 0",ct);
                await EnsureColumnAsync(db,tx,"report_outbox","delivery_state","TEXT NOT NULL DEFAULT 'Pending'",ct);
                await EnsureColumnAsync(db,tx,"report_outbox","last_http_status","INTEGER NULL",ct);
                await EnsureColumnAsync(db,tx,"report_outbox","last_error_code","TEXT NULL",ct);
                await EnsureColumnAsync(db,tx,"report_outbox","updated_at","TEXT NULL",ct);
                await EnsureColumnAsync(db,tx,"report_outbox","agent_version","TEXT NOT NULL DEFAULT '6.2.0'",ct);
                await EnsureColumnAsync(db,tx,"report_outbox","server_scope","TEXT NOT NULL DEFAULT 'legacy-unbound'",ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            try{await tx.RollbackAsync(CancellationToken.None);}catch{}
            throw;
        }
    }

    private static async Task EnsureColumnAsync(SqliteConnection db,SqliteTransaction tx,string table,string column,string definition,CancellationToken ct)
    {
        await using var check=db.CreateCommand();
        check.Transaction=tx;
        check.CommandText=$"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$name";
        check.Parameters.AddWithValue("$name",column);
        if(Convert.ToInt32(await check.ExecuteScalarAsync(ct))!=0)return;

        await using var alter=db.CreateCommand();
        alter.Transaction=tx;
        alter.CommandText=$"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        await alter.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection db,SqliteTransaction tx,string table,CancellationToken ct)
    {
        await using var command=db.CreateCommand();
        command.Transaction=tx;
        command.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name)";
        command.Parameters.AddWithValue("$name",table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct))==1;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection db,string table,CancellationToken ct)
    {
        await using var command=db.CreateCommand();
        command.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name)";
        command.Parameters.AddWithValue("$name",table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct))==1;
    }

    private static async Task<long> CountRowsAsync(SqliteConnection db,string table,CancellationToken ct)
    {
        if(table is not ("local_jobs" or "report_outbox"))throw new ArgumentOutOfRangeException(nameof(table));
        await using var command=db.CreateCommand();
        command.CommandText=$"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static void MoveIfExists(string source,string destination)
    {
        if(File.Exists(source))File.Move(source,destination,false);
    }

    private static string Bound(string? value,int max)
    {
        if(string.IsNullOrWhiteSpace(value))return "unspecified";
        var safe=SafeLogText.Sanitize(value,max);
        return string.IsNullOrWhiteSpace(safe)?"unspecified":safe;
    }
}
