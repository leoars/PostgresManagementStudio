using System.Globalization;
using Npgsql;

namespace PGMS.PostgreSql;

public enum SequenceOperation { Inspect, Create, Rename, Comment, Drop, SetIncrement, SetCache, SetCycle, SetStart, SetBounds, SetDataType, Restart, DetachOwnedBy, SetOwnedBy }
public enum SequenceDataType { SmallInt, Integer, BigInt }

public sealed record SequenceCreateEdit(string Schema, string Name) : IAdministrativeEdit
{
    public SequenceDataType DataType { get; init; } = SequenceDataType.BigInt;
    public long Increment { get; init; } = 1;
    public long? Start { get; init; }
    public long? Minimum { get; init; }
    public long? Maximum { get; init; }
    public long Cache { get; init; } = 1;
    public bool Cycle { get; init; }
    public string ToSql()
    {
        string target = ObjectSql.Relation(Schema, Name);
        (string type, long low, long high) = DataType switch
        {
            SequenceDataType.SmallInt => ("smallint", short.MinValue, short.MaxValue),
            SequenceDataType.Integer => ("integer", int.MinValue, int.MaxValue),
            SequenceDataType.BigInt => ("bigint", long.MinValue, long.MaxValue),
            _ => throw new ArgumentException("Unsupported sequence type.")
        };
        if (Increment == 0) throw new ArgumentException("Increment must not be zero.");
        if (Cache < 1) throw new ArgumentException("Cache must be a positive integer.");
        long minimum = Minimum ?? (Increment > 0 ? 1 : low), maximum = Maximum ?? (Increment > 0 ? high : -1);
        long start = Start ?? (Increment > 0 ? minimum : maximum);
        if (minimum < low || maximum > high || minimum >= maximum)
            throw new ArgumentException("Minimum and maximum must fit the selected type, with minimum below maximum.");
        if (start < minimum || start > maximum) throw new ArgumentException("Start must be within the effective minimum and maximum.");
        static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        return $"CREATE SEQUENCE {target} AS {type} INCREMENT BY {Number(Increment)}"
            + (Minimum is { } min ? " MINVALUE " + Number(min) : "")
            + (Maximum is { } max ? " MAXVALUE " + Number(max) : "")
            + (Start is { } first ? " START WITH " + Number(first) : "")
            + " CACHE " + Number(Cache) + (Cycle ? " CYCLE" : " NO CYCLE") + ";";
    }
}


public sealed record SequenceChangeEdit(string Schema, string Name, SequenceOperation Operation) : IAdministrativeEdit
{
    public uint? ExpectedOid { get; init; }
    public string NewName { get; init; } = "";
    public string Comment { get; init; } = "";
    public bool RemoveComment { get; init; }
    public string ToSql()
    {
        string target = ObjectSql.Relation(Schema, Name);
        return Operation switch
        {
            SequenceOperation.Rename when Name == NewName => throw new ArgumentException("Choose a different sequence name."),
            SequenceOperation.Rename => $"ALTER SEQUENCE {target} RENAME TO {SqlIdentifier.Quote(NewName)};",
            SequenceOperation.Drop => $"DROP SEQUENCE {target} RESTRICT;",
            SequenceOperation.Comment => $"COMMENT ON SEQUENCE {target} IS {CommentSql()};",
            _ => throw new ArgumentException("Unsupported sequence change.")
        };
    }
    private string CommentSql()
    {
        if (RemoveComment) return "NULL";
        if (Comment.Length == 0 || Comment.Length > 10000 || Comment.Contains('\0'))
            throw new ArgumentException("Enter a comment (up to 10000 characters, no NUL), or select Remove existing comment.");
        return "E'" + Comment.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal) + "'";
    }
    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        if (ExpectedOid is null) throw new InvalidOperationException("Inspect the sequence before Apply.");
        // A SELECT without rows obtains AccessShareLock without consuming sequence values.
        // Keep it to transaction end, then resolve the name again while DROP/rename are blocked.
        // This deliberately requires SELECT privilege in addition to DDL ownership.
        await using var guard = new NpgsqlCommand($"SELECT 1 FROM {ObjectSql.Relation(Schema, Name)} LIMIT 0;", connection, transaction) { CommandTimeout = 30 };
        await guard.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (await CurrentOidAsync(connection, transaction, Name, token).ConfigureAwait(false) != ExpectedOid)
            throw new InvalidOperationException("Sequence identity changed. Inspect again and review the current sequence.");
    }
    private async Task<uint?> CurrentOidAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string name, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT c.oid FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@name AND c.relkind='S'", connection, transaction) { CommandTimeout = 30 };
        command.Parameters.AddWithValue("schema", Schema); command.Parameters.AddWithValue("name", name);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is uint oid ? oid : null;
    }
    public async Task VerifyAppliedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        if (Operation == SequenceOperation.Drop)
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_class WHERE oid=@oid)", connection, transaction);
            command.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, ExpectedOid!.Value);
            if ((bool)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!)
                throw new InvalidOperationException("Inspected sequence was not removed. The change was not committed.");
            return;
        }
        if (await CurrentOidAsync(connection, transaction, Operation == SequenceOperation.Rename ? NewName : Name, token).ConfigureAwait(false) != ExpectedOid)
            throw new InvalidOperationException("Sequence identity changed during the operation. The change was not committed.");
    }
}
