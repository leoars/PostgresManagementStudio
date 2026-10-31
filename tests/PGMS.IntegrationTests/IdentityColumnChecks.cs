using Npgsql;
using PGMS.PostgreSql;

internal static class IdentityColumnChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        var plain=new TableColumn("id",ColumnType.Integer);
        check(plain.ToSql()=="\"id\" integer NULL","Identity None preserves exact legacy nullable-column SQL");
        check((plain with{Nullable=false}).ToSql()=="\"id\" integer NOT NULL","Identity None preserves exact legacy NOT NULL SQL");
        foreach(var type in Enum.GetValues<ColumnType>().Where(t=>t is not(ColumnType.Smallint or ColumnType.Integer or ColumnType.Bigint)))
        {
            bool refused=false;try{(plain with{Type=type,Nullable=false,Identity=ColumnIdentity.Always}).ToSql();}catch(ArgumentException){refused=true;}
            check(refused,"Non-sequence datatype cannot be an identity: "+type);
        }
        bool nullable=false;try{(plain with{Identity=ColumnIdentity.ByDefault}).ToSql();}catch(ArgumentException){nullable=true;}
        check(nullable,"Identity refuses a nullable model rather than silently changing its SQL");
        bool unknown=false;try{(plain with{Nullable=false,Identity=(ColumnIdentity)900}).ToSql();}catch(ArgumentException){unknown=true;}
        check(unknown,"Unknown identity generation mode is refused before connection");
        string suffix=Guid.NewGuid().ToString("N"),schema="identity_table_é\"_"+suffix,owner="identity_column_owner_"+suffix;
        string qs=SqlIdentifier.Quote(schema),qo=SqlIdentifier.Quote(owner);
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        async Task Sql(string sql){await using var cmd=new NpgsqlCommand(sql,admin);await cmd.ExecuteNonQueryAsync();}
        string limited=new NpgsqlConnectionStringBuilder(connection){Username=owner,Pooling=false}.ConnectionString;
        try
        {
            await Sql($"CREATE ROLE {qo} LOGIN;CREATE SCHEMA {qs} AUTHORIZATION {qo}");
            var service=new AdministrativeEditService(limited);
            await using var client=new NpgsqlConnection(limited);await client.OpenAsync();
            async Task<object?> Scalar(string sql){await using var cmd=new NpgsqlCommand(sql,client);return await cmd.ExecuteScalarAsync();}
            check((bool)(await Scalar("SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls FROM pg_catalog.pg_roles WHERE rolname=current_user"))!,"Identity DDL executes as an ordinary schema owner without cluster flags");
            foreach(var type in new[]{ColumnType.Smallint,ColumnType.Integer,ColumnType.Bigint})foreach(var mode in new[]{ColumnIdentity.ByDefault,ColumnIdentity.Always})
            {
                string name=type.ToString().ToLowerInvariant()+"_"+mode.ToString().ToLowerInvariant(),table=ObjectSql.Relation(schema,name),label=type+"/"+mode;
                var column=new TableColumn("id",type,false){Identity=mode};
                await service.ApplyAsync(new CreateTableEdit(schema,name,[column,new("note",ColumnType.Text)]));
                check(Convert.ToInt64(await Scalar($"INSERT INTO {table}(note) VALUES('generated') RETURNING id"))==1,"Typed identity generates its first value: "+label);
                bool accepted=true;try{await Scalar($"INSERT INTO {table}(id,note) VALUES(88,'explicit') RETURNING id");}catch(PostgresException ex)when(ex.SqlState=="428C9"){accepted=false;}
                check(accepted==(mode==ColumnIdentity.ByDefault),"ALWAYS/BY DEFAULT controls actual explicit insertion: "+label);
                if(mode==ColumnIdentity.Always)check(Convert.ToInt64(await Scalar($"INSERT INTO {table}(id,note) OVERRIDING SYSTEM VALUE VALUES(88,'override') RETURNING id"))==88,"Explicit SQL override remains a PostgreSQL decision: "+label);
                check(Convert.ToInt64(await Scalar($"INSERT INTO {table}(note) VALUES('next') RETURNING id"))==2,"Explicit or overriding values do not advance the implicit sequence: "+label);
                await using(var metadata=new NpgsqlCommand("SELECT a.attidentity::text,a.attnotnull,(SELECT count(*) FROM pg_catalog.pg_constraint k WHERE k.conrelid=a.attrelid AND k.contype='p') FROM pg_catalog.pg_attribute a WHERE a.attrelid=@table::regclass AND a.attname='id'",client))
                {
                    metadata.Parameters.AddWithValue("table",table);await using var row=await metadata.ExecuteReaderAsync();await row.ReadAsync();
                    check(row.GetString(0)==(mode==ColumnIdentity.Always?"a":"d") && row.GetBoolean(1) && row.GetInt64(2)==0,"Identity metadata is NOT NULL without an invented primary key: "+label);
                }
            }
            string added=ObjectSql.Relation(schema,"added");await service.ApplyAsync(new CreateTableEdit(schema,"added",[new("note",ColumnType.Text)]));
            await Scalar($"INSERT INTO {added}(note) VALUES('first'),('second')");
            var inspected=await new TablePropertiesService(limited).InspectAsync(schema,"added");
            await service.ApplyAsync(new TableEdit(schema,"added",TableOperation.AddColumn,Column:"id",Type:ColumnType.Bigint,Nullable:false){Identity=ColumnIdentity.ByDefault,ExpectedTableOid=inspected.Oid});
            check(Convert.ToInt64(await Scalar($"SELECT count(DISTINCT id) FROM {added} WHERE id IS NOT NULL"))==2,"Adding identity fills existing rows using the implicit sequence");
            check(Convert.ToInt64(await Scalar($"INSERT INTO {added}(note) VALUES('third') RETURNING id"))==3,"Added identity continues after values assigned to existing rows");
        }
        finally{await Sql($"DROP SCHEMA IF EXISTS {qs} CASCADE;DROP ROLE IF EXISTS {qo}");}
    }
}
