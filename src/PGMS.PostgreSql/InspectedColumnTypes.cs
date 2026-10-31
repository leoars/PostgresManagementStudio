using Npgsql;
using NpgsqlTypes;

namespace PGMS.PostgreSql;

public enum ColumnTypeConversion { Cast, TrimText, EmptyTextToNull, LowerText }

public sealed class InspectedColumnTypeEdit : IAdministrativeEdit
{
    public ExpressionIndexTarget Target { get; }
    public string Column { get; }
    public SelectedColumnType DataType { get; }
    public bool Add { get; }
    public bool Nullable { get; }
    public ColumnTypeConversion Conversion { get; }
    public InspectedColumnTypeEdit(ExpressionIndexTarget target,string column,SelectedColumnType dataType,bool add=false,bool nullable=true,ColumnTypeConversion conversion=ColumnTypeConversion.Cast)
    {Target=target;Column=column;DataType=dataType;Add=add;Nullable=nullable;Conversion=conversion;}
    public string ToSql()
    {
        ArgumentNullException.ThrowIfNull(Target);ArgumentNullException.ThrowIfNull(DataType);
        string column=SqlIdentifier.Quote(Column),type=DataType.ToSql(),table=ObjectSql.Relation(Target.Schema,Target.Name);
        if(Add)
        {
            if(Target.Columns.Any(c=>c.Name==Column))throw new ArgumentException("The inspected table already has this column.");
            if(Conversion!=ColumnTypeConversion.Cast)throw new ArgumentException("Adding a column does not accept USING conversion.");
            return $"ALTER TABLE ONLY {table} ADD COLUMN {column} {type}"+(Nullable?" NULL;":" NOT NULL;");
        }
        if(!Target.Columns.Any(c=>c.Name==Column))throw new ArgumentException("Select an exact inspected column.");
        string expression=Conversion switch
        {
            ColumnTypeConversion.Cast=>column,
            ColumnTypeConversion.TrimText=>$"pg_catalog.btrim({column}::pg_catalog.text)",
            ColumnTypeConversion.EmptyTextToNull=>$"NULLIF({column}::pg_catalog.text, '')",
            ColumnTypeConversion.LowerText=>$"pg_catalog.lower({column}::pg_catalog.text)",
            _=>throw new ArgumentException("Choose a supported USING conversion.")
        };
        return $"ALTER TABLE ONLY {table} ALTER COLUMN {column} TYPE {type} USING ({expression})::{type};";
    }
    public async Task ValidateAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token=default)
    {
        await using(var command=new NpgsqlCommand("LOCK TABLE ONLY "+ObjectSql.Relation(Target.Schema,Target.Name)+" IN ACCESS EXCLUSIVE MODE",c,tx){CommandTimeout=30})await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var observed=await ExpressionIndexCatalog.Read(c,tx,Target.Schema,Target.Name,token).ConfigureAwait(false);
        if(!Target.Matches(observed))throw new InvalidOperationException("Column target identity or shape changed. Inspect again.");
        await DataTypeCatalog.Verify(c,tx,DataType.Type,token).ConfigureAwait(false);
        if(!Add)
        {
            await using var command=new NpgsqlCommand("SELECT attidentity='' AND attgenerated='' FROM pg_catalog.pg_attribute WHERE attrelid=@table AND attname=@name AND NOT attisdropped",c,tx);
            command.Parameters.AddWithValue("table",NpgsqlDbType.Oid,Target.Oid);command.Parameters.AddWithValue("name",Column);
            if(await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)throw new InvalidOperationException("Identity/generated columns require their dedicated operation; this type-change flow refuses them.");
        }
    }
    public async Task VerifyAppliedAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken token=default)
    {
        await DataTypeCatalog.Verify(c,tx,DataType.Type,token).ConfigureAwait(false);
        var observed=await ExpressionIndexCatalog.Read(c,tx,Target.Schema,Target.Name,token).ConfigureAwait(false);
        if(Target.DatabaseOid!=observed.DatabaseOid || Target.NamespaceOid!=observed.NamespaceOid || Target.Oid!=observed.Oid || Target.OwnerOid!=observed.OwnerOid ||
            !Target.Columns.Where(x=>x.Name!=Column).SequenceEqual(observed.Columns.Where(x=>x.Name!=Column)))
            throw new InvalidOperationException("Column operation changed an unreviewed target or column.");
        var written=observed.Columns.SingleOrDefault(x=>x.Name==Column);
        if(written is null || written.TypeOid!=DataType.Type.Oid || written.TypeModifier!=ExpectedModifier(DataType) ||
            (!Add && written.Position!=Target.Columns.Single(x=>x.Name==Column).Position))
            throw new InvalidOperationException("Written column type/modifier could not be verified.");
    }
    internal static int ExpectedModifier(SelectedColumnType type)
    {
        _=type.ToSql();
        if(type.Length is {} length)return length+4;
        if(type.Precision is {} precision)return type.Type.Oid==1700?((precision<<16)|type.Scale!.Value)+4:precision;
        return -1;
    }
}
