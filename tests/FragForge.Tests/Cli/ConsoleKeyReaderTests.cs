using FragForge.Cli;
using Xunit;

namespace FragForge.Tests.Cli;

public sealed class ConsoleKeyReaderTests
{
    // Se inyectan los delegados sin tocar la consola real.
    private static ConsoleKeyReader Create(Func<bool> redirected, Func<bool> available, Func<ConsoleKey> read) =>
        new(redirected, available, read);

    [Fact]
    public void Redirected_NeverReadsKeys()
    {
        var calls = 0;
        var reader = Create(() => true, () => { calls++; return true; }, () => { calls++; return ConsoleKey.F10; });
        // Entrada redirigida: cero llamadas a disponibilidad y lectura.
        Assert.False(reader.IsAvailable);
        Assert.Null(reader.TryReadKey());
        Assert.Equal(0, calls);
    }

    [Fact]
    public void NoPendingKey_ReturnsNull()
    {
        var calls = 0;
        var reader = Create(() => false, () => false, () => { calls++; return ConsoleKey.F10; });
        // Sin tecla pendiente: no se llama al lector.
        Assert.True(reader.IsAvailable);
        Assert.Null(reader.TryReadKey());
        Assert.Equal(0, calls);
    }

    [Fact]
    public void PendingKey_ReturnsKey()
    {
        var reader = Create(() => false, () => true, () => ConsoleKey.F10);
        // El delegado devuelve F10, sin convertirla en otro valor.
        Assert.Equal(ConsoleKey.F10, reader.TryReadKey());
    }

    [Fact]
    public void DelegateThrows_ReaderBecomesUnavailable()
    {
        var reader = Create(() => false, () => throw new InvalidOperationException("fallo"), () => ConsoleKey.F10);
        // Una excepción se traduce a null y el lector queda no disponible.
        Assert.Null(reader.TryReadKey());
        Assert.False(reader.IsAvailable);
    }
}
