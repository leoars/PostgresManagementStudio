using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using PGMS.Desktop;

internal static partial class SqlLinkChecks
{
    private static async Task OptionsAsync(Action<bool,string> check)
    {
        await using var first=new QueryView(Guid.NewGuid(),_=>{});
        await using var second=new QueryView(Guid.NewGuid(),_=>{});
        var a=(TextEditor)first.FindName("Editor");var b=(TextEditor)second.FindName("Editor");
        LinkElementGenerator[] Links(TextEditor e)=>e.TextArea.TextView.ElementGenerators.OfType<LinkElementGenerator>().ToArray();
        check(a.Options.EnableHyperlinks && a.Options.EnableEmailHyperlinks && Links(a).Length==2,"Link defaults retain both enabled options and exactly two generators");
        check(Links(a).All(g=>g is SqlLinkElementGenerator),"Built-in backtracking generators are replaced, not duplicated");
        a.Options.EnableHyperlinks=false;
        check(Links(a).Length==1 && a.Options.EnableEmailHyperlinks,"URL option removes only URL matcher");
        a.Options.EnableEmailHyperlinks=false;
        check(Links(a).Length==0,"Disabling both options removes every link matcher");
        a.Options.EnableHyperlinks=true;
        check(Links(a).Length==1 && Links(a).All(g=>g is SqlLinkElementGenerator),"Reenabled URL uses only linear matcher");
        a.Options.EnableEmailHyperlinks=true;
        check(Links(a).Length==2 && Links(a).All(g=>g is SqlLinkElementGenerator),"Reenabled email replaces recreated built-in matcher synchronously");
        a.Options.RequireControlModifierForHyperlinkClick=false;
        check(Links(a).All(g=>!g.RequireControlModifierForClick),"Modifier option reaches both linear generators");
        a.Options.ShowSpaces=true;
        check(Links(a).Length==2 && Links(a).Distinct().Count()==2,"Unrelated options neither duplicate nor drop matchers");
        check(Links(b).Length==2 && Links(b).All(g=>g.RequireControlModifierForClick),"Options remain independent between editors");
        check(!Links(a).Intersect(Links(b)).Any(),"Editors never share visual generator instances");
        a.Options=new ICSharpCode.AvalonEdit.TextEditorOptions {EnableHyperlinks=false,EnableEmailHyperlinks=true,RequireControlModifierForHyperlinkClick=false};
        check(Links(a).Length==1 && Links(a).All(g=>g is SqlLinkElementGenerator && !g.RequireControlModifierForClick),"Replacing the Options object retains linear matchers and new flags");
        a.Options.EnableHyperlinks=true;
        check(Links(a).Length==2 && Links(a).All(g=>g is SqlLinkElementGenerator),"Changes on replacement Options stay synchronized");
        a.Options.RequireControlModifierForHyperlinkClick=true;
        check(Links(a).All(g=>g.RequireControlModifierForClick),"Modifier can be restored without a new document");
    }
}
