using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OyiEditor.App.ViewModels;

/// <summary>
/// Connection panel and command console. The RPC client lands once OyiProtocol publishes its first tag and the C#
/// bindings exist; until then the console only reports that no engine is connected.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        Log.Add("Start the engine with `oyi serve --rpc` and enter the port it prints.");
    }

    [ObservableProperty]
    public partial string Host { get; set; } = "127.0.0.1";

    // `oyi serve --rpc` listens on an ephemeral port unless --port is given, so there is no default.
    [ObservableProperty]
    public partial decimal? Port { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Disconnected";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string CommandLine { get; set; } = string.Empty;

    public ObservableCollection<string> Log { get; } = [];

    [RelayCommand]
    private void Connect()
    {
        Status = "Disconnected";
        Log.Add($"Cannot connect to {Host}:{Port}: protocol bindings are not available yet (waiting for OyiProtocol v1.0.0).");
    }

    private bool CanSend() => !string.IsNullOrWhiteSpace(CommandLine);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        Log.Add($"> {CommandLine}");
        Log.Add("Not connected.");
        CommandLine = string.Empty;
    }
}
