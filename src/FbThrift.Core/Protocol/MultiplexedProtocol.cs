// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Protocol;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Multiplexed Protocol (Thrift Multiplexed Protocol)
/// Allows multiple services over a single connection.
/// Based on fbthrift's TMultiplexedProtocol.
/// </summary>
public sealed class MultiplexedProtocol : IProtocol
{
    private readonly IProtocol _protocol;
    private readonly string _serviceName;
    private readonly string _separator = ":";
    
    public MultiplexedProtocol(IProtocol protocol, string serviceName)
    {
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
    }
    
    public void WriteMessageBegin(string name, TMessageType messageType, int seqId)
    {
        var multiplexedName = _serviceName + _separator + name;
        _protocol.WriteMessageBegin(multiplexedName, messageType, seqId);
    }
    
    public void WriteMessageEnd()
    {
        _protocol.WriteMessageEnd();
    }
    
    public void WriteStructBegin(string name)
    {
        _protocol.WriteStructBegin(name);
    }
    
    public void WriteStructEnd()
    {
        _protocol.WriteStructEnd();
    }
    
    public void WriteFieldBegin(string name, TType fieldType, int fieldId)
    {
        _protocol.WriteFieldBegin(name, fieldType, fieldId);
    }
    
    public void WriteFieldEnd()
    {
        _protocol.WriteFieldEnd();
    }
    
    public void WriteFieldStop()
    {
        _protocol.WriteFieldStop();
    }
    
    public void WriteBool(bool value) => _protocol.WriteBool(value);
    public void WriteByte(sbyte value) => _protocol.WriteByte(value);
    public void WriteI16(short value) => _protocol.WriteI16(value);
    public void WriteI32(int value) => _protocol.WriteI32(value);
    public void WriteI64(long value) => _protocol.WriteI64(value);
    public void WriteDouble(double value) => _protocol.WriteDouble(value);
    public void WriteFloat(float value) => _protocol.WriteFloat(value);
    public void WriteString(string value) => _protocol.WriteString(value);
    public void WriteBinary(ReadOnlySpan<byte> value) => _protocol.WriteBinary(value);
    public void WriteBinary(byte[] value) => _protocol.WriteBinary(value);
    
    public void WriteListBegin(TType elemType, int size) => _protocol.WriteListBegin(elemType, size);
    public void WriteListEnd() => _protocol.WriteListEnd();
    public void WriteSetBegin(TType elemType, int size) => _protocol.WriteSetBegin(elemType, size);
    public void WriteSetEnd() => _protocol.WriteSetEnd();
public void WriteMapBegin(TType keyType, TType valType, int size) => _protocol.WriteMapBegin(keyType, valType, size);
    public void WriteMapEnd() => _protocol.WriteMapEnd();
}
public interface IProtocol
{
    void WriteMessageBegin(string name, TMessageType messageType, int seqId);
    void WriteMessageEnd();
    void WriteStructBegin(string name);
    void WriteStructEnd();
    void WriteFieldBegin(string name, TType fieldType, int fieldId);
    void WriteFieldEnd();
    void WriteFieldStop();
    void WriteBool(bool value);
    void WriteByte(sbyte value);
    void WriteI16(short value);
    void WriteI32(int value);
    void WriteI64(long value);
    void WriteDouble(double value);
    void WriteFloat(float value);
    void WriteString(string value);
    void WriteBinary(ReadOnlySpan<byte> value);
    void WriteBinary(byte[] value);
    void WriteListBegin(TType elemType, int size);
    void WriteListEnd();
    void WriteSetBegin(TType elemType, int size);
    void WriteSetEnd();
    void WriteMapBegin(TType keyType, TType valType, int size);
    void WriteMapEnd();
}

/// <summary>
/// TMessageType is defined in ProtocolConstants.cs
/// </summary>