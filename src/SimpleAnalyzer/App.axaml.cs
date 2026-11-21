using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SimpleAnalyzer.ViewModels;
using SimpleAnalyzer.Views;

namespace SimpleAnalyzer;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = Settings.Load();
            // --host and --port override the saved server for this run (and are saved on connect).
            var args = desktop.Args ?? [];
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--host") settings.Host = args[i + 1];
                else if (args[i] == "--port" && int.TryParse(args[i + 1], out int port)) settings.Port = port;
            }
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(settings) };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
