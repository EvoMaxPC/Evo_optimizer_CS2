using System.Text;

namespace FragForge.Core.Analysis.Live;

/// <summary>
/// Rearma líneas CSV desde trozos de texto de tamaño arbitrario (el stream de PresentMon entrega cortes
/// arbitrarios). Una línea termina con <c>'\n'</c>; si termina en <c>"\r\n"</c> se le quita el <c>'\r'</c>
/// final. Un <c>'\r'</c> suelto NO es terminador y queda dentro de la línea. Un CRLF cortado entre dos
/// trozos (<c>"...\r"</c> + <c>"\n..."</c>) funciona porque solo se parte ante <c>'\n'</c>.
/// El resto sin terminar se retiene hasta el próximo <see cref="Push"/>.
/// </summary>
public sealed class LiveLineSplitter
{
    /// <summary>Límite de seguridad, no validado. Tope de longitud de una línea retenida.</summary>
    public const int MaxLineLength = 65_536;

    private readonly StringBuilder _buffer = new();
    private bool _discarding;

    /// <summary>Líneas descartadas por superar <see cref="MaxLineLength"/> (una por línea).</summary>
    public long OverlongLines { get; private set; }

    /// <summary>
    /// Agrega un trozo y devuelve las líneas COMPLETAS (sin terminador). Retiene el resto sin terminar.
    /// Si la línea retenida supera <see cref="MaxLineLength"/>, se descarta entera hasta el siguiente
    /// <c>'\n'</c> y se cuenta una vez en <see cref="OverlongLines"/>; la línea siguiente se procesa normal.
    /// </summary>
    public IReadOnlyList<string> Push(string chunk)
    {
        var lines = new List<string>();
        foreach (var c in chunk)
        {
            if (c == '\n')
            {
                if (_discarding)
                {
                    _discarding = false;
                    _buffer.Clear();
                }
                else
                {
                    lines.Add(StripTrailingCarriageReturn(_buffer.ToString()));
                    _buffer.Clear();
                }
            }
            else if (!_discarding)
            {
                _buffer.Append(c);
                if (_buffer.Length > MaxLineLength)
                {
                    _discarding = true;
                    OverlongLines++;
                    _buffer.Clear();
                }
            }
        }
        return lines;
    }

    /// <summary>
    /// Fin del stream: DESCARTA la línea parcial retenida (no se entrega al parser: una fila cortada podría
    /// parsear como un frametime truncado pero "válido"). Devuelve <c>true</c> si había algo no vacío,
    /// ignorando solo <c>'\r'</c> y espacios.
    /// </summary>
    public bool Complete()
    {
        var retained = _buffer.ToString();
        _buffer.Clear();
        _discarding = false;
        return retained.Trim(' ', '\r').Length > 0;
    }

    private static string StripTrailingCarriageReturn(string line) =>
        line.EndsWith('\r') ? line[..^1] : line;
}
