// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

/// <summary>
/// High-performance SocketAsyncEventArgs pool for zero-Task overhead UDP I/O.
/// Bypasses async/await state machine for maximum throughput.
/// Based on Kestrel's SAEA pooling pattern.
/// </summary>
public sealed class SocketAsyncEventArgsPool : IDisposable
{
    private readonly ConcurrentQueue<SocketAsyncEventArgs> _pool = new();
    private readonly int _bufferSize;
    private readonly int _maxPoolSize;
    private readonly MemoryPool<byte> _pinnedPool;
    private int _totalCreated = 0;
    private int _inUse = 0;
    private bool _disposed = false;

    /// <summary>
    /// Initializes a new SAEA pool.
    /// </summary>
    /// <param name="maxPoolSize">Maximum number of SAEA objects to pool</param>
    /// <param name="bufferSize">Size of each receive buffer</param>
    public SocketAsyncEventArgsPool(int maxPoolSize = 128, int bufferSize = 65536)
    {
        _maxPoolSize = maxPoolSize;
        _bufferSize = bufferSize;
        
        // Use POH for pinned buffers
        _pinnedPool = MemoryPool<byte>.Create(bufferSize, maxPoolSize * 2, pinned: true);
        
        // Pre-allocate initial pool
        for (int i = 0; i < Math.Min(maxPoolSize, 16); i++)
        {
            _pool.Enqueue(CreateSocketAsyncEventArgs());
        }
    }
    
    /// <summary>
    /// Rents an SAEA from the pool, creating new if necessary.
    /// </summary>
    public SocketAsyncEventArgs Rent()
    {
        if (_pool.TryDequeue(out var saea))
        {
            Interlocked.Increment(ref _inUse);
            return saea;
        }
        
        // Create new if pool exhausted
        if (Interlocked.Increment(ref _totalCreated) <= _maxPoolSize)
        {
            var saea = CreateSocketAsyncEventArgs();
            Interlocked.Increment(ref _inUse);
            return saea;
        }
        
        // Pool exhausted, wait for available
        while (!_pool.TryDequeue(out var saea))
        {
            Thread.SpinWait(10);
        }
        Interlocked.Increment(ref _inUse);
        return saea;
    }
    
    /// <summary>
    /// Returns an SAEA to the pool.
    /// </summary>
    public void Return(SocketAsyncEventArgs saea)
    {
        if (saea == null) return;
        
        // Reset SAEA state
        saea.RemoteEndPoint = null;
        saea.SetBuffer(null, 0, 0);
        
        // Return buffer to POH pool if it was from our pool
        if (saea.UserToken is IMemoryOwner<byte> owner)
        {
            owner.Dispose();
            saea.UserToken = null;
        }
        
        if (!_disposed)
        {
            _pool.Enqueue(saea);
            Interlocked.Decrement(ref _inUse);
        }
        else
        {
            // Pool disposed, dispose SAEA
            saea.Dispose();
        }
    }
    
    /// <summary>
    /// Creates a new SAEA with a pinned buffer from POH.
    /// </summary>
    private SocketAsyncEventArgs CreateSocketAsyncEventArgs()
    {
        var saea = new SocketAsyncEventArgs();
        var buffer = _pinnedPool.Rent(_bufferSize);
        saea.SetBuffer(buffer.Memory);
        saea.UserToken = buffer; // Store owner for return
        return saea;
    }
    
    /// <summary>
    /// Gets the number of SAEA objects currently in use.
    /// </summary>
    public int InUseCount => _inUse;
    
    /// <summary>
    /// Gets the total number of SAEA objects created.
    /// </summary>
    public int TotalCreated => _totalCreated;
    
    /// <summary>
    /// Gets the number of available SAEA objects in the pool.
    /// </summary>
    public int AvailableCount => _pool.Count;
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        // Dispose all SAEA in pool
        while (_pool.TryDequeue(out var saea))
        {
            saea.Dispose();
        }
        
        _pinnedPool.Dispose();
    }
}

/// <summary>
/// High-performance UDP batch receiver using SAEA pool and completion callbacks.
/// Zero-Task overhead receive pattern for maximum throughput.
/// </summary>
public sealed class SaeaUdpBatchReceiver : IDisposable
{
    private readonly Socket _socket;
    private readonly SocketAsyncEventArgsPool _saeaPool;
    private readonly int _batchSize;
    private readonly Action<int, EndPoint> _onBatchReceived;
    private volatile bool _disposed = false;
    
    public SaeaUdpBatchReceiver(
        EndPoint endpoint, 
        int batchSize = 64, 
        int bufferSize = 65536,
        Action<int, EndPoint> onBatchReceived = null)
    {
        _batchSize = batchSize;
        _onBatchReceived = onBatchReceived ?? (_ => { });
        
        _saeaPool = new SocketAsyncEventArgsPool(batchSize * 2, 65536);
        
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(endpoint);
        
        // Enable UDP GRO if supported
        TryEnableGro();
        
        // Start initial receives
        for (int i = 0; i < _batchSize; i++)
        {
            StartReceive();
        }
    }
    
    private void StartReceive()
    {
        var args = _saeaPool.Rent();
        args.Completed += OnReceiveCompleted;
        
        if (!_socket.ReceiveFromAsync(args))
        {
            // Completed synchronously
            OnReceiveCompleted(this, args);
        }
    }
    
    private void OnReceiveCompleted(object sender, SocketAsyncEventArgs args)
    {
        if (_disposed) 
        {
            _saeaPool.Return(args);
            return;
        }
        
        try
        {
            int bytesReceived = args.BytesTransferred;
            EndPoint remoteEndPoint = args.RemoteEndPoint!;
            
            if (bytesReceived > 0)
            {
                // Process the received data
                _onBatchReceived(bytesReceived, remoteEndPoint);
            }
        }
        catch
        {
            // Ignore processing errors
        }
        finally
        {
            // Return to pool and start next receive
            _saeaPool.Return(args);
            
            if (!_disposed)
            {
                StartReceive();
            }
        }
    }
    
    private bool TryEnableGro()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _socket.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)40, 1); // SO_GRO
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                const int SIO_UDP_GRO = -2147483648 + (11 << 16);
                var inValue = BitConverter.GetBytes(1);
                var outValue = new byte[4];
                _socket.IOControl(-2147483648 + (11 << 16), inValue, outValue);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _socket.Dispose();
        _saeaPool.Dispose();
    }
}
