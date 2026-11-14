namespace FragForge.Core.Analysis;

/// <summary>
/// Método de percentil usado en todo el motor. Se declara explícitamente porque el número CAMBIA según el
/// método y la diferencia no es cosmética: con el ejemplo de referencia (1000 frames, 990 de 10 ms y 10 de
/// 50 ms) el percentil 99 vale 10.4 ms con interpolación lineal y 10 ms con rango más cercano.
/// </summary>
public enum PercentileMethod
{
    /// <summary>
    /// Interpolación lineal, tipo 7 de Hyndman y Fan, equivalente a <c>numpy.percentile</c> por defecto y a
    /// <c>PERCENTILE.INC</c> de Excel. Con N valores ordenados y probabilidad p en [0,1]:
    /// <c>h = (N - 1) * p</c>; índices base 0; se interpola entre <c>floor(h)</c> y <c>ceil(h)</c>.
    /// Es el que usa el motor.
    /// </summary>
    LinearType7
}
