namespace PGMS.PostgreSql;

public enum BackupFormat { Custom,Tar,PlainSql,Directory }
public enum BackupDataScope { All,SchemaOnly,DataOnly }
public enum BackupCompression { Default,None,Gzip }
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
        if(!Enum.IsDefined(Compression) || (CompressionLevel is not null && (Compression!=BackupCompression.Gzip || CompressionLevel is <1 or >9)))throw new ArgumentException("Select gzip level 1 to 9, or leave the level unset for the tool default.");
        if(Format is BackupFormat.Tar or BackupFormat.PlainSql && Compression!=BackupCompression.Default)throw new ArgumentException("Compression choices require custom or directory archives. Compressed SQL input is not supported.");
        var args=new List<string>{"--format="+(Format switch{BackupFormat.Custom=>"custom",BackupFormat.Tar=>"tar",BackupFormat.Directory=>"directory",_=>"plain"})};
        if(Format==BackupFormat.Directory)args.Add("--jobs="+Jobs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if(Selection is not null)args.Add("--strict-names");
        if(LargeObjects!=BackupLargeObjects.Default)args.Add(LargeObjects==BackupLargeObjects.Include?"--large-objects":"--no-large-objects");
        if(Compression!=BackupCompression.Default)args.Add("--compress="+(Compression==BackupCompression.None?"none":"gzip"+(CompressionLevel is int level?":"+level.ToString(System.Globalization.CultureInfo.InvariantCulture):"")));
        if(Scope!=BackupDataScope.All)args.Add(Scope==BackupDataScope.SchemaOnly?"--schema-only":"--data-only");
        if(Format==BackupFormat.PlainSql){if(!SqlOwnership)args.Add("--no-owner");if(!SqlPrivileges)args.Add("--no-privileges");}
        return args.ToArray();
    }
}
