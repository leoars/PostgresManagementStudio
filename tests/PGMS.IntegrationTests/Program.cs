using System.Data;
using System.Security;
using System.Security.Cryptography;
using Npgsql;
using PGMS.PostgreSql;

// Only disposable local clusters are used: the runner's source and the
// global-backup fixture's fresh target under artifacts/global-backup.
string connection = $"Host=127.0.0.1;Port={TestClusterEndpoint.Port};Database=postgres;Username=pgms_test;SSL Mode=Disable;Pooling=false;Application Name=PGMS.IntegrationTests;Timeout=3";
if(args.Length==4 && args[0]=="--restore-stage-child"){await RestoreInputStagingChecks.ChildAsync(args[1],args[2],args[3]);return;}
int passed = 0;
if(args.Contains("--test-port-guards"))
{
    Check(TestClusterEndpoint.ParsePort(null) == 55439, "Missing port keeps the isolated default");
    Check(TestClusterEndpoint.ParsePort("55441") == 55441, "Explicit isolated port is preserved");
    Check(TestClusterEndpoint.ParsePort("1024") == 1024 && TestClusterEndpoint.ParsePort("65535") == 65535, "Valid port boundaries accepted");
    foreach (string invalid in new[] { "0", "1023", "65536", "-1", "+55441", " 55441", "55441;Host=remote", "" })
    {
        bool rejected = false;
        try { _ = TestClusterEndpoint.ParsePort(invalid); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Invalid isolated port refused: " + invalid);
    }
    Console.WriteLine($"{passed} test port guard checks passed.");
    return;
}
if(args.Contains("--sessions")){await QuerySessionInfoChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} session checks passed.");return;}
if(args.Contains("--plans")){await QueryPlanChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} plan checks passed.");return;}
if(args.Contains("--export-roundtrip")){await ResultExportChecks.RunAsync(Check);await ExportRoundtripChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} export roundtrip checks passed.");return;}
if(args.Length==0)await ExportRoundtripChecks.RunAsync(connection,Check);
if(args.Contains("--export")){await ResultExportChecks.RunAsync(Check);Console.WriteLine($"{passed} export checks passed.");return;}
if(args.Length==0)await ResultExportChecks.RunAsync(Check);
if(args.Contains("--activity")){await ActivityTimingChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} activity checks passed.");return;}
if(args.Length==0)await ActivityTimingChecks.RunAsync(connection,Check);
if(args.Contains("--query-statistics")){await QueryStatisticsChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} query statistics checks passed.");return;}
if(args.Length==0)await QueryStatisticsChecks.RunAsync(connection,Check);
if(args.Contains("--global-backup")){await GlobalBackupChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} global backup checks passed.");return;}
if(args.Length==0)await GlobalBackupChecks.RunAsync(connection,Check);
if(args.Contains("--column-types")){await ColumnTypeChecks.RunAsync(connection,Check);await EnumEditChecks.RunAsync(connection,Check);await DomainEditChecks.RunAsync(connection,Check);await CreateUserTypeChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} column type checks passed.");return;}
if(args.Length==0){await ColumnTypeChecks.RunAsync(connection,Check);await EnumEditChecks.RunAsync(connection,Check);await DomainEditChecks.RunAsync(connection,Check);await CreateUserTypeChecks.RunAsync(connection,Check);}
if(args.Contains("--index-maintenance")){await ConcurrentIndexPreflightChecks.RunAsync(connection,Check);await ConcurrentIndexServiceChecks.RunAsync(connection,Check);await IndexMaintenanceChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} concurrent index preflight checks passed.");return;}
if(args.Length==0){await ConcurrentIndexPreflightChecks.RunAsync(connection,Check);await ConcurrentIndexServiceChecks.RunAsync(connection,Check);await IndexMaintenanceChecks.RunAsync(connection,Check);}
if(args.Contains("--index-expressions")){await ExpressionIndexChecks.RunAsync(connection,Check);await ExclusionChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} expression and exclusion checks passed.");return;}
if(args.Length==0){await ExpressionIndexChecks.RunAsync(connection,Check);await ExclusionChecks.RunAsync(connection,Check);}
if(args.Contains("--partition-tables")){await PartitionTableChecks.RunAsync(connection,Check);await PartitionChildChecks.RunAsync(connection,Check);await PartitionLinkChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} partition table checks passed.");return;}
if(args.Length==0){await PartitionTableChecks.RunAsync(connection,Check);await PartitionChildChecks.RunAsync(connection,Check);await PartitionLinkChecks.RunAsync(connection,Check);}
if(args.Contains("--cascade-routines")){await CascadeRoutineChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} cascade routine checks passed.");return;}
if(args.Length==0)await CascadeRoutineChecks.RunAsync(connection,Check);
if(args.Contains("--cascade-rollback")){await CascadeRollbackChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} cascade rollback checks passed.");return;}
if(args.Length==0)await CascadeRollbackChecks.RunAsync(connection,Check);
if(args.Contains("--revoke-preview")){await RevokePreviewChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} revoke preview checks passed.");return;}
if(args.Length==0)await RevokePreviewChecks.RunAsync(connection,Check);
if(args.Contains("--grantor-concurrency")){await GrantorConcurrencyChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} grantor concurrency checks passed.");return;}
if(args.Length==0)await GrantorConcurrencyChecks.RunAsync(connection,Check);
if(args.Contains("--explicit-grantor")){await ExplicitGrantorChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} explicit grantor checks passed.");return;}
if(args.Length==0)await ExplicitGrantorChecks.RunAsync(connection,Check);
if(args.Contains("--grantor-catalog")){await GrantorCatalogChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} grantor catalog checks passed.");return;}
if(args.Length==0)await GrantorCatalogChecks.RunAsync(connection,Check);
if(args.Contains("--default-delegation")){await DefaultDelegationChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} default delegation checks passed.");return;}
if(args.Length==0)await DefaultDelegationChecks.RunAsync(connection,Check);
if(args.Contains("--default-schemas")){await DefaultSchemaChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} default schema checks passed.");return;}
if(args.Length==0)await DefaultSchemaChecks.RunAsync(connection,Check);
if(args.Contains("--default-types")){await DefaultTypeChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} default type checks passed.");return;}
if(args.Length==0)await DefaultTypeChecks.RunAsync(connection,Check);
if(args.Contains("--default-routines")){await DefaultRoutineChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} default routine checks passed.");return;}
if(args.Length==0)await DefaultRoutineChecks.RunAsync(connection,Check);
if(args.Contains("--default-sequences")){await DefaultSequenceChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} default sequence checks passed.");return;}
if(args.Length==0)await DefaultSequenceChecks.RunAsync(connection,Check);
if(args.Contains("--default-tables")){await DefaultTableChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} default table checks passed.");return;}
if(args.Length==0)await DefaultTableChecks.RunAsync(connection,Check);
if(args.Contains("--default-privileges")){await DefaultPrivilegeChecks.RunAsync(connection,Check);await DefaultPrivilegeApplyChecks.RunAsync(connection,Check);await DefaultPrivilegeAuthorityChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} default privilege checks passed.");return;}
if(args.Length==0){await DefaultPrivilegeChecks.RunAsync(connection,Check);await DefaultPrivilegeApplyChecks.RunAsync(connection,Check);await DefaultPrivilegeAuthorityChecks.RunAsync(connection,Check);}
if(args.Contains("--routine-concurrency")){await RoutineConcurrencyChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} routine concurrency checks passed.");return;}
if(args.Contains("--routine-privileges")){await RoutinePrivilegeChecks.RunAsync(connection,Check);await RoutineConcurrencyChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} routine privilege checks passed.");return;}
if(args.Length==0){await RoutinePrivilegeChecks.RunAsync(connection,Check);await RoutineConcurrencyChecks.RunAsync(connection,Check);}
if(args.Contains("--restore-staging")){await RestoreInputStagingChecks.RunAsync(Check);Console.WriteLine($"{passed} restore staging checks passed.");return;}
if(args.Length==0)await RestoreInputStagingChecks.RunAsync(Check);
if(args.Contains("--backup-formats")){await BackupFormatChecks.RunAsync(connection,Check);Console.WriteLine($"{passed} backup format checks passed.");return;}
if(args.Length==0)await BackupFormatChecks.RunAsync(connection,Check);
if (args.Contains("--identity-columns")) { await IdentityColumnChecks.RunAsync(connection,Check); await IdentityColumnEditChecks.RunAsync(connection,Check); await IdentitySequenceParameterChecks.RunAsync(connection,Check); Console.WriteLine($"{passed} identity column checks passed."); return; }
if (args.Length == 0) { await IdentityColumnChecks.RunAsync(connection,Check); await IdentityColumnEditChecks.RunAsync(connection,Check); await IdentitySequenceParameterChecks.RunAsync(connection,Check); }
if (args.Contains("--restore-ownership")) { await RestoreOwnershipChecks.RunAsync(connection,Check); Console.WriteLine($"{passed} restore ownership checks passed."); return; }
if (args.Length == 0) await RestoreOwnershipChecks.RunAsync(connection,Check);
if (args.Contains("--unicode-backup")) { await UnicodeBackupChecks.RunAsync(connection,Check); await UnicodeToolIdentifierChecks.RunAsync(connection,Check); Console.WriteLine($"{passed} Unicode backup checks passed."); return; }
if (args.Length == 0) { await UnicodeBackupChecks.RunAsync(connection,Check); await UnicodeToolIdentifierChecks.RunAsync(connection,Check); }
if (args.Contains("--maintain-privilege")) { await MaintainPrivilegeChecks.RunAsync(connection,Check); Console.WriteLine($"{passed} MAINTAIN privilege checks passed."); return; }
if (args.Length == 0) await MaintainPrivilegeChecks.RunAsync(connection,Check);
if (args.Contains("--privilege-commit")) { await PrivilegeCommitChecks.RunAsync(connection,Check); Console.WriteLine($"{passed} commit reconciliation checks passed."); return; }
if (args.Length == 0) await PrivilegeCommitChecks.RunAsync(connection,Check);
if (args.Contains("--unicode-password")) { await UnicodePasswordChecks.RunAsync(connection,Check); Console.WriteLine($"{passed} Unicode password checks passed."); return; }
if (args.Length == 0) await UnicodePasswordChecks.RunAsync(connection,Check);
if (args.Contains("--privilege-identity")) { await PrivilegeIdentityChecks.RunAsync(connection,Check); await PrivilegeRecipientChecks.RunAsync(connection,Check); Console.WriteLine($"{passed} privilege identity checks passed."); return; }
if (args.Length == 0) { await PrivilegeIdentityChecks.RunAsync(connection,Check); await PrivilegeRecipientChecks.RunAsync(connection,Check); }
if (args.Contains("--membership-option-sql")) { MembershipOptionChecks.RunSql(Check); Console.WriteLine($"{passed} membership SQL checks passed."); return; }
if (args.Contains("--membership-options")) { MembershipOptionChecks.RunSql(Check); await MembershipOptionChecks.RunAsync(connection,Check); Console.WriteLine($"{passed} membership option checks passed."); return; }
if (args.Contains("--column-privileges")) { ColumnPrivilegeChecks.RunSql(Check); await ColumnPrivilegeChecks.RunAsync(connection,Check); Console.WriteLine($"{passed} column privilege checks passed."); return; }
if (args.Contains("--column-privilege-sql")) { ColumnPrivilegeChecks.RunSql(Check); Console.WriteLine($"{passed} column SQL checks passed."); return; }
if (args.Contains("--privilege-cancellation-guards")) { await PrivilegeCancellationChecks.RunGuardsAsync(Check); Console.WriteLine($"{passed} cancellation guard checks passed."); return; }
if (args.Contains("--privilege-cancellation")) { await PrivilegeCancellationChecks.RunAsync(connection,Check); Console.WriteLine($"{passed} privilege cancellation checks passed."); return; }
if (args.Contains("--sequence-privileges")) { SequencePrivilegeChecks.RunSql(Check); await SequencePrivilegeChecks.RunAsync(connection,Check); Console.WriteLine($"{passed} sequence privilege checks passed."); return; }
if (args.Contains("--sequence-privilege-sql")) { SequencePrivilegeChecks.RunSql(Check); Console.WriteLine($"{passed} sequence privilege SQL checks passed."); return; }
if (args.Contains("--privilege-commands")) { PrivilegeSqlChecks.Run(Check); await PrivilegeCommandChecks.RunAsync(connection,Check); Console.WriteLine($"{passed} privilege command checks passed."); return; }
if (args.Contains("--privilege-sql")) { PrivilegeSqlChecks.Run(Check); Console.WriteLine($"{passed} privilege SQL checks passed."); return; }
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAILED: " + description);
    Console.WriteLine("PASS: " + description);
    passed++;
}
if (args.Contains("--effective-privileges")) { await EffectivePrivilegeChecks.RunAsync(connection,Check); await PrivilegeAclChecks.RunAsync(connection,Check); Console.WriteLine($"{passed} effective privilege checks passed."); return; }
if (args.Contains("--sequence-owner")) { await SequenceOwnerTargetChecks.RunAsync(connection, Check); await SequenceAttachChecks.RunAsync(connection, Check); await SequenceOwnerJourneyChecks.RunAsync(connection, Check); Console.WriteLine($"{passed} sequence owner checks passed."); return; }
if (args.Contains("--sequence-state")) { await SequenceRestartChecks.RunAsync(connection, Check); await SequenceDetachChecks.RunAsync(connection, Check); await SequenceIdentifierChecks.RunAsync(connection, Check); Console.WriteLine($"{passed} sequence state checks passed."); return; }
if (args.Contains("--sequences")) { await SequenceChecks.RunAsync(connection, Check); Console.WriteLine($"{passed} sequence integration checks passed."); return; }
if (args.Contains("--advanced-index")) { await AdvancedIndexChecks.RunAsync(connection, Check); Console.WriteLine($"{passed} advanced integration checks passed."); return; }
if (args.Contains("--streaming")) { await QueryStreamingChecks.RunAsync(connection, Check); Console.WriteLine($"{passed} streaming integration checks passed."); return; }
if (args.Contains("--properties")) { await ObjectPropertyChecks.RunAsync(connection, Check); Console.WriteLine($"{passed} property integration checks passed."); return; }
if (args.Contains("--acceptance")) { await AcceptanceJourneyChecks.RunAsync(connection, Check); Console.WriteLine($"{passed} acceptance checks passed."); return; }
MembershipOptionChecks.RunSql(Check);
await MembershipOptionChecks.RunAsync(connection,Check);
await PrivilegeCancellationChecks.RunAsync(connection,Check);
await EffectivePrivilegeChecks.RunAsync(connection, Check);
ColumnPrivilegeChecks.RunSql(Check);
await ColumnPrivilegeChecks.RunAsync(connection,Check);
PrivilegeSqlChecks.Run(Check);
SequencePrivilegeChecks.RunSql(Check);
await SequencePrivilegeChecks.RunAsync(connection,Check);
await PrivilegeCommandChecks.RunAsync(connection,Check);
await PrivilegeAclChecks.RunAsync(connection, Check);
await AcceptanceJourneyChecks.RunAsync(connection, Check);
await ObjectPropertyChecks.RunAsync(connection, Check);
await QueryStreamingChecks.RunAsync(connection, Check);
await using var session = new QuerySession(connection);
await session.OpenAsync();
var plainTest = await new ConnectionTestService(connection).TestAsync();
var encryptedTest = await new ConnectionTestService(new NpgsqlConnectionStringBuilder(connection) { SslMode = SslMode.Require }.ConnectionString).TestAsync();
Check(plainTest.Database == "postgres" && plainTest.User == "pgms_test" && !plainTest.Tls, "connection test reports database, role and disabled TLS accurately");
Check(encryptedTest.Database == "postgres" && encryptedTest.Tls && encryptedTest.Version.Contains("PostgreSQL", StringComparison.Ordinal), "connection test confirms encrypted PostgreSQL session");
using (var csvTable = new DataTable())
{
    foreach (string name in new[] { "text", "null", "empty", "literal" }) csvTable.Columns.Add(name, typeof(object));
    csvTable.Columns[0].Caption = "quoted,\"header";
    csvTable.Columns[1].Caption = "same"; csvTable.Columns[2].Caption = "same";
    csvTable.Rows.Add("ação, \"x\"\r\n😀", DBNull.Value, "", "\\N");
    using var csvText = new StringWriter();
    await CsvResults.WriteAsync(csvTable, csvText);
    string csv = csvText.ToString();
    Check(csv.StartsWith("\"quoted,\"\"header\",\"same\",\"same\",\"literal\"\r\n"), "CSV quotes original headers including duplicate labels");
    await using var copyConnection = new NpgsqlConnection(connection);
    await copyConnection.OpenAsync();
    await using (var create = new NpgsqlCommand("CREATE TEMP TABLE csv_roundtrip(t text, n text, e text, l text)", copyConnection)) await create.ExecuteNonQueryAsync();
    using (var importer = await copyConnection.BeginTextImportAsync("COPY csv_roundtrip FROM STDIN WITH (FORMAT CSV, HEADER TRUE, NULL '\\N')")) await importer.WriteAsync(csv);
    await using (var read = new NpgsqlCommand("SELECT t,n,e,l FROM csv_roundtrip", copyConnection))
    await using (var row = await read.ExecuteReaderAsync())
    {
        await row.ReadAsync();
        Check(row.GetString(0) == "ação, \"x\"\r\n😀", "CSV roundtrip via PostgreSQL COPY preserves Unicode, comma, quotes and newlines");
        Check(row.IsDBNull(1) && row.GetString(2) == "" && row.GetString(3) == "\\N", "CSV COPY distinguishes NULL, empty string and literal null marker");
    }
    string csvPath = Path.Combine(Path.GetTempPath(), "pgms_csv_" + Guid.NewGuid().ToString("N") + ".csv");
    try
    {
        await CsvResults.SaveAsync(csvTable, csvPath);
        Check(await File.ReadAllTextAsync(csvPath) == csv, "CSV file contains exact exported data");
        bool existsRejected = false;
        try { await CsvResults.SaveAsync(csvTable, csvPath); } catch (IOException) { existsRejected = true; }
        Check(existsRejected && await File.ReadAllTextAsync(csvPath) == csv, "CSV export refuses unapproved overwrite and preserves existing destination");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool canceledExport = false;
        try { await CsvResults.SaveAsync(csvTable, csvPath, true, canceled.Token); } catch (OperationCanceledException) { canceledExport = true; }
        Check(canceledExport && await File.ReadAllTextAsync(csvPath) == csv && Directory.GetFiles(Path.GetDirectoryName(csvPath)!, Path.GetFileName(csvPath) + ".*.partial").Length == 0, "canceled CSV export preserves existing file and leaves no partial output");
    }
    finally { if (File.Exists(csvPath)) File.Delete(csvPath); }
    csvTable.Clear(); using var emptyCsv = new StringWriter(); await CsvResults.WriteAsync(csvTable, emptyCsv);
    Check(emptyCsv.ToString().Split('\n').Length == 2, "empty result exports header only");
}
var results = await session.ExecuteAsync("SELECT 42 AS answer, NULL::text AS missing, ''::text AS empty; SELECT 'second' AS value;");
Check(results.Tables.Count == 2, "multiple result sets");
Check((string)results.Tables[0].Rows[0][0] == "42", "PostgreSQL text result value");
Check(results.Tables[0].Rows[0][1] is DBNull && (string)results.Tables[0].Rows[0][2] == "", "NULL distinct from empty string");
results = await session.ExecuteAsync("SELECT 1 AS same, 2 AS same;");
Check(results.Tables[0].Columns[0].Caption == results.Tables[0].Columns[1].Caption && (string)results.Tables[0].Rows[0][1] == "2", "duplicate column labels");
await session.ExecuteAsync("CREATE TEMP TABLE pgms_test_state(value int); INSERT INTO pgms_test_state VALUES (10);");
results = await session.ExecuteAsync("SELECT value FROM pgms_test_state;");
Check((string)results.Tables[0].Rows[0][0] == "10", "temporary objects persist between executions");
await session.ExecuteAsync("BEGIN; INSERT INTO pgms_test_state VALUES (20);");
await session.ExecuteAsync("ROLLBACK;");
results = await session.ExecuteAsync("SELECT count(*) FROM pgms_test_state;");
Check((string)results.Tables[0].Rows[0][0] == "1", "explicit transaction rollback preserves session");
results = await session.ExecuteAsync("SELECT $$a;b$$ AS text; -- ; is not a batch separator\n SELECT ';' AS text;");
Check(results.Tables.Count == 2 && (string)results.Tables[0].Rows[0][0] == "a;b", "SQL strings and dollar quoting are not split by the client");
results = await session.ExecuteAsync("SELECT generate_series(1,12000); SELECT 7;");
Check(results.Truncated && results.Tables.Sum(t => t.Rows.Count) == 10000, "display row budget across result sets");
await using (var other = new QuerySession(connection))
{
    await other.OpenAsync();
    var isolated = await other.ExecuteAsync("SELECT to_regclass('pg_temp.pgms_test_state');");
    Check(isolated.Tables[0].Rows[0][0] is DBNull, "independent editor sessions");
}
using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
{
    bool canceled = false;
    try { await session.ExecuteAsync("SELECT pg_sleep(10)", cancel.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Check(canceled, "running command canceled");
}
results = await session.ExecuteAsync("SELECT 99");
Check((string)results.Tables[0].Rows[0][0] == "99", "connection reusable after cancellation outside transaction");
await session.ExecuteAsync("BEGIN;");
try { await session.ExecuteAsync("SELECT 1 / 0;"); } catch (PostgresException ex) when (ex.SqlState == "22012") { }
bool transactionAborted = false;
try { await session.ExecuteAsync("SELECT 1;"); } catch (PostgresException ex) when (ex.SqlState == "25P02") { transactionAborted = true; }
Check(transactionAborted, "failed explicit transaction is not silently rolled back");
await session.ExecuteAsync("ROLLBACK;");
string suffix = Guid.NewGuid().ToString("N")[..8];
string schemaName = $"pgms space \"semi;{suffix}";
string databaseName = $"pgms_db_{suffix}";
string roleName = $"pgms_reader_{suffix}";
string groupRoleName = $"pgms_group_{suffix}";
string managedRoleName = $"pgms_managed_{suffix}";
string failedRoleName = $"pgms_failed_{suffix}";
var admin = new AdministrationService(connection);
var catalog = new CatalogService(connection);
try
{
    var createSchema = new CreateObjectRequest(CreateObjectKind.Schema, schemaName);
    Check(createSchema.ToSql().Contains("\"\"semi;"), "quoted identifiers escape embedded quotes");
    bool invalidName = false;
    try { new CreateObjectRequest(CreateObjectKind.Schema, new string('é', 32)).ToSql(); } catch (ArgumentException) { invalidName = true; }
    Check(invalidName, "UTF-8 identifier length validated before execution");
    await session.ExecuteAsync("BEGIN;");
    await admin.CreateAsync(createSchema);
    await session.ExecuteAsync("ROLLBACK;");
    var schemas = await catalog.ChildrenAsync(new(CatalogKind.Schemas, "Schemas", "postgres"));
    Check(schemas.Any(s => s.Name == schemaName), "empty schema appears; administration does not use editor transaction");
    string schemaId = SqlIdentifier.Quote(schemaName);
    await session.ExecuteAsync($"CREATE TABLE {schemaId}.sample(id integer PRIMARY KEY, note text); CREATE FUNCTION {schemaId}.f(a integer) RETURNS integer LANGUAGE sql AS 'SELECT a'; CREATE FUNCTION {schemaId}.f(a text) RETURNS text LANGUAGE sql AS 'SELECT a';");
    var tables = await catalog.ChildrenAsync(new(CatalogKind.Tables, "Tables", "postgres", schemaName));
    Check(tables.Count == 1 && tables[0].Name == "sample", "schema catalog handles quotes without SQL interpolation");
    var columns = await catalog.ChildrenAsync(new(CatalogKind.Columns, "Columns", "postgres", schemaName, tables[0].Oid));
    Check(columns.Count == 2 && columns[0].Name.Contains("not null"), "relation columns include type and nullability");
    var indexes = await catalog.ChildrenAsync(new(CatalogKind.Indexes, "Indexes", "postgres", schemaName, tables[0].Oid));
    Check(indexes.Count == 1, "relation index catalog");
    var functions = await catalog.ChildrenAsync(new(CatalogKind.Functions, "Functions", "postgres", schemaName));
    Check(functions.Count == 2 && functions[0].Name != functions[1].Name, "overloaded functions retain identity arguments");
    await admin.CreateAsync(new(CreateObjectKind.Database, databaseName));
    var databases = await catalog.ChildrenAsync(new(CatalogKind.Databases, "Databases"));
    Check(databases.Any(d => d.Name == databaseName), "created database appears in server catalog");
    string renamedDatabase = databaseName + "_renamed";
    var databaseEdits = new DatabaseEditService(connection);
    Check(new DatabaseEdit(databaseName, DatabaseOperation.Rename, renamedDatabase).ToSql().Contains("RENAME TO", StringComparison.Ordinal), "database rename SQL is generated with quoted names");
    bool protectedDatabaseRejected = false;
    try { new DatabaseEdit("template0", DatabaseOperation.Drop).ToSql(); } catch (ArgumentException) { protectedDatabaseRejected = true; }
    Check(protectedDatabaseRejected, "template databases are protected before execution");
    await databaseEdits.ApplyAsync(new(databaseName, DatabaseOperation.Rename, renamedDatabase));
    Check((await databaseEdits.ListAsync()).Contains(renamedDatabase), "database rename changes actual server catalog");
    await databaseEdits.ApplyAsync(new(renamedDatabase, DatabaseOperation.Rename, databaseName));
    Check((await databaseEdits.ListAsync()).Contains(databaseName), "database rename can restore the exact original identifier");
    await using (var secondDatabase = new QuerySession(catalog.ConnectionFor(databaseName)))
    {
        await secondDatabase.OpenAsync();
        var target = await secondDatabase.ExecuteAsync("SELECT current_database();");
        Check((string)target.Tables[0].Rows[0][0] == databaseName, "database node targets the selected database");
    }
    string archive = Path.Combine(Path.GetTempPath(), "pgms_" + suffix + ".backup");
    string tools = Environment.GetEnvironmentVariable("PGMS_TEST_POSTGRES_BIN") ?? @"C:\Program Files\PostgreSQLin";
    try
    {
        await session.ExecuteAsync($"INSERT INTO {schemaId}.sample VALUES (1, 'ação'), (2, NULL);");
        var backup = new BackupService(connection, tools);
        await backup.BackupAsync(archive);
        Check(File.Exists(archive) && new FileInfo(archive).Length > 0, "custom archive created by pg_dump");
        bool overwriteRejected = false;
        try { await backup.BackupAsync(archive); } catch (IOException) { overwriteRejected = true; }
        Check(overwriteRejected, "existing archive cannot be overwritten");
        var restore = new BackupService(catalog.ConnectionFor(databaseName), tools);
        await restore.RestoreAsync(archive);
        await using var restored = new QuerySession(catalog.ConnectionFor(databaseName));
        await restored.OpenAsync();
        var rows = await restored.ExecuteAsync($"SELECT note FROM {schemaId}.sample ORDER BY id;");
        Check(rows.Tables[0].Rows.Count == 2 && (string)rows.Tables[0].Rows[0][0] == "ação" && rows.Tables[0].Rows[1][0] is DBNull, "backup/restore preserves schema, Unicode and NULL");
        bool conflict = false;
        try { await restore.RestoreAsync(archive); } catch (IOException) { conflict = true; }
        rows = await restored.ExecuteAsync($"SELECT count(*) FROM {schemaId}.sample;");
        Check(conflict && (string)rows.Tables[0].Rows[0][0] == "2", "restore conflict fails without duplicating data");
        await session.ExecuteAsync($"BEGIN; LOCK TABLE {schemaId}.sample IN ACCESS EXCLUSIVE MODE;");
        string canceledPath = archive + ".canceled";
        using var cancelBackup = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        bool canceled = false;
        try { await backup.BackupAsync(canceledPath, cancelBackup.Token); } catch (OperationCanceledException) { canceled = true; }
        finally { await session.ExecuteAsync("ROLLBACK;"); }
        Check(canceled && !File.Exists(canceledPath) && Directory.GetFiles(Path.GetDirectoryName(archive)!, Path.GetFileName(canceledPath) + ".*.partial").Length == 0, "cancel locked backup terminates process and removes partial archive");
    }
    finally { if (File.Exists(archive)) File.Delete(archive); }
    bool duplicateRejected = false;
    try { await admin.CreateAsync(new(CreateObjectKind.Database, databaseName)); }
    catch (PostgresException ex) when (ex.SqlState == "42P04") { duplicateRejected = true; }
    Check(duplicateRejected, "duplicate database propagates actionable SQLSTATE");
    var roles = new RoleAdministrationService(connection);
    await roles.CreateAsync(new(groupRoleName, CanLogin: false));
    var managedRequest = new CreateRoleRequest(managedRoleName, CanLogin: true, Inherit: false,
        CreateDatabase: true, ConnectionLimit: 3, MemberOf: [groupRoleName]);
    Check(managedRequest.ToSqlScript().Contains($"GRANT \"{groupRoleName}\" TO \"{managedRoleName}\";"), "role SQL preview includes quoted membership grant");
    await roles.CreateAsync(managedRequest);
    results = await session.ExecuteAsync($"SELECT CASE WHEN rolcanlogin THEN 'true' ELSE 'false' END, CASE WHEN rolinherit THEN 'true' ELSE 'false' END, CASE WHEN rolcreatedb THEN 'true' ELSE 'false' END, rolconnlimit FROM pg_catalog.pg_roles WHERE rolname='{managedRoleName}'; SELECT CASE WHEN pg_has_role('{managedRoleName}', '{groupRoleName}', 'MEMBER') THEN 'true' ELSE 'false' END;");
    Check((string)results.Tables[0].Rows[0][0] == "true" && (string)results.Tables[0].Rows[0][1] == "false" && (string)results.Tables[0].Rows[0][2] == "true" && (string)results.Tables[0].Rows[0][3] == "3", "role attributes created exactly");
    Check((string)results.Tables[1].Rows[0][0] == "true", "role membership granted atomically");
    var editService = new AdministrativeEditService(connection);
    await editService.ApplyAsync(new RoleAttributeEdit(managedRoleName, RoleAttribute.ConnectionLimit, "7"));
    await editService.ApplyAsync(new RoleAttributeEdit(managedRoleName, RoleAttribute.Login, "false"));
    var attributes = await editService.InspectRoleAsync(managedRoleName);
    Check(attributes.Contains("CONNECTION LIMIT = 7") && attributes.Contains("LOGIN = False"), "role edits persist selected attributes");
    Check(attributes.Contains("CREATEDB = True") && attributes.Contains("INHERIT = False"), "role edit leaves unselected attributes unchanged");
    bool invalidLimit = false;
    try { new RoleAttributeEdit(managedRoleName, RoleAttribute.ConnectionLimit, "-2").ToSql(); } catch (ArgumentException) { invalidLimit = true; }
    Check(invalidLimit, "role change rejects invalid connection limit");
    bool selfMembershipRejected = false;
    try { new CreateRoleRequest("self", MemberOf: ["self"]).ToSqlScript(); } catch (ArgumentException) { selfMembershipRejected = true; }
    Check(selfMembershipRejected, "self membership rejected before execution");
    bool membershipFailure = false;
    try { await roles.CreateAsync(new(failedRoleName, MemberOf: ["pgms_missing_" + suffix])); }
    catch (PostgresException ex) when (ex.SqlState == "42704") { membershipFailure = true; }
    results = await session.ExecuteAsync($"SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname='{failedRoleName}';");
    Check(membershipFailure && (string)results.Tables[0].Rows[0][0] == "0", "failed membership rolls back role creation");
    Check((await roles.ListRolesAsync()).Contains(managedRoleName), "role administration lists available memberships");
    string oldRoleName = "pgms_rename_" + suffix, newRoleName = "pgms_renamed_" + suffix;
    await roles.CreateAsync(new(oldRoleName, CanLogin: false));
    await editService.ApplyAsync(new RoleRenameEdit(oldRoleName, newRoleName));
    Check((await roles.ListRolesAsync()).Contains(newRoleName) && !(await roles.ListRolesAsync()).Contains(oldRoleName), "role rename changes the exact server catalog identifier");
    bool currentRoleProtected = false;
    try { await editService.ApplyAsync(new RoleRenameEdit("pgms_test", "pgms_test_renamed")); } catch (InvalidOperationException ex) when (ex.Message.Contains("cannot be renamed", StringComparison.Ordinal)) { currentRoleProtected = true; }
    bool predefinedRoleProtected = false;
    try { new RoleRenameEdit("ordinary", "pg_monitor").ToSql(); } catch (ArgumentException) { predefinedRoleProtected = true; }
    Check(currentRoleProtected && predefinedRoleProtected, "role rename protects connected and predefined roles");
    await editService.ApplyAsync(new RoleRenameEdit(newRoleName, oldRoleName));
    string disposableRole = "pgms_drop_" + suffix, ownedSchema = "pgms_owned_" + suffix;
    await roles.CreateAsync(new(disposableRole, CanLogin: false));
    await admin.CreateAsync(new(CreateObjectKind.Schema, ownedSchema, disposableRole));
    bool dependencyRefused = false;
    try { await editService.ApplyAsync(new RoleDropEdit(disposableRole)); } catch (PostgresException ex) when (ex.SqlState == "2BP01") { dependencyRefused = true; }
    Check(dependencyRefused && (await roles.ListRolesAsync()).Contains(disposableRole), "DROP ROLE preserves owner when PostgreSQL reports dependencies");
    await session.ExecuteAsync($"DROP SCHEMA {SqlIdentifier.Quote(ownedSchema)};");
    await editService.ApplyAsync(new RoleDropEdit(disposableRole));
    Check(!(await roles.ListRolesAsync()).Contains(disposableRole), "DROP ROLE removes dependency-free role without CASCADE");
    bool connectedRoleDropRefused = false;
    try { await editService.ApplyAsync(new RoleDropEdit("pgms_test")); } catch (InvalidOperationException ex) when (ex.Message.Contains("cannot be dropped", StringComparison.Ordinal)) { connectedRoleDropRefused = true; }
    Check(connectedRoleDropRefused, "DROP ROLE refuses the connected role");
    string passwordRole = "pgms_password_" + suffix;
    char[] passwordChars = RandomNumberGenerator.GetBytes(32).Select(b => (char)(33 + b % 90)).ToArray();
    passwordChars[0] = '\''; passwordChars[1] = '\\';
    await roles.CreateAsync(new(passwordRole, CanLogin: true));
    using (var securePassword = new SecureString())
    {
        foreach (char ch in passwordChars) securePassword.AppendChar(ch);
        securePassword.MakeReadOnly();
        var tlsConnection = new NpgsqlConnectionStringBuilder(connection) { SslMode = SslMode.Require }.ConnectionString;
        await RolePasswordService.SetAsync(tlsConnection, passwordRole, securePassword);
        await using var passwordCheck = new NpgsqlConnection(connection);
        await passwordCheck.OpenAsync();
        await using var passwordCommand = new NpgsqlCommand("SELECT left(rolpassword, 13) FROM pg_catalog.pg_authid WHERE rolname=@name", passwordCheck);
        passwordCommand.Parameters.AddWithValue("name", passwordRole);
        string? passwordPrefix = (string?)await passwordCommand.ExecuteScalarAsync();
        Check(passwordPrefix == "SCRAM-SHA-256", "role password changes over TLS and PostgreSQL stores a SCRAM verifier");
        string passwordString = new(passwordChars);
        await using (var hbaPathCommand = new NpgsqlCommand("SHOW hba_file", passwordCheck))
        {
            string hbaPath = (string)(await hbaPathCommand.ExecuteScalarAsync())!;
            string originalHba = await File.ReadAllTextAsync(hbaPath);
            try
            {
                await File.WriteAllTextAsync(hbaPath, $"host all {passwordRole} 127.0.0.1/32 scram-sha-256{Environment.NewLine}" + originalHba);
                await using (var reload = new NpgsqlCommand("SELECT pg_catalog.pg_reload_conf()", passwordCheck)) await reload.ExecuteNonQueryAsync();
                var loginBuilder = new NpgsqlConnectionStringBuilder(connection) { Username = passwordRole, Password = passwordString, SslMode = SslMode.Require };
                await using (var authenticated = new NpgsqlConnection(loginBuilder.ConnectionString)) { await authenticated.OpenAsync(); Check(true, "SCRAM verifier authenticates the generated role password over TLS"); }
                loginBuilder.Password = passwordString + "x";
                bool wrongPasswordRejected = false;
                try { await using var rejected = new NpgsqlConnection(loginBuilder.ConnectionString); await rejected.OpenAsync(); }
                catch (PostgresException ex) when (ex.SqlState == "28P01") { wrongPasswordRejected = true; }
                Check(wrongPasswordRejected, "SCRAM authentication rejects an incorrect password");
            }
            finally
            {
                await File.WriteAllTextAsync(hbaPath, originalHba);
                await using var reload = new NpgsqlCommand("SELECT pg_catalog.pg_reload_conf()", passwordCheck); await reload.ExecuteNonQueryAsync();
            }
        }
        passwordString = "";
        bool insecureTransportRefused = false;
        try { await RolePasswordService.SetAsync(connection, passwordRole, securePassword); } catch (InvalidOperationException ex) when (ex.Message.Contains("only over TLS", StringComparison.Ordinal)) { insecureTransportRefused = true; }
        Check(insecureTransportRefused, "role password service refuses a non-TLS profile");
    }
    Array.Clear(passwordChars);
    await session.ExecuteAsync($"CREATE ROLE {SqlIdentifier.Quote(roleName)} LOGIN;");
    string limitedConnection = new NpgsqlConnectionStringBuilder(connection) { Username = roleName }.ConnectionString;
    bool alterDenied = false;
    try { await new AdministrativeEditService(limitedConnection).ApplyAsync(new RoleAttributeEdit(managedRoleName, RoleAttribute.Login, "true")); }
    catch (PostgresException ex) when (ex.SqlState == "42501") { alterDenied = true; }
    Check(alterDenied, "role alteration respects connected role permissions");
    var membership = new RoleMembershipEdit(groupRoleName, roleName);
    bool grantDenied = false;
    try { await new AdministrativeEditService(limitedConnection).ApplyAsync(membership); }
    catch (PostgresException ex) when (ex.SqlState == "42501") { grantDenied = true; }
    Check(grantDenied, "existing membership grant respects ADMIN authority");
    await editService.ApplyAsync(membership);
    results = await session.ExecuteAsync($"SELECT CASE WHEN pg_has_role('{roleName}', '{groupRoleName}', 'MEMBER') THEN 'yes' ELSE 'no' END;");
    Check((string)results.Tables[0].Rows[0][0] == "yes", "GRANT between existing roles establishes membership");
    string directGrants = await new RoleMembershipService(connection).InspectAsync(roleName);
    Check(directGrants.Contains(groupRoleName) && directGrants.Contains("grantor:") && directGrants.Contains("ADMIN="), "membership inspection reports direct grantor and options");
    bool redundantGrantNoticed = false;
    try { await editService.ApplyAsync(membership); } catch (InvalidOperationException) { redundantGrantNoticed = true; }
    Check(redundantGrantNoticed, "membership no-op Notice is surfaced rather than reported as a new grant");
    bool cycleDenied = false;
    try { await editService.ApplyAsync(new RoleMembershipEdit(managedRoleName, groupRoleName)); } catch (PostgresException) { cycleDenied = true; }
    Check(cycleDenied, "PostgreSQL rejects cyclic role memberships");
    await editService.ApplyAsync(membership with { Revoke = true });
    Check((await new RoleMembershipService(connection).InspectAsync(roleName)).Contains("No direct"), "REVOKE removes direct membership from existing role");
    string editableSchema = "pgms_edit_" + suffix;
    string renamedSchema = "pgms renamed \"" + suffix;
    try
    {
        await admin.CreateAsync(new(CreateObjectKind.Schema, editableSchema));
        await session.ExecuteAsync($"CREATE TABLE {SqlIdentifier.Quote(editableSchema)}.kept(value text); INSERT INTO {SqlIdentifier.Quote(editableSchema)}.kept VALUES ('ação');");
        await editService.ApplyAsync(new SchemaEdit(editableSchema, SchemaOperation.Rename, renamedSchema));
        var names = await catalog.ChildrenAsync(new(CatalogKind.Schemas, "Schemas", "postgres"));
        Check(names.Any(s => s.Name == renamedSchema) && names.All(s => s.Name != editableSchema), "schema rename updates actual catalog with escaped new name");
        results = await session.ExecuteAsync($"SELECT value FROM {SqlIdentifier.Quote(renamedSchema)}.kept;");
        Check((string)results.Tables[0].Rows[0][0] == "ação", "schema rename preserves contained objects and data");
        bool nonemptyRejected = false;
        try { await editService.ApplyAsync(new SchemaEdit(renamedSchema, SchemaOperation.Drop)); }
        catch (PostgresException ex) when (ex.SqlState == "2BP01") { nonemptyRejected = true; }
        Check(nonemptyRejected, "DROP SCHEMA RESTRICT refuses populated schema");
        bool schemaDenied = false;
        try { await new AdministrativeEditService(limitedConnection).ApplyAsync(new SchemaEdit(renamedSchema, SchemaOperation.Rename, editableSchema)); }
        catch (PostgresException ex) when (ex.SqlState == "42501") { schemaDenied = true; }
        Check(schemaDenied, "schema rename respects ownership permissions");
        await session.ExecuteAsync($"DROP TABLE {SqlIdentifier.Quote(renamedSchema)}.kept;");
        await editService.ApplyAsync(new SchemaEdit(renamedSchema, SchemaOperation.Drop));
        names = await catalog.ChildrenAsync(new(CatalogKind.Schemas, "Schemas", "postgres"));
        Check(names.All(s => s.Name != renamedSchema), "empty schema deletion updates actual catalog");
        bool systemRejected = false;
        try { new SchemaEdit("pg_catalog", SchemaOperation.Drop).ToSql(); } catch (ArgumentException) { systemRejected = true; }
        Check(systemRejected, "system schema mutation rejected before SQL execution");
    }
    finally { await session.ExecuteAsync($"DROP SCHEMA IF EXISTS {SqlIdentifier.Quote(editableSchema)} CASCADE; DROP SCHEMA IF EXISTS {SqlIdentifier.Quote(renamedSchema)} CASCADE;"); }
    string typedTable = "typed table\"" + suffix;
    TableColumn[] typedColumns = [new("id", ColumnType.Integer, false), new("note, \"text", ColumnType.Text), new("n", ColumnType.Numeric), new("flag", ColumnType.Boolean), new("uid", ColumnType.Uuid), new("day", ColumnType.Date), new("stamp", ColumnType.TimestampWithTimeZone), new("big", ColumnType.Bigint)];
    await editService.ApplyAsync(new CreateTableEdit(schemaName, typedTable, typedColumns));
    var typedTables = await catalog.ChildrenAsync(new(CatalogKind.Tables, "Tables", "postgres", schemaName));
    var typedEntry = typedTables.Single(t => t.Name == typedTable);
    var typedCatalogColumns = await catalog.ChildrenAsync(new(CatalogKind.Columns, "Columns", "postgres", schemaName, typedEntry.Oid));
    Check(typedCatalogColumns.Count == 8 && typedCatalogColumns[0].Name.Contains("not null") && typedCatalogColumns.Any(c => c.Name.Contains("timestamp with time zone")), "typed CREATE TABLE persists types and nullability in actual catalog");
    await session.ExecuteAsync($"INSERT INTO {schemaId}.{SqlIdentifier.Quote(typedTable)} (id, {SqlIdentifier.Quote("note, \"text")}) VALUES (1, 'ação');");
    results = await session.ExecuteAsync($"SELECT {SqlIdentifier.Quote("note, \"text")} FROM {schemaId}.{SqlIdentifier.Quote(typedTable)};");
    Check((string)results.Tables[0].Rows[0][0] == "ação", "created table with escaped identifiers supports actual data operations");
    bool nullRejected = false;
    try { await session.ExecuteAsync($"INSERT INTO {schemaId}.{SqlIdentifier.Quote(typedTable)} (id) VALUES (NULL);"); } catch (PostgresException ex) when (ex.SqlState == "23502") { nullRejected = true; }
    Check(nullRejected, "created NOT NULL column enforces server constraint");
    bool duplicateColumnRejected = false;
    try { new CreateTableEdit(schemaName, "bad", [new("same", ColumnType.Text), new("same", ColumnType.Integer)]).ToSql(); } catch (ArgumentException) { duplicateColumnRejected = true; }
    Check(duplicateColumnRejected, "duplicate table columns rejected before execution");
    bool tableDenied = false;
    try { await new AdministrativeEditService(limitedConnection).ApplyAsync(new CreateTableEdit(schemaName, "denied_table", [new("id", ColumnType.Integer)])); } catch (PostgresException ex) when (ex.SqlState == "42501") { tableDenied = true; }
    Check(tableDenied, "CREATE TABLE respects schema permissions");
    string indexName = "index, \"" + suffix;
    var indexEdit = new RelationEdit(RelationOperation.CreateIndex, schemaName, typedTable, indexName, ["id", "note, \"text"], true);
    await editService.ApplyAsync(indexEdit);
    var actualIndexes = await catalog.ChildrenAsync(new(CatalogKind.Indexes, "Indexes", "postgres", schemaName, typedEntry.Oid));
    Check(actualIndexes.Any(i => i.Name == indexName), "composite unique index with escaped names exists in catalog");
    bool indexDuplicate = false;
    try { await session.ExecuteAsync($"INSERT INTO {schemaId}.{SqlIdentifier.Quote(typedTable)} (id, {SqlIdentifier.Quote("note, \"text")}) VALUES (1, 'ação');"); } catch (PostgresException ex) when (ex.SqlState == "23505") { indexDuplicate = true; }
    Check(indexDuplicate, "unique index rejects actual duplicate composite values");
    bool invalidIndexColumn = false;
    try { await editService.ApplyAsync(indexEdit with { Name = indexName + "bad", Columns = ["missing"] }); } catch (PostgresException ex) when (ex.SqlState == "42703") { invalidIndexColumn = true; }
    actualIndexes = await catalog.ChildrenAsync(new(CatalogKind.Indexes, "Indexes", "postgres", schemaName, typedEntry.Oid));
    Check(invalidIndexColumn && actualIndexes.Count == 1, "failed index creation leaves no partial index");
    bool indexDenied = false;
    try { await new AdministrativeEditService(limitedConnection).ApplyAsync(indexEdit with { Name = "denied_idx" }); } catch (PostgresException ex) when (ex.SqlState == "42501") { indexDenied = true; }
    Check(indexDenied, "index creation respects table ownership");
    await editService.ApplyAsync(indexEdit with { Operation = RelationOperation.DropIndex });
    await session.ExecuteAsync($"INSERT INTO {schemaId}.{SqlIdentifier.Quote(typedTable)} (id, {SqlIdentifier.Quote("note, \"text")}) VALUES (1, 'ação');");
    Check((await catalog.ChildrenAsync(new(CatalogKind.Indexes, "Indexes", "postgres", schemaName, typedEntry.Oid))).Count == 0, "DROP INDEX removes uniqueness enforcement");
    bool duplicateIndexColumns = false;
    try { (indexEdit with { Columns = ["id", "id"] }).ToSql(); } catch (ArgumentException) { duplicateIndexColumns = true; }
    Check(duplicateIndexColumns && RelationEdit.ParseColumns("odd, name\r\nquote\"name").Length == 2, "index columns use exact newline names and reject duplicates");
    string constraintTable = "constraint table\"" + suffix;
    string primaryKeyName = "primary, \"" + suffix;
    string uniqueConstraintName = "unique, \"" + suffix;
    await editService.ApplyAsync(new CreateTableEdit(schemaName, constraintTable, [new("id", ColumnType.Integer), new("code, \"text", ColumnType.Text)]));
    string constraintTableSql = $"{schemaId}.{SqlIdentifier.Quote(constraintTable)}";
    await session.ExecuteAsync($"INSERT INTO {constraintTableSql} (id, {SqlIdentifier.Quote("code, \"text")}) VALUES (1, 'ação');");
    var constraintService = new TableConstraintService(connection);
    var primaryKeyEdit = new TableConstraintEdit(TableConstraintOperation.AddPrimaryKey, schemaName, constraintTable, primaryKeyName, ["id"]);
    await editService.ApplyAsync(primaryKeyEdit);
    var inspectedConstraints = await constraintService.InspectAsync(schemaName, constraintTable);
    Check(inspectedConstraints.Count == 1 && inspectedConstraints[0].Name == primaryKeyName && inspectedConstraints[0].Kind == "PRIMARY KEY", "constraint inspection reports exact primary key definition");
    bool primaryKeyDuplicate = false;
    try { await session.ExecuteAsync($"INSERT INTO {constraintTableSql} (id, {SqlIdentifier.Quote("code, \"text")}) VALUES (1, 'other');"); } catch (PostgresException ex) when (ex.SqlState == "23505") { primaryKeyDuplicate = true; }
    bool primaryKeyNull = false;
    try { await session.ExecuteAsync($"INSERT INTO {constraintTableSql} (id) VALUES (NULL);"); } catch (PostgresException ex) when (ex.SqlState == "23502") { primaryKeyNull = true; }
    Check(primaryKeyDuplicate && primaryKeyNull, "PRIMARY KEY rejects duplicate and NULL values on actual table");
    var uniqueConstraintEdit = new TableConstraintEdit(TableConstraintOperation.AddUnique, schemaName, constraintTable, uniqueConstraintName, ["code, \"text"]);
    await editService.ApplyAsync(uniqueConstraintEdit);
    inspectedConstraints = await constraintService.InspectAsync(schemaName, constraintTable);
    Check(inspectedConstraints.Count == 2 && inspectedConstraints.Any(c => c.Name == uniqueConstraintName && c.Kind == "UNIQUE"), "UNIQUE constraint appears with its exact name in inspection");
    string checkConstraintName = "check, \"" + suffix;
    var checkConstraintService = new CheckConstraintService(connection);
    var checkEdit = new CheckConstraintEdit(CheckConstraintOperation.Add, schemaName, constraintTable, checkConstraintName, "id > 0");
    await editService.ApplyAsync(checkEdit);
    var checks = await checkConstraintService.InspectAsync(schemaName, constraintTable);
    Check(checks.Count == 1 && checks[0].Name == checkConstraintName && checks[0].Definition.Contains("id > 0", StringComparison.Ordinal), "CHECK constraint inspection reports its exact name and expression");
    bool checkInvalidInsertRejected = false;
    try { await session.ExecuteAsync($"INSERT INTO {constraintTableSql} (id) VALUES (0);"); } catch (PostgresException ex) when (ex.SqlState == "23514") { checkInvalidInsertRejected = true; }
    Check(checkInvalidInsertRejected, "CHECK constraint rejects invalid rows on the actual table");
    bool unrelatedConstraintBlocked = false;
    try { await editService.ApplyAsync(new TableConstraintEdit(TableConstraintOperation.Drop, schemaName, constraintTable, checkConstraintName, [])); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("PRIMARY KEY and UNIQUE", StringComparison.Ordinal)) { unrelatedConstraintBlocked = true; }
    bool checkConstraintRemains = (await checkConstraintService.InspectAsync(schemaName, constraintTable)).Any(c => c.Name == checkConstraintName);
    Check(unrelatedConstraintBlocked && checkConstraintRemains, "PK/UNIQUE dialog refuses to drop CHECK constraints");
    bool malformedCheck = false;
    try { (checkEdit with { Expression = "id > 0; DROP TABLE x" }).ToSql(); } catch (ArgumentException) { malformedCheck = true; }
    bool wrongCheckTypeProtected = false;
    try { await editService.ApplyAsync(new CheckConstraintEdit(CheckConstraintOperation.Drop, schemaName, constraintTable, primaryKeyName, "")); } catch (InvalidOperationException ex) when (ex.Message.Contains("CHECK constraints only", StringComparison.Ordinal)) { wrongCheckTypeProtected = true; }
    Check(malformedCheck && wrongCheckTypeProtected, "CHECK operations reject multi-statement expressions and protect other constraint types");
    bool uniqueDuplicate = false;
    try { await session.ExecuteAsync($"INSERT INTO {constraintTableSql} (id, {SqlIdentifier.Quote("code, \"text")}) VALUES (2, 'ação');"); } catch (PostgresException ex) when (ex.SqlState == "23505") { uniqueDuplicate = true; }
    await session.ExecuteAsync($"INSERT INTO {constraintTableSql} (id) VALUES (2), (3);");
    Check(uniqueDuplicate && (string)(await session.ExecuteAsync($"SELECT count(*) FROM {constraintTableSql} WHERE {SqlIdentifier.Quote("code, \"text")} IS NULL;")).Tables[0].Rows[0][0] == "2", "UNIQUE rejects duplicate values and permits multiple NULL values by default");
    bool invalidConstraintColumn = false;
    try { await editService.ApplyAsync(uniqueConstraintEdit with { Name = uniqueConstraintName + "bad", Columns = ["missing"] }); } catch (PostgresException ex) when (ex.SqlState == "42703") { invalidConstraintColumn = true; }
    bool constraintDenied = false;
    try { await new AdministrativeEditService(limitedConnection).ApplyAsync(uniqueConstraintEdit with { Name = "denied_unique" }); } catch (PostgresException ex) when (ex.SqlState == "42501") { constraintDenied = true; }
    Check(invalidConstraintColumn && constraintDenied && (await constraintService.InspectAsync(schemaName, constraintTable)).Count == 2, "invalid columns and missing ownership leave constraints unchanged");
    await editService.ApplyAsync(uniqueConstraintEdit with { Operation = TableConstraintOperation.Drop, Columns = [] });
    Check((await constraintService.InspectAsync(schemaName, constraintTable)).All(c => c.Name != uniqueConstraintName), "DROP UNIQUE constraint with RESTRICT updates catalog");
    await session.ExecuteAsync($"INSERT INTO {constraintTableSql} (id, {SqlIdentifier.Quote("code, \"text")}) VALUES (4, 'ação');");
    await editService.ApplyAsync(primaryKeyEdit with { Operation = TableConstraintOperation.Drop, Columns = [] });
    await session.ExecuteAsync($"INSERT INTO {constraintTableSql} (id, {SqlIdentifier.Quote("code, \"text")}) VALUES (1, 'ação');");
    Check((await constraintService.InspectAsync(schemaName, constraintTable)).Count == 0, "DROP PRIMARY KEY permits duplicate keys after removal");
    await editService.ApplyAsync(checkEdit with { Operation = CheckConstraintOperation.Drop });
    Check((await checkConstraintService.InspectAsync(schemaName, constraintTable)).Count == 0, "DROP CHECK uses RESTRICT and removes the selected CHECK constraint");
    string fkParent = "fk_parent_" + suffix, fkChild = "fk_child_" + suffix, fkName = "fk, \"" + suffix;
    await editService.ApplyAsync(new CreateTableEdit(schemaName, fkParent, [new("tenant", ColumnType.Integer), new("id", ColumnType.Integer)]));
    await session.ExecuteAsync($"ALTER TABLE {ObjectSql.Relation(schemaName, fkParent)} ADD CONSTRAINT {SqlIdentifier.Quote("pk_fk_" + suffix)} PRIMARY KEY (tenant, id);");
    await editService.ApplyAsync(new CreateTableEdit(schemaName, fkChild, [new("tenant", ColumnType.Integer), new("parent_id", ColumnType.Integer)]));
    string fkChildSql = ObjectSql.Relation(schemaName, fkChild), fkParentSql = ObjectSql.Relation(schemaName, fkParent);
    await session.ExecuteAsync($"INSERT INTO {fkParentSql} VALUES (1, 7);");
    var foreignKeyService = new ForeignKeyService(connection);
    var foreignKeyEdit = new ForeignKeyEdit(ForeignKeyOperation.Add, schemaName, fkChild, fkName, ["tenant", "parent_id"], schemaName, fkParent, ["tenant", "id"], ReferentialAction.NoAction, ReferentialAction.Cascade);
    await editService.ApplyAsync(foreignKeyEdit);
    var foreignKeys = await foreignKeyService.InspectAsync(schemaName, fkChild);
    Check(foreignKeys.Count == 1 && foreignKeys[0].Name == fkName && foreignKeys[0].Definition.Contains("ON DELETE CASCADE", StringComparison.Ordinal), "composite FOREIGN KEY inspection reports exact key and referential action");
    await session.ExecuteAsync($"INSERT INTO {fkChildSql} VALUES (1, 7);");
    bool foreignKeyViolation = false;
    try { await session.ExecuteAsync($"INSERT INTO {fkChildSql} VALUES (1, 8);"); } catch (PostgresException ex) when (ex.SqlState == "23503") { foreignKeyViolation = true; }
    await session.ExecuteAsync($"DELETE FROM {fkParentSql} WHERE tenant=1 AND id=7;");
    Check(foreignKeyViolation && (string)(await session.ExecuteAsync($"SELECT count(*) FROM {fkChildSql};")).Tables[0].Rows[0][0] == "0", "FOREIGN KEY rejects missing parent and CASCADE deletes matching child");
    await session.ExecuteAsync($"ALTER TABLE {fkChildSql} ADD CONSTRAINT {SqlIdentifier.Quote(checkConstraintName)} CHECK (tenant > 0);");
    bool foreignKeyWrongTypeProtected = false;
    try { await editService.ApplyAsync(foreignKeyEdit with { Operation = ForeignKeyOperation.Drop, Name = checkConstraintName }); } catch (InvalidOperationException ex) when (ex.Message.Contains("FOREIGN KEY constraints only", StringComparison.Ordinal)) { foreignKeyWrongTypeProtected = true; }
    bool mismatchedForeignKeyColumns = false;
    try { (foreignKeyEdit with { ReferencedColumns = ["id"] }).ToSql(); } catch (ArgumentException) { mismatchedForeignKeyColumns = true; }
    Check(foreignKeyWrongTypeProtected && mismatchedForeignKeyColumns, "FOREIGN KEY drop protects other types and rejects mismatched composite columns");
    await editService.ApplyAsync(foreignKeyEdit with { Operation = ForeignKeyOperation.Drop });
    Check((await foreignKeyService.InspectAsync(schemaName, fkChild)).Count == 0, "DROP FOREIGN KEY uses RESTRICT and removes selected key");
    await session.ExecuteAsync($"DROP TABLE {fkChildSql}, {fkParentSql};");
    bool duplicateConstraintColumns = false;
    try { (primaryKeyEdit with { Columns = ["id", "id"] }).ToSql(); } catch (ArgumentException) { duplicateConstraintColumns = true; }
    bool missingConstraintTable = false;
    try { await constraintService.InspectAsync(schemaName, "missing_table"); } catch (InvalidOperationException) { missingConstraintTable = true; }
    Check(duplicateConstraintColumns && missingConstraintTable && TableConstraintEdit.ParseColumns("id\r\ncode").Length == 2, "constraint names/columns validate and inspection distinguishes missing table");
    var limited = new CatalogService(limitedConnection);
    var visible = await limited.ChildrenAsync(new(CatalogKind.Databases, "Databases"));
    Check(visible.Any(d => d.Name == "postgres"), "ordinary role can read database catalog");
    bool denied = false;
    try { await new AdministrationService(limitedConnection).CreateAsync(new(CreateObjectKind.Database, databaseName + "_denied")); }
    catch (PostgresException ex) when (ex.SqlState == "42501") { denied = true; }
    Check(denied, "database creation respects server permissions");
    var activity = new ActivityService(connection);
    await using (var worker = new NpgsqlConnection(connection))
    {
        await worker.OpenAsync();
        await using var sleep = new NpgsqlCommand("SELECT pg_sleep(15)", worker);
        Task sleeping = sleep.ExecuteNonQueryAsync();
        ActivitySession? selected = null;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            selected = (await activity.ReadAsync()).FirstOrDefault(r => r.Pid == worker.ProcessID && r.State == "active");
            if (selected is not null) break;
            await Task.Delay(25);
        }
        Check(selected is not null, "activity monitor finds running query by PID");
        var filteredActivity = await activity.ReadAsync(new ActivityFilter(Database: "POSTGRES", User: "pgms_test", Application: "PGMS.IntegrationTests", State: "active", Pid: worker.ProcessID));
        Check(filteredActivity.Any(r => r.Pid == worker.ProcessID), "activity filters match database/user/application/state and exact PID");
        Check((await activity.ReadAsync(new ActivityFilter(Database: "no_such_database"))).Count == 0, "activity filter can return an empty result set");
        bool invalidActivityFilter = false;
        try { await activity.ReadAsync(new ActivityFilter(State: "starting")); } catch (ArgumentException) { invalidActivityFilter = true; }
        Check(invalidActivityFilter, "activity service rejects unsupported state values");
        Check(!await activity.SignalAsync(selected! with { BackendStart = selected!.BackendStart.AddSeconds(-1) }, false), "stale backend identity is refused");
        Check(await activity.SignalAsync(selected!, false), "cancel query signal sent to selected session");
        bool interrupted = false;
        try { await sleeping; } catch (PostgresException ex) when (ex.SqlState == "57014") { interrupted = true; }
        Check(interrupted, "cancel backend interrupts actual query");
        await using var ping = new NpgsqlCommand("SELECT 1", worker);
        Check((int)(await ping.ExecuteScalarAsync())! == 1, "cancel query preserves the connection");
        Check(await activity.SignalAsync(selected!, true), "terminate backend confirms selected connection ended");
    }
    await using (var blocker = new NpgsqlConnection(connection))
    await using (var waiter = new NpgsqlConnection(connection))
    {
        await blocker.OpenAsync(); await waiter.OpenAsync();
        await using var begin = new NpgsqlCommand($"BEGIN; LOCK TABLE {schemaId}.sample IN ACCESS EXCLUSIVE MODE", blocker);
        await begin.ExecuteNonQueryAsync();
        await using var waiting = new NpgsqlCommand($"SELECT * FROM {schemaId}.sample", waiter);
        Task waitingTask = waiting.ExecuteNonQueryAsync();
        bool foundBlocker = false;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            foundBlocker = (await activity.ReadAsync()).Any(r => r.Pid == waiter.ProcessID && r.BlockingPids.Contains(blocker.ProcessID));
            if (foundBlocker) break;
            await Task.Delay(25);
        }
        await using var rollback = new NpgsqlCommand("ROLLBACK", blocker); await rollback.ExecuteNonQueryAsync(); await waitingTask;
        Check(foundBlocker, "activity monitor reports actual blocking PID");
    }
    var permissions = new PrivilegeService(connection);
    await permissions.ApplyAsync(new(PrivilegeTarget.Schema, schemaName, roleName, "USAGE"));
    var select = new PrivilegeRequest(PrivilegeTarget.Table, "sample", roleName, "SELECT", Schema: schemaName);
    await permissions.ApplyAsync(select);
    await using (var readerSession = new QuerySession(limitedConnection))
    {
        await readerSession.OpenAsync();
        var data = await readerSession.ExecuteAsync($"SELECT count(*) FROM {schemaId}.sample;");
        Check((string)data.Tables[0].Rows[0][0] == "2", "granted schema USAGE and table SELECT permit limited role reads");
        await permissions.ApplyAsync(select with { Revoke = true });
        bool readDenied = false;
        try { await readerSession.ExecuteAsync($"SELECT * FROM {schemaId}.sample;"); }
        catch (PostgresException ex) when (ex.SqlState == "42501") { readDenied = true; }
        Check(readDenied, "revoking SELECT removes actual limited-role access");
    }
    await permissions.ApplyAsync(new(PrivilegeTarget.Schema, schemaName, roleName, "USAGE", Revoke: true));
    await permissions.ApplyAsync(new(PrivilegeTarget.Database, databaseName, roleName, "CREATE"));
    await permissions.ApplyAsync(new(PrivilegeTarget.Database, databaseName, roleName, "CREATE", Revoke: true));
    Check(true, "database CREATE grant and revoke execute successfully");
    bool invalidPrivilege = false;
    try { new PrivilegeRequest(PrivilegeTarget.Schema, "public", roleName, "SELECT; DROP ROLE x").ToSql(); }
    catch (ArgumentException) { invalidPrivilege = true; }
    Check(invalidPrivilege, "privilege keywords are restricted to object-specific allowlists");
    await new DatabaseEditService(connection).ApplyAsync(new(databaseName, DatabaseOperation.Drop));
    Check(!(await new DatabaseEditService(connection).ListAsync()).Contains(databaseName), "DROP DATABASE removes the selected database without FORCE");
}
finally
{
    // All names were generated by this test in the isolated cluster.
    await session.ExecuteAsync($"ROLLBACK; DROP SCHEMA IF EXISTS {SqlIdentifier.Quote(schemaName)} CASCADE; DROP ROLE IF EXISTS {SqlIdentifier.Quote(roleName)}; DROP ROLE IF EXISTS {SqlIdentifier.Quote(managedRoleName)}; DROP ROLE IF EXISTS {SqlIdentifier.Quote(failedRoleName)}; DROP ROLE IF EXISTS {SqlIdentifier.Quote(groupRoleName)}; DROP ROLE IF EXISTS {SqlIdentifier.Quote("pgms_rename_" + suffix)}; DROP ROLE IF EXISTS {SqlIdentifier.Quote("pgms_password_" + suffix)};");
    await session.ExecuteAsync($"DROP DATABASE IF EXISTS {SqlIdentifier.Quote(databaseName)};");
}
await TableEditChecks.RunAsync(connection, Check);
                await TablePropertyChecks.RunAsync(connection, Check);
                await InspectionEditChecks.RunAsync(connection, Check);
await ConstraintIdentityChecks.RunAsync(connection, Check);
await ConstraintValidationChecks.RunAsync(connection, Check);
                await IndexInspectionChecks.RunAsync(connection, Check);
await AdvancedIndexChecks.RunAsync(connection, Check);
await SequenceChecks.RunAsync(connection, Check);
await IndexIdentityChecks.RunAsync(connection, Check);
Console.WriteLine($"{passed} integration checks passed.");
