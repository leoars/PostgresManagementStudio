using System.IO;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using PGMS.Desktop;

/// <summary>PGMS-065 (M1083): Template Explorer, Specify Values for Template Parameters, Insert Snippet and Surround With.</summary>
internal static class TemplateChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        // Template parameters, as SSMS writes them: <name, type, value>
        var found=SqlTemplateParameters.Parse("USE <db, sysname, Sample>\r\nALTER TABLE <t,, x> ADD c int; -- <db, other, ignored> and a < b, c > d\r\nSELECT <Create Date,,>");
        check(found.Select(p=>p.Name).SequenceEqual(["db","t","Create Date"]) && found[0]==new SqlTemplateParameter("db","sysname","Sample") && found[1].Type=="" && found[2].Value=="","Parameters are listed once, in order, with type and value trimmed; the type may be empty");
        check(SqlTemplateParameters.Replace("<a, int, 1> + <a,int,2> + <b,,3>",new Dictionary<string,string>{["a"]="10"})=="10 + 10 + <b,,3>","Every occurrence of a parameter is replaced; parameters without a value stay");
        check(SqlTemplateParameters.Parse("select 1 where a < b").Count==0 && SqlTemplateParameters.Parse("<x>").Count==0,"Comparisons and tags without commas are not parameters");
        var templates=SqlTemplateCatalog.BuiltInTemplates;
        check(templates.Count>=30 && templates.Select(t=>t.Folder).Distinct().Count()>=10,"Built-in PostgreSQL templates in folders (Database, Schema, Table, Index, View, Function…)");
        var bad=templates.Where(t=>SqlTemplateParameters.Parse(t.Text) is var ps && (ps.Count==0 || SqlTemplateParameters.Replace(t.Text,ps.ToDictionary(p=>p.Name,p=>p.Value)) is var filled && (filled.Contains("<") && SqlTemplateParameters.Parse(filled).Count>0))).Select(t=>t.Name).ToArray();
        check(bad.Length==0,"Every built-in template has parameters and none is left after filling the defaults: "+string.Join(", ",bad));
        check(templates.All(t=>!t.Text.Contains('\n') || t.Text.Contains("\r\n")) && templates.Single(t=>t.Name=="Create Table").Text.StartsWith("-- Create table template\r\nCREATE TABLE <schema_name, name, public>."),"Templates use CRLF and start with a comment, as in SSMS");
        // Snippets in the Visual Studio format of SSMS
        const string xml="""
            <?xml version="1.0" encoding="utf-8"?>
            <CodeSnippets xmlns="http://schemas.microsoft.com/VisualStudio/2005/CodeSnippet">
              <CodeSnippet Format="1.0.0">
                <Header><Title>Guarded</Title><Description>Wraps code.</Description><SnippetTypes><SnippetType>SurroundsWith</SnippetType></SnippetTypes></Header>
                <Snippet>
                  <Declarations><Literal><ID>Condition</ID><ToolTip>Condition to evaluate</ToolTip><Default>true</Default></Literal></Declarations>
                  <Code Language="SQL"><![CDATA[DO $$ BEGIN IF $Condition$ THEN $selected$$end$ END IF; END $$; -- $Condition$ $other$]]></Code>
                </Snippet>
              </CodeSnippet>
            </CodeSnippets>
            """;
        var loaded=SqlSnippet.Load(xml,"Mine");
        check(loaded.Count==1 && loaded[0] is {Title:"Guarded",IsSurroundsWith:true,IsExpansion:false,Folder:"Mine"} && loaded[0].Literals.Single().Default=="true","A .snippet file is read: title, kind, literals and folder");
        var (expanded,caret)=loaded[0].Expand("PERFORM 1;");
        check(expanded=="DO $$ BEGIN IF true THEN PERFORM 1; END IF; END $$; -- true $other$" && caret==expanded.IndexOf(" END IF",StringComparison.Ordinal),"Literals take their default everywhere, $selected$ takes the selection, $end$ marks the caret; $$ and undeclared $…$ stay as text");
        check(SqlSnippet.Load("<not xml",  "x").Count==0 && SqlSnippet.Load("<CodeSnippets/>","x").Count==0,"Invalid or empty files give no snippets");
        var snippets=SqlTemplateCatalog.BuiltInSnippets;
        check(snippets.Count(s=>s.IsSurroundsWith)>=4 && snippets.Count(s=>s.IsExpansion)>=15,"Built-in snippets to insert and to surround with");
        check(snippets.All(s=>s.Literals.All(l=>!s.Expand().Text.Contains("$"+l.Id+"$")) && !s.Expand().Text.Contains("$end$") && !s.Expand().Text.Contains("$selected$")),"Every built-in snippet expands without leftover markers");
        check(snippets.Single(s=>s.Title=="DO Block").Expand("x").Text.Contains("DO $do$"),"Dollar quoting in a snippet stays as written");
        // The user's own templates and snippets
        string root=Path.Combine(Path.GetTempPath(),"pgms-templates-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root,"T","Reports"));Directory.CreateDirectory(Path.Combine(root,"S","Team"));
            File.WriteAllText(Path.Combine(root,"T","Reports","Monthly.sql"),"select <month, int, 1>;");File.WriteAllText(Path.Combine(root,"T","Loose.sql"),"select 1;");
            File.WriteAllText(Path.Combine(root,"S","Team","guard.snippet"),xml);File.WriteAllText(Path.Combine(root,"S","broken.snippet"),"<oops");
            var all=SqlTemplateCatalog.Templates(Path.Combine(root,"T"));
            check(all.Any(t=>t is {Folder:"Reports",Name:"Monthly"} && t.Path!=null) && all.Any(t=>t is {Folder:SqlTemplateCatalog.UserRootFolder,Name:"Loose"}) && all.Count==templates.Count+2,"The user's .sql files join the templates, by subfolder (files at the root go to Custom)");
            var mine=SqlTemplateCatalog.Snippets(Path.Combine(root,"S"));
            check(mine.Count==snippets.Count+1 && mine.Any(s=>s is {Folder:"Team",Title:"Guarded"}),"The user's .snippet files join the snippets; a broken file is skipped");
            check(SqlTemplateCatalog.Templates(Path.Combine(root,"missing")).Count==templates.Count,"A missing user folder lists only the built-in templates");
            // Template Explorer
            var explorer=new TemplateExplorerView(Path.Combine(root,"T"));SqlTemplate? opened=null;explorer.OpenTemplate=t=>opened=t;
            var folders=explorer.Tree.Items.OfType<TreeViewItem>().Select(i=>(string)i.Header).ToList();
            check(folders.Contains("Table") && folders.Contains("Reports") && folders.SequenceEqual(folders.OrderBy(f=>f,StringComparer.CurrentCultureIgnoreCase)),"Template Explorer shows the folders in order, the user's included");
            check(!explorer.OpenSelected() && opened is null,"Nothing opens without a selected template");
            check(explorer.Select("Table","Add Column") && explorer.OpenSelected() && opened?.Name=="Add Column","Opening the selected template hands it to the main window");
            check(explorer.Tree.ContextMenu!.Items.OfType<MenuItem>().Select(i=>(string)i.Header).SequenceEqual(["_Open","_Refresh","Open Templates _Folder"]),"Context menu: Open, Refresh, Open Templates Folder");
        }
        finally{try{Directory.Delete(root,true);}catch(IOException){}}
        // Editor: Insert Snippet, Surround With, Specify Values
        var view=new QueryView();var editor=(TextEditor)view.FindName("Editor");var status=(TextBlock)view.FindName("QueryStatus");
        try
        {
            var table=snippets.Single(s=>s.Folder=="Table" && s.Title=="Create Table");
            editor.Text="";view.InsertSnippet(table);
            check(editor.Text==table.Expand().Text,"Insert Snippet writes the snippet with its defaults");
            check(editor.SelectedText=="public","The first field is selected for typing, as in SSMS");
            editor.Text="SELECT 1;";editor.Select(0,editor.Text.Length);
            view.InsertSnippet(snippets.Single(s=>s.Title=="Transaction"));
            check(editor.Text.StartsWith("BEGIN;") && editor.Text.Contains("SELECT 1;") && editor.Text.TrimEnd().EndsWith("COMMIT;"),"Surround With wraps the selection");
            view.SnippetSource=()=>snippets;
            var menu=view.ShowSnippetPicker(false);
            check(menu is not null && (string)((MenuItem)menu.Items[0]).Header=="Insert Snippet:" && menu.Items.OfType<MenuItem>().Skip(1).Select(i=>(string)i.Header).Contains("Table") && !menu.Items.OfType<MenuItem>().Skip(1).Any(i=>(string)i.Header=="Control Flow"),"Insert Snippet lists the folders of snippets to insert");
            if(menu is not null)menu.IsOpen=false;
            menu=view.ShowSnippetPicker(true);
            check(menu is not null && (string)((MenuItem)menu.Items[0]).Header=="Surround With:" && menu.Items.OfType<MenuItem>().Skip(1).Select(i=>(string)i.Header).SequenceEqual(["Control Flow"]),"Surround With lists only the snippets that wrap text");
            if(menu is not null)
            {
                var item=((MenuItem)menu.Items[1]).Items.OfType<MenuItem>().First(i=>(string)i.Header=="If");editor.Text="x := 1;";editor.Select(0,7);
                item.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
                check(editor.Text.StartsWith("IF condition THEN") && editor.Text.Contains("x := 1;") && !menu.IsOpen,"Choosing a snippet in the picker inserts it and closes the picker");
            }
            view.SnippetSource=()=>[];check(view.ShowSnippetPicker(false) is null && status.Text=="No snippets are available.","Without snippets the tab says so");
            editor.Text="no parameters here";check(view.SpecifyTemplateParameters(_=>throw new InvalidOperationException()) is null && status.Text.StartsWith("The query has no template parameters"),"Without parameters the dialog is not shown");
            editor.Text="CREATE SCHEMA <s, name, a> AUTHORIZATION <o,,me>; -- <s, name, a>";
            IReadOnlyList<SqlTemplateParameter>? asked=null;
            check(view.SpecifyTemplateParameters(p=>{asked=p;return null;}) is null && editor.Text.Contains("<s, name, a>"),"Cancel leaves the text unchanged");
            int? replaced=view.SpecifyTemplateParameters(p=>new Dictionary<string,string>{["s"]="sales",["o"]="alice"});
            check(asked?.Count==2 && replaced==3 && editor.Text=="CREATE SCHEMA sales AUTHORIZATION alice; -- sales","OK replaces every occurrence");
            editor.Undo();check(editor.Text=="CREATE SCHEMA <s, name, a> AUTHORIZATION <o,,me>; -- <s, name, a>","One Undo restores the parameters");
            var dialog=new TemplateParametersDialog(SqlTemplateParameters.Parse(editor.Text)){ShowInTaskbar=false};
            check(dialog.Title=="Specify Values for Template Parameters" && dialog.Grid.Columns.Select(c=>(string)c.Header).SequenceEqual(["Parameter","Type","Value"]) && dialog.Grid.Columns[0].IsReadOnly && !dialog.Grid.Columns[2].IsReadOnly,"The dialog has the SSMS columns; only Value is edited");
            dialog.Rows[0].Value="x";check(dialog.Values["s"]=="x" && dialog.Values["o"]=="me","The dialog starts with the template values and returns the edited ones");
        }
        finally{await view.DisposeAsync();}
        // Main window
        var shell=new MainWindow{ShowInTaskbar=false};shell.Show();await shell.RecoveryCompletion;
        QueryView? query=null;
        try
        {
            var viewMenu=shell.Menu().Items.OfType<MenuItem>().Single(m=>(string)m.Header=="_View");var names=viewMenu.Items.OfType<MenuItem>().Select(i=>i.Header as string).ToList();
            check(names.IndexOf("_Template Explorer")==names.IndexOf("Re_gistered Servers")+1 && ((MenuItem)shell.FindName("TemplateExplorerMenu")).InputGestureText=="Ctrl+Alt+T","View lists Template Explorer (Ctrl+Alt+T) after Registered Servers, as in SSMS");
            var queryMenu=shell.Menu().Items.OfType<MenuItem>().Single(m=>(string)m.Header=="_Query");var qnames=queryMenu.Items.OfType<MenuItem>().Select(i=>i.Header as string).ToList();
            check(qnames.IndexOf("Specify Values for Te_mplate Parameters…")==qnames.IndexOf("Open Ser_ver in Object Explorer")+1,"Query lists Specify Values for Template Parameters after Open Server in Object Explorer");
            var intelli=((MenuItem)shell.FindName("IntelliSenseEditMenu")).Items.OfType<MenuItem>().Select(i=>i.Header as string).ToList();
            check(intelli.TakeLast(2).SequenceEqual(["_Surround With…","Insert S_nippet…"]) && ((MenuItem)shell.FindName("InsertSnippetMenu")).InputGestureText=="Ctrl+K, Ctrl+X","Edit ▸ IntelliSense ends with Surround With and Insert Snippet");
            check(MainWindow.SpecifyTemplateParametersCommand.InputGestures.OfType<System.Windows.Input.KeyGesture>().Any(g=>g.Key==System.Windows.Input.Key.M && g.Modifiers==(System.Windows.Input.ModifierKeys.Control|System.Windows.Input.ModifierKeys.Shift)),"Ctrl+Shift+M is the shortcut, as in SSMS");
            shell.ShowTemplateExplorer();
            check(shell.TemplateExplorer.Tree.Items.Count>=10,"Template Explorer opens as a docked window");
            query=await shell.OpenTemplateAsync(templates.Single(t=>t.Name=="Create Schema"));
            check(((TextEditor)query.FindName("Editor")).Text.Contains("CREATE SCHEMA IF NOT EXISTS <schema_name, name, sample_schema>"),"Opening a template starts a query tab with its text");
            shell.TemplateParametersPrompt=p=>p.ToDictionary(x=>x.Name,x=>x.Name=="schema_name"?"sales":x.Value);
            check(shell.SpecifyTemplateParameters()==2 && ((TextEditor)query.FindName("Editor")).Text.Contains("CREATE SCHEMA IF NOT EXISTS sales"),"Specify Values from the main window fills the active query");
        }
        finally{if(query is not null)query.IsDirty=false;shell.Close();await shell.CloseCompletion;}
    }

    private static Menu Menu(this MainWindow shell)=>(Menu)((System.Windows.FrameworkElement)shell.FindName("EditMenu")).Parent;
}
