using System.Globalization;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record SequenceParameterEdit(string Schema, string Name, SequenceOperation Operation) : IAdministrativeEdit
{
    public SequenceSnapshot? Expected { get; init; }
    public long Value { get; init; }
    public bool Cycle { get; init; }
    public long Maximum { get; init; }
    public SequenceDataType DataType { get; init; } = SequenceDataType.BigInt;

    public string ToSql()
    {
        if (Operation == SequenceOperation.SetStart && Expected is { } snapshot && (Value < snapshot.Minimum || Value > snapshot.Maximum))
            throw new ArgumentException("Configured START must be within the inspected minimum and maximum.");
        if (Operation == SequenceOperation.SetBounds)
        {
            if (Value >= Maximum) throw new ArgumentException("Minimum must be below maximum.");
            if (Expected is { } bounds)
            {
                (long low, long high) = bounds.DataType switch
                {
                    "smallint" => (short.MinValue, short.MaxValue),
                    "integer" => (int.MinValue, int.MaxValue),
                    "bigint" => (long.MinValue, long.MaxValue),
                    _ => throw new ArgumentException("Unsupported inspected sequence type.")
                };
                if (Value < low || Maximum > high) throw new ArgumentException("Bounds must fit the inspected sequence type.");
                if (bounds.Start < Value || bounds.Start > Maximum)
                    throw new ArgumentException("Bounds must include configured START. Change START first if needed.");
            }
        }
        string clause = Operation switch
        {
            SequenceOperation.SetIncrement when Value != 0 => "INCREMENT BY " + Value.ToString(CultureInfo.InvariantCulture),
            SequenceOperation.SetDataType => TypeClause(),
            SequenceOperation.SetBounds => "MINVALUE " + Value.ToString(CultureInfo.InvariantCulture) + " MAXVALUE " + Maximum.ToString(CultureInfo.InvariantCulture),
            SequenceOperation.SetStart => "START WITH " + Value.ToString(CultureInfo.InvariantCulture),
            SequenceOperation.SetCycle => Cycle ? "CYCLE" : "NO CYCLE",
            SequenceOperation.SetCache when Value >= 1 => "CACHE " + Value.ToString(CultureInfo.InvariantCulture),
            SequenceOperation.SetCache => throw new ArgumentException("Cache must be a positive whole number."),
            SequenceOperation.SetIncrement => throw new ArgumentException("Increment must be a nonzero whole number."),
            _ => throw new ArgumentException("Unsupported sequence parameter.")
        };
        return $"ALTER SEQUENCE {ObjectSql.Relation(Schema, Name)} {clause};";
    }

    private (string Name, long Low, long High) TypeRange => DataType switch
    {
        SequenceDataType.SmallInt => ("smallint", short.MinValue, short.MaxValue),
        SequenceDataType.Integer => ("integer", int.MinValue, int.MaxValue),
        SequenceDataType.BigInt => ("bigint", long.MinValue, long.MaxValue),
        _ => throw new ArgumentException("Unsupported sequence type.")
    };
    private string TypeClause()
    {
        if (Expected is null || Expected.Schema != Schema || Expected.Name != Name)
            throw new ArgumentException("Inspect this sequence before reviewing a type change.");
        var type = TypeRange;
        if (Expected.Minimum < type.Low || Expected.Maximum > type.High || Expected.Increment < type.Low || Expected.Increment > type.High)
            throw new ArgumentException("Inspected bounds and increment must fit the new type. Adjust them first, then inspect again.");
        // Explicit bounds prevent PostgreSQL from automatically widening old default bounds.
        return "AS " + type.Name + " MINVALUE " + Expected.Minimum.ToString(CultureInfo.InvariantCulture)
            + " MAXVALUE " + Expected.Maximum.ToString(CultureInfo.InvariantCulture);
    }

    public async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        if (Expected is null || Expected.Schema != Schema || Expected.Name != Name)
            throw new InvalidOperationException("Inspect this sequence before Apply.");
        // Pin identity and ownership with AccessShareLock before upgrading the lock.
        await new SequenceChangeEdit(Schema, Name, SequenceOperation.Rename) { ExpectedOid = Expected.Oid }
            .ValidateAsync(connection, transaction, token).ConfigureAwait(false);
        await using (var owner = new NpgsqlCommand("SELECT pg_catalog.pg_get_userbyid(relowner) FROM pg_catalog.pg_class WHERE oid=@oid", connection, transaction))
        {
            owner.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Oid, Expected.Oid);
            if ((string?)await owner.ExecuteScalarAsync(token).ConfigureAwait(false) != Expected.Owner)
                throw new InvalidOperationException("Sequence owner changed. Inspect again.");
        }
        // OWNER TO the already-checked owner is a no-op with AccessExclusiveLock.
        // Unlike AccessShareLock alone, it serializes parameter ALTERs, including owned sequences.
        await using var guard = new NpgsqlCommand($"ALTER SEQUENCE {ObjectSql.Relation(Schema, Name)} OWNER TO {SqlIdentifier.Quote(Expected.Owner)};", connection, transaction) { CommandTimeout = 30 };
        await guard.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var actual = await ReadAsync(connection, transaction, token).ConfigureAwait(false);
        if (actual != Configuration(Expected))
            throw new InvalidOperationException("Sequence configuration or identity changed. Inspect again and review your proposal.");
    }

    private sealed record State(uint Oid, string Type, long Start, long Increment, long Minimum, long Maximum, long Cache, bool Cycle);
    private static State Configuration(SequenceSnapshot s) => new(s.Oid, s.DataType, s.Start, s.Increment, s.Minimum, s.Maximum, s.Cache, s.Cycle);
    private async Task<State> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT c.oid, pg_catalog.format_type(s.seqtypid,NULL), s.seqstart, s.seqincrement,
                   s.seqmin, s.seqmax, s.seqcache, s.seqcycle
            FROM pg_catalog.pg_sequence s JOIN pg_catalog.pg_class c ON c.oid=s.seqrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=@schema AND c.relname=@name AND c.relkind='S'
            """, connection, transaction) { CommandTimeout = 30 };
        command.Parameters.AddWithValue("schema", Schema); command.Parameters.AddWithValue("name", Name);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Sequence identity changed. Inspect again.");
        return new(reader.GetFieldValue<uint>(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetBoolean(7));
    }

    public async Task VerifyAppliedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        var original = Configuration(Expected!);
        var desired = Operation switch
        {
            SequenceOperation.SetDataType => original with { Type = TypeRange.Name },
            SequenceOperation.SetIncrement => original with { Increment = Value },
            SequenceOperation.SetCache => original with { Cache = Value },
            SequenceOperation.SetBounds => original with { Minimum = Value, Maximum = Maximum },
            SequenceOperation.SetStart => original with { Start = Value },
            SequenceOperation.SetCycle => original with { Cycle = Cycle },
            _ => throw new ArgumentException("Unsupported sequence parameter.")
        };
        if (await ReadAsync(connection, transaction, token).ConfigureAwait(false) != desired)
            throw new InvalidOperationException("Sequence configuration did not match the proposal. The change was not committed.");
    }
}
