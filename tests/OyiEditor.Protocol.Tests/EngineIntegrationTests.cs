using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OyiEditor.Protocol.Tests;

/// <summary>
/// Runs against a real <c>oyi serve --rpc --once</c>. Set <c>OYI_EXE</c> to the engine CLI to enable; skipped
/// otherwise.
/// </summary>
public sealed class EngineIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BuiltInCommandsAgainstTheRealEngine()
    {
        var oyi = Environment.GetEnvironmentVariable("OYI_EXE");
        Assert.SkipWhen(string.IsNullOrEmpty(oyi), "OYI_EXE is not set");
        Assert.True(File.Exists(oyi), $"OYI_EXE points at a missing file: {oyi}");

        using var engine = Process.Start(new ProcessStartInfo(oyi, ["serve", "--rpc", "--once", "--json"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        try
        {
            var banner = await engine.StandardOutput.ReadLineAsync(Token);
            Assert.NotNull(banner);
            using var listening = JsonDocument.Parse(banner);
            var port = listening.RootElement.GetProperty("listening").GetInt32();
            Assert.Equal(RpcClient.ProtocolVersion, listening.RootElement.GetProperty("protocol").GetUInt32());

            await using (var client = await RpcClient.ConnectAsync("127.0.0.1", port, cancellationToken: Token))
            {
                Assert.Equal(RpcClient.ProtocolVersion, client.Welcome.ProtocolVersion);
                Assert.NotEmpty(client.Welcome.EngineVersion);

                var ping = await client.CallAsync("ping", cancellationToken: Token);
                Assert.True(ping.Ok);
                Assert.Equal("pong", ping.Message);

                var version = await client.CallAsync("version", cancellationToken: Token);
                Assert.True(version.Ok);
                using var versionJson = JsonDocument.Parse(version.Payload);
                Assert.Equal(client.Welcome.EngineVersion, versionJson.RootElement.GetProperty("engine").GetString());
                Assert.Equal(
                    $"0x{client.Welcome.SchemaHash:x16}",
                    versionJson.RootElement.GetProperty("schemaHash").GetString());

                var echo = await client.CallAsync("echo", ["x"], "hello engine"u8.ToArray(), Token);
                Assert.True(echo.Ok);
                Assert.Equal("hello engine", Encoding.UTF8.GetString(echo.Payload.Span));

                var unknown = await client.CallAsync("no-such-command", cancellationToken: Token);
                Assert.False(unknown.Ok);
                Assert.Equal("NotFound", unknown.ErrorCode);
            }

            // --once: the engine exits after its only client leaves.
            using var exit = CancellationTokenSource.CreateLinkedTokenSource(Token);
            exit.CancelAfter(TimeSpan.FromSeconds(10));
            await engine.WaitForExitAsync(exit.Token);
            Assert.Equal(0, engine.ExitCode);
        }
        finally
        {
            if (!engine.HasExited)
            {
                engine.Kill();
            }
        }
    }
}
