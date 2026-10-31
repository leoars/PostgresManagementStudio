using System.Collections.ObjectModel;
using System.Data;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public sealed record EnumLabel(uint Oid, string Value);
public sealed class EnumSnapshot
{
    public InspectedDataType Type { get; }
    public ReadOnlyCollection<EnumLabel> Labels { get; }
    internal EnumSnapshot(InspectedDataType type, IEnumerable<EnumLabel> labels)
    { Type = type; Labels = Array.AsReadOnly(labels.ToArray()); }
}

public sealed class EnumCatalog(string connectionString)
{
    public async Task<EnumSnapshot> InspectAsync(string schema, string name, CancellationToken token = default)
    {
        SqlIdentifier.Quote(schema); SqlIdentifier.Quote(name);
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(token).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, token).ConfigureAwait(false);
        await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", c, tx))
            await ro.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return await Read(c, tx, schema, name, token).ConfigureAwait(false);
    }
    internal static async Task<EnumSnapshot> Read(NpgsqlConnection c, NpgsqlTransaction tx, string schema, string name, CancellationToken token)
    {
        var types = await DataTypeCatalog.Read(c, tx, schema, name, token).ConfigureAwait(false);
        if (types.Count != 1 || types[0].Kind != "e") throw new InvalidOperationException("Enum not found or USAGE unavailable. Inspect the exact enum name.");
        var type = types[0];
        await using var command = new NpgsqlCommand("SELECT oid,enumlabel FROM pg_catalog.pg_enum WHERE enumtypid=@oid ORDER BY enumsortorder LIMIT 1001", c, tx) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, type.Oid);
        var labels = new List<EnumLabel>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) labels.Add(new(reader.GetFieldValue<uint>(0), reader.GetString(1)));
        if (labels.Count > 1000) throw new InvalidOperationException("Enum exceeds the 1000-label inspection limit.");
        return new(type, labels);
    }
}

public enum EnumValueOperation { Append, Before, After, Rename }
public sealed class EnumValueEdit(EnumSnapshot target, EnumValueOperation operation, string value, string? existingValue = null) : IAdministrativeEdit
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0') || StrictUtf8.GetByteCount(value) > 63) throw new ArgumentException("Enum labels must contain no NUL and at most 63 UTF-8 bytes.");
        return "E'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";
    }
    private List<EnumLabel> Expected()
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Type.Kind != "e") throw new ArgumentException("Select an enum.");
        Literal(value);
        if (target.Labels.Any(x => x.Value == value)) throw new ArgumentException("The new label already exists.");
        var labels = target.Labels.ToList();
        int index = existingValue is null ? -1 : labels.FindIndex(x => x.Value == existingValue);
        if (operation != EnumValueOperation.Append && index < 0) throw new ArgumentException("Select an existing label.");
        if (operation != EnumValueOperation.Rename && labels.Count >= 1000) throw new ArgumentException("Enum cannot exceed 1000 labels in this flow.");
        switch (operation)
        {
            case EnumValueOperation.Append when existingValue is null: labels.Add(new(0, value)); break;
            case EnumValueOperation.Before: labels.Insert(index, new(0, value)); break;
            case EnumValueOperation.After: labels.Insert(index + 1, new(0, value)); break;
            case EnumValueOperation.Rename: labels[index] = labels[index] with { Value = value }; break;
            default: throw new ArgumentException("Unsupported enum operation or unused existing label.");
        }
        return labels;
    }
    public string ToSql()
    {
        Expected();
        string prefix = "ALTER TYPE " + target.Type.ToSql();
        return operation switch
        {
            EnumValueOperation.Rename => prefix + " RENAME VALUE " + Literal(existingValue!) + " TO " + Literal(value) + ";",
            EnumValueOperation.Append => prefix + " ADD VALUE " + Literal(value) + ";",
            _ => prefix + " ADD VALUE " + Literal(value) + (operation == EnumValueOperation.Before ? " BEFORE " : " AFTER ") + Literal(existingValue!) + ";"
        };
    }
    public async Task ValidateAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken token)
    {
        Expected();
        var actual = await EnumCatalog.Read(c, tx, target.Type.Schema, target.Type.Name, token).ConfigureAwait(false);
        if (actual.Type != target.Type || !actual.Labels.SequenceEqual(target.Labels))
            throw new InvalidOperationException("Enum identity or definition changed. Inspect again.");
    }
    public async Task VerifyAppliedAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken token)
    {
        var actual = await EnumCatalog.Read(c, tx, target.Type.Schema, target.Type.Name, token).ConfigureAwait(false);
        var expected = Expected();
        if (actual.Type with { Signature = target.Type.Signature } != target.Type || actual.Labels.Count != expected.Count ||
            actual.Labels.Where((label, i) => label.Value != expected[i].Value || expected[i].Oid != 0 && label.Oid != expected[i].Oid).Any())
            throw new InvalidOperationException("Enum identity or ordered labels differ from the reviewed result. Operation rolled back.");
    }
}
