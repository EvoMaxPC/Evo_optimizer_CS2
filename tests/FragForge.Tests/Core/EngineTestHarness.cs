using FragForge.Core.Abstractions;
using FragForge.Core.Conditions;
using FragForge.Core.Engine;
using FragForge.Core.Localization;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Core.Preflight;
using FragForge.Tests.Fakes;
using FragForge.Tweaks;
using Microsoft.Extensions.Logging.Abstractions;

namespace FragForge.Tests.Core;

public sealed class EngineTestHarness : IDisposable
{
    public FakeRegistry Registry { get; } = new();
    public FakeHardwareProfiler Profiler { get; } = new();
    public FakeSystemRestoreService Restore { get; } = new();
    public FakeJournal? JournalOverride { get; set; }
    public TempDirectory Directory { get; } = new();
    public FragForgePaths Paths { get; }
    public IJournal Journal { get; }
    public ISnapshotStore Snapshots { get; }
    public IProtectionPolicy Policy { get; } = ProtectionPolicy.CreateDefault();
    public FakeActionFactory Factory { get; }
    public TweakCatalog Catalog { get; private set; } = new([]);
    public string UserSid => TestProfiles.Pn64().InteractiveUserSid!;

    public EngineTestHarness(Dictionary<string, ScriptedActionOptions>? scripts = null, bool inMemoryJournal = false)
    {
        if (inMemoryJournal) JournalOverride = new FakeJournal();
        Paths = new FragForgePaths(Directory.Path);
        Paths.EnsureCreated();
        Journal = (IJournal?)JournalOverride ?? new FileJournal(Paths);
        Snapshots = new FileSnapshotStore(Paths);
        Factory = new FakeActionFactory(Registry, Policy, scripts);
    }
    public EngineTestHarness WithCatalog(params TweakDefinition[] definitions)
    {
        Catalog = new TweakCatalog(definitions);
        return this;
    }
    public EngineTestHarness WithProfile(HardwareProfile profile) { Profiler.Profile = profile; return this; }
    /// <summary>
    /// <paramref name="factory"/> permite probar acciones REALES (p. ej. ScheduledTaskAction) contra un
    /// doble del store, sin tocar el FakeActionFactory que usan los demas tests.
    /// </summary>
    public TweakEngine CreateEngine(IEnumerable<IExperimentHook>? hooks = null, ITweakActionFactory? factory = null) => new(
        Catalog,
        Profiler,
        factory ?? Factory,
        new ConditionFactory(),
        Snapshots,
        Journal,
        Restore,
        // El localizador REAL, el mismo que inyecta CompositionRoot en produccion (:77 y :95). Sin el,
        // PreflightService cae a LocalizerVacio (PreflightService.cs:93) y todo mensaje localizado sale
        // como su CLAVE: los tests leian "preflight.recovery" en vez del motivo. No cambia que se bloquee,
        // cambia que el mensaje sea el que el usuario ve.
        new PreflightService(Restore, Catalog, null, PreflightLocalizer),
        new FileExecutionGate(Paths),
        hooks ?? [],
        NullLogger<TweakEngine>.Instance);
    /// <summary>
    /// Los textos de la app, cargados una vez. Ver el comentario del preflight en CreateEngine.
    /// Se lee el recurso embebido de forma SINCRONA a proposito: este campo se inicializa en un helper de
    /// test que no es async, y bloquear un hilo con .GetAwaiter().GetResult() es justo lo que xUnit1031
    /// senala. Localizer.FromJson ya es publico (Localizer.cs:17), asi que no hace falta tocar produccion.
    /// </summary>
    private static readonly Localizer PreflightLocalizer = LoadPreflightLocalizer();

    private static Localizer LoadPreflightLocalizer()
    {
        const string resource = "FragForge.Tweaks.Localization.es-MX.json";
        using var stream = typeof(FragForge.Tweaks.Localization.LocalizationLoader).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException($"Recurso de localizacion '{resource}' ausente.");
        using var reader = new StreamReader(stream);
        return Localizer.FromJson("es-MX", reader.ReadToEnd());
    }

    public ApplyOptions Options(bool dryRun = false, bool force = false, TweakProfile profile = TweakProfile.Advanced) => new() { DryRun = dryRun, Force = force, CreateRestorePoint = true, Profile = profile, TargetUserSid = UserSid };
    public void Dispose() => Directory.Dispose();
}
