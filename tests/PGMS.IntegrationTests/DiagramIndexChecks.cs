using System.Text.Json;
using Npgsql;
using PGMS.PostgreSql;

/// <summary>Indexes in database diagrams (PGMS-044, M1061): what the catalog says about each index, foreign keys without an index, and the indexes
/// written into the saved diagram (format 2). Runs inside the throwaway database of <see cref="DiagramChecks"/>, after Install Diagram Support.</summary>
internal static class DiagramIndexChecks
{
    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql) { await using var command = new NpgsqlCommand(sql, admin); return await command.ExecuteScalarAsync(); }
        var diagrams = new DiagramService(connection);

        await Sql("""
            CREATE EXTENSION IF NOT EXISTS pg_trgm;
            CREATE EXTENSION IF NOT EXISTS btree_gist;
            CREATE SCHEMA ix;
            CREATE TABLE ix.authors (id integer PRIMARY KEY, email text NOT NULL CONSTRAINT authors_email_key UNIQUE, name text NOT NULL, born date, tags text[], data jsonb, place point, active boolean NOT NULL DEFAULT true);
            CREATE INDEX authors_name_trgm ON ix.authors USING gin (name gin_trgm_ops);
            CREATE INDEX authors_data_path ON ix.authors USING gin (data jsonb_path_ops);
            CREATE INDEX authors_tags ON ix.authors USING gin (tags);
            CREATE INDEX authors_place ON ix.authors USING gist (place);
            CREATE INDEX authors_place_spg ON ix.authors USING spgist (place);
            CREATE INDEX authors_born_brin ON ix.authors USING brin (born);
            CREATE INDEX authors_email_hash ON ix.authors USING hash (email);
            CREATE INDEX authors_lower_email ON ix.authors (lower(email));
            CREATE INDEX authors_active_name ON ix.authors (name DESC) INCLUDE (born) WHERE active;
            CREATE INDEX authors_email_pattern ON ix.authors (email text_pattern_ops);
            CREATE TABLE ix.books (id integer PRIMARY KEY, author_id integer NOT NULL CONSTRAINT books_author_fk REFERENCES ix.authors(id), editor_id integer CONSTRAINT books_editor_fk REFERENCES ix.authors(id),
                room integer, during tsrange, CONSTRAINT books_no_overlap EXCLUDE USING gist (room WITH =, during WITH &&));
            CREATE INDEX books_editor_partial ON ix.books (editor_id) WHERE editor_id IS NOT NULL;
            CREATE TABLE ix.reviews (book_id integer NOT NULL CONSTRAINT reviews_book_fk REFERENCES ix.books(id), stars integer);
            CREATE INDEX reviews_book ON ix.reviews (book_id);
            CREATE TABLE ix.events (at date NOT NULL, book_id integer CONSTRAINT events_book_fk REFERENCES ix.books(id)) PARTITION BY RANGE (at);
            CREATE INDEX events_at ON ix.events (at);
            CREATE TABLE ix.events_2026 PARTITION OF ix.events FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
            CREATE TABLE ix.bare (v integer);
            CREATE TABLE ix.dups (v integer);
            INSERT INTO ix.dups VALUES (1), (1);
            """);
        // A failed concurrent build leaves an invalid index behind, the case Find Unindexed Foreign Keys warns about.
        try { await Sql("CREATE UNIQUE INDEX CONCURRENTLY dups_v ON ix.dups (v)"); } catch (PostgresException) { }

        var placements = new[] { "authors", "books", "reviews", "events", "bare", "dups" }.Select((n, i) => new DiagramTablePlacement("ix", n, 20 + 200 * i, 20)).ToArray();
        var (designs, missing, indexes) = await diagrams.ReadTablesAndIndexesAsync(placements, []);
        DiagramTableIndexes Of(string table) => indexes[designs.Single(d => d.Name == table).Oid];
        DiagramIndex Index(string table, string name) => Of(table).Indexes.Single(i => i.Name == name);
        check(missing.Count == 0 && designs.Count == 6 && designs.All(d => indexes.ContainsKey(d.Oid)), "The diagram reads the indexes of its tables in the same snapshot as the tables");

        var authors = Of("authors").Indexes;
        check(authors.Select(i => i.Name).SequenceEqual(["authors_pkey", "authors_email_key", "authors_active_name", "authors_born_brin", "authors_data_path", "authors_email_hash", "authors_email_pattern", "authors_lower_email", "authors_name_trgm", "authors_place", "authors_place_spg", "authors_tags"]),
            "Indexes come in a stable order: primary key, unique, then the others by name");
        check(authors[0] is { Primary: true, Unique: true, Method: "btree", MethodLabel: "" } pk && pk.Keys.Single().Column == "id" && authors[1] is { Primary: false, Unique: true } && authors[1].Kind == "Unique", "The primary key and a unique key are told apart");
        var trgm = Index("authors", "authors_name_trgm");
        check(trgm is { Method: "gin", MethodLabel: "GIN" } && trgm.Keys.Single() is { Column: "name", OperatorClass: "gin_trgm_ops" } && trgm.KeysText == "(name gin_trgm_ops)", "A trigram index shows GIN and its operator class gin_trgm_ops");
        check(Index("authors", "authors_data_path").Keys.Single().OperatorClass == "jsonb_path_ops" && Index("authors", "authors_tags").Keys.Single().OperatorClass is null, "jsonb_path_ops is shown; the default operator class of an array is not");
        check(Index("authors", "authors_email_pattern").Keys.Single().OperatorClass == "text_pattern_ops" && Index("authors", "authors_lower_email").Keys.Single().OperatorClass is null, "A b-tree with text_pattern_ops shows it; a plain b-tree does not");
        check(new[] { ("authors_place", "GiST"), ("authors_place_spg", "SP-GiST"), ("authors_born_brin", "BRIN"), ("authors_email_hash", "Hash"), ("authors_tags", "GIN") }.All(p => Index("authors", p.Item1).MethodLabel == p.Item2), "Each method has its short label: GiST, SP-GiST, BRIN, Hash, GIN");
        check(Index("authors", "authors_lower_email").Keys.Single() is { Column: null, Expression: "lower(email)" }, "An expression key has the expression and no column");
        var partial = Index("authors", "authors_active_name");
        check(partial.Predicate == "active" && partial.Include.SequenceEqual(["born"]) && partial.Keys.Single() is { Column: "name", Descending: true } && partial.KeysText == "(name DESC) INCLUDE (born)" && partial.Flags.SequenceEqual(["partial"]),
            "A partial index keeps its predicate, INCLUDE columns and DESC");
        check(trgm.Definition.StartsWith("CREATE INDEX authors_name_trgm ON ix.authors USING gin (name gin_trgm_ops)", StringComparison.Ordinal), "Each index keeps its full definition from pg_get_indexdef");

        var exclusion = Index("books", "books_no_overlap");
        check(exclusion is { Exclusion: true, Method: "gist", Unique: false } && exclusion.Keys.Select(k => k.Column).SequenceEqual(["room", "during"]) && exclusion.Flags.SequenceEqual(["EXCLUDE"]), "An exclusion constraint is shown as EXCLUDE with its keys");
        check(Index("dups", "dups_v") is { Valid: false } invalid && invalid.Flags.SequenceEqual(["UNIQUE", "invalid"]), "An index left invalid by a failed concurrent build is marked invalid");
        check(Of("bare").Indexes.Count == 0 && Of("bare").UnindexedForeignKeys.Count == 0, "A table without indexes has an empty list");
        check(Of("events").Indexes.Single().Name == "events_at", "A partitioned table shows the indexes defined on it");

        var books = Of("books").UnindexedForeignKeys;
        check(books.Select(k => k.Name).SequenceEqual(["books_author_fk", "books_editor_fk"]) && books[0].Columns.SequenceEqual(["author_id"]) && books[0].NearIndexes.Count == 0
            && books[1].NearIndexes.SequenceEqual(["books_editor_partial is partial"]), "Foreign keys without an index are found, with the index that almost serves one");
        check(Of("reviews").UnindexedForeignKeys.Count == 0 && Of("events").UnindexedForeignKeys.Single().Name == "events_book_fk" && Of("authors").UnindexedForeignKeys.Count == 0, "A key served by an index has no warning; a partitioned table is checked too");
        var (scanKeys, scanNames) = await ForeignKeyIndexScan.ReadAsync(connection);
        var scanned = ForeignKeyIndexScan.FindUnindexed(scanKeys.Where(k => k.Schema == "ix").ToArray(), scanNames).Select(k => k.Key.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var fromDiagram = indexes.Values.SelectMany(t => t.UnindexedForeignKeys).Select(k => k.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        check(scanned.SequenceEqual(fromDiagram), $"The diagram warns about the same keys as Find Unindexed Foreign Keys ({string.Join(", ", fromDiagram)})");

        // Saved with the diagram, for tools that read pgms.diagrams.
        var layout = new DiagramLayout { Tables = placements.Take(2).Select(p => p with { Oid = designs.Single(d => d.Name == p.Name).Oid }).ToArray(), ShowIndexes = false };
        layout = layout with { Tables = [layout.Tables[0] with { IndexesExpanded = true }, layout.Tables[1]] };
        var saved = await diagrams.SaveAsync(null, "Indexes", layout, []);
        string json = (string)(await Scalar($"SELECT definition::text FROM pgms.diagrams WHERE diagram_id={saved.Id}"))!;
        using (var document = JsonDocument.Parse(json))
        {
            var root = document.RootElement;
            var first = root.GetProperty("tables")[0];
            check(root.GetProperty("format").GetInt32() == DiagramLayout.CurrentFormat && root.GetProperty("catalogRead").ValueKind == JsonValueKind.String && !root.GetProperty("showIndexes").GetBoolean() && first.GetProperty("indexesExpanded").GetBoolean(),
                "The saved diagram is in the current format, with when the indexes were read, Show Indexes and the open sections");
            var trgmJson = first.GetProperty("indexes").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "authors_name_trgm");
            check(trgmJson.GetProperty("method").GetString() == "gin" && trgmJson.GetProperty("keys")[0].GetProperty("operatorClass").GetString() == "gin_trgm_ops" && trgmJson.GetProperty("definition").GetString()!.Contains("gin_trgm_ops"),
                "The saved diagram has each index with method, keys, operator class and definition");
            check(root.GetProperty("tables")[1].GetProperty("unindexedForeignKeys").EnumerateArray().Select(k => k.GetProperty("name").GetString()).SequenceEqual(["books_author_fk", "books_editor_fk"]), "The saved diagram names the foreign keys without an index");
            var saved0 = first.GetProperty("indexes")[0];
            check(!saved0.TryGetProperty("methodLabel", out _) && !saved0.TryGetProperty("kind", out _) && !saved0.TryGetProperty("flags", out _) && !saved0.GetProperty("keys")[0].TryGetProperty("text", out _), "Only catalog facts are saved, not the labels of the screen");
        }
        var reread = await diagrams.ReadAsync(saved.Id);
        check(reread.Layout is { Format: DiagramLayout.CurrentFormat, ShowIndexes: false } && reread.Layout.Tables[0].IndexesExpanded && reread.Layout.Tables[0].Indexes!.Count == authors.Count && reread.Layout.Tables[0].Indexes![0].Keys[0].Column == "id", "Reading the diagram returns the saved indexes");

        // The same catalog gives the same JSON; a schema change saved with the diagram is in the JSON of that save.
        var again = await diagrams.SaveAsync(saved, "Indexes", reread.Layout, []);
        string IndexesJson(string definition) { using var d = JsonDocument.Parse(definition); return string.Join("|", d.RootElement.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("indexes").GetRawText() + t.GetProperty("unindexedForeignKeys").GetRawText())); }
        string json2 = (string)(await Scalar($"SELECT definition::text FROM pgms.diagrams WHERE diagram_id={saved.Id}"))!;
        check(IndexesJson(json2) == IndexesJson(json), "Saving again with no change writes the same indexes, in the same order");
        var book = designs.Single(d => d.Name == "books");
        var keyed = book with { Indexes = [.. book.Indexes, new DesignIndex(null, "books_room_key", DesignIndexKind.UniqueKey, [new("room")]) { Unique = true, ConstraintBacked = true }] };
        await diagrams.SaveAsync(again, "Indexes", reread.Layout, DiagramSchemaChanges.Build([(book, keyed)], []).Edits);
        string json3 = (string)(await Scalar($"SELECT definition::text FROM pgms.diagrams WHERE diagram_id={saved.Id}"))!;
        check(json3.Contains("\"books_room_key\"") && !json2.Contains("\"books_room_key\""), "A key created by the save is in the indexes written by that save");

        // A diagram saved before PGMS-044 opens and gets the indexes on its next save.
        int oldId = (int)(await Scalar("""INSERT INTO pgms.diagrams (name, definition) VALUES ('Old', '{"format":1,"tables":[{"schema":"ix","name":"reviews","x":1,"y":2}],"zoom":1}') RETURNING diagram_id"""))!;
        var old = await diagrams.ReadAsync(oldId);
        check(old.Layout is { Format: 1, ShowIndexes: true, CatalogRead: null } && old.Layout.Tables.Single() is { IndexesExpanded: false, Indexes: null, UnindexedForeignKeys: null }, "A format 1 diagram opens with Show Indexes on, sections closed and no saved indexes");
        await diagrams.SaveAsync(old.Info, "Old", old.Layout, []);
        var upgraded = await diagrams.ReadAsync(oldId);
        check(upgraded.Layout.Format == DiagramLayout.CurrentFormat && upgraded.Layout.Tables.Single().Indexes!.Single().Name == "reviews_book" && upgraded.Layout.Tables.Single().Oid != 0, "The next save writes it in the current format with the indexes and the OID of the table");
        check(DiagramLayout.FromJson("""{"format":99,"tables":[],"zoom":1,"later":{"a":1}}""").Format == 99, "A diagram from a newer format still opens");
        await diagrams.DeleteAsync(oldId); await diagrams.DeleteAsync(saved.Id);
        await Sql("DROP SCHEMA ix CASCADE");
    }
}
