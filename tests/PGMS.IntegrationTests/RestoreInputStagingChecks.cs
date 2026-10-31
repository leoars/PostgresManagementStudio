using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using PGMS.PostgreSql;

internal static class RestoreInputStagingChecks
{
    internal static async Task ChildAsync(string directory,string source,string ready)
    {
        string allowed=Path.GetFullPath("artifacts/restore-staging")+Path.DirectorySeparatorChar;
        directory=Path.GetFullPath(directory);source=Path.GetFullPath(source);ready=Path.GetFullPath(ready);
        if(!directory.StartsWith(allowed,StringComparison.OrdinalIgnoreCase) || !source.StartsWith(directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || !ready.StartsWith(directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Staging child requires its synthetic workspace.");
        await using var staging=await RestoreInputStaging.CopyAsync(source,directory);await File.WriteAllTextAsync(ready,staging.FilePath);await Task.Delay(Timeout.Infinite);
    }
    internal static async Task RunAsync(Action<bool,string> check)
    {
        string root=Path.GetFullPath(Path.Combine("artifacts/restore-staging",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);string source=Path.Combine(root,"origem_密碼.sql"),ready=Path.Combine(root,"child-ready");
        byte[] original=System.Text.Encoding.UTF8.GetBytes(new string('x',300000)+"ação 密碼 🔑");await File.WriteAllBytesAsync(source,original);
        var active=await RestoreInputStaging.CopyAsync(source,root);
        check(Enumerable.SequenceEqual(original,await File.ReadAllBytesAsync(active.FilePath)),"Restore staging copies exact bytes without decoding the source");
        check(Path.GetFileName(active.FilePath).All(c=>c<128),"Restore staging basename is ASCII for native tools");
        if(OperatingSystem.IsWindows())
        {
            using var identity=WindowsIdentity.GetCurrent();var security=new FileInfo(active.FilePath).GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner);var rules=security.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            check(identity.User is not null && security.AreAccessRulesProtected && security.GetOwner(typeof(SecurityIdentifier))?.Equals(identity.User)==true,"Restore staging is created with the current Windows owner and protected DACL");
            check(rules.Length==1 && rules[0].IdentityReference.Equals(identity.User) && !rules[0].IsInherited && rules[0].AccessControlType==AccessControlType.Allow && (rules[0].FileSystemRights&FileSystemRights.FullControl)==FileSystemRights.FullControl,"Restore staging grants file access only to the current Windows identity, without inherited readers");
        }
        check(RestoreInputStaging.CleanAbandoned(root)==0 && File.Exists(active.FilePath),"Cleanup preserves the current process's active restore input");
        var parts=Path.GetFileName(active.FilePath).Split('-');parts[5]=(long.Parse(parts[5],CultureInfo.InvariantCulture)+1).ToString(CultureInfo.InvariantCulture);string reused=Path.Combine(root,string.Join('-',parts));await File.WriteAllTextAsync(reused,"synthetic stale process identity");
        check(RestoreInputStaging.CleanAbandoned(root,protectedSource:reused)==0 && File.Exists(reused),"Cleanup never removes the explicitly selected restore source even if it resembles an abandoned staging name");
        check(RestoreInputStaging.CleanAbandoned(root)==1 && !File.Exists(reused) && File.Exists(active.FilePath),"PID reuse simulation removes only mismatched start identity, preserving the active input");
        string malformed=Path.Combine(root,"PGMS-restore-v1-unknown.partial");await File.WriteAllTextAsync(malformed,"foreign");
        parts=Path.GetFileName(active.FilePath).Split('-');parts[3]=parts[3]==new string('0',16)?new string('F',16):new string('0',16);string otherHost=Path.Combine(root,string.Join('-',parts));await File.WriteAllTextAsync(otherHost,"other host");
        check(RestoreInputStaging.CleanAbandoned(root)==0 && File.Exists(malformed) && File.Exists(otherHost),"Cleanup preserves malformed and other-host filenames rather than treating them as owned staging");
        await active.DisposeAsync();check(!File.Exists(active.FilePath) && File.Exists(source),"Normal restore staging disposal removes its copy and preserves the original");
        using(var canceled=new CancellationTokenSource()){canceled.Cancel();int before=Directory.EnumerateFiles(root).Count();bool stopped=false;try{await RestoreInputStaging.CopyAsync(source,root,canceled.Token);}catch(OperationCanceledException){stopped=true;}check(stopped && Directory.EnumerateFiles(root).Count()==before,"Pre-canceled staging creates no file");}
        int existing=Directory.EnumerateFiles(root).Count();bool missing=false;try{await RestoreInputStaging.CopyAsync(Path.Combine(root,"missing"),root);}catch(FileNotFoundException){missing=true;}
        check(missing && Directory.EnumerateFiles(root).Count()==existing,"Missing staging source cannot leave a partial output");
        var blocked=await RestoreInputStaging.CopyAsync(source,root);
        if(OperatingSystem.IsWindows())
        {
            using(var reader=new FileStream(blocked.FilePath,FileMode.Open,FileAccess.Read,FileShare.None))
            {await blocked.DisposeAsync();check(File.Exists(blocked.FilePath) && RestoreInputStaging.CleanAbandoned(root)==0,"Temporarily locked completed staging is retained without touching other files");}
            check(RestoreInputStaging.CleanAbandoned(root)==1 && !File.Exists(blocked.FilePath),"Next cleanup reclaims a completed current-process copy once its file lock is released");
        }
        else await blocked.DisposeAsync();
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach(string argument in new[]{"--restore-stage-child",root,source,ready})start.ArgumentList.Add(argument);
        using var child=new Process{StartInfo=start};child.Start();Task<string> errors=child.StandardError.ReadToEndAsync(),output=child.StandardOutput.ReadToEndAsync();
        try
        {
            DateTime deadline=DateTime.UtcNow.AddSeconds(12);while(!File.Exists(ready) && !child.HasExited && DateTime.UtcNow<deadline)await Task.Delay(25);
            if(!File.Exists(ready))throw new Exception("Staging child failed to reach checkpoint: "+(child.HasExited?await errors:"timeout"));
            string path=await File.ReadAllTextAsync(ready);if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Child staging escaped its workspace.");
            check(File.Exists(path) && RestoreInputStaging.CleanAbandoned(root)==0,"Cleanup preserves a real other process's live staged restore input");
            child.Kill(entireProcessTree:true);await child.WaitForExitAsync();await Task.WhenAll(errors,output);
            check(RestoreInputStaging.CleanAbandoned(root)==1 && !File.Exists(path),"Next operation reclaims staging left by an actually interrupted process");
            check(Enumerable.SequenceEqual(original,await File.ReadAllBytesAsync(source)) && File.Exists(malformed) && File.Exists(otherHost),"Interrupted-process cleanup preserves original bytes and unrelated files");
        }
        finally{if(!child.HasExited){child.Kill(entireProcessTree:true);await child.WaitForExitAsync();}await Task.WhenAll(errors,output);}
    }
}
