using System.Windows;
using FragForge.App.Services;
using FragForge.UI.Core;
using FragForge.Composition;
using FragForge.Core.Sessions;
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
            // Capa 9: el modo sesion se inyecta por propiedad para no tocar el constructor del ViewModel.
            viewModel.Sessions = _host.Services.GetRequiredService<GameSessionService>();
            // Capa 10.1: panel de postura de seguridad. Se inyecta por propiedad, igual que Sessions, para no
            // tocar el constructor del ViewModel. Es solo lectura.
            viewModel.SecurityPostureService = _host.Services.GetRequiredService<FragForge.Core.Abstractions.ISecurityPostureService>();
            // Capa 11.3: lista de operaciones para revertir UNA concreta. Tambien por propiedad, mismo criterio.
            viewModel.Snapshots = _host.Services.GetRequiredService<FragForge.Core.Abstractions.ISnapshotStore>();
            // Capa 9.3: recuperacion al arrancar. Si quedo un marcador de una caida y CS2 no corre, se
            // revierte y se limpia; si el juego sigue abierto, no se toca nada.
            try
            {
                var recovery = await viewModel.Sessions.RecoverAsync();
                foreach (var error in recovery.Errors) viewModel.Logs.Add("[Sesion] " + error);
            }
            catch (Exception ex) { viewModel.Logs.Add("[Sesion] No se pudo recuperar la sesion anterior: " + ex.Message); }
            var window = new MainWindow { DataContext = viewModel };
            MainWindow = window;
            window.Show();
            await viewModel.ScanCommand.ExecuteAsync(null);
            await viewModel.ReadSecurityCommand.ExecuteAsync(null);
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
