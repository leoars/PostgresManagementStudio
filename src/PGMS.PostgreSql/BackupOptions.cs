namespace PGMS.PostgreSql;

public enum BackupFormat { Custom,Tar,PlainSql,Directory }
public enum BackupDataScope { All,SchemaOnly,DataOnly }
/// <summary>pg_dump --compress methods; lz4 and zstd need pg_dump 16 or later built with them (PGMS-064, M1100).</summary>
public enum BackupCompression { Default,None,Gzip,Lz4,Zstd }
public enum BackupLargeObjects { Default,Include,Exclude }
public sealed record BackupOptions(BackupFormat Format=BackupFormat.Custom,BackupDataScope Scope=BackupDataScope.All,bool SqlOwnership=false,bool SqlPrivileges=false)
{
    public int Jobs { get; init; } = 1;
    public BackupCompression Compression { get; init; } = BackupCompression.Default;
    public int? CompressionLevel { get; init; }
    public BackupSelection? Selection { get; init; }
    public BackupLargeObjects LargeObjects { get; init; }
    public string[] ToArguments()
    {
        if(!Enum.IsDefined(Format) || !Enum.IsDefined(Scope))throw new ArgumentException("Select a supported backup format and data scope.");
        if(!Enum.IsDefined(LargeObjects) || (Scope==BackupDataScope.SchemaOnly && LargeObjects!=BackupLargeObjects.Default))throw new ArgumentException("Large objects are data. Choose their inclusion only when dumping data.");
        if(Format!=BackupFormat.PlainSql && (SqlOwnership || SqlPrivileges))throw new ArgumentException("SQL ownership and privilege choices apply only to plain SQL. Choose archive ownership and privileges when restoring.");
        if(Jobs<1 || Jobs>32 || (Jobs!=1 && Format!=BackupFormat.Directory))throw new ArgumentException("Select 1 to 32 jobs. Parallel backup requires directory format.");
        if(!Enum.IsDefined(Compression) || (CompressionLevel is int chosen && (MaximumLevel(Compression) is not int maximum || chosen<1 || chosen>maximum)))throw new ArgumentException("Select gzip level 1 to 9, lz4 level 1 to 12 or zstd level 1 to 22, or leave the level unset for the tool default.");
        if(Format is BackupFormat.Tar or BackupFormat.PlainSql && Compression!=BackupCompression.Default)throw new ArgumentException("Compression choices require custom or directory archives. Compressed SQL input is not supported.");
        var args=new List<string>{"--format="+(Format switch{BackupFormat.Custom=>"custom",BackupFormat.Tar=>"tar",BackupFormat.Directory=>"directory",_=>"plain"})};
        if(Format==BackupFormat.Directory)args.Add("--jobs="+Jobs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if(Selection is not null)args.Add("--strict-names");
        if(LargeObjects!=BackupLargeObjects.Default)args.Add(LargeObjects==BackupLargeObjects.Include?"--large-objects":"--no-large-objects");
        if(Compression!=BackupCompression.Default)args.Add("--compress="+(Compression==BackupCompression.None?"none":MethodName(Compression)+(CompressionLevel is int level?":"+level.ToString(System.Globalization.CultureInfo.InvariantCulture):"")));
        if(Scope!=BackupDataScope.All)args.Add(Scope==BackupDataScope.SchemaOnly?"--schema-only":"--data-only");
        if(Format==BackupFormat.PlainSql){if(!SqlOwnership)args.Add("--no-owner");if(!SqlPrivileges)args.Add("--no-privileges");}
        return args.ToArray();
    }

    /// <summary>The highest level pg_dump accepts for a method with levels; null for Default and None.</summary>
    public static int? MaximumLevel(BackupCompression method)=>method switch{BackupCompression.Gzip=>9,BackupCompression.Lz4=>12,BackupCompression.Zstd=>22,_=>null};
    /// <summary>The level pg_dump uses when none is given (gzip 6, lz4 1, zstd 3 in the PostgreSQL 16 documentation and source).</summary>
    public static int? DefaultLevel(BackupCompression method)=>method switch{BackupCompression.Gzip=>6,BackupCompression.Lz4=>1,BackupCompression.Zstd=>3,_=>null};
    private static string MethodName(BackupCompression method)=>method switch{BackupCompression.Lz4=>"lz4",BackupCompression.Zstd=>"zstd",_=>"gzip"};
}
