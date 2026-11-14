using System.Text.Json;
using FragForge.Actions.DependencyInjection;
using FragForge.Actions.Diagnostics;
using FragForge.Core.Conditions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using FragForge.Tweaks.Validation;
using Xunit;

namespace FragForge.Tests.Latency;

/// <summary>
/// Capa 7.0/7.1 — el CONTRATO de los tweaks <c>latency.*</c> y de los dos <c>gpu.*</c> nuevos.
///
/// Estos tweaks ya existían; lo que se comprueba aquí es que cada valor, cada tipo de registro y cada
/// bandera son los que dice el contrato de la ronda. Si alguien cambia un REG_SZ por un DWORD o escribe en
/// la clave equivocada, esto falla.
///
/// Se afirma sobre el JSON del catálogo REAL (no sobre una copia), así que una edición del catálogo que
/// rompa el contrato se detecta. No se afirma el tamaño del catálogo (R-N).
/// </summary>
public sealed class LatencyContractTests
{
    private static async Task<TweakCatalog> LoadAsync() => await LoadRealCatalogAsync();

    // ---------------------------------------------------------------- 7.1 game-mode-on

    [Fact]
    public async Task GameModeOn_WritesBothValuesAsDwordOne_InHkcuGameBar()
    {
        var definition = (await LoadAsync()).Get("latency.game-mode-on");
        var values = RegistryValues(definition);

        Assert.Equal(2, values.Count);
        Assert.All(values, v => Assert.Equal(@"HKCU\Software\Microsoft\GameBar", v.Key));
        Assert.All(values, v => Assert.Equal("DWord", v.Kind));
        Assert.All(values, v => Assert.Equal(1, v.IntValue));
        Assert.Contains(values, v => v.Name == "AllowAutoGameMode");
        Assert.Contains(values, v => v.Name == "AutoGameModeEnabled");
    }

    // ---------------------------------------------------------------- 7.1 mouse-acceleration-off

    /// <summary>
    /// Los tres valores del contrato son **REG_SZ** (cadenas), no DWORD. Si alguien los cambia a DWord,
    /// Windows no los interpreta igual y el ajuste deja de funcionar.
    /// </summary>
    [Fact]
    public async Task MouseAcceleration_UsesStringType_ForTheThreeContractedValues()
    {
        var definition = (await LoadAsync()).Get("latency.mouse-acceleration-off");
        var values = RegistryValues(definition);

        Assert.All(values, v => Assert.Equal(@"HKCU\Control Panel\Mouse", v.Key));

        foreach (var name in new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" })
        {
            var value = Assert.Single(values, v => v.Name == name);
            Assert.Equal("String", value.Kind);
            Assert.Equal("0", value.StringValue);
        }
    }

    /// <summary>
    /// Membresía en los CUATRO perfiles, comprobada con <see cref="FragForge.Core.Engine.ProfileSelector.Allows"/>
    /// y no con la tabla de <c>CatalogTests</c>: así el test falla si cambia la REGLA de selección, no solo el
    /// JSON. El ratón no declara <c>profiles</c> (ausente, no <c>[]</c>), no lleva tag <c>opt-in</c> y su riesgo
    /// es Safe con evidencia Low: por eso entra en los cuatro, incluido el más conservador.
    /// </summary>
    [Fact]
    public async Task MouseAcceleration_IsInAllFourProfiles_ByTheActualSelectorRule()
    {
        var definition = (await LoadAsync()).Get("latency.mouse-acceleration-off");

        Assert.Null(definition.Profiles);                                   // ausente: rige la regla por riesgo
        Assert.DoesNotContain("opt-in", definition.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(RiskLevel.Safe, definition.Risk);

        foreach (var profile in Enum.GetValues<TweakProfile>())
            Assert.True(FragForge.Core.Engine.ProfileSelector.Allows(profile, definition),
                $"El ratón debería entrar en {profile}: es el tweak de input con menos riesgo del catálogo.");
    }

    /// <summary>
    /// El ratón escribe HKCU. Se comprueba que NO se toca nada fuera del perfil del usuario: si algún día
    /// pasara a HKLM, exigiría admin de otra forma y el aviso sería otro.
    /// </summary>
    [Fact]
    public async Task MouseAcceleration_OnlyWritesInTheCurrentUserHive()
    {
        var definition = (await LoadAsync()).Get("latency.mouse-acceleration-off");

        Assert.All(RegistryValues(definition), v => Assert.StartsWith(@"HKCU\", v.Key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Honestidad del efecto: CS2 con raw input no pasa por el puntero de Windows. El JSON tiene que decirlo,
    /// y el EvidenceLevel tiene que reflejarlo.
    /// </summary>
    [Fact]
    public async Task MouseAcceleration_DeclaresThatTheEffectIsDesktopOnly()
    {
        var definition = (await LoadAsync()).Get("latency.mouse-acceleration-off");

        var text = string.Join(" ", definition.Notes!.EsMx, definition.Notes.EnUs, definition.ExpectedImpact.InputLatency);
        Assert.Contains("raw input", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("escritorio", definition.Notes.EsMx, StringComparison.OrdinalIgnoreCase);
        Assert.True(definition.Evidence is EvidenceLevel.Low or EvidenceLevel.Experimental,
            $"el efecto no está medido en CS2; EvidenceLevel no debería ser {definition.Evidence}");
    }

    /// <summary>
    /// Las curvas SmoothMouse se guardan como **5 puntos × 8 bytes**. Cada punto es un valor en formato
    /// **16.16 fixed-point** en los 4 bytes bajos; los 4 bytes altos son cero.
    ///
    /// Formato verificado en fuente externa (curva canónica de Windows: `c0,cc,0c,00` = 12.7998), no supuesto.
    /// Leerlo como dos `int32` da (x, 0) y hace creer que la curva es degenerada: eso fue exactamente el error
    /// de la primera pasada de esta auditoría.
    ///
    /// Lo que se exige aquí es lo que promete la descripción: **curva lineal 1:1**, es decir pendiente
    /// constante = 1.0 (Y y X son la misma curva). Con pendiente 4.375 la sensibilidad quedaría multiplicada.
    /// </summary>
    [Fact]
    public async Task MouseAcceleration_CurvesAreLinearOneToOne()
    {
        var definition = (await LoadAsync()).Get("latency.mouse-acceleration-off");
        var values = RegistryValues(definition).Where(v => v.Name.StartsWith("SmoothMouse", StringComparison.Ordinal)).ToArray();

        Assert.Equal(2, values.Length);
        var x = DecodeCurve(values.Single(v => v.Name == "SmoothMouseXCurve").BinaryValue!);
        var y = DecodeCurve(values.Single(v => v.Name == "SmoothMouseYCurve").BinaryValue!);

        Assert.Equal(5, x.Length);
        Assert.Equal(5, y.Length);
        Assert.Equal(0d, x[0]);          // el primer punto es (0,0) por definición
        Assert.Equal(0d, y[0]);

        // Lineal 1:1: X e Y son la MISMA curva y la pendiente es 1 en cada punto.
        for (var i = 1; i < 5; i++)
        {
            Assert.True(x[i] > 0, "la curva debe crecer: un punto en 0 no es una curva lineal");
            Assert.Equal(1.0, y[i] / x[i], 6);
        }
    }

    /// <summary>
    /// Decodifica los 5 puntos en 16.16 fixed-point. Los 4 bytes altos de cada punto tienen que ser cero:
    /// si no, el valor se leyó con el formato equivocado.
    /// </summary>
    private static double[] DecodeCurve(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        Assert.Equal(40, bytes.Length); // 5 puntos × 8 bytes

        var points = new double[5];
        for (var i = 0; i < 5; i++)
        {
            // 16.16 fixed point en los 4 bytes bajos, little-endian.
            var low = (uint)(bytes[i * 8] | (bytes[i * 8 + 1] << 8) | (bytes[i * 8 + 2] << 16) | (bytes[i * 8 + 3] << 24));
            Assert.Equal(0, bytes[i * 8 + 4]);
            Assert.Equal(0, bytes[i * 8 + 5]);
            Assert.Equal(0, bytes[i * 8 + 6]);
            Assert.Equal(0, bytes[i * 8 + 7]);
            points[i] = (low >> 16) + (low & 0xFFFF) / 65536.0;
        }
        return points;
    }

    // ---------------------------------------------------------------- 7.1 game-dvr-capture-off

    [Fact]
    public async Task GameDvr_WritesBothUserValuesAndTheMachinePolicy()
    {
        var definition = (await LoadAsync()).Get("latency.game-dvr-capture-off");
        var values = RegistryValues(definition).ToDictionary(v => v.Key + "\\" + v.Name, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(0, values[@"HKCU\System\GameConfigStore\GameDVR_Enabled"].IntValue);
        Assert.Equal(0, values[@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\AppCaptureEnabled"].IntValue);
        // La política de máquina está INCLUIDA en este tweak (no como tweak separado): se fija aquí para
        // documentar la decisión y que un cambio accidental se note.
        Assert.Equal(0, values[@"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR\AllowGameDVR"].IntValue);
    }

    // ---------------------------------------------------------------- 7.1 global-timer-resolution

    [Fact]
    public async Task GlobalTimerResolution_WritesOneDwordOne_AndRequiresReboot()
    {
        var definition = (await LoadAsync()).Get("latency.global-timer-resolution");
        var value = Assert.Single(RegistryValues(definition));

        Assert.Equal(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", value.Key);
        Assert.Equal("GlobalTimerResolutionRequests", value.Name);
        Assert.Equal(1, value.IntValue);
        Assert.True(definition.RequiresReboot, "el ajuste solo surte efecto tras reiniciar");
    }

    /// <summary>
    /// **El punto crítico de 7.1**: la clave <c>Session Manager\kernel</c> NO puede caer bajo el prefijo
    /// protegido <c>Session Manager\Memory Management</c>. Se comprueba con la política REAL, no a mano, y
    /// se comprueba también el caso positivo para demostrar que la comprobación no es un no-op.
    /// </summary>
    [Fact]
    public async Task GlobalTimerResolution_KeyIsNotUnderTheProtectedMemoryManagementPrefix()
    {
        var definition = (await LoadAsync()).Get("latency.global-timer-resolution");
        var key = Assert.Single(RegistryValues(definition)).Key;
        var policy = ProtectionPolicy.CreateDefault();

        // No lanza: la clave es hermana de Memory Management, no hija.
        policy.EnsureRegistryAllowed(key, "GlobalTimerResolutionRequests");

        // Control positivo: la misma comprobación SÍ rechaza una clave bajo el prefijo protegido.
        Assert.Throws<ProtectedTargetException>(() =>
            policy.EnsureRegistryAllowed(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "ClearPageFileAtShutdown"));
    }

    // ---------------------------------------------------------------- 6.3 gpu.*

    [Fact]
    public async Task HagsToggle_OnlyWritesOneOrTwo_AndIsOptInWithReboot()
    {
        var definition = (await LoadAsync()).Get("gpu.hags-toggle");
        var value = Assert.Single(RegistryValues(definition));

        Assert.Equal(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", value.Key);
        Assert.Equal("HwSchMode", value.Name);
        Assert.Equal("DWord", value.Kind);
        // R-L: solo 1 o 2. Cualquier otro valor se leería como Unknown.
        Assert.True(value.IntValue is 1 or 2, $"HwSchMode solo admite 1 o 2, no {value.IntValue}");
        Assert.True(definition.RequiresReboot);
        // profiles: [] = presente y vacío = "en ningún perfil", solo aplicable por id.
        Assert.NotNull(definition.Profiles);
        Assert.Empty(definition.Profiles);
        Assert.Contains("opt-in", definition.Tags);
        Assert.True(definition.Evidence == EvidenceLevel.Low, "sin fuente propia: evidencia baja");
    }

    [Fact]
    public async Task IntelGraphicsPlan_WritesOnlyTheEnumeratedMaximumValue_ByGuid()
    {
        var definition = (await LoadAsync()).Get("gpu.intel-graphics-power-plan-max");
        var parameters = JsonSerializer.SerializeToElement(definition.Actions.Single().Parameters);

        Assert.Equal("PowerPlan", definition.Actions.Single().Kind);
        Assert.Equal("settingsOnly", parameters.GetProperty("mode").GetString());
        Assert.True(parameters.GetProperty("skipUnenumeratedSettings").GetBoolean(), "si el GUID no se enumera, se salta (R-L)");

        var setting = parameters.GetProperty("settings")[0];
        // Se escribe POR GUID (R-C): nunca por la etiqueta "Intel(R) Graphics Settings".
        Assert.Equal("44f3beca-a7c0-460e-9df2-bb8b99e0cba6", setting.GetProperty("subgroupGuid").GetString());
        Assert.Equal("3619c3f2-afb2-4afc-b0e9-e7fef372de36", setting.GetProperty("settingGuid").GetString());
        Assert.Equal(2, setting.GetProperty("acValue").GetInt32());
        Assert.Equal(2, setting.GetProperty("dcValue").GetInt32());
    }

    // ---------------------------------------------------------------- 7.3 conteos

    /// <summary>
    /// Los dos tweaks nuevos son <c>profiles: []</c> (solo por id), así que NO alteran los conteos de
    /// perfil. Se afirma la relación no decreciente y que Safe &lt; Advanced, no los tamaños exactos (R-N).
    /// </summary>
    [Fact]
    public async Task NewTweaks_DoNotJoinAnyProfile_SoTheCountsStayOrdered()
    {
        var catalog = await LoadAsync();

        var counts = new[] { TweakProfile.Safe, TweakProfile.Balanced, TweakProfile.Competitive, TweakProfile.Advanced }
            .Select(p => (p, n: FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, p).Count))
            .ToArray();

        for (var i = 1; i < counts.Length; i++)
            Assert.True(counts[i].n >= counts[i - 1].n, $"el conteo decrece: {string.Join(", ", counts.Select(c => $"{c.p}={c.n}"))}");
        Assert.True(counts[0].n < counts[^1].n, "Safe debe ser estrictamente menor que Advanced");

        // Y ninguno de los dos nuevos aparece en ningún perfil.
        foreach (var id in new[] { "gpu.hags-toggle", "gpu.intel-graphics-power-plan-max" })
        {
            var definition = catalog.Get(id);
            Assert.DoesNotContain(definition.Id, FragForge.Core.Engine.ProfileSelector.Select(catalog.Definitions, TweakProfile.Advanced).Select(d => d.Id));
        }
    }

    // ---------------------------------------------------------------- helpers

    private sealed record RegistryValue(string Key, string Name, string Kind, int? IntValue, string? StringValue, string? BinaryValue);

    private static IReadOnlyList<RegistryValue> RegistryValues(TweakDefinition definition)
    {
        var action = Assert.Single(definition.Actions);
        Assert.Equal("Registry", action.Kind);
        var values = JsonSerializer.SerializeToElement(action.Parameters).GetProperty("values");
        return values.EnumerateArray().Select(v => new RegistryValue(
            v.GetProperty("key").GetString()!,
            v.GetProperty("name").GetString()!,
            v.GetProperty("kind").GetString()!,
            v.GetProperty("value").ValueKind == JsonValueKind.Number ? v.GetProperty("value").GetInt32() : null,
            v.GetProperty("value").ValueKind == JsonValueKind.String ? v.GetProperty("value").GetString() : null,
            v.GetProperty("value").ValueKind == JsonValueKind.String && v.GetProperty("kind").GetString() == "Binary"
                ? v.GetProperty("value").GetString() : null)).ToArray();
    }

    private static async Task<TweakCatalog> LoadRealCatalogAsync()
    {
        var schema = new JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema());
        var registry = new FakeRegistry();
        var factory = new TweakActionFactory(
            registry, ProtectionPolicy.CreateDefault(), new FakeServiceController(), new FakeScheduledTaskStore(),
            new FakeNetworkAdapterController(), new FakeProcessRunner(), DiagnosticProbeFactory.CreateDefaults(registry),
            adapterResolver: new FragForge.Actions.Network.UnsupportedNetworkAdapterResolver());
        var validator = new TweakCatalogValidator(schema, new ConditionFactory(), factory);
        return await new JsonTweakCatalogLoader(validator).LoadEmbeddedAsync();
    }
}
