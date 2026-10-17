using Dinamico.LoadEngine.Contracts;

namespace Dinamico.LoadEngine.Transport;

// Internal seam for deterministic lifecycle tests. The public runner owns a real HTTP transport.
internal interface ILocalRunTransport : IDisposable
{
    Task<RequestObservation> SendAsync(long index, CancellationToken cancellationToken,
        Action<ResponseHeadersObservation>? observeHeaders);
}
