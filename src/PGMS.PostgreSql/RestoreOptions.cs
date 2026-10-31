namespace PGMS.PostgreSql;

public sealed record RestoreOptions(bool RestoreOwnership=false,bool RestorePrivileges=false)
{
    public BackupDataScope Scope { get; init; } = BackupDataScope.All;
    public string[] ToArguments()
    {
        if(!Enum.IsDefined(Scope))throw new ArgumentException("Unknown restore content scope.");
        var args=new List<string>{"--single-transaction","--exit-on-error"};
        if(!RestoreOwnership)args.Add("--no-owner");
        if(!RestorePrivileges)args.Add("--no-privileges");
        if(Scope==BackupDataScope.SchemaOnly)args.Add("--schema-only");
        if(Scope==BackupDataScope.DataOnly)args.Add("--data-only");
        return args.ToArray();
    }
}
