// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Exceptions;

using System;
using System.Runtime.Serialization;

/// <summary>
/// Base exception for all Thrift-related errors.
/// </summary>
[Serializable]
public class ThriftException : Exception
{
    public ThriftException() { }
    public ThriftException(string message) : base(message) { }
    public ThriftException(string message, Exception innerException) : base(message, innerException) { }
    protected ThriftException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>
/// Exception thrown when a transport operation fails.
/// </summary>
[Serializable]
public class TransportException : ThriftException
{
    public TransportException() { }
    public TransportException(string message) : base(message) { }
    public TransportException(string message, Exception innerException) : base(message, innerException) { }
    protected TransportException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>
/// Exception thrown when a connection fails.
/// </summary>
[Serializable]
public class ConnectionException : TransportException
{
    public ConnectionException() { }
    public ConnectionException(string message) : base(message) { }
    public ConnectionException(string message, Exception innerException) : base(message, innerException) { }
    protected ConnectionException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>
/// Exception thrown when a connection times out.
/// </summary>
[Serializable]
public class ConnectionTimeoutException : ConnectionException
{
    public ConnectionTimeoutException() { }
    public ConnectionTimeoutException(string message) : base(message) { }
    public ConnectionTimeoutException(string message, Exception innerException) : base(message, innerException) { }
    protected ConnectionTimeoutException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>
/// Exception thrown when a protocol operation fails.
/// </summary>
[Serializable]
public class ProtocolException : ThriftException
{
    public ProtocolException() { }
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception innerException) : base(message, innerException) { }
    protected ProtocolException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>
/// Exception thrown when serialization/deserialization fails.
/// </summary>
[Serializable]
public class SerializationException : ThriftException
{
    public SerializationException() { }
    public SerializationException(string message) : base(message) { }
    public SerializationException(string message, Exception innerException) : base(message, innerException) { }
    protected SerializationException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>
/// Exception thrown when an RPC call fails.
/// </summary>
[Serializable]
public class RpcException : ThriftException
{
    public RpcException() { }
    public RpcException(string message) : base(message) { }
    public RpcException(string message, Exception innerException) : base(message, innerException) { }
    protected RpcException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>
/// Exception thrown when an RPC call times out.
/// </summary>
[Serializable]
public class RpcTimeoutException : RpcException
{
    public RpcTimeoutException() { }
    public RpcTimeoutException(string message) : base(message) { }
    public RpcTimeoutException(string message, Exception innerException) : base(message, innerException) { }
    protected RpcTimeoutException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>
/// Exception thrown when an RPC is cancelled.
/// </summary>
[Serializable]
public class RpcCancelledException : RpcException
{
    public RpcCancelledException() { }
    public RpcCancelledException(string message) : base(message) { }
    public RpcCancelledException(string message, Exception innerException) : base(message, innerException) { }
    protected RpcCancelledException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>
/// Exception thrown when a method is not found.
/// </summary>
[Serializable]
public class MethodNotFoundException : RpcException
{
    public string MethodName { get; }

    public MethodNotFoundException(string methodName) : base($"Method '{methodName}' not found")
    {
        MethodName = methodName;
    }

    public MethodNotFoundException(string methodName, Exception innerException) : base($"Method '{methodName}' not found", innerException)
    {
        MethodName = methodName;
    }

    protected MethodNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
        MethodName = info.GetString("MethodName") ?? string.Empty;
    }

    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue("MethodName", MethodName);
    }
}

/// <summary>
/// Exception thrown when a service is not available.
/// </summary>
[Serializable]
public class ServiceUnavailableException : RpcException
{
    public ServiceUnavailableException() { }
    public ServiceUnavailableException(string message) : base(message) { }
    public ServiceUnavailableException(string message, Exception innerException) : base(message, innerException) { }
    protected ServiceUnavailableException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>
/// Exception thrown when protocol version is not supported.
/// </summary>
[Serializable]
public class UnsupportedProtocolVersionException : ProtocolException
{
    public UnsupportedProtocolVersionException() { }
    public UnsupportedProtocolVersionException(string message) : base(message) { }
    public UnsupportedProtocolVersionException(string message, Exception innerException) : base(message, innerException) { }
    protected UnsupportedProtocolVersionException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>
/// Exception thrown when compression is not supported.
/// </summary>
[Serializable]
public class UnsupportedCompressionException : ThriftException
{
    public UnsupportedCompressionException() { }
    public UnsupportedCompressionException(string message) : base(message) { }
    public UnsupportedCompressionException(string message, Exception innerException) : base(message, innerException) { }
    protected UnsupportedCompressionException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>
/// Exception thrown when checksum verification fails.
/// </summary>
[Serializable]
public class ChecksumMismatchException : ThriftException
{
    public ChecksumMismatchException() { }
    public ChecksumMismatchException(string message) : base(message) { }
    public ChecksumMismatchException(string message, Exception innerException) : base(message, innerException) { }
    protected ChecksumMismatchException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>
/// Exception thrown when a field is not found in a struct.
/// </summary>
[Serializable]
public class FieldNotFoundException : SerializationException
{
    public string FieldName { get; }

    public FieldNotFoundException(string fieldName) : base($"Field '{fieldName}' not found")
    {
        FieldName = fieldName;
    }

    public FieldNotFoundException(string fieldName, Exception innerException) : base($"Field '{fieldName}' not found", innerException)
    {
        FieldName = fieldName;
    }

    protected FieldNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
        FieldName = info.GetString("FieldName") ?? string.Empty;
    }

    public override void GetObjectData(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue("FieldName", FieldName);
    }
}