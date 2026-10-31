using Npgsql;
using PGMS.PostgreSql;

internal static class EnumEditChecks
{
    private sealed class BetweenValidationAndDdl(IAdministrativeEdit edit, Func<Task> change) : IAdministrativeEdit
    {
        public string ToSql() => edit.ToSql();
        public async Task ValidateAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken token)
        { await edit.ValidateAsync(c, tx, token); await change(); }
        public Task VerifyAppliedAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken token) => edit.VerifyAppliedAsync(c, tx, token);
    }
    private sealed class RejectAfter(IAdministrativeEdit edit) : IAdministrativeEdit
    {
        public string ToSql() => edit.ToSql();
        public Task ValidateAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken token) => edit.ValidateAsync(c, tx, token);
        public async Task VerifyAppliedAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken token)
        { await edit.VerifyAppliedAsync(c, tx, token); throw new IOException("fixture after DDL"); }
    }
    internal static async Task RunAsync(string connection, Action<bool,string> check)
    {
        await using var f = await FutureDefaultFixture.CreateAsync(connection);
        await f.Owner($"CREATE TYPE {f.QS}.mood AS ENUM('a','c');CREATE TABLE {f.QS}.values(v {f.QS}.mood);INSERT INTO {f.QS}.values VALUES('a')");
        var catalog = new EnumCatalog(f.CreatorConnection);
        var service = new AdministrativeEditService(f.CreatorConnection);
        Task<EnumSnapshot> Inspect() => catalog.InspectAsync(f.Schema, "mood");
        var original = await Inspect();
        check(original.Labels.Select(x => x.Value).SequenceEqual(new[]{"a","c"}) && original.Labels.All(x => x.Oid != 0), "Enum inspection preserves ordered labels and member OIDs");
        await service.ApplyAsync(new EnumValueEdit(original, EnumValueOperation.Before, "b", "c"));
        var after = await Inspect();
        check(after.Labels.Select(x => x.Value).SequenceEqual(new[]{"a","b","c"}), "Enum BEFORE inserts at the reviewed ordinal position");
        bool stale = false; try { await service.ApplyAsync(new EnumValueEdit(original, EnumValueOperation.Append, "stale")); } catch (InvalidOperationException) { stale = true; }
        check(stale, "Enum edit refuses stale label snapshot");
        await service.ApplyAsync(new EnumValueEdit(after, EnumValueOperation.After, "b2", "b"));
        check((await Inspect()).Labels.Select(x => x.Value).SequenceEqual(new[]{"a","b","b2","c"}), "Enum AFTER preserves relative order");
        string unicode = "ação漢字'\\;--";
        after = await Inspect();
        await service.ApplyAsync(new EnumValueEdit(after, EnumValueOperation.Rename, unicode, "a"));
        var renamed = await Inspect();
        check(renamed.Labels[0].Value == unicode && renamed.Labels[0].Oid == original.Labels[0].Oid, "Enum rename preserves member identity with Unicode, quote and backslash");
        await using var c = new NpgsqlConnection(f.CreatorConnection); await c.OpenAsync();
        await using (var command = new NpgsqlCommand($"SELECT v::text FROM {f.QS}.values", c))
            check((string)(await command.ExecuteScalarAsync())! == unicode, "Enum rename changes the text representation of already stored values");
        await service.ApplyAsync(new EnumValueEdit(renamed, EnumValueOperation.Append, ""));
        check((await Inspect()).Labels.Last().Value == "", "Enum supports an explicit empty label distinct from NULL");
        bool rolledBack = false;
        try { await service.ApplyAsync(new RejectAfter(new EnumValueEdit(await Inspect(), EnumValueOperation.Append, "rollback"))); } catch (IOException) { rolledBack = true; }
        check(rolledBack && !(await Inspect()).Labels.Any(x => x.Value == "rollback"), "Failure after enum DDL rolls back the new label");
        foreach (string invalid in new[]{new string('é',32), "nul\0label"})
        {
            bool rejected = false; try { new EnumValueEdit(await Inspect(), EnumValueOperation.Append, invalid).ToSql(); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Enum rejects invalid NUL or oversized UTF-8 label before SQL");
        }
        bool duplicate = false; try { new EnumValueEdit(await Inspect(), EnumValueOperation.Append, "b").ToSql(); } catch (ArgumentException) { duplicate = true; }
        check(duplicate, "Enum duplicate label is refused without IF NOT EXISTS masking conflicts");
        bool denied = false;
        await f.Owner($"GRANT USAGE ON SCHEMA {f.QS} TO {f.QR}");
        try { await new AdministrativeEditService(f.RecipientConnection).ApplyAsync(new EnumValueEdit(await Inspect(), EnumValueOperation.Append, "unauthorized")); } catch (PostgresException ex) when (ex.SqlState == "42501") { denied = true; }
        check(denied && !(await Inspect()).Labels.Any(x => x.Value == "unauthorized"), "USAGE-only connection cannot alter an enum");
        await f.Owner($"CREATE TYPE {f.QS}.replaceable AS ENUM('a')");
        var old = await catalog.InspectAsync(f.Schema,"replaceable");
        await f.Owner($"DROP TYPE {f.QS}.replaceable;CREATE TYPE {f.QS}.replaceable AS ENUM('a')");
        bool replaced = false; try { await service.ApplyAsync(new EnumValueEdit(old, EnumValueOperation.Append, "new")); } catch (InvalidOperationException) { replaced = true; }
        check(replaced, "Enum edit rejects same-name replacement even with identical labels");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool cancel = false; try { await service.ApplyAsync(new EnumValueEdit(await Inspect(), EnumValueOperation.Append, "canceled"), canceled.Token); } catch (OperationCanceledException) { cancel = true; }
        check(cancel && !(await Inspect()).Labels.Any(x => x.Value == "canceled"), "Canceled enum edit sends no DDL");
        bool concurrent = false;
        try { await service.ApplyAsync(new BetweenValidationAndDdl(new EnumValueEdit(await Inspect(), EnumValueOperation.Append, "ours"), () => f.Owner($"ALTER TYPE {f.QS}.mood ADD VALUE 'theirs'"))); }
        catch (InvalidOperationException) { concurrent = true; }
        after = await Inspect();
        check(concurrent && after.Labels.Any(x => x.Value == "theirs") && !after.Labels.Any(x => x.Value == "ours"), "Concurrent label change after validation rolls back only our enum DDL");
        old = await catalog.InspectAsync(f.Schema,"replaceable");
        replaced = false;
        try { await service.ApplyAsync(new BetweenValidationAndDdl(new EnumValueEdit(old, EnumValueOperation.Append, "ours"), () => f.Owner($"DROP TYPE {f.QS}.replaceable;CREATE TYPE {f.QS}.replaceable AS ENUM('a')"))); }
        catch (InvalidOperationException) { replaced = true; }
        var replacement = await catalog.InspectAsync(f.Schema,"replaceable");
        check(replaced && replacement.Type.Oid != old.Type.Oid && replacement.Labels.Count == 1, "Same-name replacement between validation and DDL is detected and our change rolled back");
        string boundary = new string('é',31) + "a";
        await service.ApplyAsync(new EnumValueEdit(await Inspect(), EnumValueOperation.Append, boundary));
        check((await Inspect()).Labels.Last().Value == boundary, "Enum accepts the exact 63-byte UTF-8 label boundary");
    }
}
