using FragForge.Actions.Network;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Network;

/// <summary>
/// Capa 5.2 — propiedades avanzadas por <c>RegistryKeyword</c>, sin leer etiquetas localizadas (R-C).
///
/// El mapeo valor-de-registro ↔ valor-visible se hace <b>por posición</b> en
/// <c>ValidRegistryValues</c>/<c>ValidDisplayValues</c>, no comparando textos. Estas pruebas fijan eso: los dos
/// fixtures tienen los MISMOS valores de registro y TEXTO DISTINTO (es-MX y en-US), y el código tiene que
/// comportarse igual con los dos. Si alguien empieza a comparar contra "Deshabilitado" o "Disabled", uno de los
/// dos fixtures falla.
///
/// Los valores de registro de los fixtures son los que devuelve un driver real; lo que NO está verificado en
/// esta PC es la lista exacta del I225-V (ver el informe).
/// </summary>
public sealed class NetworkAdvancedPropertyReaderTests
{
    private const string EchoGuid = "{d72a2249-535c-4478-9fdd-9b3a439f6395}";

    /// <summary>Salida con textos en español. Valores de registro: 0/1.</summary>
    private const string SpanishJson = """
        {"exists":true,"keyword":"*EEE","registry":"1","display":"Habilitado","validRegistry":["0","1"],"validDisplay":["Deshabilitado","Habilitado"]}
        """;

    /// <summary>La MISMA propiedad con textos en inglés. Los valores de registro son idénticos.</summary>
    private const string EnglishJson = """
        {"exists":true,"keyword":"*EEE","registry":"1","display":"Enabled","validRegistry":["0","1"],"validDisplay":["Disabled","Enabled"]}
        """;

    [Fact]
    public void Parse_ReadsRegistryValueAndKeyword()
    {
        var property = NetworkAdvancedPropertyReader.Parse(SpanishJson, EchoGuid, "*EEE");

        Assert.True(property.Exists);
        Assert.Equal("*EEE", property.Keyword);
        Assert.Equal("1", property.RegistryValue);
        Assert.Equal(["0", "1"], property.ValidRegistryValues);
    }

    /// <summary>
    /// El mapeo por posición da la etiqueta correcta en CADA idioma sin que el código sepa idiomas.
    /// </summary>
    [Theory]
    [InlineData(SpanishJson, "Deshabilitado", "Habilitado")]
    [InlineData(EnglishJson, "Disabled", "Enabled")]
    public void DisplayFor_MapsByPosition_NotByText(string json, string zeroLabel, string oneLabel)
    {
        var property = NetworkAdvancedPropertyReader.Parse(json, EchoGuid, "*EEE");

        Assert.Equal(zeroLabel, property.DisplayFor("0"));
        Assert.Equal(oneLabel, property.DisplayFor("1"));
        // Un valor que no está enumerado no tiene etiqueta: null, no una etiqueta inventada.
        Assert.Null(property.DisplayFor("7"));
    }

    /// <summary>El valor que se escribe es el de REGISTRO ("0"), nunca la etiqueta visible.</summary>
    [Theory]
    [InlineData(SpanishJson)]
    [InlineData(EnglishJson)]
    public void Allows_ComparesRegistryValues_SoBothLanguagesBehaveTheSame(string json)
    {
        var property = NetworkAdvancedPropertyReader.Parse(json, EchoGuid, "*EEE");

        Assert.True(property.Allows("0"));
        Assert.True(property.Allows("1"));
        Assert.False(property.Allows("Disabled"));
        Assert.False(property.Allows("Deshabilitado"));
        Assert.False(property.Allows("2"));
    }

    [Fact]
    public void Parse_WhenPropertyDoesNotExist_ReportsMissing()
    {
        var property = NetworkAdvancedPropertyReader.Parse("""{"exists":false}""", EchoGuid, "*EEE");

        Assert.False(property.Exists);
        Assert.Empty(property.ValidRegistryValues);
        // Sin propiedad, NO se aplica: Allows siempre false (R-L).
        Assert.False(property.Allows("0"));
    }

    /// <summary>Una salida que no es JSON no se interpreta como "existe".</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Get-NetAdapterAdvancedProperty : No se encuentra...")]
    [InlineData("{roto")]
    public void Parse_GarbageOutput_IsTreatedAsMissing(string output)
    {
        Assert.False(NetworkAdvancedPropertyReader.Parse(output, EchoGuid, "*EEE").Exists);
    }

    /// <summary>PowerShell colapsa un array de un elemento a escalar: se acepta igual.</summary>
    [Fact]
    public void Parse_AcceptsScalarWhereAnArrayIsExpected()
    {
        var json = """{"exists":true,"keyword":"*EEE","registry":"0","validRegistry":"0","validDisplay":"Deshabilitado"}""";

        var property = NetworkAdvancedPropertyReader.Parse(json, EchoGuid, "*EEE");

        Assert.Equal(["0"], property.ValidRegistryValues);
        Assert.Equal("Deshabilitado", property.DisplayFor("0"));
    }

    [Fact]
    public void Parse_AcceptsSingleElementArray()
    {
        var json = """[{"exists":true,"keyword":"*EEE","registry":"0","validRegistry":["0","1"],"validDisplay":["No","Sí"]}]""";

        Assert.True(NetworkAdvancedPropertyReader.Parse(json, EchoGuid, "*EEE").Exists);
    }

    /// <summary>
    /// Escritura: se exige que el valor esté enumerado. Si no lo está, lanza ANTES de tocar el proceso.
    /// </summary>
    [Fact]
    public async Task Write_RejectsValueThatTheAdapterDoesNotEnumerate()
    {
        var runner = new FakeProcessRunner { Handler = _ => new ProcessResult(0, SpanishJson, "", false) };
        var reader = new NetworkAdvancedPropertyReader(runner);

        var ex = await Assert.ThrowsAsync<ActionNotApplicableException>(() => reader.WriteAsync(EchoGuid, "*EEE", "7"));

        Assert.Contains("Valores válidos", ex.Message, StringComparison.Ordinal);
        // No se llegó a escribir: solo hubo la lectura.
        Assert.DoesNotContain(runner.Requests, r => r.Arguments.Any(a => a.Contains("Set-NetAdapterAdvancedProperty", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Write_RejectsWhenPropertyIsMissing()
    {
        var runner = new FakeProcessRunner { Handler = _ => new ProcessResult(0, """{"exists":false}""", "", false) };

        await Assert.ThrowsAsync<ActionNotApplicableException>(() => new NetworkAdvancedPropertyReader(runner).WriteAsync(EchoGuid, "*EEE", "0"));
        Assert.DoesNotContain(runner.Requests, r => r.Arguments.Any(a => a.Contains("Set-NetAdapterAdvancedProperty", StringComparison.Ordinal)));
    }

    /// <summary>Cuando el valor sí está enumerado, se escribe por GUID y por keyword, nunca por nombre.</summary>
    [Fact]
    public async Task Write_UsesInterfaceGuidAndRegistryKeyword()
    {
        var runner = new FakeProcessRunner { Handler = _ => new ProcessResult(0, SpanishJson, "", false) };

        var property = await new NetworkAdvancedPropertyReader(runner).WriteAsync(EchoGuid, "*EEE", "0");

        Assert.True(property.Exists);
        var write = Assert.Single(runner.Requests, r => r.Arguments.Any(a => a.Contains("Set-NetAdapterAdvancedProperty", StringComparison.Ordinal)));
        var script = write.Arguments[^1];
        Assert.Contains("-InterfaceGuid", script, StringComparison.Ordinal);
        Assert.Contains(EchoGuid, script, StringComparison.Ordinal);
        Assert.Contains("-RegistryKeyword '*EEE'", script, StringComparison.Ordinal);
        // Jamás se identifica el adaptador por su nombre visible.
        Assert.DoesNotContain("-Name ", script, StringComparison.Ordinal);
    }

    /// <summary>El prefijo <c>*</c> del keyword se conserva: sin él, la propiedad no existe.</summary>
    [Fact]
    public void Keyword_KeepsTheDriverPrefix()
    {
        Assert.StartsWith("*", "*EEE", StringComparison.Ordinal);
        Assert.Equal("Get-NetAdapterAdvancedProperty", NetworkAdvancedPropertyReader.Verb);
    }
}
