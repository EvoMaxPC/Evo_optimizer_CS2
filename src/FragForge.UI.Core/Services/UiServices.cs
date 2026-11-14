namespace FragForge.UI.Core.Services;

/// <summary>
/// Confirmación al usuario (Capa 11.2). La interfaz vive aquí, sin WPF, para que el ViewModel sea testeable
/// en <c>net10.0</c>; la implementación concreta (MessageBox) es de la App.
/// </summary>
public interface IConfirmationService
{
    bool Confirm(string message, string title);

    /// <summary>Dos confirmaciones seguidas para acciones de riesgo alto. Devuelve true solo si acepta las dos.</summary>
    bool ConfirmRisk(string first, string second, string title);
}

/// <summary>Selección de archivos. Misma separación que <see cref="IConfirmationService"/>.</summary>
public interface IFileDialogService
{
    string? SaveReport();
    string? OpenCs2();
}
