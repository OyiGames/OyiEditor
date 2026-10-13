using System.Collections.ObjectModel;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OyiEditor.App.Console;
using OyiEditor.Protocol;

namespace OyiEditor.App.ViewModels;

/// <summary>
/// Connection panel and command console over one <see cref="RpcClient"/>. Runs on the UI thread: awaits resume on
/// the Avalonia dispatcher, so state and <see cref="Log"/> are only touched there.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 2000;

    private RpcClient? _client;

    public MainViewModel()
    {
        Append("Start the engine with `oyi serve --rpc`, enter the port it prints, and connect.");
        Append("Console: `command arg1 arg2 -- payload text`, e.g. `ping`, `version`, `echo -- hello`.");
    }

    [ObservableProperty]
    public partial string Host { get; set; } = "127.0.0.1";

    // `oyi serve --rpc` listens on an ephemeral port unless --port is given, so there is no default.
    [ObservableProperty]
    public partial decimal? Port { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Disconnected";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditEndpoint))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditEndpoint))]
    public partial bool IsConnecting { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string CommandLine { get; set; } = string.Empty;

    public bool CanEditEndpoint => !IsConnected && !IsConnecting;

    public ObservableCollection<string> Log { get; } = [];

    private bool CanConnect() => !IsConnected && !IsConnecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (Port is not { } port)
        {
            Append("Enter the port that `oyi serve --rpc` printed.");
            return;
        }

        var host = Host.Trim();
        IsConnecting = true;
        Status = "Connecting...";
        try
        {
            var client = await RpcClient.ConnectAsync(host, (int)port);
            _client = client;
            IsConnected = true;
            var welcome = client.Welcome;
            Status = $"Connected to engine {welcome.EngineVersion}";
            Append(
                $"Connected to {host}:{port}: engine {welcome.EngineVersion}, protocol {welcome.ProtocolVersion}, "
                + $"schema 0x{welcome.SchemaHash:x16}, session {welcome.SessionId}.");
            _ = PumpStreamsAsync(client);
            _ = WatchCloseAsync(client);
        }
        catch (Exception exception) when (exception is RpcRejectedException or TimeoutException or SocketException
                                              or IOException or ProtocolException or ArgumentException)
        {
            Status = "Disconnected";
            Append($"Connect failed: {exception.Message}");
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private bool CanDisconnect() => IsConnected;

    // WatchCloseAsync resets the state once the client reports it is closed.
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (_client is { } client)
        {
            await client.DisposeAsync();
        }
    }

    private bool CanSend() => IsConnected && !string.IsNullOrWhiteSpace(CommandLine);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var line = CommandLine;
        CommandLine = string.Empty;
        Append($"> {line}");
        if (!CommandInput.TryParse(line, out var input))
        {
            return;
        }

        if (_client is not { } client)
        {
            Append("Not connected.");
            return;
        }

        try
        {
            var reply = await client.CallAsync(input.Command, input.Args, input.Payload);
            foreach (var text in ReplyFormat.Describe(reply))
            {
                Append(text);
            }
        }
        catch (Exception exception) when (exception is IOException or ProtocolException or ObjectDisposedException)
        {
            Append($"Call failed: {exception.Message}");
        }
    }

    private async Task PumpStreamsAsync(RpcClient client)
    {
        await foreach (var frame in client.Streams.ReadAllAsync())
        {
            Append(ReplyFormat.Describe(frame));
        }
    }

    private async Task WatchCloseAsync(RpcClient client)
    {
        var error = await client.Closed;
        await client.DisposeAsync();
        if (!ReferenceEquals(_client, client))
        {
            return;
        }

        _client = null;
        IsConnected = false;
        Status = "Disconnected";
        Append(error is null ? "Disconnected." : $"Connection lost: {error.Message}");
    }

    private void Append(string line)
    {
        Log.Add(line);
        while (Log.Count > MaxLogLines)
        {
            Log.RemoveAt(0);
        }
    }
}
