using System;

namespace FbThrift.Transports.Tcp;

/// <summary>
/// Abstraction over a Thrift transport that sends request frames and awaits matching reply frames.
/// </summary>
public interface IFbThriftTransport : IAsyncDisposable
{
    /// <summary>
    /// Sends a request frame and asynchronously awaits the matching reply frame.
    /// </summary>
    /// <param name="body">The serialized request body to send.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation.</param>
    /// <returns>A task whose result is the deserialized reply body.</returns>
    ValueTask<ReadOnlyMemory<byte>> SendRequestAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default);
}
