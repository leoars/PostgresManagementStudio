using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace PGMS.Desktop;

public partial class AiSettingsDialog : Window
{
    private readonly AiSettingsStore store;
    private AiSettings settings = AiSettings.Default;
    private AiProvider shown;
    private bool ready, busy, loadFailed;
    // Keys typed but not saved yet, per provider; dropped on close.
    private readonly Dictionary<AiProvider, string> pendingKeys = [];

    public AiSettingsDialog(string? path = null)
    {
        InitializeComponent(); store = new(path ?? AiSettingsStore.DefaultPath);
        Provider.ItemsSource = Enum.GetValues<AiProvider>();
        try { settings = store.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { loadFailed = true; Feedback.Text = "AI settings could not be read and were left unchanged: " + ex.Message; }
        ShowProvider(settings.ActiveProvider); ready = true; Update();
    }

    public AiSettings Current => settings;

    private void ShowProvider(AiProvider provider)
    {
        shown = provider; var p = settings.For(provider);
        Provider.SelectedItem = provider; Endpoint.Text = p.Endpoint; Model.Text = p.Model; Model.ItemsSource = null;
        Key.Password = pendingKeys.GetValueOrDefault(provider, "");
    }

    private void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || Provider.SelectedItem is not AiProvider next || next == shown) return;
        Remember(); ShowProvider(next); Feedback.Text = ""; Update();
    }

    // Keeps the visible provider's address, model and typed key while another provider is shown.
    private void Remember()
    {
        if (Key.Password.Length > 0) pendingKeys[shown] = Key.Password; else pendingKeys.Remove(shown);
        try { settings = settings.With(settings.For(shown) with { Endpoint = Endpoint.Text.Trim(), Model = Model.Text.Trim() }); }
        catch (ArgumentException) { } // Invalid edits are reported again on Save or Test.
    }

    private void Changed(object sender, RoutedEventArgs e) { if (ready) Update(); }

    private void Update()
    {
        var p = settings.For(shown);
        Key.IsEnabled = KeyLabel.IsEnabled = p.NeedsKey;
        RemoveKeyButton.IsEnabled = !busy && !loadFailed && p.HasKey;
        KeyStatus.Text = !p.NeedsKey ? "Ollama runs without an API key." : p.HasKey ? (Key.Password.Length > 0 ? "A key is saved; Save replaces it with the key typed above." : "A key is saved. Type a new key to replace it.") : "No key saved.";
        SaveButton.IsEnabled = !busy && !loadFailed;
        TestButton.IsEnabled = !busy && (!p.NeedsKey || p.HasKey || Key.Password.Length > 0);
    }

    private void SetBusy(bool value) { busy = value; Fields.IsEnabled = CloseButton.IsEnabled = !value; Update(); }

    /// <summary>The visible provider as it would be saved, with a typed key encrypted in memory.</summary>
    private AiProviderSettings Edited()
    {
        var p = settings.For(shown) with { Endpoint = Endpoint.Text.Trim(), Model = Model.Text.Trim() };
        if (p.NeedsKey && Key.Password.Length > 0) p = p.WithKey(Key.Password);
        return p.Validate();
    }

    private async void Test(object sender, RoutedEventArgs e) => await TestAsync();
    public async Task TestAsync()
    {
        if (busy) return;
        AiProviderSettings p;
        try { p = Edited(); }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException) { Feedback.Text = ex.Message; return; }
        if (p.NeedsKey && !p.HasKey) { Feedback.Text = "Enter an API key to test."; return; }
        SetBusy(true); Feedback.Text = $"Contacting {p.Provider}…";
        string message;
        try
        {
            var models = await AiProviderClient.ListModelsAsync(p);
            string typed = Model.Text; Model.ItemsSource = models; Model.Text = typed;
            message = models.Count == 0 ? $"Connected to {p.Provider}, but it listed no models." : $"Connected to {p.Provider}: {models.Count} model(s) available.";
            if (p.Model.Length > 0 && models.Count > 0 && !models.Contains(p.Model, StringComparer.Ordinal)) message += $" The model \"{p.Model}\" is not in the list.";
        }
        catch (Exception ex) when (ex is AiProviderException or CryptographicException) { message = ex.Message; }
        finally { SetBusy(false); }
        Feedback.Text = message;
    }

    private void Save(object sender, RoutedEventArgs e) => SaveSettings();
    public bool SaveSettings()
    {
        if (busy || loadFailed) return false;
        try
        {
            var p = Edited();
            if (p.Provider != AiProvider.Ollama && p.Model.Length == 0) throw new ArgumentException("Enter or choose a model (Test Connection lists the available ones).");
            if (p.Provider == AiProvider.Ollama && p.Model.Length == 0) throw new ArgumentException("Enter or choose an Ollama model (Test Connection lists the installed ones).");
            var next = settings.With(p) with { ActiveProvider = shown };
            store.Save(next); settings = next;
            pendingKeys.Remove(shown); Key.Clear();
            Feedback.Text = $"Saved. The query assistant will use {shown}{(p.Model.Length > 0 ? " (" + p.Model + ")" : "")}.";
            Update(); return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or CryptographicException) { Feedback.Text = "Not saved: " + ex.Message; return false; }
    }

    private void RemoveKey(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Remove the saved {shown} API key from this computer?", "AI Assistant Settings", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        RemoveSavedKey();
    }
    public void RemoveSavedKey()
    {
        try
        {
            var next = settings.With(settings.For(shown).WithoutKey());
            store.Save(next); settings = next; pendingKeys.Remove(shown); Key.Clear();
            Feedback.Text = $"The {shown} API key was removed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Feedback.Text = "The key was not removed: " + ex.Message; }
        Update();
    }

    private void OnClosing(object? sender, CancelEventArgs e) { if (busy) { e.Cancel = true; return; } Key.Clear(); pendingKeys.Clear(); }
}
