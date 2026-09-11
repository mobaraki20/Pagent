using Microsoft.Data.Sqlite;

if(args.Length!=2)
{
    Console.Error.WriteLine("usage: Sokna.PrintAgent.V620SchemaTool <seed|verify> <queue.db>");
    return 2;
}

var mode=args[0].Trim().ToLowerInvariant();
var path=Path.GetFullPath(args[1]);
try
{
    if(mode=="seed")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach(var candidate in new[]{path,path+"-wal",path+"-shm"})
            if(File.Exists(candidate))File.Delete(candidate);
        await SeedAsync(path);
        Console.WriteLine("V620_SCHEMA_SEED=success");
        return 0;
    }
    if(mode=="verify")
    {
        await VerifyAsync(path);
        Console.WriteLine("V620_SCHEMA_VERIFY=success");
        return 0;
    }
    Console.Error.WriteLine("Unknown mode: "+mode);
    return 2;
}
catch(Exception ex)
{
    Console.Error.WriteLine($"V620_SCHEMA_{mode.ToUpperInvariant()}=failure type={ex.GetType().Name} message={ex.Message.Replace('\r',' ').Replace('\n',' ')}");
    return 1;
}

static async Task SeedAsync(string path)
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

static async Task VerifyAsync(string path)
{
    if(!File.Exists(path))throw new FileNotFoundException("queue.db missing",path);
    await using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWrite}.ToString());
    await db.OpenAsync();
    if(await ScalarStringAsync(db,"SELECT value FROM agent_meta WHERE key='schema_version'")!="4")throw new InvalidDataException("schema_version is not 4");
    foreach(var column in new[]{"delivery_state","last_http_status","last_error_code","updated_at","agent_version","server_scope"})
        if(!await ColumnExistsAsync(db,"report_outbox",column))throw new InvalidDataException($"report_outbox.{column} missing");
    if(!await ColumnExistsAsync(db,"local_jobs","server_scope"))throw new InvalidDataException("local_jobs.server_scope missing");
    if(!await IndexExistsAsync(db,"idx_report_delivery_state"))throw new InvalidDataException("idx_report_delivery_state missing");
    if(!await IndexExistsAsync(db,"idx_report_server_scope"))throw new InvalidDataException("idx_report_server_scope missing");
    if(await ScalarLongAsync(db,"SELECT COUNT(*) FROM local_jobs")!=2)throw new InvalidDataException("local_jobs durable rows changed");
    if(await ScalarLongAsync(db,"SELECT COUNT(*) FROM report_outbox")!=1)throw new InvalidDataException("report_outbox durable rows changed");
    if(await ScalarStringAsync(db,"SELECT delivery_state FROM report_outbox WHERE id=1")!="Delivered")throw new InvalidDataException("sent report did not normalize to Delivered");
}

static async Task<bool> ColumnExistsAsync(SqliteConnection db,string table,string column)
{
    await using var command=db.CreateCommand();
    command.CommandText=$"SELECT EXISTS(SELECT 1 FROM pragma_table_info('{table}') WHERE name=$name)";
    command.Parameters.AddWithValue("$name",column);
    return Convert.ToInt32(await command.ExecuteScalarAsync())==1;
}

static async Task<bool> IndexExistsAsync(SqliteConnection db,string name)
{
    await using var command=db.CreateCommand();
    command.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='index' AND name=$name)";
    command.Parameters.AddWithValue("$name",name);
    return Convert.ToInt32(await command.ExecuteScalarAsync())==1;
}

static async Task<long> ScalarLongAsync(SqliteConnection db,string sql)
{
    await using var command=db.CreateCommand();
    command.CommandText=sql;
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static async Task<string?> ScalarStringAsync(SqliteConnection db,string sql)
{
    await using var command=db.CreateCommand();
    command.CommandText=sql;
    return Convert.ToString(await command.ExecuteScalarAsync());
}
