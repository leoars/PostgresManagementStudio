using Npgsql;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PGMS.PostgreSql;

// Value-only snapshot: no mutable argument array and no display text parsed as SQL.
public sealed record RoutinePrivilegeSelection
{
    // Internal JSON data, never HTML. Avoid expanding quoted identifiers to \u0022
    // so a valid catalog snapshot can be restored under the same size bound.
    private static readonly JsonSerializerOptions IdentityJson = new()
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public uint DatabaseOid { get; }
    public uint ObjectOid { get; }
    public uint NamespaceOid { get; }
    public string Schema { get; }
    public string Name { get; }
    public string Kind { get; }
    public string ArgumentTypesJson { get; }
    [JsonIgnore] public string InputTypeOids { get; }
    [JsonIgnore] public string ArgumentSql { get; }
    private sealed record ArgumentType(uint Oid, uint NamespaceOid, string Schema, string Name);

    // Only identifiers and OIDs are restored; SQL is always regenerated with quoting.
    // The service still checks this snapshot against the live catalog before commit.
    [JsonConstructor]
    public RoutinePrivilegeSelection(uint databaseOid, uint objectOid, uint namespaceOid,
        string schema, string name, string kind, string argumentTypesJson)
    {
        if (databaseOid == 0 || objectOid == 0 || namespaceOid == 0 || kind is not ("f" or "p" or "a" or "w"))
            throw new ArgumentException("Invalid routine identity.");
        _ = SqlIdentifier.Quote(schema); _ = SqlIdentifier.Quote(name);
        if (argumentTypesJson is null || argumentTypesJson.Length > 32768) throw new ArgumentException("Invalid routine argument types.");
        var arguments = JsonSerializer.Deserialize<ArgumentType[]>(argumentTypesJson) ?? throw new ArgumentException("Missing routine argument types.");
        if (arguments.Length > 100) throw new ArgumentException("Too many routine input arguments.");
        foreach (var argument in arguments)
        {
            if (argument is null || argument.Oid == 0 || argument.NamespaceOid == 0) throw new ArgumentException("Invalid routine argument identity.");
            _ = SqlIdentifier.Quote(argument.Schema); _ = SqlIdentifier.Quote(argument.Name);
        }
        DatabaseOid = databaseOid; ObjectOid = objectOid; NamespaceOid = namespaceOid;
        Schema = schema; Name = name; Kind = kind;
        ArgumentTypesJson = JsonSerializer.Serialize(arguments, IdentityJson);
        if (ArgumentTypesJson.Length > 32768) throw new ArgumentException("Routine argument identity exceeds its size limit.");
        InputTypeOids = string.Join(" ", arguments.Select(a => a.Oid.ToString(CultureInfo.InvariantCulture)));
        ArgumentSql = string.Join(", ", arguments.Select(a => SqlIdentifier.Quote(a.Schema) + "." + SqlIdentifier.Quote(a.Name)));
    }
    [JsonIgnore] public string QualifiedSignature => $"{SqlIdentifier.Quote(Schema)}.{SqlIdentifier.Quote(Name)}({ArgumentSql})";
}

public sealed class RoutinePrivilegeCatalog(string connectionString)
{
    public async Task<RoutinePrivilegeSelection> InspectAsync(uint oid, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        return (await ReadAsync(connection, transaction, oid, token).ConfigureAwait(false)).Selection;
    }

    internal static async Task<(RoutinePrivilegeSelection Selection, bool Written)> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, uint oid, CancellationToken token)
    {
        const string sql = """
            SELECT (SELECT oid FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database()),
                p.oid,n.oid,n.nspname,p.proname,p.prokind::text,p.proargtypes::text,
                COALESCE((SELECT pg_catalog.json_agg(pg_catalog.json_build_object('Oid',t.oid::bigint,'NamespaceOid',tn.oid::bigint,'Schema',tn.nspname,'Name',t.typname) ORDER BY a.position)::text
                    FROM pg_catalog.unnest(p.proargtypes::oid[]) WITH ORDINALITY a(type_oid,position)
                    JOIN pg_catalog.pg_type t ON t.oid=a.type_oid
                    JOIN pg_catalog.pg_namespace tn ON tn.oid=t.typnamespace),'[]'),
                COALESCE(p.xmin::text::numeric=mod(pg_catalog.pg_current_xact_id_if_assigned()::text::numeric,4294967296),false)
            FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            WHERE p.oid=@oid
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, oid);
        await using var row = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await row.ReadAsync(token).ConfigureAwait(false)) throw new PrivilegeTargetMissingException();
        var selection = new RoutinePrivilegeSelection(row.GetFieldValue<uint>(0), row.GetFieldValue<uint>(1), row.GetFieldValue<uint>(2),
            row.GetString(3), row.GetString(4), row.GetString(5), row.GetString(7));
        if (selection.InputTypeOids != row.GetString(6)) throw PrivilegeTargetIdentity.Conflict();
        return (selection, row.GetBoolean(8));
    }
}
