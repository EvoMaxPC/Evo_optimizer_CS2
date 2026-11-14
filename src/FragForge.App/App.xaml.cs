using System.Windows;
using FragForge.App.Services;
using FragForge.App.ViewModels;
using FragForge.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FragForge.App;

public partial class App : Application
{
    private IHost? _host;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var options = new ShellOptions(ConsoleLogging: false, ConfigureServices: services =>
            {
                services.AddSingleton<IConfirmationService, WpfConfirmationService>();
                services.AddSingleton<IFileDialogService, WpfFileDialogService>();
                services.AddSingleton<MainViewModel>();
            });
            _host = await CompositionRoot.BuildAsync(options);
            var viewModel = _host.Services.GetRequiredService<MainViewModel>();
            var window = new MainWindow { DataContext = viewModel };
            MainWindow = window;
            window.Show();
            await viewModel.ScanCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo iniciar FragForge:\n" + ex.Message, "FragForge", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow?.DataContext is MainViewModel viewModel) viewModel.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }
}
