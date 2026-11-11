namespace FBThrift;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class Json5ProtocolWriter : IThriftProtocolWriter
{
    private readonly JsonObject _root;
    private readonly Stack<JsonNode> _stack = new();
    private string _currentFieldName;
    private ThriftWireType _currentFieldType;

    public Json5ProtocolWriter()
    {
        _root = new JsonObject();
        _stack.Push(_root);
    }

    public JsonNode GetResult() => _root;

    public void WriteFieldBegin(ThriftWireType fieldType, short fieldId)
    {
        _currentFieldType = fieldType;
        _currentFieldName = fieldId.ToString();
    }

    public void WriteFieldStop()
    {
        _currentFieldName = null;
        _currentFieldType = default;
    }

    public void WriteBool(bool value) => WriteValue(_currentFieldName, value);
    public void WriteByte(sbyte value) => WriteValue(_currentFieldName, value);
    public void WriteI16(short value) => WriteValue(_currentFieldName, value);
    public void WriteI32(int value) => WriteValue(_currentFieldName, value);
    public void WriteI64(long value) => WriteValue(_currentFieldName, value);
    public void WriteFloat(float value) => WriteValue(_currentFieldName, value);
    public void WriteDouble(double value) => WriteValue(_currentFieldName, value);
    public void WriteString(string value) => WriteValue(_currentFieldName, value);

    public void WriteBinary(byte[] value)
    {
        var obj = new JsonObject();
        var isPrintable = true;
        foreach (var b in value)
        {
            if (b < 0x20 || b == 0x7F || (b >= 0x80 && b <= 0x9F))
            {
                isPrintable = false;
                break;
            }
        }

        if (isPrintable)
        {
            var utf8 = System.Text.Encoding.UTF8.GetString(value);
            obj["utf-8"] = utf8;
        }
        else
        {
            var base64 = Convert.ToBase64String(value)
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            obj["base64url"] = base64;
        }
        WriteValue(_currentFieldName, obj);
    }

    public void WriteListBegin(ThriftWireType elemType, int size)
    {
        var arr = new JsonArray();
        for (int i = 0; i < size; i++) arr.Add(null);
        WriteValue(_currentFieldName, arr);
        _stack.Push(_stack.Peek()[_currentFieldName] as JsonArray);
    }

    public void WriteSetBegin(ThriftWireType elemType, int size) => WriteListBegin(elemType, size);

    public void WriteMapBegin(ThriftWireType keyType, ThriftWireType valType, int size)
    {
        var obj = new JsonObject();
        WriteValue(_currentFieldName, obj);
        _stack.Push(obj);
    }

    public void WriteStruct<T>(T value) where T : IThriftSerializable
    {
        var obj = new JsonObject();
        WriteValue(_currentFieldName, obj);
        _stack.Push(obj);
        value.__fbthrift_write(this);
        _stack.Pop();
    }

    private void WriteValue(string fieldId, object value)
    {
        var current = _stack.Peek();
        if (current is JsonObject obj)
        {
            JsonValue jsonValue;
            if (value == null)
                jsonValue = JsonValue.Create((object)null);
            else if (value is bool b)
            {
                jsonValue = b ? (JsonValue)JsonValue.Parse("true") : (JsonValue)JsonValue.Parse("false");
            }
            else if (value is sbyte sb)
                jsonValue = JsonValue.Create(sb);
            else if (value is short s)
                jsonValue = JsonValue.Create(s);
            else if (value is int i)
                jsonValue = JsonValue.Create(i);
            else if (value is long l)
                jsonValue = JsonValue.Create(l);
            else if (value is float f)
                jsonValue = JsonValue.Create(f);
            else if (value is double d)
                jsonValue = JsonValue.Create(d);
            else if (value is string str)
                jsonValue = JsonValue.Create(str);
            else if (value is JsonObject o)
                jsonValue = JsonValue.Create(o);
            else if (value is JsonArray a)
                jsonValue = JsonValue.Create(a);
            else
                throw new NotSupportedException("Type " + value?.GetType() + " not supported");

            obj[fieldId] = jsonValue;
        }
        else if (current is JsonArray arr)
        {
            var index = int.Parse(fieldId);
            if (index < arr.Count)
            {
                JsonValue jsonValue;
                if (value == null)
                    jsonValue = JsonValue.Create((object)null);
                else if (value is bool b)
                {
                    jsonValue = b ? (JsonValue)JsonValue.Parse("true") : (JsonValue)JsonValue.Parse("false");
                }
                else if (value is sbyte sb)
                    jsonValue = JsonValue.Create(sb);
                else if (value is short s)
                    jsonValue = JsonValue.Create(s);
                else if (value is int i)
                    jsonValue = JsonValue.Create(i);
                else if (value is long l)
                    jsonValue = JsonValue.Create(l);
                else if (value is float f)
                    jsonValue = JsonValue.Create(f);
                else if (value is double d)
                    jsonValue = JsonValue.Create(d);
                else if (value is string str)
                    jsonValue = JsonValue.Create(str);
                else if (value is JsonObject o)
                    jsonValue = JsonValue.Create(o);
                else if (value is JsonArray a)
                    jsonValue = JsonValue.Create(a);
                else
                    throw new NotSupportedException("Type " + value?.GetType() + " not supported");

                arr[index] = jsonValue;
            }
        }
    }

    public void WriteListEnd() => _stack.Pop();
    public void WriteSetEnd() => _stack.Pop();
    public void WriteMapEnd() => _stack.Pop();
    public void WriteFieldEnd() { }
    public void WriteStructBegin(string name) { }
    public void WriteStructEnd() { }

        public void Dispose()
        {
            // No unmanaged resources to dispose
        }
    }