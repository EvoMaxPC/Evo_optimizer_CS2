using FragForge.Core.Abstractions;

namespace FragForge.Actions.Network;

/// <summary>
/// Resolutor de la rama no-Windows. Devuelve lista vacía: en Linux no hay <c>MSFT_NetAdapter</c> ni clase de
/// red, y devolver un adaptador inventado sería peor que no devolver ninguno (R-L).
///
/// Existe para que el resto del código no tenga que preguntar por la plataforma y para que la app arranque
/// fuera de Windows sin dependencias condicionales en cada uso.
/// </summary>
public sealed class UnsupportedNetworkAdapterResolver : INetworkAdapterResolver
{
    public Task<IReadOnlyList<NetworkAdapterIdentity>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NetworkAdapterIdentity>>([]);

    public Task<NetworkAdapterIdentity?> ResolveAsync(NetworkAdapterSelector selector, CancellationToken cancellationToken = default) =>
        Task.FromResult<NetworkAdapterIdentity?>(null);
}
