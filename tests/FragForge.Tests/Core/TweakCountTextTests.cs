using FragForge.Actions.DependencyInjection;
using FragForge.Actions.Diagnostics;
using FragForge.Core.Conditions;
using FragForge.Core.Engine;
using FragForge.Core.Localization;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using FragForge.Tweaks.Validation;
using Xunit;

namespace FragForge.Tests.Core;

/// <summary>
/// Ronda 6 — el texto del contador al cambiar de perfil.
///
/// El bug: al pasar de Safe a Balanced/Competitive/Advanced la tabla sí cambiaba (7/9/12 filas) pero el
/// texto seguía en "Mostrando 3 de 30 tweaks en el perfil Safe.". La causa era que el mensaje de estado
/// del escaneo es TEXTO YA FORMADO con una copia del contador dentro
/// (MainViewModel.ScanAsync lo interpolaba una sola vez), y el cambio de perfil no lo rehacía.
///
/// Tests de las dos funciones PURAS que ahora gobiernan eso (<see cref="TweakCountFormatter"/> y
/// <see cref="ScanStatusText"/>), más el conteo real del catálogo. Nada de esto toca WPF: los tests son
/// net10.0 y no pueden referenciar la App.
/// </summary>
public sealed class TweakCountTextTests
{
    // ---------------------------------------------------------------- el texto

    [Theory]
    [InlineData("es-MX")]
    [InlineData("en-US")]
    public void CountText_SaysShownTotalAndProfile(string culture)
    {
        Assert.Equal("Mostrando 3 de 30 tweaks en el perfil Safe.", TweakCountFormatter.Format(TweakProfile.Safe, 3, 30));
        Assert.Equal("Mostrando 7 de 30 tweaks en el perfil Balanced.", TweakCountFormatter.Format(TweakProfile.Balanced, 7, 30));
        Assert.Equal("Mostrando 12 de 30 tweaks en el perfil Advanced.", TweakCountFormatter.Format(TweakProfile.Advanced, 12, 30));

        // El texto no depende de la cultura actual: los números son enteros y el perfil es el nombre del
        // enum. Se comprueba cambiando la cultura del hilo, que es lo que hace el combo de Idioma.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            Assert.Equal("Mostrando 7 de 30 tweaks en el perfil Balanced.", TweakCountFormatter.Format(TweakProfile.Balanced, 7, 30));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void CountText_ChangesWhenTheProfileChanges()
    {
        // El bug en una línea: mismos totales, distinto perfil => distinto texto.
        var safe = TweakCountFormatter.Format(TweakProfile.Safe, 3, 30);
        var balanced = TweakCountFormatter.Format(TweakProfile.Balanced, 7, 30);
        Assert.NotEqual(safe, balanced);
        Assert.Contains("Balanced", balanced, StringComparison.Ordinal);
        Assert.Contains("Safe", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void CountText_RejectsNegativeCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TweakCountFormatter.Format(TweakProfile.Safe, -1, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => TweakCountFormatter.Format(TweakProfile.Safe, 3, -1));
    }

    // ---------------------------------------------------------------- el mensaje congelado

    [Fact]
    public void ScanMessage_EmbedsTheCountTextItWasGiven()
    {
        var countText = TweakCountFormatter.Format(TweakProfile.Safe, 3, 30);
        var message = ScanStatusText.Format(30, countText);
        Assert.Equal("Escaneo completado: 30 evaluados; Mostrando 3 de 30 tweaks en el perfil Safe.", message);
    }

    /// <summary>
    /// El centro de la ronda: el mensaje que dejó el escaneo se reconoce como "el del escaneo" SOLO
    /// mientras siga intacto. Es lo que autoriza a rehacerlo al cambiar de perfil.
    /// </summary>
    [Fact]
    public void ScanMessage_IsRecognisedOnlyWhileUnchanged()
    {
        var scanMessage = ScanStatusText.Format(30, TweakCountFormatter.Format(TweakProfile.Safe, 3, 30));

        Assert.True(ScanStatusText.IsStillTheScanMessage(scanMessage, scanMessage), "el mensaje intacto debe reconocerse");

        // Reemplazado por el resultado de aplicar: NO se debe pisar.
        Assert.True(!ScanStatusText.IsStillTheScanMessage("Aplicación completada. Snapshot: snp-1", scanMessage),
            "el resultado de aplicar no es el mensaje del escaneo");
        Assert.True(!ScanStatusText.IsStillTheScanMessage("Listo", scanMessage));
        Assert.True(!ScanStatusText.IsStillTheScanMessage("Selecciona al menos un tweak.", scanMessage));

        // Sin escaneo previo no hay nada que rehacer.
        Assert.True(!ScanStatusText.IsStillTheScanMessage(scanMessage, null));
        Assert.True(!ScanStatusText.IsStillTheScanMessage(null, null));
    }

    /// <summary>
    /// El flujo del bug, extremo a extremo y sin UI: escanear en Safe, cambiar a Balanced, y comprobar que
    /// el mensaje reescrito anuncia Balanced y no Safe.
    /// </summary>
    [Fact]
    public void ChangingProfile_ReformatsTheScanMessage()
    {
        const int total = 30;
        const int evaluated = 30;

        // 1. Se escanea en Safe: el mensaje queda congelado con "3 de 30 ... Safe".
        var lastScanStatus = ScanStatusText.Format(evaluated, TweakCountFormatter.Format(TweakProfile.Safe, 3, total));
        var statusMessage = lastScanStatus;
        Assert.Contains("perfil Safe", statusMessage, StringComparison.Ordinal);

        // 2. Se cambia el perfil a Balanced: la tabla pasa a 7 filas.
        //    (Misma secuencia que MainViewModel.RebuildTweaks.)
        var shownAfter = 7;
        var countText = TweakCountFormatter.Format(TweakProfile.Balanced, shownAfter, total);
        if (ScanStatusText.IsStillTheScanMessage(statusMessage, lastScanStatus))
        {
            lastScanStatus = ScanStatusText.Format(evaluated, countText);
            statusMessage = lastScanStatus;
        }

        // 3. Lo que el usuario tiene que ver.
        Assert.Contains("perfil Balanced", statusMessage, StringComparison.Ordinal);
        Assert.Contains("7 de 30", statusMessage, StringComparison.Ordinal);
        Assert.True(!statusMessage.Contains("perfil Safe", StringComparison.Ordinal),
            $"el mensaje no puede seguir anunciando el perfil anterior: [{statusMessage}]");
    }

    /// <summary>
    /// Control negativo: si el mensaje ya no es el del escaneo, el cambio de perfil NO lo reescribe. Sin
    /// esto, el arreglo borraría el resultado de aplicar o un error.
    /// </summary>
    [Fact]
    public void ChangingProfile_DoesNotOverwriteAnotherMessage()
    {
        var lastScanStatus = ScanStatusText.Format(30, TweakCountFormatter.Format(TweakProfile.Safe, 3, 30));
        var statusMessage = "Aplicación completada. Snapshot: snp-1";

        if (ScanStatusText.IsStillTheScanMessage(statusMessage, lastScanStatus))
        {
            statusMessage = ScanStatusText.Format(30, TweakCountFormatter.Format(TweakProfile.Balanced, 7, 30));
        }

        Assert.Equal("Aplicación completada. Snapshot: snp-1", statusMessage);
    }

    // ---------------------------------------------------------------- catálogo REAL (R-N: solo relaciones)

    /// <summary>
    /// Con el catálogo REAL: el conteo no puede decrecer al subir de perfil y Safe tiene que ser
    /// estrictamente menor que Advanced. No se afirman tamaños exactos (R-N).
    /// </summary>
    [Fact]
    public async Task RealCatalog_CountsDoNotDecreaseAsTheProfileGetsBroader()
    {
        var catalog = await LoadRealCatalogAsync();

        var counts = new[] { TweakProfile.Safe, TweakProfile.Balanced, TweakProfile.Competitive, TweakProfile.Advanced }
            .Select(p => (Profile: p, Count: ProfileSelector.Select(catalog.Definitions, p).Count))
            .ToArray();

        var detail = string.Join(", ", counts.Select(c => $"{c.Profile}={c.Count}"));

        for (var i = 1; i < counts.Length; i++)
        {
            Assert.True(counts[i].Count >= counts[i - 1].Count,
                $"el conteo decrece al pasar de {counts[i - 1].Profile} a {counts[i].Profile} ({detail})");
        }

        Assert.True(counts[0].Count < counts[^1].Count,
            $"Safe debe tener estrictamente menos tweaks que Advanced ({detail})");

        // El texto se forma con esos conteos sin romperse.
        Assert.All(counts, c => Assert.Contains($"de {catalog.Definitions.Count} tweaks",
            TweakCountFormatter.Format(c.Profile, c.Count, catalog.Definitions.Count), StringComparison.Ordinal));
    }

    /// <summary>
    /// Catálogo REAL embebido, con el mismo armado que RealCatalogArchitectureTests. Fake en todo lo que
    /// toca la máquina: lo único que se lee es el catálogo que va dentro del binario.
    /// </summary>
    private static async Task<TweakCatalog> LoadRealCatalogAsync()
    {
        var schema = new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema());
        var registry = new FakeRegistry();
        var factory = new TweakActionFactory(
            registry, ProtectionPolicy.CreateDefault(), new FakeServiceController(), new FakeScheduledTaskStore(),
            new FakeNetworkAdapterController(), new FakeProcessRunner(), DiagnosticProbeFactory.CreateDefaults(registry));
        var validator = new TweakCatalogValidator(schema, new ConditionFactory(), factory);
        return await new JsonTweakCatalogLoader(validator).LoadEmbeddedAsync();
    }
}
