using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public sealed record InspectedDataType(uint DatabaseOid,uint NamespaceOid,uint Oid,uint OwnerOid,
    string Schema,string Name,string Kind,uint BaseTypeOid,string Signature)
{
    public string DisplayName=>Schema+"."+Name+" ("+(Kind=="e"?"enum":Kind=="d"?"domain":"built-in")+")";
    public string ToSql()=>SqlIdentifier.Quote(Schema)+"."+SqlIdentifier.Quote(Name);
}

public sealed record SelectedColumnType(InspectedDataType Type,int? Length=null,int? Precision=null,int? Scale=null)
{
    public string ToSql()
    {
        ArgumentNullException.ThrowIfNull(Type);
        string name=Type.ToSql();
        if(Length is null && Precision is null && Scale is null)return name;
        if(Type.Schema!="pg_catalog")throw new ArgumentException("User-defined types do not accept modifiers in this flow.");
        if(Type.Oid is 1042 or 1043 && Length is >=1 and <=10485760 && Precision is null && Scale is null)return name+"("+Length.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)+")";
        if(Type.Oid==1700 && Length is null && Precision is >=1 and <=1000 && Scale is >=0 && Scale<=Precision)
            return name+"("+Precision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)+", "+Scale.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)+")";
        if(Type.Oid is 1083 or 1114 or 1184 or 1266 && Length is null && Precision is >=0 and <=6 && Scale is null)
            return name+"("+Precision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)+")";
        throw new ArgumentException("Use a supported length, numeric precision/scale or time precision for the selected type.");
    }
}

public sealed class DataTypeCatalog(string connectionString)
{
    public async Task<IReadOnlyList<InspectedDataType>> InspectAsync(CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();await using var c=new NpgsqlConnection(connectionString);await c.OpenAsync(token).ConfigureAwait(false);
        await using var tx=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,token).ConfigureAwait(false);
        await using(var ro=new NpgsqlCommand("SET TRANSACTION READ ONLY",c,tx))await ro.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return await Read(c,tx,null,null,token).ConfigureAwait(false);
    }
    internal static async Task Verify(NpgsqlConnection c,NpgsqlTransaction tx,InspectedDataType expected,CancellationToken token)
    {
        var values=await Read(c,tx,expected.Schema,expected.Name,token).ConfigureAwait(false);
        if(values.Count!=1 || values[0]!=expected)throw new InvalidOperationException("Selected type identity, definition or USAGE authority changed. Inspect types again.");
    }
    internal static async Task<IReadOnlyList<InspectedDataType>> Read(NpgsqlConnection c,NpgsqlTransaction tx,string? schema,string? name,CancellationToken token)
    {
        await using var command=new NpgsqlCommand("""
            SELECT d.oid,n.oid,t.oid,t.typowner,n.nspname,t.typname,t.typtype::text,t.typbasetype,
              left(ROW(t.xmin::text,t.typbasetype,t.typtypmod,t.typnotnull,t.typdefaultbin::text,t.typcollation,
                (SELECT jsonb_agg(ROW(e.oid,e.enumsortorder,e.enumlabel)::text ORDER BY e.enumsortorder)
                 FROM (SELECT oid,enumsortorder,enumlabel FROM pg_catalog.pg_enum WHERE enumtypid=t.oid ORDER BY enumsortorder LIMIT 1001) e),
                (SELECT jsonb_agg(ROW(x.oid,x.conname,x.convalidated,pg_catalog.pg_get_constraintdef(x.oid))::text ORDER BY x.oid)
                 FROM (SELECT oid,conname,convalidated FROM pg_catalog.pg_constraint WHERE contypid=t.oid ORDER BY oid LIMIT 1001) x),
                base_graph.definition
              )::text,131073),
              (SELECT count(*) FROM (SELECT 1 FROM pg_catalog.pg_enum WHERE enumtypid=t.oid LIMIT 1001) e),
              (SELECT count(*) FROM (SELECT 1 FROM pg_catalog.pg_constraint WHERE contypid=t.oid LIMIT 1001) x),
              coalesce(base_graph.incomplete,false)
            FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid=t.typnamespace
            JOIN pg_catalog.pg_database d ON d.datname=pg_catalog.current_database()
            LEFT JOIN LATERAL (
              WITH RECURSIVE bases AS (
                SELECT b.*,b.xmin::text AS row_version,0 AS depth FROM pg_catalog.pg_type b WHERE t.typtype='d' AND b.oid=t.typbasetype
                UNION ALL
                SELECT next.*,next.xmin::text,b.depth+1 FROM bases b JOIN pg_catalog.pg_type next ON next.oid=b.typbasetype WHERE b.depth<15
              )
              SELECT jsonb_agg(ROW(b.oid,b.typnamespace,b.typowner,b.typname,b.row_version,b.typtype,b.typbasetype,
                       b.typtypmod,b.typnotnull,b.typdefaultbin::text,b.typcollation,e.definition,x.definition)::text ORDER BY b.depth) AS definition,
                     bool_or((b.depth=15 AND b.typbasetype<>0) OR e.members>1000 OR x.members>1000) AS incomplete
              FROM bases b
              LEFT JOIN LATERAL (
                SELECT jsonb_agg(ROW(v.oid,v.enumsortorder,v.enumlabel)::text ORDER BY v.enumsortorder) AS definition,count(*) AS members
                FROM (SELECT oid,enumsortorder,enumlabel FROM pg_catalog.pg_enum WHERE enumtypid=b.oid ORDER BY enumsortorder LIMIT 1001) v
              ) e ON true
              LEFT JOIN LATERAL (
                SELECT jsonb_agg(ROW(v.oid,v.conname,v.convalidated,pg_catalog.pg_get_constraintdef(v.oid))::text ORDER BY v.oid) AS definition,count(*) AS members
                FROM (SELECT oid,conname,convalidated FROM pg_catalog.pg_constraint WHERE contypid=b.oid ORDER BY oid LIMIT 1001) v
              ) x ON true
            ) base_graph ON true
            WHERE t.typisdefined AND pg_catalog.has_type_privilege(t.oid,'USAGE')
              AND pg_catalog.has_schema_privilege(n.oid,'USAGE')
              AND ((n.nspname='pg_catalog' AND t.oid IN(16,17,20,21,23,25,700,701,1042,1043,1082,1083,1114,1184,1186,1266,1700,2950,114,3802,3904,3906,3908,3910,3912,3926))
                OR (n.nspname NOT LIKE 'pg\_%' ESCAPE '\' AND n.nspname<>'information_schema' AND t.typtype IN('e','d')))
              AND (@schema IS NULL OR n.nspname=@schema) AND (@name IS NULL OR t.typname=@name)
            ORDER BY n.nspname,t.typname LIMIT 1001
            """,c,tx){CommandTimeout=15};
        command.Parameters.AddWithValue("schema",NpgsqlDbType.Text,(object?)schema??DBNull.Value);
        command.Parameters.AddWithValue("name",NpgsqlDbType.Text,(object?)name??DBNull.Value);
        await using var r=await command.ExecuteReaderAsync(token).ConfigureAwait(false);var result=new List<InspectedDataType>();
        while(await r.ReadAsync(token).ConfigureAwait(false))
        {
            string signature=r.GetString(8);
            if(signature.Length>131072 || r.GetInt64(9)>1000 || r.GetInt64(10)>1000 || r.GetBoolean(11))
                throw new InvalidOperationException("Type definition exceeds the bounded inspection size.");
            result.Add(new(r.GetFieldValue<uint>(0),r.GetFieldValue<uint>(1),r.GetFieldValue<uint>(2),r.GetFieldValue<uint>(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetFieldValue<uint>(7),signature));
        }
        if(result.Count>1000)throw new InvalidOperationException("More than1000 selectable types; inspection is incomplete and was refused.");
        return result.AsReadOnly();
    }
}
