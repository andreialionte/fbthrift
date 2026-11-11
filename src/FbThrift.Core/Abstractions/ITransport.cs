// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Abstractions;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Base interface for all Thrift transports.
/// A transport handles the low-level I/O for sending and receiving Thrift messages.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>
    /// Gets whether the transport is currently open and connected.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Opens the transport connection.
    /// </summary>
    ValueTask OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the transport connection.
    /// </summary>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a single frame from the transport.
    /// </summary>
    /// <param name="buffer">Buffer to read the frame into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of bytes read, or 0 if the connection was closed.</returns>
    ValueTask<int> ReadFrameAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a frame to the transport.
    /// </summary>
    /// <param name="frame">The frame data to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes any buffered data to the underlying transport.
    /// </summary>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>
    /// Base interface for client transports that can initiate connections.
    /// </summary>
    public interface IClientTransport : ITransport
    {
        /// <summary>
        /// Connects to the specified endpoint.
        /// </summary>
        ValueTask ConnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a request and receives a response.
        /// </summary>
        /// <param name="request">The request data to send.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The response data.</returns>
        ValueTask<ReadOnlyMemory<byte>> SendRequestAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the remote endpoint of the connection.
        /// </summary>
        EndPoint? RemoteEndPoint { get; }
    }

/// <summary>
/// Base interface for server transports that accept connections.
/// </summary>
public interface IServerTransport : ITransport
{
    /// <summary>
    /// Starts listening for incoming connections.
    /// </summary>
    ValueTask ListenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts an incoming connection.
    /// </summary>
    /// <returns>A new transport for the accepted connection, or null if no connection is available.</returns>
    ValueTask<IClientTransport?> AcceptAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Factory for creating transport instances.
/// </summary>
public interface ITransportFactory
{
    /// <summary>
    /// Creates a client transport for the specified endpoint.
    /// </summary>
    IClientTransport CreateClientTransport(Uri endpoint, TransportOptions options);

    /// <summary>
    /// Creates a server transport for the specified endpoint.
    /// </summary>
    IServerTransport CreateServerTransport(Uri endpoint, TransportOptions options);

    /// <summary>
    /// Gets the transport name/identifier.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the supported transport schemes (e.g., "tcp", "http", "ws", "rocket").
    /// </summary>
    IReadOnlyList<string> SupportedSchemes { get; }
}

/// <summary>
/// Configuration options for transports.
/// </summary>
public sealed class TransportOptions
{
    /// <summary>
    /// Connection timeout.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Read timeout.
    /// </summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Write timeout.
    /// </summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum frame size.
    /// </summary>
    public int MaxFrameSize { get; set; } = 16 * 1024 * 1024; // 16MB

    /// <summary>
    /// Whether to enable TLS/SSL.
    /// </summary>
    public bool UseTls { get; set; } = false;

    /// <summary>
    /// Client certificate for mutual TLS.
    /// </summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2? ClientCertificate { get; set; }

    /// <summary>
    /// Server certificate for TLS.
    /// </summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2? ServerCertificate { get; set; }

    /// <summary>
    /// Custom headers to send with requests.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    /// Compression algorithm to use.
    /// </summary>
    public CompressionAlgorithm Compression { get; set; } = CompressionAlgorithm.None;

    /// <summary>
    /// Checksum algorithm to use.
    /// </summary>
    public ChecksumAlgorithm Checksum { get; set; } = ChecksumAlgorithm.None;

    /// <summary>
    /// Additional transport-specific properties.
    /// </summary>
    public Dictionary<string, object> Properties { get; set; } = new();
}

/// <summary>
/// Base interface for all Thrift protocols.
/// A protocol defines how Thrift data structures are serialized to/from bytes.
/// </summary>
public interface IProtocol : IAsyncDisposable
{
    /// <summary>
    /// Gets the protocol identifier.
    /// </summary>
    ProtocolId Id { get; }

    /// <summary>
    /// Gets the protocol name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Reads a value of type T from the input stream.
    /// </summary>
    ValueTask<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default) where T : class, IThriftSerializable;

    /// <summary>
    /// Writes a value to the output stream.
    /// </summary>
    ValueTask WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default) where T : class, IThriftSerializable;

    /// <summary>
    /// Reads a message header.
    /// </summary>
    ValueTask<(string MethodName, MessageType MessageType, int SequenceId)> ReadMessageHeaderAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a message header.
    /// </summary>
    ValueTask WriteMessageHeaderAsync(Stream stream, string methodName, MessageType messageType, int sequenceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Message types in Thrift protocol.
/// </summary>
public enum MessageType : byte
{
    Call = 1,
    Reply = 2,
    Exception = 3,
    Oneway = 4
}

/// <summary>
/// Protocol identifiers.
/// </summary>
public enum ProtocolId : ushort
{
    Binary = 0x0001,
    Compact = 0x0002,
    Json = 0x0003,
    SimpleJson = 0x0004,
    Terse = 0x0005,
    Header = 0x0006
}

/// <summary>
/// Factory for creating protocol instances.
/// </summary>
public interface IProtocolFactory
{
    /// <summary>
    /// Creates a protocol instance for the specified protocol ID.
    /// </summary>
    IProtocol Create(ProtocolId protocolId);

    /// <summary>
    /// Creates a protocol instance for the specified protocol name.
    /// </summary>
    IProtocol Create(string protocolName);

    /// <summary>
    /// Gets all supported protocol IDs.
    /// </summary>
    IReadOnlyList<ProtocolId> SupportedProtocols { get; }
}

/// <summary>
/// Interface for types that can be serialized/deserialized by Thrift protocols.
/// </summary>
public interface IThriftSerializable
{
    /// <summary>
    /// Writes this value to the protocol writer.
    /// </summary>
    void Write(IProtocolWriter writer);

    /// <summary>
    /// Reads this value from the protocol reader.
    /// </summary>
    void Read(IProtocolReader reader);
}

/// <summary>
/// Writer interface for protocol implementations.
/// </summary>
public interface IProtocolWriter
{
    void WriteBool(bool value);
    void WriteByte(byte value);
    void WriteI16(short value);
    void WriteI32(int value);
    void WriteI64(long value);
    void WriteDouble(double value);
    void WriteFloat(float value);
    void WriteString(string value);
    void WriteBinary(byte[] value);
    void WriteListBegin(ListType listType, int size);
    void WriteListEnd();
    void WriteSetBegin(SetType setType, int size);
    void WriteSetEnd();
    void WriteMapBegin(MapType mapType, int size);
    void WriteMapEnd();
    void WriteStructBegin(string? name);
    void WriteStructEnd();
    void WriteFieldBegin(string name, FieldType fieldType, short fieldId);
    void WriteFieldEnd();
    void WriteFieldStop();
}

/// <summary>
/// Reader interface for protocol implementations.
/// </summary>
public interface IProtocolReader
{
    bool ReadBool();
    byte ReadByte();
    short ReadI16();
    int ReadI32();
    long ReadI64();
    double ReadDouble();
    float ReadFloat();
    string ReadString();
    byte[] ReadBinary();
    (ListType listType, int size) ReadListBegin();
    void ReadListEnd();
    (SetType setType, int size) ReadSetBegin();
    void ReadSetEnd();
    (MapType mapType, int size) ReadMapBegin();
    void ReadMapEnd();
    void ReadStructBegin();
    void ReadStructEnd();
    (string name, FieldType fieldType, short fieldId) ReadFieldBegin();
    void ReadFieldEnd();
}

/// <summary>
/// List type for protocol serialization.
/// </summary>
public enum ListType : byte { Byte = 0, I16 = 1, I32 = 2, I64 = 3, String = 4, Struct = 5 }

/// <summary>
/// Set type for protocol serialization.
/// </summary>
public enum SetType : byte { Byte = 0, I16 = 1, I32 = 2, I64 = 3, String = 3, Struct = 4 }

/// <summary>
/// Map type for protocol serialization.
/// </summary>
public enum MapType : byte { Byte = 0, I16 = 1, I32 = 2, I64 = 3, String = 4, Struct = 5 }

/// <summary>
/// Field type for protocol serialization.
/// </summary>
public enum FieldType : byte { Stop = 0, Bool = 1, Byte = 2, I16 = 3, I32 = 4, I64 = 5, Double = 6, Float = 7, String = 8, Struct = 9, Map = 10, Set = 11, List = 12 }