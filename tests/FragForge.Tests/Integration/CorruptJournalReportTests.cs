using FragForge.Composition;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Tests.Core;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Integration;

/// <summary>
/// E2: un journal ILEGIBLE no puede tumbar el panel. El informe se genera igual y deja constancia del
/// motivo real.
/// </summary>
/// <remarks>
/// <para>
/// El fallo que esto fija: <c>FileJournal.ReadAllAsync</c> no captura nada (líneas 46-57), así que un
/// journal corrupto propagaba <c>JsonException</c> y <c>ReportService.CreateAsync:17</c> se caía entero. El
/// panel de Operaciones es justo lo que el usuario abre cuando algo va mal; que sea lo primero que se rompe
/// es lo peor que puede pasar.
/// </para>
/// <para>
/// Por qué el test de C4.6 (<c>IntegrityTests.ACorruptJournal_CannotBeRead_AndSaysSo</c>) NO lo detectó:
/// aquel comprueba <c>FileJournal</c> aislado y <b>afirma que ReadAllAsync lanza</b>. Es decir, dejaba escrito
/// el comportamiento correcto de <c>FileJournal</c> pero nunca pasaba por <c>ReportService</c>, que es quien
/// debía protegerse. Un test de la pieza no es un test del recorrido.
/// </para>
/// </remarks>
public sealed class CorruptJournalReportTests
{
    /// <summary>
    /// Escribe bytes truncados de verdad en el journal DEL HARNESS (mismo directorio que snapshots y demas,
    /// para no mezclar dos raices de datos en el mismo test): media entrada JSON, sin cerrar.
    /// </summary>
    private static async Task CorruptJournalAsync(EngineTestHarness harness)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(harness.Paths.Journal)!);
        // Media entrada válida + basura: lo que deja un corte de luz a mitad de escritura.
        await File.WriteAllTextAsync(harness.Paths.Journal, """{"entryId":"e1","operationId":"op","eventType":"Opera""");
    }

    private static ReportService Service(EngineTestHarness harness) => new(
        harness.CreateEngine(),
        new FragForge.Core.Preflight.PreflightService(harness.Restore, harness.Catalog),
        harness.Snapshots,
        new FileJournal(harness.Paths),
        new ApplicationContext(harness.Paths.Root, "es-MX"));

    [Fact]
    public async Task ACorruptJournal_DoesNotBreakTheReport_AndNamesTheProblem()
    {
        using var harness = new EngineTestHarness();
        await CorruptJournalAsync(harness);

        // 1) NO lanza.
        var report = await Service(harness).CreateAsync();

        // 2) El informe dice que el journal no se pudo leer, y da el motivo real (no "vacío").
        Assert.False(report.JournalIntegrity.IsValid);
        Assert.Contains("no se pudo leer", report.JournalIntegrity.Error!, StringComparison.OrdinalIgnoreCase);
        // El mensaje de la excepción de verdad viaja: JsonException habla de la posición/línea.
        Assert.DoesNotContain("journal vacío", report.JournalIntegrity.Error!, StringComparison.OrdinalIgnoreCase);

        // 3) Y el resto del informe está completo: es lo que hace útil el panel.
        Assert.Empty(report.RecentJournal);
        Assert.NotNull(report.Hardware);
        Assert.NotNull(report.Preflight);
    }

    [Fact]
    public async Task ACorruptJournal_StillPropagatesCancellation()
    {
        using var harness = new EngineTestHarness();
        await CorruptJournalAsync(harness);
        var service = Service(harness);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Cancelar no es "journal ilegible": se propaga, no se convierte en un dato del informe.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(cancellation.Token));
    }
}
