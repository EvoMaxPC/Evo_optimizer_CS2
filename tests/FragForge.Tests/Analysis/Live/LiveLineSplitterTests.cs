using FragForge.Core.Analysis.Live;
using Xunit;

namespace FragForge.Tests.Analysis.Live;

/// <summary>
/// Splitter de líneas en vivo: rearma líneas desde trozos arbitrarios.
/// Todos los valores esperados están calculados a mano en el comentario de cada caso.
/// </summary>
public sealed class LiveLineSplitterTests
{
    [Fact]
    public void Push_TextoCompleto_DevuelveDosLineas()
    {
        var splitter = new LiveLineSplitter();
        var lines = splitter.Push("a,b\r\nc,d\r\n");
        Assert.Equal(new[] { "a,b", "c,d" }, lines);
    }

    [Fact]
    public void Push_TrozosDeUnCaracter_DevuelveLasMismasLineas()
    {
        var splitter = new LiveLineSplitter();
        var all = new List<string>();
        foreach (var c in "a,b\r\nc,d\r\n")
            all.AddRange(splitter.Push(c.ToString()));
        Assert.Equal(new[] { "a,b", "c,d" }, all);
    }

    [Fact]
    public void Push_CrlfCortadoEntreTrozos_DevuelveDosLineas()
    {
        var splitter = new LiveLineSplitter();
        Assert.Empty(splitter.Push("a\r"));
        var lines = splitter.Push("\nb\r\n");
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void Push_CarriageReturnSuelto_NoParteLaLinea()
    {
        var splitter = new LiveLineSplitter();
        // El '\r' del medio no es terminador: queda dentro de la línea.
        var lines = splitter.Push("a\rb\r\n");
        Assert.Equal(new[] { "a\rb" }, lines);
    }

    [Fact]
    public void Complete_ParcialFinalDescartada_SinParcialFalse()
    {
        var splitter = new LiveLineSplitter();
        var lines = splitter.Push("x,y\r\nzz");
        Assert.Equal(new[] { "x,y" }, lines);
        // "zz" retenido sin terminar: se descarta y Complete avisa que había algo.
        Assert.True(splitter.Complete());

        var clean = new LiveLineSplitter();
        clean.Push("x,y\r\n");
        Assert.False(clean.Complete());

        // Retenido solo con espacios y '\r': no cuenta como parcial.
        var blank = new LiveLineSplitter();
        blank.Push("x,y\r\n  \r");
        Assert.False(blank.Complete());
    }

    [Fact]
    public void Push_LineaSobreElTope_SeDescartaYLaSiguienteSale()
    {
        var splitter = new LiveLineSplitter();
        // Línea de MaxLineLength+1 caracteres en dos trozos, luego una válida.
        var overlong = new string('x', LiveLineSplitter.MaxLineLength + 1);
        Assert.Empty(splitter.Push(overlong[..40_000]));
        var lines = splitter.Push(overlong[40_000..] + "\nvalid\n");
        Assert.Equal(new[] { "valid" }, lines);
        Assert.Equal(1L, splitter.OverlongLines);
    }
}
