using System.ComponentModel;
using FragForge.Core.Abstractions;
using FragForge.Core.Localization;
using FragForge.Core.Models;
using FragForge.Tests.Core;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using FragForge.Tweaks.Localization;
using FragForge.UI.Core;
using FragForge.UI.Core.Services;
using Xunit;

namespace FragForge.Tests.UI;

/// <summary>
/// Capa 11.2: los ViewModels viven en un proyecto <c>net10.0</c> sin WPF, así que se pueden probar de verdad.
/// Antes estaban dentro de la App (net10.0-windows) y el proyecto de tests no podía referenciarlos.
/// </summary>
public sealed class MainViewModelTests
{
    /// <summary>Espía de <see cref="INotifyPropertyChanged"/>: guarda los nombres notificados, en orden.</summary>
    private sealed class PropertySpy : IDisposable
    {
        private readonly INotifyPropertyChanged _source;
        public List<string> Names { get; } = [];
        public PropertySpy(INotifyPropertyChanged source)
        {
            _source = source;
            _source.PropertyChanged += OnChanged;
        }
        private void OnChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is { Length: > 0 } name) Names.Add(name); }
        public void Clear() => Names.Clear();
        public bool Saw(string name) => Names.Contains(name, StringComparer.Ordinal);
        public void Dispose() => _source.PropertyChanged -= OnChanged;
    }

    private sealed class NoDialogs : IConfirmationService, IFileDialogService
    {
        public bool Confirm(string message, string title) => false;
        public bool ConfirmRisk(string first, string second, string title) => false;
        public string? SaveReport() => null;
        public string? OpenCs2() => null;
    }

    /// <summary>
    /// Construye el ViewModel sobre el catálogo EMBEBIDO real: es lo que se quiere probar (que los conteos y
    /// los textos salgan bien con el catálogo de verdad), no una fixture de tres tweaks.
    /// </summary>
    private static async Task<MainViewModel> CreateAsync(string culture = "es-MX")
    {
        var localizer = await LocalizationLoader.LoadAsync(culture);
        var catalog = await new JsonTweakCatalogLoader(NewValidator()).LoadEmbeddedAsync();
        var dialogs = new NoDialogs();
        return new MainViewModel(
            new StubEngine(),
            catalog,
            localizer,
            new StubReports(),
            new FragForge.Core.Localization.UiLogSink(),
            dialogs,
            dialogs,
            new StubPreflight(),
            new FakeHardwareProfiler(),
            new StubContext());
    }

    /// <summary>
    /// El listado inicial sale del perfil por defecto y el contador dice cuántos de cuántos. No se fija el
    /// tamaño exacto del catálogo (R-N): se compara contra el propio catálogo.
    /// </summary>
    [Fact]
    public async Task TheInitialListing_MatchesTheSelectedProfile_AndTheCounterSaysSo()
    {
        using var vm = await CreateAsync();
        var all = vm.Tweaks.Count;
        var catalogCount = (await new JsonTweakCatalogLoader(NewValidator()).LoadEmbeddedAsync()).Definitions.Count;

        Assert.True(all > 0);
        Assert.True(all <= catalogCount);
        Assert.Contains($"Mostrando {all} de {catalogCount} tweaks", vm.TweakCountText, StringComparison.Ordinal);
        Assert.Contains(vm.SelectedProfile.ToString(), vm.TweakCountText, StringComparison.Ordinal);
    }

    /// <summary>Subir de perfil nunca puede mostrar MENOS tweaks que un perfil más conservador.</summary>
    [Theory]
    [InlineData(TweakProfile.Safe, TweakProfile.Balanced)]
    [InlineData(TweakProfile.Balanced, TweakProfile.Competitive)]
    [InlineData(TweakProfile.Competitive, TweakProfile.Advanced)]
    public async Task RaisingTheProfile_NeverShowsFewerTweaks(TweakProfile lower, TweakProfile higher)
    {
        using var vm = await CreateAsync();

        vm.SelectedProfile = lower;
        var low = vm.Tweaks.Count;
        vm.SelectedProfile = higher;
        var high = vm.Tweaks.Count;

        Assert.True(high >= low, $"{higher} mostró {high} y {lower} mostró {low}.");
    }

    /// <summary>El contador está en dos idiomas y cada uno usa SU texto.</summary>
    [Fact]
    public async Task TheCounterText_IsLocalizedInBothCultures()
    {
        using var es = await CreateAsync("es-MX");
        using var en = await CreateAsync("en-US");

        Assert.Contains("Mostrando", es.TweakCountText, StringComparison.Ordinal);
        Assert.Contains("de", es.TweakCountText, StringComparison.Ordinal);
        // El en-US conserva el mismo formato del formateador (que no se traduce), pero el resto de la UI sí.
        Assert.NotEqual(es.Localizer["nav.apply"], en.Localizer["nav.apply"]);
    }

    /// <summary>
    /// Cambiar de perfil notifica TODAS las propiedades escalares que dependen de él. Un espía de
    /// PropertyChanged es la única forma de comprobar que la UI se entera.
    ///
    /// <c>Tweaks</c> no se notifica a propósito: es un <c>ObservableCollection</c> y la UI se entera por sus
    /// propios eventos de colección. Notificarlo además sería ruido; lo que se fija aquí es el CONTENIDO.
    /// </summary>
    [Fact]
    public async Task ChangingTheProfile_NotifiesEveryDependentProperty()
    {
        using var vm = await CreateAsync();
        using var spy = new PropertySpy(vm);
        var other = vm.SelectedProfile == TweakProfile.Advanced ? TweakProfile.Safe : TweakProfile.Advanced;
        var before = vm.Tweaks.Count;

        vm.SelectedProfile = other;

        Assert.True(spy.Saw(nameof(MainViewModel.TweakCountText)), "No se notificó TweakCountText.");
        Assert.True(spy.Saw(nameof(MainViewModel.SelectedProfile)), "No se notificó SelectedProfile.");
        Assert.True(spy.Saw(nameof(MainViewModel.SelectedProfileOption)), "No se notificó SelectedProfileOption.");
        // Y el contenido sí cambió de verdad (si no, notificar sería decorativo).
        Assert.NotEqual(before, vm.Tweaks.Count);
    }

    /// <summary>Cambiar de idioma notifica las propiedades de texto que el localizador resuelve.</summary>
    [Fact]
    public async Task ChangingTheCulture_NotifiesTheLocalizedProperties()
    {
        using var vm = await CreateAsync("es-MX");

        // El valor inicial NO es fijo: el constructor lo elige con la cultura de la maquina
        // (CultureDefaults.PickInitialCulture(Cultures, CultureInfo.CurrentUICulture.Name)). Escribir
        // "en-US" a pelo hacia que, en un equipo con la UI en ingles, el valor asignado FUERA ya el actual:
        // CommunityToolkit no notifica si el valor no cambia, OnCultureChanged no corria y el test fallaba
        // con "No se notificó Localizer". En CI, con otra cultura, pasaba. Se parte del valor real y se
        // cambia al OTRO, que siempre es un cambio de verdad.
        var other = vm.Culture == "en-US" ? "es-MX" : "en-US";

        using var spy = new PropertySpy(vm);

        vm.Culture = other;
        // El cambio de cultura recarga el localizador de forma asincrona y OnCultureChanged descarta la tarea
        // (`_ = ChangeCultureAsync(value)`), asi que no hay nada que esperar. Un Task.Delay fijo convierte el
        // test en una carrera: pasa o falla segun lo cargada que este la maquina. Se espera a la CONDICION,
        // con tope, y si no llega se falla con el motivo.
        for (var i = 0; i < 200 && !spy.Saw(nameof(MainViewModel.Localizer)); i++) await Task.Delay(10);

        Assert.True(spy.Saw(nameof(MainViewModel.Localizer)), "No se notificó Localizer.");
        Assert.Equal(other, vm.Localizer.Culture);
    }

    /// <summary>Cada tweak expone el aviso anticheat (Capa 10.2) sin dejar la celda vacía.</summary>
    [Fact]
    public async Task EveryTweakRow_ShowsAnAnticheatNote()
    {
        using var vm = await CreateAsync();

        Assert.All(vm.Tweaks, t => Assert.False(string.IsNullOrWhiteSpace(t.AnticheatNote)));
    }

    /// <summary>El título de la ventana sale del ensamblado (Capa 11.1), no de un literal de fase.</summary>
    [Fact]
    public async Task TheWindowTitle_ComesFromTheAssembly()
    {
        using var vm = await CreateAsync();

        Assert.Equal(AssemblyInfoText.WindowTitle(), vm.WindowTitle);
        Assert.DoesNotContain("Capa", vm.WindowTitle, StringComparison.OrdinalIgnoreCase);
    }

    private static FragForge.Tweaks.Validation.TweakCatalogValidator NewValidator()
    {
        var registry = new FakeRegistry();
        var factory = new FragForge.Actions.DependencyInjection.TweakActionFactory(
            registry, FragForge.Core.Persistence.ProtectionPolicy.CreateDefault(),
            new FakeServiceController(), new FakeScheduledTaskStore(), new FakeNetworkAdapterController(),
            new FakeProcessRunner(), FragForge.Actions.Diagnostics.DiagnosticProbeFactory.CreateDefaults(registry),
            adapterResolver: new FragForge.Actions.Network.UnsupportedNetworkAdapterResolver());
        return new FragForge.Tweaks.Validation.TweakCatalogValidator(
            new FragForge.Tweaks.Validation.JsonSchemaValidator(JsonTweakCatalogLoader.ReadEmbeddedSchema()),
            new FragForge.Core.Conditions.ConditionFactory(), factory);
    }
}

// --- Stubs mínimos: el ViewModel no debe tocar nada real en estos tests ---

internal sealed class StubEngine : FragForge.Core.Abstractions.ITweakEngine
{
    public Task<ScanReport> ScanAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) =>
        Task.FromResult(new ScanReport(new HardwareProfile(), [], DateTimeOffset.UtcNow));
    public Task<BatchReport> ApplyAsync(IEnumerable<string> ids, ApplyOptions options, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) =>
        // BatchReport.OperationId es `required` (Results.cs:65): sin el, CS9035.
        Task.FromResult(new BatchReport { OperationId = "stub-apply", Success = true });
    public Task<BatchReport> RevertAsync(RevertRequest request, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) =>
        Task.FromResult(new BatchReport { OperationId = "stub-revert", Success = true });
    public Task<TweakStatus> GetStateAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TweakStatus(id, TweakState.Unknown, "", [], []));
    public Task<SystemSnapshot> CreateSnapshotAsync(string label, bool createRestorePoint = true, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) =>
        Task.FromResult(new SystemSnapshot { Id = "stub" });
}

internal sealed class StubReports : FragForge.Core.Abstractions.IReportService
{
    public Task<ReportDocument> CreateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("El test no exporta informes.");
    public Task ExportAsync(string path, string format, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class StubPreflight : FragForge.Core.Abstractions.IPreflightService
{
    public Task<PreflightReport> CheckAsync(ApplyOptions options, HardwareProfile profile, CancellationToken cancellationToken = default) =>
        Task.FromResult(PreflightReport.Passed([]));
}

internal sealed class StubContext : FragForge.Core.Abstractions.IApplicationContext
{
    public string Culture { get; set; } = "es-MX";
    public string DataRoot => Path.GetTempPath();
    public string ApplicationVersion => "test";
}
