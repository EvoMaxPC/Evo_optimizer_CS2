namespace FragForge.Cli;

/// <summary>
/// Consola mínima para las preguntas interactivas del CLI. Existe para poder PROBAR una pregunta sin
/// tocar <see cref="Console"/>, que es estado global del proceso y en xUnit se comparte entre tests.
/// </summary>
/// <remarks>
/// Vive en FragForge.Cli y no en Core/Abstractions a propósito: no es un contrato del motor, es una
/// necesidad de la capa de presentación, y añadirla a Abstractions habría cambiado interfaces que esta
/// ronda no debe tocar.
/// </remarks>
public interface IConsolePrompt
{
    /// <summary>Escribe una línea de salida normal (no es una pregunta).</summary>
    void WriteLine(string text);

    /// <summary>Escribe una línea de error / aviso.</summary>
    void WriteWarning(string text);

    /// <summary>
    /// Hace una pregunta y devuelve la respuesta SIN recortar (el llamador decide qué hacer con espacios).
    /// Devuelve null si la entrada se acabó (EOF): no se reintenta para siempre.
    /// </summary>
    string? Ask(string question);

    /// <summary>Confirmación de sí/no. Null en EOF.</summary>
    bool? Confirm(string question);
}

/// <summary>Implementación real sobre <see cref="Console"/>.</summary>
public sealed class ConsolePrompt : IConsolePrompt
{
    public void WriteLine(string text) => Console.WriteLine(text);
    public void WriteWarning(string text) => Console.Error.WriteLine(text);

    public string? Ask(string question)
    {
        Console.Write(question);
        return Console.ReadLine();
    }

    public bool? Confirm(string question)
    {
        var answer = Ask(question + " [s/N] ");
        if (answer is null) return null;
        var trimmed = answer.Trim();
        return trimmed.Equals("s", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("si", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("sí", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("y", StringComparison.OrdinalIgnoreCase);
    }
}
