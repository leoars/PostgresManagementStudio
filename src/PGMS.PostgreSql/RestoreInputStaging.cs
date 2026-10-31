using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;

namespace PGMS.PostgreSql;

// Only this application's versioned filenames on this host are reclaimable.
// A live process identity and the current process's active set protect readers.
internal sealed class RestoreInputStaging:IAsyncDisposable
{
    private static readonly string Host=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)))[..16];
    private static readonly long Started=GetStarted();
    private static readonly ConcurrentDictionary<string,byte> Active=new(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
    private const string Prefix="PGMS-restore-v1-";
    internal string FilePath{get;}
    private RestoreInputStaging(string path)=>FilePath=path;
    private static long GetStarted(){using var process=Process.GetCurrentProcess();return process.StartTime.ToUniversalTime().Ticks;}
    private static void GuardDirectory(string directory)
    {
        if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("Restore staging directory must not be a filesystem link.");
    }
    internal static int CleanAbandoned(string directory,CancellationToken token=default,string? protectedSource=null)
    {
        directory=Path.GetFullPath(directory);if(!Directory.Exists(directory))return 0;GuardDirectory(directory);int removed=0;
        foreach(string candidate in Directory.EnumerateFiles(directory,Prefix+Host+"-*.partial",SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();string path=Path.GetFullPath(candidate);
            if(protectedSource is not null && path.Equals(Path.GetFullPath(protectedSource),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))continue;
            var parts=Path.GetFileName(path).Split('-');
            if(parts.Length!=7 || parts[0]!="PGMS" || parts[1]!="restore" || parts[2]!="v1" || parts[3]!=Host || !parts[6].EndsWith(".partial",StringComparison.Ordinal) || !Guid.TryParseExact(parts[6][..^8],"N",out _) || !int.TryParse(parts[4],NumberStyles.None,CultureInfo.InvariantCulture,out int pid) || pid<=0 || !long.TryParse(parts[5],NumberStyles.None,CultureInfo.InvariantCulture,out long started) || started<=0)continue;
            if(pid==Environment.ProcessId && started==Started)
            {if(Active.ContainsKey(path))continue;}
            else
            {
                try{using var process=Process.GetProcessById(pid);if(!process.HasExited && process.StartTime.ToUniversalTime().Ticks==started)continue;}
                catch(ArgumentException){/* Process no longer exists. */}
                catch(InvalidOperationException){/* Process exited during inspection. */}
                catch(Win32Exception){continue;/* Unknown process identity must be preserved. */}
            }
            if(TryDelete(path))removed++;
        }
        return removed;
    }
    private static bool TryDelete(string path)
    {
        try{if(!File.Exists(path) || (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)return false;File.Delete(path);return true;}
        catch(IOException){return false;}
        catch(UnauthorizedAccessException){return false;}
    }
    [SupportedOSPlatform("windows")]
    private static FileStream CreatePrivate(string path)
    {
        using var identity=WindowsIdentity.GetCurrent();var sid=identity.User??throw new UnauthorizedAccessException("Cannot determine the Windows identity for restore staging.");
        var security=new FileSecurity();security.SetOwner(sid);security.SetAccessRuleProtection(true,false);security.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew,FileSystemRights.FullControl,FileShare.None,131072,FileOptions.Asynchronous|FileOptions.SequentialScan,security);
    }
    internal static async Task<RestoreInputStaging> CopyAsync(string source,string directory,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();source=Path.GetFullPath(source);directory=Path.GetFullPath(directory);Directory.CreateDirectory(directory);GuardDirectory(directory);
        await Task.Run(()=>CleanAbandoned(directory,token,source),token).ConfigureAwait(false);
        string path=Path.Combine(directory,Prefix+Host+"-"+Environment.ProcessId.ToString(CultureInfo.InvariantCulture)+"-"+Started.ToString(CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")+".partial");
        bool created=false,completed=false;Active.TryAdd(path,0);
        try
        {
            await using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.Asynchronous|FileOptions.SequentialScan))
            await using(var output=OperatingSystem.IsWindows()?CreatePrivate(path):new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,FileOptions.Asynchronous|FileOptions.SequentialScan))
            {created=true;await input.CopyToAsync(output,token).ConfigureAwait(false);}
            completed=true;return new(path);
        }
        finally{if(!completed){Active.TryRemove(path,out _);if(created)TryDelete(path);}}
    }
    public ValueTask DisposeAsync(){Active.TryRemove(FilePath,out _);TryDelete(FilePath);return ValueTask.CompletedTask;}

    internal static async Task<RestoreInputStaging> CreateTextAsync(string text,string directory,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();directory=Path.GetFullPath(directory);Directory.CreateDirectory(directory);GuardDirectory(directory);
        byte[] bytes=new UTF8Encoding(false,true).GetBytes(text);
        string path=Path.Combine(directory,Prefix+Host+"-"+Environment.ProcessId.ToString(CultureInfo.InvariantCulture)+"-"+Started.ToString(CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")+".partial");
        bool created=false,completed=false;Active.TryAdd(path,0);
        try
        {
            await using(var output=OperatingSystem.IsWindows()?CreatePrivate(path):new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,FileOptions.Asynchronous))
            {created=true;await output.WriteAsync(bytes,token).ConfigureAwait(false);}
            completed=true;return new(path);
        }
        finally{if(!completed){Active.TryRemove(path,out _);if(created)TryDelete(path);}}
    }
}
