using Microsoft.Win32;
using System.Windows;

namespace FragForge.App.Services;

public interface IConfirmationService
{
    bool Confirm(string message, string title);
    bool ConfirmRisk(string first, string second, string title);
}
public interface IFileDialogService
{
    string? SaveReport();
    string? OpenCs2();
}
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
