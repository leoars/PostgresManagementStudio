namespace PGMS.PostgreSql;

public sealed record RestoreOptions(bool RestoreOwnership=false,bool RestorePrivileges=false)
{
    public BackupDataScope Scope { get; init; } = BackupDataScope.All;
    /// <summary>pg_restore --jobs (PGMS-064, M1100). Above 1 the restore cannot run in one transaction: pg_restore refuses --single-transaction with --jobs.</summary>
    public int Jobs { get; init; } = 1;
    /// <summary>Lines of the archive's table of contents (pg_restore --list) to restore; null restores everything. Written to a --use-list file.</summary>
    public IReadOnlyList<string>? Items { get; init; }
    public string[] ToArguments()
    {
        if(!Enum.IsDefined(Scope))throw new ArgumentException("Unknown restore content scope.");
        if(Jobs<1 || Jobs>32)throw new ArgumentException("Select 1 to 32 restore jobs.");
        if(Items is { Count: 0 })throw new ArgumentException("Choose at least one item of the archive to restore.");
        var args=new List<string>();
        if(Jobs==1)args.Add("--single-transaction");
        else args.Add("--jobs="+Jobs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        args.Add("--exit-on-error");
        if(!RestoreOwnership)args.Add("--no-owner");
        if(!RestorePrivileges)args.Add("--no-privileges");
        if(Scope==BackupDataScope.SchemaOnly)args.Add("--schema-only");
        if(Scope==BackupDataScope.DataOnly)args.Add("--data-only");
        return args.ToArray();
    }
}

/// <summary>One entry of an archive's table of contents as pg_restore --list prints it: "id; tableoid oid TYPE schema name owner".
/// <see cref="Description"/> is what follows the two OIDs (for example "TABLE DATA public t1 postgres"); <see cref="Line"/> goes to the --use-list file.</summary>
public sealed record ArchiveItem(int Id, string Description, string Line)
{
    /// <summary>Parses the --list output; comment lines (starting with ;) and blank lines are skipped.</summary>
    public static IReadOnlyList<ArchiveItem> Parse(string list)
    {
        var items=new List<ArchiveItem>();
        foreach(string raw in list.Split('\n'))
        {
            string line=raw.TrimEnd('\r');
            if(line.Length==0 || line[0]==';')continue;
            int semicolon=line.IndexOf(';');
            if(semicolon<=0 || !int.TryParse(line.AsSpan(0,semicolon),System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out int id))continue;
            string[] words=line[(semicolon+1)..].Trim().Split(' ',3,StringSplitOptions.RemoveEmptyEntries);
            items.Add(new(id,words.Length==3?words[2]:line[(semicolon+1)..].Trim(),line));
        }
        return items;
    }
}
