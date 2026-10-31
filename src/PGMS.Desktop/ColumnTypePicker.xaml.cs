using System.ComponentModel;
using System.Globalization;
using System.Windows;
using Npgsql;
using PGMS.PostgreSql;

namespace PGMS.Desktop;
public partial class ColumnTypePicker : Window
{
    private readonly string connection;
    private bool ready,busy;
    private CancellationTokenSource? cancellation;
    public SelectedColumnType? SelectedType { get; private set; }
    public ColumnTypePicker(string connection)
    {
        this.connection=connection;InitializeComponent();ready=true;Update();
        ContentRendered+=(_,_)=>InspectButton.Focus();
    }
    private static int? Number(string value)=>string.IsNullOrWhiteSpace(value)?null:
        int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out int result)?result:throw new ArgumentException("Modifiers must be nonnegative whole numbers.");
    private SelectedColumnType Request=>new(Types.SelectedItem as InspectedDataType ?? throw new ArgumentException("Load types and select one."),Number(Length.Text),Number(Precision.Text),Number(Scale.Text));
    private void Changed(object sender,RoutedEventArgs e){if(ready)Update();}
    private void Update()
    {
        var type=Types.SelectedItem as InspectedDataType;
        Help.Text=type?.Kind=="d"?"Domain constraints, defaults and null restrictions remain enforced by PostgreSQL. Leave all modifiers empty."
            :type?.Kind=="e"?"Enum labels come from the selected database type. Leave all modifiers empty."
            :"Optional modifiers: character length; numeric precision and scale; or time precision (0–6). Leave unused fields empty.";
        Types.IsEnabled=Modifiers.IsEnabled=!busy;InspectButton.IsEnabled=!busy;CancelButton.IsEnabled=busy && cancellation?.IsCancellationRequested==false;
        try{Preview.Text=Request.ToSql();ChooseButton.IsEnabled=!busy;Feedback.Text="Selection does not change the database. Apply will check for changes since this inspection.";}
        catch(ArgumentException ex){Preview.Clear();ChooseButton.IsEnabled=false;Feedback.Text=ex.Message;}
    }
    private async void Load(object sender,RoutedEventArgs e)=>await LoadAsync();
    public async Task LoadAsync()
    {
        if(busy)return;busy=true;cancellation=new();Update();Feedback.Text="Loading usable types…";CancelButton.Focus();
        string? error=null;
        try{var types=await new DataTypeCatalog(connection).InspectAsync(cancellation.Token);Types.ItemsSource=types;Types.SelectedIndex=-1;Length.Clear();Precision.Clear();Scale.Clear();}
        catch(Exception ex)when(ex is NpgsqlException or OperationCanceledException or InvalidOperationException){Types.ItemsSource=null;error=ex is OperationCanceledException?"Type loading canceled. Load types again.":ex is PostgresException pg?$"{pg.SqlState}: {pg.MessageText}":ex.Message;}
        finally{busy=false;cancellation.Dispose();cancellation=null;Update();if(error is not null)Feedback.Text=error;InspectButton.Focus();}
    }
    private void Cancel(object sender,RoutedEventArgs e){cancellation?.Cancel();CancelButton.IsEnabled=false;}
    private void Choose(object sender,RoutedEventArgs e){if(busy)return;try{var request=Request;_=request.ToSql();SelectedType=request;DialogResult=true;}catch(ArgumentException ex){Feedback.Text=ex.Message;}}
    private void OnClosing(object? sender,CancelEventArgs e){if(busy){e.Cancel=true;cancellation?.Cancel();}}
}
