using System.Globalization;
using Npgsql;

namespace PGMS.PostgreSql;

public sealed record SequenceRestartEdit(string Schema, string Name, long Value) : IAdministrativeEdit
{
    public SequenceSnapshot? Expected { get; init; }
    private SequenceParameterEdit Guard => new(Schema, Name, SequenceOperation.SetStart)
        { Expected = Expected, Value = Expected?.Start ?? 0 };

    public string ToSql()
    {
        if (Expected is null || Expected.Schema != Schema || Expected.Name != Name)
            throw new ArgumentException("Inspect this sequence before reviewing a restart.");
        if (Value < Expected.Minimum || Value > Expected.Maximum)
            throw new ArgumentException("Restart value must be within the inspected limits.");
        return $"ALTER SEQUENCE {ObjectSql.Relation(Schema, Name)} RESTART WITH {Value.ToString(CultureInfo.InvariantCulture)};";
    }

    public Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
        => Guard.ValidateAsync(connection, transaction, token);

    public async Task VerifyAppliedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token = default)
    {
        await Guard.VerifyAppliedAsync(connection, transaction, token).ConfigureAwait(false);
        // The guard holds an exclusive lock; verification never calls nextval.
        await using var command = new NpgsqlCommand($"SELECT last_value, is_called FROM {ObjectSql.Relation(Schema, Name)}", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetInt64(0) != Value || reader.GetBoolean(1))
            throw new InvalidOperationException("Restart state did not match the proposal. The change was not committed.");
    }
}
