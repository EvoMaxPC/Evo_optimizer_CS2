using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FragForge.App.Services;
using FragForge.Core.Abstractions;
using FragForge.Core.Engine;
using FragForge.Core.Localization;
using FragForge.Core.Models;
using FragForge.Core.Sessions;
using FragForge.Tweaks.Localization;

namespace FragForge.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ITweakEngine _engine;
    private readonly ITweakCatalog _catalog;
    private readonly IReportService _reports;
    private readonly IConfirmationService _confirmation;
    private readonly IFileDialogService _files;
    private readonly IPreflightService _preflight;
    private readonly IHardwareProfiler _profiler;
    private readonly IApplicationContext _context;
    private readonly SynchronizationContext? _uiContext;
    private readonly UiLogSink _sink;
    private readonly Dictionary<string, TweakStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private Localizer _localizer;
    private CancellationTokenSource? _operation;
    /// <summary>
    /// Cuántos tweaks evaluó el último escaneo (0 si no hubo). Solo se usa para rehacer el mensaje.
    /// </summary>
    private int _lastScanEvaluated;
    /// <summary>
    /// Mensaje de estado que dejó el ÚLTIMO escaneo, tal cual se escribió (texto ya formado, con una
    /// copia congelada del contador dentro), o null si todavía no se escaneó. Se guarda el texto, no solo
    /// el número, para poder comprobar si el mensaje actual SIGUE siendo ese: si otra acción ya lo
    /// reemplazó ("Aplicación completada. Snapshot: …"), el cambio de perfil no debe pisarla.
    /// </summary>
    private string? _lastScanStatus;

    public ObservableCollection<TweakItemViewModel> Tweaks { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<PreflightCheck> Checks { get; } = [];
    /// <summary>Perfiles disponibles, con nombre para mostrar. El combo enlaza a esta lista.</summary>
    public IReadOnlyList<ProfileOption> Profiles { get; } = [.. Enum.GetValues<TweakProfile>().Select(p => new ProfileOption(p, DescribeProfile(p)))];

    /// <summary>Culturas que el binario realmente puede cargar (los JSON embebidos).</summary>
    public IReadOnlyList<string> Cultures { get; } = CultureDefaults.Supported;

    /// <summary>
    /// Valor del combo de perfil. Es el perfil en sí (enum), no una etiqueta: así el enlace no depende
    /// de texto y la selección siempre existe al construir el ViewModel.
    /// </summary>
    public TweakProfile SelectedProfileOption
    {
        get => SelectedProfile;
        set
        {
            if (value == SelectedProfile) return;
            SelectedProfile = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Texto del contador: cuántos tweaks del perfil se están mostrando de cuántos existen.</summary>
    public string TweakCountText => TweakCountFormatter.Format(SelectedProfile, Tweaks.Count, _catalog.Definitions.Count);

    private static string DescribeProfile(TweakProfile profile) => profile switch
    {
        TweakProfile.Safe => "Safe (menor riesgo)",
        TweakProfile.Balanced => "Balanced",
        TweakProfile.Competitive => "Competitive",
        TweakProfile.Advanced => "Advanced (riesgo)",
        _ => profile.ToString()
    };

    [ObservableProperty] private string hardwareSummary = "Ejecuta Escanear para detectar el equipo.";
    [ObservableProperty] private string statusMessage = "Listo";
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private TweakProfile selectedProfile = TweakProfile.Safe;
    [ObservableProperty] private string culture = "es-MX";
    [ObservableProperty] private string cs2Path = "";
    [ObservableProperty] private string riskPhrase = "";
    [ObservableProperty] private bool createRestorePoint = true;
    [ObservableProperty] private bool enableSystemProtection;
    [ObservableProperty] private bool force;
    [ObservableProperty] private bool allowPowerShell;

    public MainViewModel(ITweakEngine engine, ITweakCatalog catalog, Localizer localizer, IReportService reports, IUiLogSink sink, IConfirmationService confirmation, IFileDialogService files, IPreflightService preflight, IHardwareProfiler profiler, IApplicationContext context)
    {
        _engine = engine; _catalog = catalog; _localizer = localizer; _reports = reports; _confirmation = confirmation; _files = files; _preflight = preflight; _profiler = profiler; _context = context;
        _uiContext = SynchronizationContext.Current;
        // El combo de idioma debe arrancar con una opción REALMENTE seleccionable: la cultura de la UI
        // si el binario la soporta; si no, es-MX y luego en-US.
        // Se asigna el CAMPO, no la propiedad: asignar la propiedad dispararía OnCultureChanged y
        // recargaría el localizador de forma asíncrona durante el constructor.
        culture = CultureDefaults.PickInitialCulture(Cultures, CultureInfo.CurrentUICulture.Name) ?? "es-MX";
        _sink = sink as UiLogSink ?? new UiLogSink();
        _sink.MessageWritten += OnLogMessage;
        foreach (var line in _sink.History) Logs.Add(line);
        RebuildTweaks();
    }
    /// <summary>
    /// Modo sesión (Capa 9). Se asigna desde <c>App.xaml.cs</c> en vez de por el constructor: el constructor de
    /// este ViewModel es una lista larga de dependencias y no se toca en esta ronda. Si queda en <c>null</c>
    /// (tests, diseño), el interruptor se muestra deshabilitado con su texto, sin romper nada.
    /// </summary>
    public GameSessionService? Sessions { get; set; }

    /// <summary>Texto del interruptor según el estado. Lo resuelve el localizador.</summary>
    public string SessionStateText => _localizer[SessionStateKey];

    private string SessionStateKey => SessionState switch
    {
        GameSessionState.Active => "session.state.active",
        GameSessionState.WaitingForGame => "session.state.waiting",
        GameSessionState.Reverting => "session.state.reverting",
        _ => "session.state.inactive"
    };

    [ObservableProperty] private GameSessionState sessionState = GameSessionState.Inactive;

    partial void OnSessionStateChanged(GameSessionState value) => OnPropertyChanged(nameof(SessionStateText));

    /// <summary>
    /// Interruptor de la UI: enciende o apaga la sesión. Al encender se aplica y se empieza a vigilar; al
    /// apagar se revierte. No hay bucle propio aquí: <see cref="GameSessionService.WatchAsync"/> ya trae su
    /// token, y se cancela al apagar o al cerrar la ventana.
    /// </summary>
    [RelayCommand]
    private async Task ToggleSessionAsync()
    {
        if (Sessions is null) { StatusMessage = _localizer["session.unavailable"]; return; }
        if (_sessionWatch is not null)
        {
            await StopSessionAsync();
            return;
        }

        try
        {
            var start = await Sessions.StartAsync(SelectedProfile);
            foreach (var message in start.Messages) Logs.Add(message);
            if (!start.Started) { StatusMessage = string.Join(" ", start.Messages); return; }

            SessionState = GameSessionState.WaitingForGame;
            StatusMessage = _localizer[SessionStateKey];
            _sessionWatch = new CancellationTokenSource();
            var token = _sessionWatch.Token;
            // La vigilancia corre en segundo plano y actualiza el estado en el hilo de UI con PostToUi.
            _ = Task.Run(async () =>
            {
                try
                {
                    PostToUi(() => SessionState = GameSessionState.Active);
                    await Sessions.WatchAsync(delay: Task.Delay, cancellationToken: token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PostToUi(() => StatusMessage = ex.Message); }
                finally { PostToUi(() => SessionState = GameSessionState.Inactive); }
            }, CancellationToken.None);
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private async Task StopSessionAsync()
    {
        SessionState = GameSessionState.Reverting;
        try
        {
            _sessionWatch?.Cancel();
            _sessionWatch?.Dispose();
            _sessionWatch = null;
            if (Sessions is null) return;
            var stop = await Sessions.StopAsync();
            foreach (var error in stop.Errors) Logs.Add("[Sesión] " + error);
            StatusMessage = stop.Reverted || stop.AlreadyReverted ? _localizer["session.stopped"] : string.Join(" ", stop.Errors);
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { SessionState = GameSessionState.Inactive; }
    }

    private CancellationTokenSource? _sessionWatch;

    private bool CanOperate() => !IsBusy;
    partial void OnIsBusyChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); DryRunCommand.NotifyCanExecuteChanged(); RevertCommand.NotifyCanExecuteChanged(); SnapshotCommand.NotifyCanExecuteChanged(); ExportCommand.NotifyCanExecuteChanged(); DoctorCommand.NotifyCanExecuteChanged(); BrowseCs2Command.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedProfileChanged(TweakProfile value) => RebuildTweaks();
    partial void OnCultureChanged(string value) => _ = ChangeCultureAsync(value);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task ScanAsync()
    {
        await RunAsync(async (token, progress) =>
        {
            var scan = await _engine.ScanAsync(token, progress);
            HardwareSummary = BuildHardwareSummary(scan.Hardware);
            _lastScanEvaluated = scan.Tweaks.Count;
            _statuses.Clear();
            foreach (var status in scan.Tweaks) _statuses[status.Id] = status;
            RebuildTweaks();
            _lastScanStatus = BuildScanStatusMessage(scan.Tweaks.Count);
            StatusMessage = _lastScanStatus;
        });
    }
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ApplyAsync() => ApplyCoreAsync(false);
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task DryRunAsync() => ApplyCoreAsync(true);
    private async Task ApplyCoreAsync(bool dryRun)
    {
        var selected = Tweaks.Where(t => t.IsSelected).ToArray();
        if (selected.Length == 0) { StatusMessage = "Selecciona al menos un tweak."; return; }
        var acknowledgements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!dryRun)
        {
            foreach (var item in selected.Where(t => t.Definition.Risk is RiskLevel.Advanced or RiskLevel.Experimental))
            {
                if (!_confirmation.ConfirmRisk(_localizer["risk.first"] + "\n" + item.Title, _localizer["risk.second"], _localizer["risk.title"])) { StatusMessage = "Confirmación cancelada."; return; }
                acknowledgements.Add(item.Id);
            }
            if (acknowledgements.Count > 0 && RiskPhrase != _localizer["risk.phrase"]) { StatusMessage = "Escribe la frase exacta de confirmación de riesgo."; return; }
        }
        var options = new ApplyOptions { DryRun = dryRun, Force = Force, CreateRestorePoint = CreateRestorePoint, EnableSystemProtection = EnableSystemProtection, Profile = SelectedProfile, RiskAcknowledgements = acknowledgements, AdvancedRiskPhrase = RiskPhrase, Cs2ExecutableOverride = string.IsNullOrWhiteSpace(Cs2Path) ? null : Cs2Path, AllowPowerShellActions = AllowPowerShell };
        await RunAsync(async (token, progress) =>
        {
            var report = await _engine.ApplyAsync(selected.Select(t => t.Id), options, token, progress);
            foreach (var result in report.Results) _statuses[result.Id] = new TweakStatus(result.Id, result.State, result.Message, [], []);
            RebuildTweaks();
            StatusMessage = report.Success ? (dryRun ? "Dry-run completado sin cambios." : $"Aplicación completada. Snapshot: {report.SnapshotId}") : string.Join(" ", report.Errors);
        });
    }
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task RevertAsync()
    {
        var selected = Tweaks.Where(t => t.IsSelected).Select(t => t.Id).ToArray();
        var last = selected.Length == 0;
        if (last && !_confirmation.Confirm("No hay tweaks seleccionados. ¿Revertir el último snapshot aplicado?", _localizer["nav.revert"])) return;
        if (!last && !_confirmation.Confirm("¿Restaurar el estado capturado de los tweaks seleccionados?", _localizer["nav.revert"])) return;
        await RunAsync(async (token, progress) =>
        {
            var report = await _engine.RevertAsync(new RevertRequest { Ids = selected, Last = last, Options = new RevertOptions { Force = Force, Cs2ExecutableOverride = string.IsNullOrWhiteSpace(Cs2Path) ? null : Cs2Path } }, token, progress);
            foreach (var result in report.Results) _statuses[result.Id] = new TweakStatus(result.Id, result.State, result.Message, [], []);
            RebuildTweaks();
            StatusMessage = report.Success ? "Reversión completada." : string.Join(" ", report.Errors);
        });
    }
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task SnapshotAsync() => await RunAsync(async (token, progress) =>
    {
        var snapshot = await _engine.CreateSnapshotAsync("WPF manual", CreateRestorePoint, token, progress);
        StatusMessage = $"Snapshot creado: {snapshot.Id}";
    });
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task ExportAsync()
    {
        var path = _files.SaveReport();
        if (path is null) return;
        await RunAsync(async (token, _) => { await _reports.ExportAsync(path, System.IO.Path.GetExtension(path).TrimStart('.'), token); StatusMessage = "Informe exportado: " + path; });
    }
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task DoctorAsync() => await RunAsync(async (token, progress) =>
    {
        var hardware = await _profiler.ProfileAsync(token, progress);
        var report = await _preflight.CheckAsync(new ApplyOptions { CreateRestorePoint = CreateRestorePoint, EnableSystemProtection = EnableSystemProtection, Profile = SelectedProfile }, hardware, token);
        Checks.Clear();
        foreach (var check in report.Checks) Checks.Add(check);
        StatusMessage = report.CanApply ? "Preflight apto." : "Preflight bloqueado.";
    });
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void BrowseCs2()
    {
        var path = _files.OpenCs2();
        if (path is not null) Cs2Path = path;
    }
    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _operation?.Cancel();

    private async Task RunAsync(Func<CancellationToken, IProgress<EngineProgress>, Task> operation)
    {
        IsBusy = true;
        ProgressValue = 0;
        _operation = new CancellationTokenSource();
        var progress = new Progress<EngineProgress>(p => { ProgressValue = Math.Clamp(p.Fraction * 100, 0, 100); StatusMessage = $"[{p.Stage}] {p.TweakId} {p.Message}"; });
        try { await operation(_operation.Token, progress); }
        catch (OperationCanceledException) { StatusMessage = "Operación cancelada."; }
        catch (Exception ex) { StatusMessage = ex.Message; PostToUi(() => Logs.Add("[Error] " + ex)); }
        finally { IsBusy = false; _operation.Dispose(); _operation = null; }
    }
    private void RebuildTweaks()
    {
        var selected = Tweaks.Where(t => t.IsSelected).Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Tweaks.Clear();
        foreach (var definition in ProfileSelector.Select(_catalog.Definitions, SelectedProfile))
        {
            _statuses.TryGetValue(definition.Id, out var status);
            Tweaks.Add(new TweakItemViewModel(definition, status, _localizer) { IsSelected = selected.Contains(definition.Id) });
        }
        // La tabla filtra por perfil: el contador tiene que decir cuántos se ven, no cuántos hay.
        OnPropertyChanged(nameof(TweakCountText));
        OnPropertyChanged(nameof(SelectedProfileOption));
        // El mensaje del último escaneo lleva DENTRO una copia del contador, formada cuando se escaneó.
        // Notificar TweakCountText no rehace ese texto: hay que volver a formarlo, o la barra de estado
        // sigue anunciando el perfil anterior mientras la tabla ya cambió.
        // Solo se rehace si el mensaje actual SIGUE siendo exactamente el del escaneo: si algo lo
        // reemplazó (un error, el resultado de aplicar, "Listo"), no se pisa.
        if (ScanStatusText.IsStillTheScanMessage(StatusMessage, _lastScanStatus))
        {
            _lastScanStatus = BuildScanStatusMessage(_lastScanEvaluated);
            StatusMessage = _lastScanStatus;
        }
    }
    /// <summary>Mensaje de estado de un escaneo. Única definición: lo usan el escaneo y el cambio de perfil.</summary>
    private string BuildScanStatusMessage(int evaluated) => ScanStatusText.Format(evaluated, TweakCountText);
    private async Task ChangeCultureAsync(string value)
    {
        try
        {
            _localizer = await LocalizationLoader.LoadAsync(value);
            _context.Culture = _localizer.Culture;
            foreach (var item in Tweaks) { _statuses.TryGetValue(item.Id, out var status); item.Update(status ?? new TweakStatus(item.Id, item.State, item.Reason, [], []), _localizer); }
            OnPropertyChanged(nameof(Localizer));
            StatusMessage = _localizer["ready"];
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }
    public Localizer Localizer => _localizer;
    private void PostToUi(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext) action();
        else _uiContext.Post(_ => action(), null);
    }
    private static string BuildHardwareSummary(HardwareProfile h)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{h.MachineName} · {h.FormFactor.SystemModel ?? "Unknown"} · {h.Os.Name} {h.Os.Build}.{h.Os.Ubr}");
        builder.AppendLine($"CPU: {h.Cpu.Model} · P={h.Cpu.PCores?.ToString() ?? "?"} E={h.Cpu.ECores?.ToString() ?? "?"} · hilos={h.Cpu.LogicalProcessors} · híbrida={h.Cpu.IsHybrid?.ToString() ?? "Unknown"}");
        builder.AppendLine($"RAM: {h.Memory.TotalBytes / 1024 / 1024 / 1024.0:F2} GiB · canales={h.Memory.Channels} · módulos={h.Memory.Modules.Count} · {h.Memory.ConfiguredSpeedMtPerSecond?.ToString() ?? "?"} MT/s");
        builder.AppendLine("GPU: " + string.Join("; ", h.Gpus.Select(g => $"{g.Name} [{g.Vendor}]")));
        builder.AppendLine("Red: " + string.Join("; ", h.NetworkAdapters.Where(a => a.IsPhysical).Select(a => $"{a.Name} ({a.Description})")));
        builder.AppendLine($"VBS={h.Security.Vbs} · HVCI={h.Security.Hvci} · SecureBoot={h.Security.SecureBoot} · TPM={h.Security.Tpm} · HAGS={h.Security.Hags}");
        builder.AppendLine($"Plan: {h.Power.ActiveSchemeName ?? "Unknown"} · reinicio pendiente={h.Maintenance.PendingReboot} · TiWorker/TrustedInstaller={h.Maintenance.WindowsServicingActive}");
        builder.AppendLine($"CS2: {h.Games.Cs2ExecutablePath ?? h.Games.Cs2DetectionSource}");
        if (h.ProbeWarnings.Count > 0) builder.AppendLine("Avisos: " + string.Join(" | ", h.ProbeWarnings));
        return builder.ToString().TrimEnd();
    }
    private void OnLogMessage(object? sender, string line) => PostToUi(() => { Logs.Add(line); while (Logs.Count > 500) Logs.RemoveAt(0); });
    public void Dispose() { _sink.MessageWritten -= OnLogMessage; _operation?.Cancel(); _operation?.Dispose(); _sessionWatch?.Cancel(); _sessionWatch?.Dispose(); }
}

/// <summary>Opción del combo de perfiles: el enum (valor) y un texto para mostrar.</summary>
public sealed record ProfileOption(TweakProfile Profile, string Label)
{
    public override string ToString() => Label;
}
