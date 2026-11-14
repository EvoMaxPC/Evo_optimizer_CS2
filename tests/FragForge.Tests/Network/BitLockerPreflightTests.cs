using FragForge.Core.Models;
using FragForge.Core.Preflight;
using FragForge.Tests.Fakes;
using Xunit;

namespace FragForge.Tests.Network;

/// <summary>
/// Capa 5.3 (R-J) — preflight de BitLocker de SOLO LECTURA.
///
/// Dos cosas se prueban aquí, y las dos importan:
/// <list type="number">
/// <item><b>No escribe.</b> El único argumento que se pasa a <c>manage-bde</c> es <c>-status</c>. Si alguien
///       añade un <c>-off</c>, <c>-suspend</c> o <c>-on</c>, este test falla.</item>
/// <item><b>No adivina.</b> Si la salida no se reconoce, el estado es <see cref="BitLockerState.Unknown"/>,
///       NO "sin cifrar": asumir que no hay cifrado es justo el error peligroso.</item>
/// </list>
///
/// Las salidas de los fixtures reproducen el FORMATO de <c>manage-bde -status</c>. El texto de las etiquetas
/// está en es-MX y en en-US a propósito: el parser no debe depender de él (R-C).
/// </summary>
public sealed class BitLockerPreflightTests
{
    /// <summary>Volumen cifrado al 100% con protección activa. Etiquetas en español, salida de Windows real.</summary>
    private const string SpanishEncrypted = """
        Administración de BitLocker: configuración del equipo

        Volumen C: [OS]
        Tamaño:                 476.94 GB
        Espacio libre del volumen: 120.00 GB
        Conversion Status:       Fully Encrypted
        Percentage Encrypted:    100.0%
        Encryption Method:       XTS-AES 128
        Protection Status:       Protection On
        """;

    /// <summary>El MISMO equipo con etiquetas en inglés: el parser tiene que dar el mismo resultado.</summary>
    private const string EnglishEncrypted = """
        BitLocker Drive Encryption: Configuration Tool

        Volume C: [OS]
        Size:                 476.94 GB
        Conversion Status:    Fully Encrypted
        Percentage Encrypted: 100.0%
        Protection Status:    Protection On
        """;

    /// <summary>Volumen sin cifrar: 0.0% y sin protección.</summary>
    private const string NotEncrypted = """
        Volume C: [OS]
        Conversion Status:    Fully Decrypted
        Percentage Encrypted: 0.0%
        Protection Status:    Protection Off
        """;

    /// <summary>Cifrado con la protección suspendida (p. ej. tras una actualización de firmware).</summary>
    private const string Suspended = """
        Volume C: [OS]
        Conversion Status:    Fully Encrypted
        Percentage Encrypted: 100.0%
        Protection Status:    Protection Off
        """;

    /// <summary>Cifrado a medias: aplicar cambios de red ahora es una mala idea.</summary>
    private const string InProgress = """
        Volume C: [OS]
        Conversion Status:    Encryption in Progress
        Percentage Encrypted: 42.7%
        Protection Status:    Protection On
        """;

    [Theory]
    [InlineData(SpanishEncrypted)]
    [InlineData(EnglishEncrypted)]
    public void Parse_FullyEncryptedWithProtection_IsEncryptedProtected(string output)
    {
        // Las dos salidas dan lo MISMO: el idioma de las etiquetas no cambia el resultado.
        Assert.Equal(BitLockerState.EncryptedProtected, NetworkChangePreflight.Parse(output).State);
    }

    [Fact]
    public void Parse_FullyDecrypted_IsNotEncrypted()
    {
        Assert.Equal(BitLockerState.NotEncrypted, NetworkChangePreflight.Parse(NotEncrypted).State);
    }

    /// <summary>Cifrado + protección OFF = suspendido. NO es "no cifrado".</summary>
    [Fact]
    public void Parse_EncryptedButProtectionOff_IsSuspended()
    {
        Assert.Equal(BitLockerState.EncryptedSuspended, NetworkChangePreflight.Parse(Suspended).State);
    }

    [Fact]
    public void Parse_ConversionInProgress_IsUnknownSoItDoesNotProceed()
    {
        var status = NetworkChangePreflight.Parse(InProgress);

        Assert.Equal(BitLockerState.Unknown, status.State);
        Assert.Contains("conversión", status.Note, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Un porcentaje con coma decimal (formato es-MX) NO se interpreta: se parsea con cultura invariante y el
    /// valor no parseable se descarta. El resultado es Unknown, que es el lado seguro. Se prefiere eso a
    /// adivinar que "100,0" es 100.
    /// </summary>
    [Fact]
    public void Parse_CommaDecimalSeparator_IsNotInterpretedAsEncrypted()
    {
        var output = "Percentage Encrypted: 100,0%\nProtection Status: Protection On";

        // No se afirma "cifrado": el número no se reconoce con la cultura invariante.
        Assert.Equal(BitLockerState.Unknown, NetworkChangePreflight.Parse(output).State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ERROR: No se encontró la unidad especificada.")]
    [InlineData("bitlocker no esta disponible")]
    public void Parse_UnrecognizedOutput_IsUnknown_NeverNotEncrypted(string output)
    {
        var status = NetworkChangePreflight.Parse(output);

        // El valor seguro es Unknown: "no se pudo leer" != "no hay cifrado".
        Assert.Equal(BitLockerState.Unknown, status.State);
        Assert.NotEqual(BitLockerState.NotEncrypted, status.State);
    }

    /// <summary>
    /// Cuando el proceso falla, el estado es Unknown y NO NotEncrypted. Es el caso de un error de permisos.
    /// </summary>
    [Fact]
    public async Task Read_WhenManageBdeFails_ReturnsUnknown()
    {
        var runner = new FakeProcessRunner { Handler = _ => new ProcessResult(1, "", "Acceso denegado", false) };

        var status = await new NetworkChangePreflight(runner).ReadAsync();

        Assert.Equal(BitLockerState.Unknown, status.State);
        Assert.Contains("1", status.Note, StringComparison.Ordinal);
    }

    /// <summary>Si el ejecutable no existe (edición sin BitLocker), Unknown y sin excepción que aborte el apply.</summary>
    [Fact]
    public async Task Read_WhenManageBdeCannotRun_ReturnsUnknown()
    {
        var runner = new FakeProcessRunner { Handler = _ => throw new InvalidOperationException("No such file") };

        Assert.Equal(BitLockerState.Unknown, (await new NetworkChangePreflight(runner).ReadAsync()).State);
    }

    /// <summary>
    /// **R-J: SOLO LECTURA.** El único argumento es <c>-status</c>. Nada de <c>-off</c>, <c>-suspend</c>,
    /// <c>-on</c> ni <c>-protectors</c>.
    /// </summary>
    [Fact]
    public async Task Read_OnlyEverPassesStatus_SoItCannotModifyBitLocker()
    {
        var runner = new FakeProcessRunner { Handler = _ => new ProcessResult(0, SpanishEncrypted, "", false) };

        await new NetworkChangePreflight(runner).ReadAsync();

        var request = Assert.Single(runner.Requests);
        Assert.Single(request.Arguments);
        Assert.Equal("-status", request.Arguments[0]);
        foreach (var forbidden in new[] { "-off", "-on", "-suspend", "-resume", "-protectors", "-setidentifier" })
            Assert.DoesNotContain(request.Arguments, a => a.Equals(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ningún estado produce un bloqueo duro: el preflight informa y el usuario decide.</summary>
    [Theory]
    [InlineData(BitLockerState.Unknown)]
    [InlineData(BitLockerState.NotEncrypted)]
    [InlineData(BitLockerState.EncryptedProtected)]
    [InlineData(BitLockerState.EncryptedSuspended)]
    [InlineData(BitLockerState.EncryptedUnknownProtection)]
    public void ToChecks_NeverBlocks(BitLockerState state)
    {
        var checks = NetworkChangePreflight.ToChecks(new BitLockerStatus { State = state, Note = "nota" });

        Assert.DoesNotContain(checks, c => c.Severity == PreflightSeverity.Blocking);
        Assert.Single(checks);
    }

    /// <summary>Con cifrado activo, el aviso es Warning y trae la remediación (guardar la clave).</summary>
    [Fact]
    public void ToChecks_WhenEncrypted_WarnsAboutRecoveryKey()
    {
        var check = Assert.Single(NetworkChangePreflight.ToChecks(new BitLockerStatus { State = BitLockerState.EncryptedProtected, Note = "" }));

        Assert.Equal(PreflightSeverity.Warning, check.Severity);
        Assert.Contains("clave de recuperación", check.Remediation ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sin cifrado el mensaje lo dice explícitamente: no pide clave porque no hay.</summary>
    [Fact]
    public void ToChecks_WhenNotEncrypted_IsInfoAndSaysSo()
    {
        var check = Assert.Single(NetworkChangePreflight.ToChecks(new BitLockerStatus { State = BitLockerState.NotEncrypted, Note = "" }));

        Assert.Equal(PreflightSeverity.Info, check.Severity);
        Assert.Contains("no está cifrando", check.Message, StringComparison.OrdinalIgnoreCase);
    }
}
