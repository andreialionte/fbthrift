// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Protocols.Json;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// JSON protocol writer (fbthrift TJSONProtocolWriter equivalent).
/// </summary>
public sealed class JsonProtocolWriter
{
    private readonly ArrayBufferWriter<byte> _output;
    private readonly Utf8JsonWriter _writer;
    
    public JsonProtocolWriter()
    {
        _output = new ArrayBufferWriter<byte>(4096);
        _writer = new Utf8JsonWriter(_output, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });
    }
    
    public JsonProtocolWriter(int initialCapacity)
    {
        _output = new ArrayBufferWriter<byte>(initialCapacity);
        _writer = new Utf8JsonWriter(_output, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });
    }
    
    public ReadOnlyMemory<byte> WrittenMemory => _output.WrittenMemory;
    public ReadOnlySpan<byte> WrittenSpan => _output.WrittenSpan;
    
    public void Reset()
    {
        _output.Clear();
        _writer.Reset(_output);
    }
    
    public void Flush()
    {
        _writer.Flush();
    }
    
    // ===== Message Writing =====
    
    public void WriteMessageBegin(string name, TMessageType messageType, int seqId)
    {
        _writer.WriteStartObject();
        _writer.WriteNumber("seqId", seqId);
        _writer.WriteString("method", name);
        _writer.WriteNumber("type", (int)messageType);
        _writer.WritePropertyName("params");
        _writer.WriteStartArray();
    }
    
    public void WriteMessageEnd()
    {
        _writer.WriteEndArray();
        _writer.WriteEndObject();
    }
    
    // ===== Struct Writing =====
    
    public void WriteStructBegin(string name)
    {
        // Nothing special needed for JSON
    }
    
    public void WriteStructEnd()
    {
        // Nothing to do
    }
    
    // ===== Field Writing =====
    
    public void WriteFieldBegin(string name, TType fieldType, int fieldId)
    {
        // In JSON, we just write the property name
        // The fieldId is not typically encoded in JSON
    }
    
    public void WriteFieldEnd()
    {
        // Nothing to do
    }
    
    public void WriteFieldStop()
    {
        // Nothing to do
    }
    
    // ===== Primitive Writing =====
    
    public void WriteBool(bool value)
    {
        _writer.WriteBooleanValue(value);
    }
    
    public void WriteByte(sbyte value)
    {
        _writer.WriteNumberValue(value);
    }
    
    public void WriteI16(short value)
    {
        _writer.WriteNumberValue(value);
    }
    
    public void WriteI32(int value)
    {
        _writer.WriteNumberValue(value);
    }
    
    public void WriteI64(long value)
    {
        _writer.WriteNumberValue(value);
    }
    
    public void WriteDouble(double value)
    {
        _writer.WriteNumberValue(value);
    }
    
    public void WriteFloat(float value)
    {
        _writer.WriteNumberValue(value);
    }
    
    public void WriteString(string value)
    {
        _writer.WriteStringValue(value ?? string.Empty);
    }
    
    public void WriteBinary(ReadOnlySpan<byte> value)
    {
        _writer.WriteStringValue(Convert.ToBase64String(value));
    }
    
    public void WriteBinary(byte[] value)
    {
        WriteBinary(value.AsSpan());
    }
    
    // ===== Container Writing =====
    
    public void WriteListBegin(TType elemType, int size)
    {
        // In JSON, we just write array start
    }
    
    public void WriteListEnd()
    {
        // Nothing to do
    }
    
    public void WriteSetBegin(TType elemType, int size)
    {
        WriteListBegin(elemType, size);
    }
    
    public void WriteSetEnd()
    {
        // Nothing to do
    }
    
    public void WriteMapBegin(TType keyType, TType valType, int size)
    {
        // In JSON, we write object start
    }
    
    public void WriteMapEnd()
    {
        // Nothing to do
    }
    
    public void WriteStructBegin(string name)
    {
        // Nothing special needed
    }
    
    public void WriteStructEnd()
    {
        // Nothing to do
    }
    
    public void WriteFieldBegin(string name, TType fieldType, int fieldId)
    {
        // In JSON, write property name
        _writer.WritePropertyName(name);
    }
    
    public void WriteFieldEnd()
    {
        // Nothing to do
    }
    
    public void WriteFieldStop()
    {
        // Nothing to do
    }
    
    // ===== Value Writing Helpers =====
    
    public void WriteBool(bool value) => _writer.WriteBooleanValue(value);
    public void WriteByte(sbyte value) => _writer.WriteNumberValue(value);
    public void WriteI16(short value) => _writer.WriteNumberValue(value);
    public void WriteI32(int value) => _writer.WriteNumberValue(value);
    public void WriteI64(long value) => _writer.WriteNumberValue(value);
    public void WriteDouble(double value) => _writer.WriteNumberValue(value);
    public void WriteFloat(float value) => _writer.WriteNumberValue(value);
    public void WriteString(string value) => _writer.WriteStringValue(value ?? string.Empty);
    public void WriteBinary(ReadOnlySpan<byte> value) => _writer.WriteStringValue(Convert.ToBase64String(value));
    public void WriteBinary(byte[] value) => WriteBinary(value.AsSpan());
    
    // ===== Container Writing =====
    
    public void WriteListBegin(TType elemType, int size)
    {
        _writer.WriteStartArray();
    }
    
    public void WriteListEnd()
    {
        _writer.WriteEndArray();
    }
    
    public void WriteSetBegin(TType elemType, int size)
    {
        _writer.WriteStartArray();
    }
    
    public void WriteSetEnd()
    {
        _writer.WriteEndArray();
    }
    
    public void WriteMapBegin(TType keyType, TType valType, int size)
    {
        _writer.WriteStartObject();
    }
    
    public void WriteMapEnd()
    {
        _writer.WriteEndObject();
    }
    
    public void WriteStructBegin(string name)
    {
        _writer.WriteStartObject();
    }
    
    public void WriteStructEnd()
    {
        _writer.WriteEndObject();
    }
    
    public void WriteFieldBegin(string name, TType fieldType, int fieldId)
    {
        _writer.WritePropertyName(name);
    }
    
    public void WriteFieldEnd()
    {
        // Nothing to do
    }
    
    public void WriteFieldStop()
    {
        // Nothing to do
    }
    
    // ===== Value Writing =====
    
    public void WriteBool(bool value)
    {
        _writer.WriteBooleanValue(value);
    }
    
    public void WriteByte(sbyte value)
    {
        _writer.WriteNumberValue(value);
    }
    
    public void WriteI16(short value)
    {
        _writer.WriteNumberValue(value);
    }
    
    public void WriteI32(int value)
    {
        _writer.WriteNumberValue(value);
    }
    
    public void WriteI64(long value)
    {
        _writer.WriteNumberValue(value);
    }
    
    public void WriteDouble(double value)
    {
        _writer.WriteNumberValue(value);
    }
    
    public void WriteFloat(float value)
    {
        _writer.WriteNumberValue(value);
    }
    
    public void WriteString(string value)
    {
        _writer.WriteStringValue(value ?? string.Empty);
    }
    
    public void WriteBinary(ReadOnlySpan<byte> value)
    {
        _writer.WriteStringValue(Convert.ToBase64String(value));
    }
    
    public void WriteBinary(byte[] value)
    {
        WriteBinary(value.AsSpan());
    }
    
    // ===== Helpers =====
    
    public ReadOnlyMemory<byte> WrittenMemory => _output.WrittenMemory;
    public ReadOnlySpan<byte> WrittenSpan => _output.WrittenSpan;
    
    public void Flush()
    {
        _writer.Flush();
    }
    
    public void Reset()
    {
        _output.Clear();
        _writer.Reset(_output);
    }
    
    public void WriteToStream(Stream stream)
    {
        stream.Write(_output.WrittenSpan);
    }
    
    public byte[] ToArray() => _output.WrittenSpan.ToArray();
}

/// <summary>
/// JSON protocol reader (fbthrift TJSONProtocolReader equivalent).
/// </summary>
public sealed class JsonProtocolReader
{
    private readonly Utf8JsonReader _reader;
    private int _depth = 0;
    
    public JsonProtocolReader(ReadOnlyMemory<byte> input)
    {
        _reader = new Utf8JsonReader(input);
    }
    
    public JsonProtocolReader(ReadOnlySequence<byte> input)
    {
        _reader = new Utf8JsonReader(input);
    }
    
    // ===== Message Reading =====
    
    public void ReadMessageBegin(out string name, out TMessageType messageType, out int seqId)
    {
        ReadExpect(JsonTokenType.StartObject);
        
        // Read seqId
        ReadExpect(JsonTokenType.PropertyName);
        if (_reader.GetString() != "seqId")
            throw new InvalidDataException("Expected 'seqId' property");
        
        ReadExpect(JsonTokenType.Number);
        seqId = _reader.GetInt32();
        
        // Read method name
        ReadExpect(JsonTokenType.PropertyName);
        if (_reader.GetString() != "method")
            throw new InvalidDataException("Expected 'method' property");
        
        ReadExpect(JsonTokenType.String);
        name = _reader.GetString() ?? string.Empty;
        
        // Read type
        ReadExpect(JsonTokenType.PropertyName);
        if (_reader.GetString() != "type")
            throw new InvalidDataException("Expected 'type' property");
        
        ReadExpect(JsonTokenType.Number);
        messageType = (TMessageType)_reader.GetInt32();
        
        // Read params array
        ReadExpect(JsonTokenType.PropertyName);
        if (_reader.GetString() != "params")
            throw new InvalidDataException("Expected 'params' property");
        
        ReadExpect(JsonTokenType.StartArray);
    }
    
    public void ReadMessageEnd()
    {
        // Expect end object
        while (_reader.TokenType != JsonTokenType.EndObject)
        {
            if (!_reader.Read())
                throw new InvalidDataException("Unexpected end of JSON");
        }
    }
    
    // ===== Struct Reading =====
    
    public void ReadStructBegin(out string name)
    {
        // JSON doesn't encode struct names
        name = string.Empty;
    }
    
    public void ReadStructEnd()
    {
        // Expect end object
        while (_reader.TokenType != JsonTokenType.EndObject)
        {
            if (!_reader.Read())
                throw new InvalidDataException("Unexpected end of JSON");
        }
    }
    
    // ===== Field Reading =====
    
    public void ReadFieldBegin(out string name, out TType fieldType, out int fieldId)
    {
        name = string.Empty;
        fieldType = TType.Stop;
        fieldId = 0;
        
        // In JSON, fields are properties
        if (_reader.TokenType == JsonTokenType.PropertyName)
        {
            name = _reader.GetString() ?? string.Empty;
            // We can't easily determine field type/ID from JSON without schema
            fieldType = TType.Stop;
            fieldId = 0;
        }
    }
    
    public void ReadFieldEnd()
    {
        // Nothing to do
    }
    
    // ===== Primitive Reading =====
    
    public bool ReadBool()
    {
        if (_reader.TokenType != JsonTokenType.True && _reader.TokenType != JsonTokenType.False)
            throw new InvalidDataException("Expected boolean value");
        
        return _reader.GetBoolean();
    }
    
    public sbyte ReadByte()
    {
        return (sbyte)ReadI32();
    }
    
    public short ReadI16()
    {
        return (short)ReadI32();
    }
    
    public int ReadI32()
    {
        if (_reader.TokenType != JsonTokenType.Number)
            throw new InvalidDataException("Expected number");
        
        return _reader.GetInt32();
    }
    
    public long ReadI64()
    {
        return _reader.GetInt64();
    }
    
    public double ReadDouble()
    {
        return _reader.GetDouble();
    }
    
    public float ReadFloat()
    {
        return (float)ReadDouble();
    }
    
    public string ReadString()
    {
        if (_reader.TokenType != JsonTokenType.String)
            throw new InvalidDataException("Expected string");
        
        return _reader.GetString() ?? string.Empty;
    }
    
    public ReadOnlyMemory<byte> ReadBinary()
    {
        string base64 = ReadString();
        return Convert.FromBase64String(base64);
    }
    
    // ===== Container Reading =====
    
    public void ReadListBegin(out TType elemType, out int size)
    {
        if (_reader.TokenType != JsonTokenType.StartArray)
            throw new InvalidDataException("Expected array start");
        
        // Can't determine element type from JSON without schema
        elemType = TType.Stop;
        size = 0;
    }
    
    public void ReadListEnd()
    {
        // Skip to end of array
        while (_reader.TokenType != JsonTokenType.EndArray)
        {
            if (!_reader.Read())
                throw new InvalidDataException("Unexpected end of JSON");
        }
    }
    
    public void ReadSetBegin(out TType elemType, out int size)
    {
        ReadListBegin(out elemType, out size);
    }
    
    public void ReadSetEnd()
    {
        ReadListEnd();
    }
    
    public void ReadMapBegin(out TType keyType, out TType valType, out int size)
    {
        if (_reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException("Expected object start");
        
        keyType = TType.Stop;
        valType = TType.Stop;
        size = 0;
    }
    
    public void ReadMapEnd()
    {
        while (_reader.TokenType != JsonTokenType.EndObject)
        {
            if (!_reader.Read())
                throw new InvalidDataException("Unexpected end of JSON");
        }
    }
    
    public void ReadStructBegin(out string name)
    {
        if (_reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException("Expected object start");
        
        name = string.Empty;
    }
    
    public void ReadStructEnd()
    {
        while (_reader.TokenType != JsonTokenType.EndObject)
        {
            if (!_reader.Read())
                throw new InvalidDataException("Unexpected end of JSON");
        }
    }
    
    public void ReadFieldBegin(out string name, out TType fieldType, out int fieldId)
    {
        if (_reader.TokenType == JsonTokenType.PropertyName)
        {
            name = _reader.GetString() ?? string.Empty;
            fieldType = TType.Stop;
            fieldId = 0;
        }
        else
        {
            name = string.Empty;
            fieldType = TType.Stop;
            fieldId = 0;
        }
    }
    
    public void ReadFieldEnd()
    {
        // Nothing to do
    }
    
    // ===== Helpers =====
    
    private void ReadExpect(JsonTokenType expected)
    {
        if (!_reader.Read())
            throw new InvalidDataException("Unexpected end of JSON");
        
        if (_reader.TokenType != expected)
            throw new InvalidDataException($"Expected {expected}, got {_reader.TokenType}");
    }
}
