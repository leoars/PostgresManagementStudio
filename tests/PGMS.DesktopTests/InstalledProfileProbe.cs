using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation;

internal static class InstalledProfileProbe
{
    [System.Runtime.InteropServices.DllImport("user32.dll",SetLastError=true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);

    internal static int Run(string executable,string state,string report)
    {
        Process? process=null;
        using var watchdog=new Timer(_=>{try {if(process is not null && !process.HasExited)process.Kill(true);}catch(InvalidOperationException){}},null,TimeSpan.FromSeconds(90),Timeout.InfiniteTimeSpan);
        try {
            string artifacts=Path.GetFullPath("artifacts")+Path.DirectorySeparatorChar;
            string acceptance=Path.GetFullPath("artifacts/acceptance")+Path.DirectorySeparatorChar;
            string Regular(string path,string allowed) {
                string full=Path.GetFullPath(path);
                if(!full.StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new IOException("Probe path escaped workspace artifacts");
                for(string? p=full;p is not null;p=Path.GetDirectoryName(p))
                    if((File.Exists(p)||Directory.Exists(p)) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Linked probe paths refused");
                return full;
            }
            executable=Regular(executable,artifacts);state=Regular(state,acceptance);report=Regular(report,acceptance);
            if(File.Exists(report))throw new IOException("Existing probe evidence preserved");
            string marker=Regular(Path.Combine(state,".pgms-synthetic-state.json"),acceptance);
            using var metadata=JsonDocument.Parse(File.ReadAllText(marker));var m=metadata.RootElement;
            if(m.GetProperty("SchemaVersion").GetInt32()!=1 || m.GetProperty("Purpose").GetString()!="PGMS synthetic acceptance state" || m.GetProperty("RealServer").GetBoolean())throw new IOException("Synthetic state marker required");
            string profile=Regular(Path.Combine(state,"connections.json"),acceptance),sentinel=Regular(Path.Combine(state,"synthetic-sentinel.txt"),acceptance);
            string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            string profileHash=m.GetProperty("ProfileSha256").GetString()!,sentinelHash=m.GetProperty("SentinelSha256").GetString()!;
            if(Hash(profile)!=profileHash || Hash(sentinel)!=sentinelHash)throw new IOException("Synthetic state changed before probe");
            using(var doc=JsonDocument.Parse(File.ReadAllText(profile))) {
                var profiles=doc.RootElement.GetProperty("Profiles");
                if(profiles.GetArrayLength()!=1 || profiles[0].GetProperty("Host").GetString()!="127.0.0.1" || profiles[0].GetProperty("Port").GetInt32()!=1 || profiles[0].GetProperty("Id").GetString()!=m.GetProperty("ProfileId").GetString())throw new IOException("Only the known loopback synthetic profile is allowed");
            }
            string package=Path.GetDirectoryName(executable)!;
            var start=new ProcessStartInfo(executable){UseShellExecute=false,WorkingDirectory=package,WindowStyle=ProcessWindowStyle.Hidden};
            start.Environment["PGMS_STATE_DIRECTORY"]=state;
            string empty=Path.Combine(state,"empty-runtime");Directory.CreateDirectory(empty);
            start.Environment["DOTNET_ROOT"]=empty;start.Environment["DOTNET_ROOT_X64"]=empty;start.Environment["DOTNET_MULTILEVEL_LOOKUP"]="0";
            process=Process.Start(start)??throw new IOException("Child launch failed");
            AutomationElement Wait(Func<AutomationElement?> find) {
                var deadline=DateTime.UtcNow.AddSeconds(20);
                do {if(process.HasExited)throw new IOException("Child exited before UI acceptance");var found=find();if(found is not null)return found;Thread.Sleep(100);}while(DateTime.UtcNow<deadline);
                throw new TimeoutException("Own child UI element not observed");
            }
            var main=Wait(()=>{process.Refresh();return process.MainWindowHandle==IntPtr.Zero?null:AutomationElement.FromHandle(process.MainWindowHandle);});
            IntPtr mainHandle=process.MainWindowHandle;
            if(main.Current.ProcessId!=process.Id || !main.Current.Name.Contains("PGMS",StringComparison.Ordinal))throw new IOException("Unexpected main window");
            var runtime=process.Modules.Cast<ProcessModule>().Single(p=>p.ModuleName.Equals("coreclr.dll",StringComparison.OrdinalIgnoreCase));
            if(!runtime.FileName.Equals(Path.Combine(package,"coreclr.dll"),StringComparison.OrdinalIgnoreCase))throw new IOException("Bundled runtime not loaded");
            var button=Wait(()=>main.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),new PropertyCondition(AutomationElement.NameProperty,"Connect…"))));
            ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var dialogCondition=new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,process.Id),new PropertyCondition(AutomationElement.NameProperty,"Connect to Server"));
            var dialog=Wait(()=>main.FindFirst(TreeScope.Descendants,dialogCondition) ?? AutomationElement.RootElement.FindFirst(TreeScope.Children,dialogCondition));
            var combo=Wait(()=>dialog.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"Profiles")));
            ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            var item=Wait(()=>combo.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,m.GetProperty("ProfileName").GetString())));
            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
            string Value(string id)=>((ValuePattern)dialog.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,id)).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
            if(Value("Host")!="127.0.0.1" || Value("Port")!="1" || Value("Database")!="pgms_synthetic" || Value("Username")!="pgms_fixture")throw new IOException("Installed application did not load synthetic profile fields");
            var remembered=dialog.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"RememberPassword"));
            if(((TogglePattern)remembered.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState!=ToggleState.On)throw new IOException("Synthetic saved credential not loaded");
            var cancel=dialog.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),new PropertyCondition(AutomationElement.NameProperty,"Cancel")));
            ((InvokePattern)cancel.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Wait(()=>main.Current.IsEnabled?main:null);
            GetWindowThreadProcessId(mainHandle,out uint windowProcess);
            if(windowProcess!=(uint)process.Id)throw new IOException("Original main HWND no longer belongs to child");
            if(!PostMessage(mainHandle,0x0010,IntPtr.Zero,IntPtr.Zero))throw new IOException("Own main WM_CLOSE could not be posted");
            if(!process.WaitForExit(15000)) {
                var status=main.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"Status"));
                throw new IOException("Own child close timed out; enabled="+main.Current.IsEnabled+"; status="+status?.Current.Name+"; layout="+File.Exists(Path.Combine(state,"layout.xml")));
            }
            if(process.ExitCode!=0)throw new IOException("Own child exit was not zero");
            if(Hash(profile)!=profileHash || Hash(sentinel)!=sentinelHash || !File.Exists(Path.Combine(state,"layout.xml")))throw new IOException("Shared profile/sentinel/layout preservation failed");
            var result=new {Valid=true,Executable=executable,SharedState=state,ProfileSelectedInInstalledUi=true,RememberedSyntheticCredentialLoaded=true,ProfileAndSentinelBytesPreserved=true,LayoutPersisted=true,RuntimePath=runtime.FileName,ExitCode=process.ExitCode,ConnectionAttempted=false,PhysicalKeyboardTested=false,CleanWindowsCertified=false};
            using var output=new FileStream(report,FileMode.CreateNew,FileAccess.Write);JsonSerializer.Serialize(output,result,new JsonSerializerOptions{WriteIndented=true});
            Console.WriteLine("Installed shared profile verified: "+report);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally {if(process is not null){if(!process.HasExited){process.Kill(true);process.WaitForExit();}process.Dispose();}}
    }
}
