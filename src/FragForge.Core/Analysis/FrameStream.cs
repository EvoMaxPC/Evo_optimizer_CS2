namespace FragForge.Core.Analysis;

/// <summary>
/// El resultado de leer un CSV de frames: una entrada por cada par (aplicación, swapchain) encontrado,
/// más los contadores de lo que se descartó y por qué.
/// </summary>
/// <remarks>
/// <para>
/// Deliberadamente NO elige una swapchain. El parser histórico de Actions
/// (<c>PresentMonCsvParser</c>, usado por la captura en vivo) elige la "mejor" y devuelve una sola serie,
/// porque su caso de uso es una corrida; aquí el caso de uso es COMPARAR, y elegir una swapchain en
/// silencio cambiaría el conjunto de datos. Cada par se reporta por separado y el llamador decide.
/// </para>
/// <para>
/// Cuando el CSV no sirve (sin la columna principal, vacío), <see cref="UnavailableReason"/> dice por qué
/// en texto, en vez de lanzar. Un archivo ilegible SÍ lanza: eso es un error del llamador, no un dato.
/// </para>
/// </remarks>
public sealed record FrameStream
{
    /// <summary>Una serie por (aplicación, swapchain). Orden determinista: por app y luego por swapchain.</summary>
    public IReadOnlyList<FrameSeriesKey> Series { get; init; } = [];

    /// <summary>Filas cuya métrica principal era <c>NA</c> (o no parseable). Se cuentan y se excluyen; jamás valen 0.</summary>
    public int NaFrames { get; init; }

    /// <summary>Líneas con menos celdas que la columna principal, o no parseables en esa celda. Se cuentan.</summary>
    public int MalformedLines { get; init; }

    /// <summary>Columnas del encabezado tal cual, para poder citar qué se leyó por nombre.</summary>
    public IReadOnlyList<string> HeaderColumns { get; init; } = [];

    /// <summary>Nombre real de la columna principal usada, o null si no había ninguna.</summary>
    public string? PrimaryColumn { get; init; }

    /// <summary>Nombre real de la columna secundaria, o null si el encabezado no la traía.</summary>
    public string? SecondaryColumn { get; init; }

    /// <summary>Motivo textual de por qué NO hay datos utilizables. Null si hay al menos una serie con frames.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>Hay al menos una serie con frames utilizables.</summary>
    public bool HasData => Series.Any(s => s.Primary.Count > 0);
}

/// <summary>
/// Los frames de UNA (aplicación, swapchain). Las listas están alineadas por índice: <c>Primary[i]</c> y
/// <c>Secondary[i]</c> son el mismo frame. <c>Secondary</c> puede ser null entero si la columna no existe.
/// </summary>
/// <param name="Application">Valor de <c>Application</c>, o "(sin aplicación)" si el CSV no la trae.</param>
/// <param name="SwapChain">Valor de <c>SwapChainAddress</c>, o "(sin swapchain)" si no la trae.</param>
/// <param name="Primary">Frametimes de la métrica principal, en ms, solo frames con dato.</param>
/// <param name="Secondary">
/// Frametimes de la métrica secundaria, alineados por índice con <paramref name="Primary"/>; null si la
/// columna no está en el encabezado. Un elemento null significa celda NA: ese frame no tiene ese dato.
/// </param>
public sealed record FrameSeriesKey(string Application, string SwapChain, IReadOnlyList<double> Primary, IReadOnlyList<double?>? Secondary);
