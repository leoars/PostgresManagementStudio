using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

internal static class EditorFactorChecks
{
    public static async Task RunAsync(Action<bool,string> check, string scenario)
    {
        string text = scenario switch {
            "unicode" => "--" + string.Concat(Enumerable.Repeat("ação漢😀",32768)),
            "links" => "--" + string.Concat(Enumerable.Repeat(" https://example.invalid/a?q=1 test@example.invalid ",64)),
            "short" => "SELECT 'ação 漢字 😀';\r\n-- https://example.invalid\r\n",
            _ => "--" + new string('x',262142)
        };
        var view = new QueryView(Guid.NewGuid(), _ => throw new InvalidOperationException("Probe must not access clipboard"));
        var editor=(TextEditor)view.FindName("Editor");
        var definition=editor.SyntaxHighlighting;
        var window=new Window {Content=view,Width=1100,Height=700,ShowInTaskbar=false,Opacity=0};
        var samples=new List<object>();
        string root=Path.GetFullPath(Path.Combine("artifacts","editor-factors",Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try {
            window.Show();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            string[] modes=["baseline","legacy-links","no-links"];
            for(int repetition=0;repetition<3;repetition++)
            foreach(string mode in modes.Skip(repetition).Concat(modes.Take(repetition))) {
                editor.Text="";
                editor.Options.EnableHyperlinks=editor.Options.EnableEmailHyperlinks=mode!="no-links";
                editor.SyntaxHighlighting=mode=="no-highlight"?null:definition;
                TextOptions.SetTextFormattingMode(editor,mode=="display"?TextFormattingMode.Display:TextFormattingMode.Ideal);
                if(mode=="legacy-links") {
                    var generators=editor.TextArea.TextView.ElementGenerators;
                    foreach(var generator in generators.OfType<ICSharpCode.AvalonEdit.Rendering.LinkElementGenerator>().ToArray()) generators.Remove(generator);
                    generators.Add(new ICSharpCode.AvalonEdit.Rendering.LinkElementGenerator());
                    var mail=typeof(TextEditor).Assembly.GetType("ICSharpCode.AvalonEdit.Rendering.MailLinkElementGenerator",true)!;
                    generators.Add((ICSharpCode.AvalonEdit.Rendering.VisualLineElementGenerator)Activator.CreateInstance(mail,true)!);
                }
                await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                long last=0,gap=0;int beats=0;
                using var process=Process.GetCurrentProcess();TimeSpan cpu=process.TotalProcessorTime;
                var watch=Stopwatch.StartNew();
                var timer=new DispatcherTimer(DispatcherPriority.Input){Interval=TimeSpan.FromMilliseconds(10)};
                timer.Tick+=(_,_)=>{long now=watch.ElapsedMilliseconds;gap=Math.Max(gap,now-last);last=now;beats++;};
                timer.Start();
                try {editor.Text=text;window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);}
                finally {timer.Stop();}
                long elapsed=watch.ElapsedMilliseconds;gap=Math.Max(gap,elapsed-last);process.Refresh();
                check(editor.Text==text,"Factor "+mode+" preserves complete text");
                editor.AppendText("-- edit");editor.Undo();
                check(editor.Text==text,"Factor "+mode+" preserves native Undo");
                samples.Add(new {mode,repetition,elapsedMs=elapsed,maxInputGapMs=gap,beats,processCpuMs=(process.TotalProcessorTime-cpu).TotalMilliseconds});
            }
            string json=JsonSerializer.Serialize(new {milestone=BuildInfo.Milestone,scenario,characters=text.Length,samples,
                limitation="Synthetic isolated QueryView, preloaded window, rotated linear/legacy/no-links order, no DB/file IO. Legacy detectors come from pinned library via reflection only in tests. Includes document creation on UI unlike production prepared Open. Process CPU is whole-process, not exact UI CPU; idle is not compositor presentation. No factor automatically applied to production."},new JsonSerializerOptions{WriteIndented=true});
            File.WriteAllText(Path.Combine(root,"factors.json"),json);Console.WriteLine("Factor report: "+root);
        }finally {window.Close();await view.DisposeAsync();}
    }
}
