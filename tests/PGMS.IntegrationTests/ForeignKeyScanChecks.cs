using Npgsql;
using PGMS.PostgreSql;

// PGMS-037: candidates from names and types, confirmation in the rows, and the statements that create the keys.
internal static class ForeignKeyScanChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        static ScanTable T(string name, string? pk, string[] columns, string[]? foreign = null, string[]? keys = null) =>
            new("public", name, columns.Select(c => c.Split(':')).Select(p => new ScanColumn(p[0], p[1])).ToList(), pk, keys ?? (pk is null ? [] : [pk]), foreign ?? []);
        var tables = new[]
        {
            T("clientes", "id", ["id:integer", "nome:text"]),
            T("Loterias", "CodigoLoteria", ["CodigoLoteria:smallint", "Nome:text"]),
            T("ordens", "id", ["id:bigint"]),
            T("tb_produtos", "codigo", ["codigo:character varying(10)"]),
            T("pedidos", "id", ["id:integer", "cliente_id:integer", "id_ordem:integer", "CodigoLoteria:integer", "produto_cod:text", "valor:numeric", "cidade_id:integer", "nome_id:uuid"]),
            T("ligado", "id", ["id:integer", "cliente_id:integer"], foreign: ["cliente_id"]),
            T("categorias", "id", ["id:integer", "categoria_id:integer", "id_cliente:uuid"]),
        };
        var found = ForeignKeyScan.FindCandidates(tables);
        ForeignKeyCandidate? Of(string table, string column) => found.SingleOrDefault(c => c.SourceTable == table && c.SourceColumn == column);
        check(Of("pedidos", "cliente_id") is { TargetTable: "clientes", TargetColumn: "id", Evidence: "Name points to table clientes" }, "cliente_id points to clientes.id");
        check(Of("pedidos", "id_ordem") is { TargetTable: "ordens", TargetColumn: "id" }, "id_ordem points to ordens, across the -ns plural and integer sizes");
        check(Of("pedidos", "CodigoLoteria") is { TargetTable: "Loterias", TargetColumn: "CodigoLoteria", Evidence: "Same name as the key of Loterias" }, "A column named like another table's key is a candidate, in camel case too");
        check(Of("pedidos", "produto_cod") is { TargetTable: "tb_produtos", TargetColumn: "codigo" }, "The tb_ prefix is ignored and text types are compatible");
        check(Of("categorias", "categoria_id") is { TargetTable: "categorias", TargetColumn: "id" }, "A self reference is a candidate");
        check(Of("pedidos", "valor") is null && Of("pedidos", "cidade_id") is null, "Columns that name no table are left out");
        check(Of("pedidos", "nome_id") is null, "A name with no table of that name is left out");
        check(Of("categorias", "id_cliente") is null, "Incompatible types are left out");
        check(Of("ligado", "cliente_id") is null, "A column already in a foreign key is left out");
        check(!found.Any(c => c.SourceColumn == "id"), "A plain id column is never a candidate");

        check(ForeignKeyScan.Stem("cliente_id") == "cliente" && ForeignKeyScan.Stem("IdCliente") == "cliente" && ForeignKeyScan.Stem("CodigoLoteria") == "loteria" && ForeignKeyScan.Stem("item_pedido_id") == "itempedido" && ForeignKeyScan.Stem("id") == "" && ForeignKeyScan.Stem("nome") == "", "Column names reduce to the table they name");
        check(ForeignKeyScan.TableForms("cidades").Contains("cidade") && ForeignKeyScan.TableForms("categories").Contains("category") && ForeignKeyScan.TableForms("opcoes").Contains("opcao") && ForeignKeyScan.TableForms("papeis").Contains("papel") && ForeignKeyScan.TableForms("paises").Contains("pais") && ForeignKeyScan.TableForms("tb_itens").Contains("item"), "Plural forms in Portuguese and English");
        check(ForeignKeyScan.Compatible("integer", "bigint") && ForeignKeyScan.Compatible("character varying(9)", "text") && ForeignKeyScan.Compatible("numeric(10,2)", "numeric") && !ForeignKeyScan.Compatible("integer", "text") && !ForeignKeyScan.Compatible("uuid", "integer"), "Type compatibility");

        var candidate = new ForeignKeyCandidate("a b", "t\"x", "c1", "integer", "s", "dest", "id", "integer", "");
        check(ForeignKeyScan.Statement(candidate, false) == "ALTER TABLE \"a b\".\"t\"\"x\" ADD CONSTRAINT \"fk_t\"x_c1\" FOREIGN KEY (\"c1\") REFERENCES \"s\".\"dest\" (\"id\");".Replace("fk_t\"x", "fk_t\"\"x", StringComparison.Ordinal), "Statements quote every name");
        check(ForeignKeyScan.Statement(candidate, true).EndsWith(" NOT VALID;", StringComparison.Ordinal), "NOT VALID goes at the end");
        var longName = candidate with { SourceTable = new string('t', 60), SourceColumn = new string('c', 60) };
        var longOther = longName with { SourceColumn = new string('c', 59) + "d" };
        check(System.Text.Encoding.UTF8.GetByteCount(ForeignKeyScan.ConstraintName(longName)) <= 63 && ForeignKeyScan.ConstraintName(longName) != ForeignKeyScan.ConstraintName(longOther), "Long constraint names fit and stay distinct");

        string schema = "qfks_" + Guid.NewGuid().ToString("N")[..8], other = schema + "x";
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<long> Count(string sql) { await using var command = new NpgsqlCommand(sql, admin); return (long)(await command.ExecuteScalarAsync())!; }
        await Sql($"""
            CREATE SCHEMA {schema}; CREATE SCHEMA {other};
            CREATE TABLE {schema}.clientes(id int PRIMARY KEY);
            CREATE TABLE {schema}.ativos(id int PRIMARY KEY);
            CREATE TABLE {other}.unicos(codigo text UNIQUE, x int);
            CREATE TABLE {schema}.pedidos(id int PRIMARY KEY, cliente_id int, ativo_id int, vazio_id int);
            CREATE TABLE {schema}.vazios(id int PRIMARY KEY);
            CREATE TABLE {schema}.ja(id int PRIMARY KEY, cliente_id int REFERENCES {schema}.clientes(id));
            CREATE TABLE {schema}.composta(a int, b int, PRIMARY KEY (a, b));
            CREATE TABLE {schema}.parts(id int, cliente_id int, PRIMARY KEY (id)) PARTITION BY RANGE (id);
            CREATE TABLE {schema}.parts_1 PARTITION OF {schema}.parts FOR VALUES FROM (0) TO (10);
            CREATE TABLE {schema}.m(id int PRIMARY KEY, codigo text);
            ALTER TABLE {schema}.pedidos ADD COLUMN apagada int; ALTER TABLE {schema}.pedidos DROP COLUMN apagada;
            INSERT INTO {schema}.clientes VALUES (1),(2); INSERT INTO {schema}.ativos VALUES (1);
            INSERT INTO {schema}.pedidos VALUES (1, 1, 1, NULL), (2, 2, 99, NULL), (3, NULL, 98, NULL);
            """);
        try
        {
            var read = await ForeignKeyScan.ReadTablesAsync(connection);
            ScanTable Table(string name) => read.Single(t => t.Schema == schema && t.Name == name);
            check(Table("clientes") is { PrimaryKey: "id", Keys: ["id"] } && read.Single(t => t.Schema == other && t.Name == "unicos") is { PrimaryKey: null, Keys: ["codigo"] }, "Primary keys and unique constraints are read");
            check(Table("composta") is { PrimaryKey: null, Keys.Count: 0 }, "A composite key cannot be a single-column target");
            check(Table("ja").ForeignKeyColumns.SequenceEqual(["cliente_id"]) && Table("parts") is not null && !read.Any(t => t.Name == "parts_1") && !read.Any(t => t.Schema.StartsWith("pg_", StringComparison.Ordinal) || t.Schema == "information_schema"), "Existing foreign keys are read; partitions and system schemas are left out");
            check(Table("pedidos").Columns.Select(c => c.Name).SequenceEqual(["id", "cliente_id", "ativo_id", "vazio_id"]), "Dropped columns are left out");

            var scoped = ForeignKeyScan.FindCandidates(read.Where(t => t.Schema == schema || t.Schema == other).ToList());
            ForeignKeyCandidate Real(string table, string column) => scoped.Single(c => c.SourceTable == table && c.SourceColumn == column && c.TargetSchema == schema);
            await using var reader = new NpgsqlConnection(connection);
            await reader.OpenAsync();
            var good = await ForeignKeyScan.CheckDataAsync(reader, Real("pedidos", "cliente_id"), 30);
            check(good is { State: ForeignKeyDataState.Matches, NonNull: 2, Orphans: 0 }, $"NULLs do not count and every value matches: {good}");
            var orphans = await ForeignKeyScan.CheckDataAsync(reader, Real("pedidos", "ativo_id"), 30);
            check(orphans is { State: ForeignKeyDataState.Orphans, NonNull: 3, Orphans: 2 }, $"Rows without a match are counted: {orphans}");
            var empty = await ForeignKeyScan.CheckDataAsync(reader, Real("pedidos", "vazio_id"), 30);
            check(empty is { State: ForeignKeyDataState.Empty, NonNull: 0 }, $"A column with no values cannot be confirmed: {empty}");
            check(await Count($"SELECT count(*) FROM {schema}.pedidos") == 3 && (await ForeignKeyScan.CheckDataAsync(reader, Real("pedidos", "cliente_id"), 30)).State == ForeignKeyDataState.Matches, "The check changes nothing and the connection stays usable");

            // A lock held elsewhere makes the check wait, so the time limit is what ends it.
            await Sql($"BEGIN; LOCK TABLE {schema}.pedidos IN ACCESS EXCLUSIVE MODE");
            var slow = await ForeignKeyScan.CheckDataAsync(reader, Real("pedidos", "cliente_id"), 1);
            await Sql("ROLLBACK");
            check(slow is { State: ForeignKeyDataState.TimedOut, Message: not null }, $"A check that takes too long stops at its limit: {slow}");
            check((await ForeignKeyScan.CheckDataAsync(reader, Real("pedidos", "cliente_id"), 30)).State == ForeignKeyDataState.Matches, "After a time-out the next candidate is still checked");

            var bad = Real("pedidos", "cliente_id") with { SourceTable = "missing_table" };
            check((await ForeignKeyScan.CheckDataAsync(reader, bad, 30)) is { State: ForeignKeyDataState.Failed, Message: not null }, "A table that vanished is reported, not fatal");

            bool refused = false;
            try { await ForeignKeyScan.ApplyAsync(connection, [ForeignKeyScan.Statement(Real("pedidos", "cliente_id"), false), ForeignKeyScan.Statement(Real("pedidos", "ativo_id"), false)]); }
            catch (PostgresException ex) { refused = ex.SqlState == "23503"; }
            check(refused && await Count($"SELECT count(*) FROM pg_constraint WHERE conrelid = '{schema}.pedidos'::regclass AND contype = 'f'") == 0, "One failure creates nothing");
            await ForeignKeyScan.ApplyAsync(connection, [ForeignKeyScan.Statement(Real("pedidos", "cliente_id"), false), ForeignKeyScan.Statement(Real("pedidos", "ativo_id"), true)]);
            check(await Count($"SELECT count(*) FROM pg_constraint WHERE conrelid = '{schema}.pedidos'::regclass AND contype = 'f' AND convalidated") == 1 && await Count($"SELECT count(*) FROM pg_constraint WHERE conrelid = '{schema}.pedidos'::regclass AND contype = 'f' AND NOT convalidated") == 1, "A clean key is validated and one with orphans is NOT VALID");
            string? rejected = null;
            try { await Sql($"INSERT INTO {schema}.pedidos VALUES (9, NULL, 77, NULL)"); } catch (PostgresException ex) { rejected = ex.SqlState; }
            check(rejected == "23503", "NOT VALID still enforces the key on new rows");
            var again = ForeignKeyScan.FindCandidates((await ForeignKeyScan.ReadTablesAsync(connection)).Where(t => t.Schema == schema || t.Schema == other).ToList());
            check(!again.Any(c => c.SourceTable == "pedidos" && c.SourceColumn is "cliente_id" or "ativo_id"), "Keys created are no longer candidates");
        }
        finally { await Sql($"DROP SCHEMA {schema} CASCADE; DROP SCHEMA {other} CASCADE"); }
    }
}
