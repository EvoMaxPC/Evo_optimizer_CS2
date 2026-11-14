namespace FragForge.Core.Inventory;

/// <summary>
/// Interpretación del valor binario de <c>StartupApproved</c>, el mecanismo real que usa el
/// Administrador de tareas de Windows para habilitar/deshabilitar entradas de arranque.
///
/// El primer byte es una máscara de estado; el resto del valor es una marca de tiempo FILETIME.
/// Windows escribe 0x02, 0x06 o 0x0E cuando la entrada está HABILITADA y 0x03, 0x07, 0x0F cuando
/// está DESHABILITADA. Un valor ausente NO significa habilitado: significa que el usuario nunca
/// la ha tocado, y el estado efectivo lo da la propia entrada Run.
/// </summary>
public static class StartupApproval
{
    /// <summary>Bytes de estado que Windows documenta como "habilitado".</summary>
    public static readonly IReadOnlySet<int> EnabledBytes = new HashSet<int> { 0x02, 0x06, 0x0E };

    /// <summary>Bytes de estado que Windows documenta como "deshabilitado".</summary>
    public static readonly IReadOnlySet<int> DisabledBytes = new HashSet<int> { 0x03, 0x07, 0x0F };

    /// <summary>
    /// Devuelve <c>true</c>/<c>false</c> según el primer byte, o <c>null</c> si no se reconoce.
    /// Nunca inventa un estado: un byte desconocido es <c>null</c>.
    /// </summary>
    public static bool? IsEnabled(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0) return null;
        var first = value[0];
        if (EnabledBytes.Contains(first)) return true;
        if (DisabledBytes.Contains(first)) return false;
        return null;
    }

    public static bool? IsEnabled(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try { return IsEnabled(Convert.FromBase64String(base64)); }
        catch (FormatException) { return null; }
    }

    /// <summary>Explica el byte para el informe. No sustituye a <see cref="IsEnabled"/>.</summary>
    public static string Explain(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0) return "Sin valor de StartupApproved.";
        var state = IsEnabled(value);
        return state switch
        {
            true => $"Habilitado (primer byte 0x{value[0]:X2}).",
            false => $"Deshabilitado (primer byte 0x{value[0]:X2}).",
            null => $"Estado desconocido (primer byte 0x{value[0]:X2}); no se interpreta como habilitado."
        };
    }
}
