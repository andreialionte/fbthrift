namespace FbThrift.Tests;

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using FbThrift.Transports.Tcp;

public class TransportTests
{
    [Fact]
    public async Task SendRequestAsync_RoundTripOverSocket_ReturnsReplyBody()
    {
        // Arrange: a loopback listener and a connected client socket.
        await using SocketListener listener = new();
        await listener.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));

        using Socket clientSocket = new(SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        await clientSocket.ConnectAsync(listener.EndPoint);

        using CancellationTokenSource connectCts = new(TimeSpan.FromSeconds(5));
        Socket serverSocket = await listener.AcceptAsync().WaitAsync(connectCts.Token);

        byte[] requestBody = System.Text.Encoding.UTF8.GetBytes("ping");
        byte[] expectedReply = System.Text.Encoding.UTF8.GetBytes("pong");

        // Server task: read the request frame, then write a reply frame with the same seq id.
        Task serverTask = ServerRoundTripAsync(serverSocket, requestBody, expectedReply);

        // Act: client sends request and awaits reply.
        await using SocketFbThriftTransport transport = new(clientSocket);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        ReadOnlyMemory<byte> reply = await transport.SendRequestAsync(requestBody, cts.Token);

        // Assert.
        await serverTask;
        Assert.Equal(expectedReply, reply.ToArray());
    }

    [Fact]
    public async Task SendRequestAsync_Cancellation_CancelsPendingRequest()
    {
        await using SocketListener listener = new();
        await listener.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));

        using Socket clientSocket = new(SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        await clientSocket.ConnectAsync(listener.EndPoint);
        using CancellationTokenSource connectCts = new(TimeSpan.FromSeconds(5));
        Socket serverSocket = await listener.AcceptAsync().WaitAsync(connectCts.Token);

        // Never reply; cancel the client's request.
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));
        await using SocketFbThriftTransport transport = new(clientSocket);

        Func<Task> act = () => transport.SendRequestAsync(System.Text.Encoding.UTF8.GetBytes("ping"), cts.Token).AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);

        serverSocket.Dispose();
    }

    private static async Task ServerRoundTripAsync(Socket serverSocket, byte[] expectedRequest, byte[] replyBody)
    {
        // Read frames until we see one matching the expected request body, then reply.
        while (true)
        {
            // Read the fixed 8-byte header.
            byte[] header = await ReadExactlyAsync(serverSocket, 8);
            ushort bodySize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            short seqId = BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(6));

            byte[] body = await ReadExactlyAsync(serverSocket, bodySize);

            if (body.SequenceEqual(expectedRequest))
            {
                // Write reply frame with the same seq id.
                byte[] replyHeader = new byte[8];
                replyHeader[0] = 0x80;
                replyHeader[1] = 0x01;
                replyHeader[2] = 1;
                replyHeader[3] = 0;
                BinaryPrimitives.WriteUInt16BigEndian(replyHeader.AsSpan(4), (ushort)replyBody.Length);
                BinaryPrimitives.WriteInt16BigEndian(replyHeader.AsSpan(6), seqId);

                await serverSocket.SendAsync(replyHeader, SocketFlags.None);
                await serverSocket.SendAsync(replyBody, SocketFlags.None);
                return;
            }
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Socket socket, int count)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await socket.ReceiveAsync(buffer.AsMemory(read, count - read), SocketFlags.None);
            if (n == 0)
                throw new EndOfStreamException("Socket closed before reading enough bytes.");
            read += n;
        }
        return buffer;
    }
}

/// <summary>
/// A small TCP listener helper for tests.
/// </summary>
public sealed class SocketListener : IAsyncDisposable
{
    private readonly Socket _socket = new(SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
    private bool _disposed;

    public Task StartAsync(IPEndPoint endPoint)
    {
        _socket.Bind(endPoint);
        _socket.Listen();
        return Task.CompletedTask;
    }

    public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    public Task<Socket> AcceptAsync() => _socket.AcceptAsync();

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return default;
        }

        _disposed = true;
        try
        {
            _socket.Dispose();
        }
        catch
        {
            // Disposal should not throw.
        }

        return default;
    }
}
