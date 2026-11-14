using Microsoft.Win32;
using System.Windows;
using FragForge.UI.Core.Services;

namespace FragForge.App.Services;

/// <summary>
/// Implementaciones WPF de las interfaces de <c>FragForge.UI.Core</c> (Capa 11.2). Las interfaces se movieron
/// al proyecto sin WPF para que los ViewModels se puedan probar en <c>net10.0</c>; aquí queda solo lo que
/// necesita una ventana.
/// </summary>
public sealed class WpfConfirmationService : IConfirmationService
{
    public bool Confirm(string message, string title) => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    public bool ConfirmRisk(string first, string second, string title) => Confirm(first, title) && Confirm(second, title);
}

public sealed class WpfFileDialogService : IFileDialogService
{
    public string? SaveReport()
    {
        var dialog = new SaveFileDialog { Title = "Exportar informe FragForge", Filter = "HTML (*.html)|*.html|JSON (*.json)|*.json", DefaultExt = ".html", FileName = "fragforge-report.html" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? OpenCs2()
    {
        var dialog = new OpenFileDialog { Title = "Seleccionar cs2.exe", Filter = "Counter-Strike 2 (cs2.exe)|cs2.exe", CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
