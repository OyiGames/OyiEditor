using Avalonia;

namespace OyiEditor.App;

internal static class Program
{
    // Nothing that touches Avalonia or a SynchronizationContext may run before BuildAvaloniaApp.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the IDE previewer; keep it side-effect free.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
