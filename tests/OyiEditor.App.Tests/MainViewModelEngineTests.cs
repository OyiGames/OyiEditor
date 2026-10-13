using System.Diagnostics;
using System.Text.Json;
using OyiEditor.App.ViewModels;

namespace OyiEditor.App.Tests;

/// <summary>
/// Drives the view model's commands against a real <c>oyi serve --rpc --once</c> (set <c>OYI_EXE</c>; skipped
/// otherwise), i.e. the same path as clicking Connect, typing commands and clicking Disconnect.
/// </summary>
public sealed class MainViewModelEngineTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConnectSendAndDisconnect()
    {
        var oyi = Environment.GetEnvironmentVariable("OYI_EXE");
        Assert.SkipWhen(string.IsNullOrEmpty(oyi), "OYI_EXE is not set");

        using var engine = Process.Start(new ProcessStartInfo(oyi, ["serve", "--rpc", "--once", "--json"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        try
        {
            using var banner = JsonDocument.Parse(await engine.StandardOutput.ReadLineAsync(Token) ?? "{}");
            var viewModel = new MainViewModel { Port = banner.RootElement.GetProperty("listening").GetInt32() };

            await viewModel.ConnectCommand.ExecuteAsync(null);
            Assert.True(viewModel.IsConnected, string.Join('\n', viewModel.Log));
            Assert.False(viewModel.CanEditEndpoint);
            Assert.Contains(viewModel.Log, line => line.StartsWith("Connected to 127.0.0.1:", StringComparison.Ordinal));

            viewModel.CommandLine = "ping";
            await viewModel.SendCommand.ExecuteAsync(null);
            viewModel.CommandLine = "echo -- hello engine";
            await viewModel.SendCommand.ExecuteAsync(null);
            viewModel.CommandLine = "nope";
            await viewModel.SendCommand.ExecuteAsync(null);
            Assert.Equal(string.Empty, viewModel.CommandLine);
            Assert.Contains("ok: pong", viewModel.Log);
            Assert.Contains("hello engine", viewModel.Log);
            Assert.Contains(viewModel.Log, line => line.StartsWith("error NotFound", StringComparison.Ordinal));

            await viewModel.DisconnectCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => !viewModel.IsConnected);
            Assert.Equal("Disconnected", viewModel.Status);
            Assert.True(viewModel.CanEditEndpoint);
            await WaitUntilAsync(() => viewModel.Log.Contains("Disconnected."));

            using var exit = CancellationTokenSource.CreateLinkedTokenSource(Token);
            exit.CancelAfter(TimeSpan.FromSeconds(10));
            await engine.WaitForExitAsync(exit.Token);
        }
        finally
        {
            if (!engine.HasExited)
            {
                engine.Kill();
            }
        }
    }

    // Without a UI dispatcher the close watcher continues on the thread pool, so poll for its effect.
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 10 s.");
            await Task.Delay(20, Token);
        }
    }
}
