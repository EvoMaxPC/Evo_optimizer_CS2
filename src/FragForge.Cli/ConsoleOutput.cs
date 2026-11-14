using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Cli;

public static class ConsoleOutput
{
    public static void Json<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Options));
    public static void Hardware(HardwareProfile h)
    {
        Console.WriteLine("=== Hardware / sistema ===");
        Console.WriteLine($"Equipo       : {h.MachineName} · {h.FormFactor.SystemModel ?? "Unknown"} · BIOS {h.FormFactor.BiosVersion ?? "Unknown"}");
        Console.WriteLine($"SO           : {h.Os.Name} {h.Os.Build}.{h.Os.Ubr} · {h.Os.Architecture} · {h.Os.Culture} · {h.Os.TimeZone}");
        Console.WriteLine($"Usuario      : {h.InteractiveUserName ?? "Unknown"} · SID {h.InteractiveUserSid ?? "Unknown"}");
        Console.WriteLine($"CPU          : {h.Cpu.Model} · {h.Cpu.PhysicalCores} núcleos / {h.Cpu.LogicalProcessors} hilos · P={h.Cpu.PCores?.ToString() ?? "?"} E={h.Cpu.ECores?.ToString() ?? "?"} · híbrida={h.Cpu.IsHybrid?.ToString() ?? "Unknown"}");
        Console.WriteLine($"RAM          : {h.Memory.TotalBytes:N0} bytes · canales={h.Memory.Channels} · módulos={h.Memory.Modules.Count} · velocidad={h.Memory.ConfiguredSpeedMtPerSecond?.ToString() ?? "Unknown"} MT/s");
        foreach (var module in h.Memory.Modules) Console.WriteLine($"  DIMM       : {module.BankLabel}/{module.DeviceLocator} {module.CapacityBytes:N0} bytes {module.PartNumber}");
        Console.WriteLine($"GPU          : {string.Join("; ", h.Gpus.Select(g => $"{g.Name} [{g.Vendor}] integrada={g.IsIntegrated} driver={g.DriverVersion}"))}");
        foreach (var disk in h.Disks) Console.WriteLine($"Disco        : {disk.Model} · {disk.Kind} · {disk.Bus} · {disk.SizeBytes:N0} bytes · salud={disk.Health} · SMART={disk.SmartPredictFailure?.ToString() ?? "Unavailable"}");
        foreach (var adapter in h.NetworkAdapters) Console.WriteLine($"Red          : {adapter.Name} · {adapter.Description} · {adapter.Kind} · {adapter.LinkSpeedBitsPerSecond?.ToString() ?? "?"} bps · driver={adapter.DriverVersion} · físico={adapter.IsPhysical}");
        foreach (var monitor in h.Monitors) Console.WriteLine($"Monitor      : {monitor.Name} · {monitor.Width}x{monitor.Height}@{monitor.RefreshRateHz}Hz ({monitor.Source})");
        Console.WriteLine($"Seguridad    : VBS={h.Security.Vbs} HVCI={h.Security.Hvci} SecureBoot={h.Security.SecureBoot} TPM={h.Security.Tpm} {h.Security.TpmVersion} HAGS={h.Security.Hags} MPO={h.Security.Mpo}");
        Console.WriteLine($"Energía      : {h.Power.ActiveSchemeName ?? "Unknown"} ({h.Power.ActiveSchemeGuid ?? "?"}) · PL1={h.Power.Pl1Watts?.ToString() ?? "Unknown"} W · PL2={h.Power.Pl2Watts?.ToString() ?? "Unknown"} W");
        Console.WriteLine($"Factor forma : portátil={h.FormFactor.IsPortable?.ToString() ?? "Unknown"} · batería={h.FormFactor.HasBattery} · chasis={string.Join(",", h.FormFactor.ChassisTypes)}");
        Console.WriteLine($"Playbooks    : {string.Join("; ", h.Playbooks.Select(p => $"{p.Name} [{p.Confidence}]"))}");
        foreach (var error in h.DeviceErrors) Console.WriteLine($"Device error : {error.Name} · código {error.ErrorCode} · {error.InstanceId}");
        Console.WriteLine($"Mantenimiento: reinicio pendiente={h.Maintenance.PendingReboot} ({string.Join("; ", h.Maintenance.PendingRebootReasons)}) · servicing={h.Maintenance.WindowsServicingActive} ({string.Join(",", h.Maintenance.ServicingProcesses)}) · admin={h.Maintenance.IsAdministrator} · libre={h.Maintenance.SystemDriveFreeBytes / 1024 / 1024} MiB · protección={h.Maintenance.SystemProtection}");
        Console.WriteLine($"CS2          : {h.Games.Cs2ExecutablePath ?? h.Games.Cs2DetectionSource}");
        foreach (var warning in h.ProbeWarnings) Console.WriteLine($"Aviso        : {warning}");
    }
    public static void Tweaks(IEnumerable<TweakStatus> statuses, IEnumerable<TweakDefinition> definitions)
    {
        var map = definitions.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        Console.WriteLine();
        Console.WriteLine($"{"ID",-42} {"Categoría",-12} {"Evidencia",-13} {"Riesgo",-13} {"Estado",-20} Motivo");
        foreach (var status in statuses)
        {
            var d = map[status.Id];
            Console.WriteLine($"{status.Id,-42} {d.Category,-12} {d.Evidence,-13} {d.Risk,-13} {status.State,-20} {status.Reason}");
            foreach (var observation in status.Observations) Console.WriteLine($"  {observation.Key}: {observation.Value} [{observation.Confidence}] {observation.Note}");
        }
    }
    public static void Definitions(IEnumerable<TweakDefinition> definitions, string culture)
    {
        Console.WriteLine($"{"ID",-42} {"Categoría",-12} {"Evidencia",-13} {"Riesgo",-13} Título");
        foreach (var d in definitions) Console.WriteLine($"{d.Id,-42} {d.Category,-12} {d.Evidence,-13} {d.Risk,-13} {d.Title.Resolve(culture)}");
    }
    public static void Batch(BatchReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"Operación: {report.OperationId} · éxito={report.Success} · dry-run={report.DryRun} · cancelado={report.Cancelled}");
        if (report.SnapshotId is not null) Console.WriteLine($"Snapshot : {report.SnapshotId}");
        if (report.RestorePointSequence is not null) Console.WriteLine($"Restore  : punto {report.RestorePointSequence}");
        foreach (var result in report.Results)
        {
            Console.WriteLine($"- {result.Id}: {result.State} · {result.Message}");
            foreach (var change in result.PlannedChanges) Console.WriteLine($"  [{change.ActionIndex}] {change.Kind}: {change.Description}");
            foreach (var resultWarning in result.Warnings) Console.WriteLine($"  Aviso: {resultWarning}");
        }
        foreach (var warning in report.Warnings) Console.WriteLine($"Aviso: {warning}");
        foreach (var error in report.Errors) Console.Error.WriteLine($"Error: {error}");
        if (report.HasCriticalFailure) Console.Error.WriteLine("CRÍTICO: una reversión quedó incompleta. No sigas aplicando cambios; revisa snapshot/journal y la guía de recuperación manual.");
    }
    public static void Preflight(PreflightReport report)
    {
        Console.WriteLine($"Preflight: {(report.CanApply ? "APTO" : "BLOQUEADO")}");
        foreach (var check in report.Checks) Console.WriteLine($"[{check.Severity}] {check.Id}: {check.Message}{(check.Remediation is null ? "" : " → " + check.Remediation)}");
    }
}
