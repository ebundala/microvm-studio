using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MicroVMStudio.App.ViewModels;
using MicroVMStudio.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MicroVMStudio.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };

            // If the host dies on its own (Kestrel fault, SIGTERM), close the
            // window too rather than leave a UI whose every action would fail.
            Program.Host.Services
                .GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopping
                .Register(() => Dispatcher.UIThread.Post(() => desktop.Shutdown()));
        }

        base.OnFrameworkInitializationCompleted();
    }
}