## 3f. FragForge.App — shell WPF MVVM

### `src/FragForge.App/App.xaml`  <sub>(27 líneas)</sub>

````xml
<Application x:Class="FragForge.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Application.Resources>
    <SolidColorBrush x:Key="PanelBrush" Color="#171E26"/>
    <SolidColorBrush x:Key="BorderBrush" Color="#2D3947"/>
    <SolidColorBrush x:Key="AccentBrush" Color="#4FB3FF"/>
    <Style TargetType="Button">
      <Setter Property="Background" Value="#243140"/>
      <Setter Property="Foreground" Value="#E8EDF2"/>
      <Setter Property="BorderBrush" Value="#3B4C60"/>
      <Setter Property="Padding" Value="10,5"/>
      <Setter Property="Margin" Value="0,0,6,0"/>
    </Style>
    <Style TargetType="TextBox">
      <Setter Property="Background" Value="#0D1218"/>
      <Setter Property="Foreground" Value="#E8EDF2"/>
      <Setter Property="BorderBrush" Value="#3B4C60"/>
      <Setter Property="Padding" Value="4"/>
    </Style>
    <Style TargetType="ComboBox">
      <Setter Property="Background" Value="#243140"/>
      <Setter Property="Foreground" Value="#E8EDF2"/>
      <Setter Property="Margin" Value="0,0,8,0"/>
    </Style>
  </Application.Resources>
</Application>
````

### `src/FragForge.App/App.xaml.cs`  <sub>(43 líneas)</sub>

````csharp
using System.Windows;
using FragForge.App.Services;
using FragForge.App.ViewModels;
using FragForge.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FragForge.App;

public partial class App : Application
{
    private IHost? _host;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var options = new ShellOptions(ConsoleLogging: false, ConfigureServices: services =>
            {
                services.AddSingleton<IConfirmationService, WpfConfirmationService>();
                services.AddSingleton<IFileDialogService, WpfFileDialogService>();
                services.AddSingleton<MainViewModel>();
            });
            _host = await CompositionRoot.BuildAsync(options);
            var viewModel = _host.Services.GetRequiredService<MainViewModel>();
            var window = new MainWindow { DataContext = viewModel };
            MainWindow = window;
            window.Show();
            await viewModel.ScanCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo iniciar FragForge:\n" + ex.Message, "FragForge", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow?.DataContext is MainViewModel viewModel) viewModel.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }
}
````

### `src/FragForge.App/FragForge.App.csproj`  <sub>(22 líneas)</sub>

````xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <AssemblyName>FragForge</AssemblyName>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <InvariantGlobalization>false</InvariantGlobalization>
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>
  <!-- Publish settings apply only when a RID is supplied, e.g.
       dotnet publish src/FragForge.App/FragForge.App.csproj -c Release -r win-x64 -->
  <PropertyGroup Condition="'$(RuntimeIdentifier)' != ''">
    <SelfContained Condition="'$(SelfContained)' == ''">true</SelfContained>
    <PublishSingleFile Condition="'$(PublishSingleFile)' == ''">true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract Condition="'$(IncludeNativeLibrariesForSelfExtract)' == ''">true</IncludeNativeLibrariesForSelfExtract>
    <EnableCompressionInSingleFile Condition="'$(EnableCompressionInSingleFile)' == ''">true</EnableCompressionInSingleFile>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="../FragForge.Composition/FragForge.Composition.csproj" /></ItemGroup>
  <ItemGroup><PackageReference Include="CommunityToolkit.Mvvm" /></ItemGroup>
</Project>
````

### `src/FragForge.App/MainWindow.xaml`  <sub>(83 líneas)</sub>

````xml
<Window x:Class="FragForge.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="{Binding Localizer[app.title]}" Height="820" Width="1280" MinHeight="640" MinWidth="980"
        Background="#101418" Foreground="#E8EDF2" WindowStartupLocation="CenterScreen">
  <Grid Margin="14">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="140"/>
    </Grid.RowDefinitions>
    <DockPanel Grid.Row="0" Margin="0,0,0,10">
      <StackPanel Orientation="Horizontal" DockPanel.Dock="Left">
        <TextBlock Text="{Binding Localizer[profile.label]}" VerticalAlignment="Center" Margin="0,0,6,0"/>
        <ComboBox Width="140" ItemsSource="{Binding Profiles}" SelectedItem="{Binding SelectedProfile}"/>
        <TextBlock Text="{Binding Localizer[culture.label]}" VerticalAlignment="Center" Margin="0,0,6,0"/>
        <ComboBox Width="90" ItemsSource="{Binding Cultures}" SelectedItem="{Binding Culture}"/>
      </StackPanel>
      <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
        <Button Content="{Binding Localizer[nav.scan]}" Command="{Binding ScanCommand}"/>
        <Button Content="{Binding Localizer[nav.dryRun]}" Command="{Binding DryRunCommand}"/>
        <Button Content="{Binding Localizer[nav.apply]}" Command="{Binding ApplyCommand}" Background="#1F5F8B"/>
        <Button Content="{Binding Localizer[nav.revert]}" Command="{Binding RevertCommand}"/>
        <Button Content="{Binding Localizer[cancel]}" Command="{Binding CancelCommand}"/>
      </StackPanel>
    </DockPanel>
    <Border Grid.Row="1" Background="{StaticResource PanelBrush}" BorderBrush="{StaticResource BorderBrush}" BorderThickness="1" Padding="10" Margin="0,0,0,10">
      <StackPanel>
        <TextBlock Text="{Binding Localizer[hardware.title]}" FontWeight="Bold" Foreground="{StaticResource AccentBrush}" Margin="0,0,0,4"/>
        <TextBlock Text="{Binding HardwareSummary}" TextWrapping="Wrap" FontFamily="Consolas" FontSize="12"/>
      </StackPanel>
    </Border>
    <Grid Grid.Row="2">
      <Grid.ColumnDefinitions><ColumnDefinition Width="3*"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
      <DataGrid Grid.Column="0" ItemsSource="{Binding Tweaks}" AutoGenerateColumns="False" CanUserAddRows="False" IsReadOnly="False" SelectionMode="Extended" HeadersVisibility="Column" GridLinesVisibility="Horizontal" Background="#0D1218" Foreground="#E8EDF2" BorderBrush="{StaticResource BorderBrush}" RowBackground="#121922" AlternatingRowBackground="#17212C">
        <DataGrid.Columns>
          <DataGridCheckBoxColumn Header="" Binding="{Binding IsSelected, UpdateSourceTrigger=PropertyChanged}" Width="36"/>
          <DataGridTextColumn Header="Tweak" Binding="{Binding Title}" Width="240" IsReadOnly="True"/>
          <DataGridTextColumn Header="Categoría" Binding="{Binding Category}" Width="90" IsReadOnly="True"/>
          <DataGridTextColumn Header="Evidencia" Binding="{Binding Evidence}" Width="90" IsReadOnly="True"/>
          <DataGridTextColumn Header="Riesgo" Binding="{Binding Risk}" Width="90" IsReadOnly="True"/>
          <DataGridTextColumn Header="Estado" Binding="{Binding StateDisplay}" Width="140" IsReadOnly="True"/>
          <DataGridTextColumn Header="Motivo" Binding="{Binding Reason}" Width="*" IsReadOnly="True"/>
        </DataGrid.Columns>
      </DataGrid>
      <Border Grid.Column="1" Background="{StaticResource PanelBrush}" BorderBrush="{StaticResource BorderBrush}" BorderThickness="1" Padding="10" Margin="10,0,0,0">
        <ScrollViewer VerticalScrollBarVisibility="Auto">
          <StackPanel>
            <TextBlock Text="{Binding Localizer[doctor.title]}" FontWeight="Bold" Foreground="{StaticResource AccentBrush}"/>
            <ItemsControl ItemsSource="{Binding Checks}">
              <ItemsControl.ItemTemplate><DataTemplate><TextBlock Text="{Binding Message}" TextWrapping="Wrap" Margin="0,4"/></DataTemplate></ItemsControl.ItemTemplate>
            </ItemsControl>
            <Button Content="Doctor" Command="{Binding DoctorCommand}" Margin="0,8,0,8"/>
            <Button Content="{Binding Localizer[nav.snapshot]}" Command="{Binding SnapshotCommand}" Margin="0,0,0,8"/>
            <Button Content="{Binding Localizer[nav.export]}" Command="{Binding ExportCommand}"/>
          </StackPanel>
        </ScrollViewer>
      </Border>
    </Grid>
    <WrapPanel Grid.Row="3" Margin="0,10,0,6">
      <CheckBox Content="{Binding Localizer[restorePoint]}" IsChecked="{Binding CreateRestorePoint}" Foreground="#E8EDF2" Margin="0,0,14,0"/>
      <CheckBox Content="{Binding Localizer[enableProtection]}" IsChecked="{Binding EnableSystemProtection}" Foreground="#E8EDF2" Margin="0,0,14,0"/>
      <CheckBox Content="{Binding Localizer[force]}" IsChecked="{Binding Force}" Foreground="#E8EDF2" Margin="0,0,14,0"/>
      <CheckBox Content="{Binding Localizer[allowPowerShell]}" IsChecked="{Binding AllowPowerShell}" Foreground="#E8EDF2"/>
    </WrapPanel>
    <Grid Grid.Row="4" Margin="0,0,0,8">
      <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="220"/></Grid.ColumnDefinitions>
      <TextBlock Text="{Binding Localizer[cs2Path]}" VerticalAlignment="Center" Margin="0,0,6,0"/>
      <TextBox Grid.Column="1" Text="{Binding Cs2Path, UpdateSourceTrigger=PropertyChanged}"/>
      <Button Grid.Column="2" Content="…" Command="{Binding BrowseCs2Command}" Margin="6,0,12,0"/>
      <TextBlock Grid.Column="3" Text="{Binding Localizer[risk.phrase]}" VerticalAlignment="Center" Margin="0,0,6,0"/>
      <TextBox Grid.Column="4" Text="{Binding RiskPhrase, UpdateSourceTrigger=PropertyChanged}"/>
    </Grid>
    <Grid Grid.Row="5">
      <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
      <DockPanel Grid.Row="0" Margin="0,0,0,4"><TextBlock Text="{Binding StatusMessage}" Foreground="{StaticResource AccentBrush}" TextTrimming="CharacterEllipsis"/><ProgressBar Width="220" Height="10" Minimum="0" Maximum="100" Value="{Binding ProgressValue}" HorizontalAlignment="Right"/></DockPanel>
      <ListBox Grid.Row="1" ItemsSource="{Binding Logs}" Background="#0D1218" Foreground="#B8C7D9" BorderBrush="{StaticResource BorderBrush}" FontFamily="Consolas" FontSize="11"/>
    </Grid>
  </Grid>
</Window>
````

### `src/FragForge.App/MainWindow.xaml.cs`  <sub>(8 líneas)</sub>

````csharp
using System.Windows;

namespace FragForge.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
````

### `src/FragForge.App/Services/DialogServices.cs`  <sub>(33 líneas)</sub>

````csharp
using Microsoft.Win32;
using System.Windows;

namespace FragForge.App.Services;

public interface IConfirmationService
{
    bool Confirm(string message, string title);
    bool ConfirmRisk(string first, string second, string title);
}
public interface IFileDialogService
{
    string? SaveReport();
    string? OpenCs2();
}
public sealed class WpfConfirmationService : IConfirmationService
{
    public bool Confirm(string message, string title) => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    public bool ConfirmRisk(string first, string second, string title) => Confirm(first, title) && Confirm(second, title);
}
public sealed class WpfFileDialogService : IFileDialogService
{
    public string? SaveReport()
    {
        var dialog = new SaveFileDialog { Title = "Exportar informe FragForge", Filter = "HTML (*.html)|*.html|JSON (*.json)|*.json", DefaultExt = ".html", FileName = "fragforge-report.html" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public string? OpenCs2()
    {
        var dialog = new OpenFileDialog { Title = "Seleccionar cs2.exe", Filter = "Counter-Strike 2 (cs2.exe)|cs2.exe", CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
````

### `src/FragForge.App/ViewModels/MainViewModel.cs`  <sub>(207 líneas)</sub>

````csharp
using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FragForge.App.Services;
using FragForge.Core.Abstractions;
using FragForge.Core.Engine;
using FragForge.Core.Localization;
using FragForge.Core.Models;
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

    public ObservableCollection<TweakItemViewModel> Tweaks { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<PreflightCheck> Checks { get; } = [];
    public IReadOnlyList<TweakProfile> Profiles { get; } = Enum.GetValues<TweakProfile>();
    public IReadOnlyList<string> Cultures { get; } = ["es-MX", "en-US"];

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
        _sink = sink as UiLogSink ?? new UiLogSink();
        _sink.MessageWritten += OnLogMessage;
        foreach (var line in _sink.History) Logs.Add(line);
        RebuildTweaks();
    }
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
            _statuses.Clear();
            foreach (var status in scan.Tweaks) _statuses[status.Id] = status;
            RebuildTweaks();
            StatusMessage = $"Escaneo completado: {scan.Tweaks.Count} tweaks.";
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
    }
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
    public void Dispose() { _sink.MessageWritten -= OnLogMessage; _operation?.Cancel(); _operation?.Dispose(); }
}
````

### `src/FragForge.App/ViewModels/TweakItemViewModel.cs`  <sub>(41 líneas)</sub>

````csharp
using CommunityToolkit.Mvvm.ComponentModel;
using FragForge.Core.Localization;
using FragForge.Core.Models;

namespace FragForge.App.ViewModels;

public sealed partial class TweakItemViewModel : ObservableObject
{
    public TweakDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Category => Definition.Category;
    public string Evidence => Definition.Evidence.ToString();
    public string Risk => Definition.Risk.ToString();
    public string Title => Definition.Title.Resolve(_culture);
    public string Description => Definition.Description.Resolve(_culture);
    public string Notes => Definition.Notes?.Resolve(_culture) ?? "";
    public string WhenNotToApply => Definition.WhenNotToApply?.Resolve(_culture) ?? "";
    public bool RequiresReboot => Definition.RequiresReboot;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private TweakState state = TweakState.Unknown;
    [ObservableProperty] private string stateDisplay = "Unknown";
    [ObservableProperty] private string reason = "";
    private string _culture;
    public TweakItemViewModel(TweakDefinition definition, TweakStatus? status, Localizer localizer)
    {
        Definition = definition;
        _culture = localizer.Culture;
        if (status is not null) Update(status, localizer);
    }
    public void Update(TweakStatus status, Localizer localizer)
    {
        _culture = localizer.Culture;
        State = status.State;
        StateDisplay = localizer["state." + status.State];
        Reason = status.Reason;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(WhenNotToApply));
    }
}
````

### `src/FragForge.App/app.manifest`  <sub>(15 líneas)</sub>

````xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="FragForge.App" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges>
    <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
  </requestedPrivileges></security></trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application>
    <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
  </application></compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings>
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
  </windowsSettings></application>
</assembly>
````

