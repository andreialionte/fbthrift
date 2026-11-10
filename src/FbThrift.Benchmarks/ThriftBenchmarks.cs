// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using FbThrift.Core.FBThrift;
using FbThrift.Serialization.Binary;

namespace FbThrift.Benchmarks;

/// <summary>
/// Benchmarks for Thrift serialization/deserialization performance.
/// Matches Meta fbthrift's benchmark runner package.
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[SimpleJob(RuntimeMoniker.Net10)]
public class ThriftSerializationBenchmarks
{
    private readonly BinarySerializer _serializer = new();
    private readonly TestStruct _testStruct = new()
    {
        Id = 12345,
        Name = "Benchmark test service name",
        Description = "This is a test struct for benchmarking Thrift serialization performance",
        Tags = { "tag1", "tag2", "tag3", "tag4", "tag5" },
        Metadata = { ["key1"] = "value1", ["key2"] = "value2", ["key3"] = "value3" }
    };
    private byte[] _serializedData = Array.Empty<byte>();

    [GlobalSetup]
    public void GlobalSetup()
    {
        _serializedData = _serializer.Serialize(_testStruct);
    }

    [Benchmark(BenchmarkMarkdown = "Serialize")]
    public byte[] Serialize() => _serializer.Serialize(_testStruct);

    [Benchmark(BenchmarkMarkdown = "Deserialize")]
    public TestStruct Deserialize() => _serializer.Deserialize<TestStruct>(_serializedData);

    [Benchmark(BenchmarkMarkdown = "RoundTrip")]
    public TestStruct RoundTrip()
    {
        var data = _serializer.Serialize(_testStruct);
        return _serializer.Deserialize<TestStruct>(data);
    }

    [Benchmark(BenchmarkMarkdown = "SerializeLarge")]
    public byte[] SerializeLarge()
    {
        var largeStruct = new TestStruct
        {
            Id = 99999,
            Name = new string('x', 10000),
            Description = new string('y', 10000)
        };
        return _serializer.Serialize(largeStruct);
    }
}

/// <summary>
/// Benchmarks for Thrift binary protocol reader/writer performance.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10)]
public class ThriftProtocolBenchmarks
{
    private readonly ThriftBinaryWriter _writer = new();
    private readonly ThriftBinaryReader _reader = new();
    private readonly MemoryStream _stream = new();
    private byte[] _frameData = Array.Empty<byte>();

    [GlobalSetup]
    public void GlobalSetup()
    {
        using var ms = new MemoryStream();
        _writer.WriteStructBegin(ms, "TestStruct");
        _writer.WriteFieldBegin(ms, 1, ThriftWireType.I32, 1);
        _writer.WriteI32(ms, 42);
        _writer.WriteFieldEnd(ms);
        _writer.WriteFieldStop(ms);
        _writer.WriteStructEnd(ms);
        _frameData = ms.ToArray();
    }

    [Benchmark(BenchmarkMarkdown = "WriteFrame")]
    public void WriteFrame()
    {
        _stream.Position = 0;
        _writer.WriteStructBegin(_stream, "TestStruct");
        _writer.WriteFieldBegin(_stream, 1, ThriftWireType.I32, 1);
        _writer.WriteI32(_stream, 42);
        _writer.WriteFieldEnd(_stream);
        _writer.WriteFieldStop(_stream);
        _writer.WriteStructEnd(_stream);
    }

    [Benchmark(BenchmarkMarkdown = "ReadFrame")]
    public void ReadFrame()
    {
        var ms = new MemoryStream(_frameData);
        _reader.ReadStructBegin(ms);
        _reader.ReadFieldBegin(ms);
        _reader.ReadI32(ms);
        _reader.ReadFieldEnd(ms);
        _reader.ReadFieldStop(ms);
        _reader.ReadStructEnd(ms);
    }
}

/// <summary>
/// Benchmarks for Thrift struct field operations.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10)]
public class ThriftStructBenchmarks
{
    private readonly BinarySerializer _serializer = new();

    [Benchmark(BenchmarkMarkdown = "CreateSmallStruct")]
    public TestStruct CreateSmallStruct() => new()
    {
        Id = 1,
        Name = "small",
        Description = "desc"
    };

    [Benchmark(BenchmarkMarkdown = "CreateLargeStruct")]
    public TestStruct CreateLargeStruct() => new()
    {
        Id = 999999,
        Name = new string('a', 5000),
        Description = new string('b', 5000),
        Tags = { "tag1", "tag2", "tag3", "tag4", "tag5", "tag6", "tag7", "tag8", "tag9", "tag10" },
        Metadata = { ["k1"] = "v1", ["k2"] = "v2", ["k3"] = "v3", ["k4"] = "v4", ["k5"] = "v5" }
    };

    [Benchmark(BenchmarkMarkdown = "SerializeSmallStruct")]
    public byte[] SerializeSmallStruct()
    {
        var s = new TestStruct { Id = 1, Name = "small", Description = "desc" };
        return _serializer.Serialize(s);
    }

    [Benchmark(BenchmarkMarkdown = "SerializeLargeStruct")]
    public byte[] SerializeLargeStruct()
    {
        var s = new TestStruct
        {
            Id = 999999,
            Name = new string('a', 5000),
            Description = new string('b', 5000)
        };
        return _serializer.Serialize(s);
    }
}

/// <summary>
/// Benchmarks for varint encoding/decoding performance.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10)]
public class VarintBenchmarks
{
    private static readonly int[] TestValues = { 0, 1, 127, 128, 255, 256, 65535, 65536, 1000000, int.MaxValue };
    private static byte[] _encoded = Array.Empty<byte>();

    [GlobalSetup]
    public void GlobalSetup()
    {
        using var ms = new MemoryStream();
        foreach (var v in TestValues)
        {
            Varint.WriteUnsigned(ms, (uint)v);
        }
        _encoded = ms.ToArray();
    }

    [Benchmark(BenchmarkMarkdown = "WriteVarint")]
    public void WriteVarint()
    {
        using var ms = new MemoryStream();
        foreach (var v in TestValues)
        {
            Varint.WriteUnsigned(ms, (uint)v);
        }
    }

    [Benchmark(BenchmarkMarkdown = "ReadVarint")]
    public void ReadVarint()
    {
        using var ms = new MemoryStream(_encoded);
        for (int i = 0; i < TestValues.Length; i++)
        {
            _ = Varint.ReadUnsigned(ms);
        }
    }
}

/// <summary>
/// Benchmarks for XXH3 hashing performance.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10)]
public class HashingBenchmarks
{
    private static readonly byte[] SmallData = new byte[64];
    private static readonly byte[] LargeData = new byte[65536];

    static HashingBenchmarks()
    {
        new Random(42).NextBytes(SmallData);
        new Random(42).NextBytes(LargeData);
    }

    [Benchmark(BenchmarkMarkdown = "HashSmall")]
    public ulong HashSmall() => System.IO.Hashing.XxHash32.HashToUInt32(SmallData);

    [Benchmark(BenchmarkMarkdown = "HashLarge")]
    public ulong HashLarge() => System.IO.Hashing.XxHash32.HashToUInt32(LargeData);
}

/// <summary>
/// Benchmarks for load balancer endpoint selection performance.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10)]
public class LoadBalancerBenchmarks
{
    private readonly FbThrift.LoadBalancing.LoadBalancer _loadBalancer = new();
    private readonly string[] _addresses =
    {
        "10.0.0.1:9090", "10.0.0.2:9090", "10.0.0.3:9090", "10.0.0.4:9090",
        "10.0.0.5:9090", "10.0.0.6:9090", "10.0.0.7:9090", "10.0.0.8:9090"
    };

    [GlobalSetup]
    public void GlobalSetup()
    {
        foreach (var addr in _addresses)
        {
            _loadBalancer.AddEndpoint(addr, weight: 1.0);
        }
    }

    [Benchmark(BenchmarkMarkdown = "SelectRoundRobin")]
    public FbThrift.LoadBalancing.EndPointInfo? SelectRoundRobin()
    {
        _loadBalancer = new FbThrift.LoadBalancing.LoadBalancer
        {
            Algorithm = FbThrift.LoadBalancing.LoadBalancingAlgorithm.RoundRobin
        };
        foreach (var addr in _addresses)
            _loadBalancer.AddEndpoint(addr, weight: 1.0);
        return _loadBalancer.SelectEndpoint();
    }

    [Benchmark(BenchmarkMarkdown = "SelectLeastConnections")]
    public FbThrift.LoadBalancing.EndPointInfo? SelectLeastConnections()
    {
        _loadBalancer = new FbThrift.LoadBalancing.LoadBalancer
        {
            Algorithm = FbThrift.LoadBalancing.LoadBalancingAlgorithm.LeastConnections
        };
        foreach (var addr in _addresses)
            _loadBalancer.AddEndpoint(addr, weight: 1.0);
        return _loadBalancer.SelectEndpoint();
    }

    [Benchmark(BenchmarkMarkdown = "SelectWeightedRoundRobin")]
    public FbThrift.LoadBalancing.EndPointInfo? SelectWeightedRoundRobin()
    {
        _loadBalancer = new FbThrift.LoadBalancing.LoadBalancer
        {
            Algorithm = FbThrift.LoadBalancing.LoadBalancingAlgorithm.WeightedRoundRobin
        };
        foreach (var addr in _addresses)
            _loadBalancer.AddEndpoint(addr, weight: 1.0);
        return _loadBalancer.SelectEndpoint();
    }

    [Benchmark(BenchmarkMarkdown = "SelectRandom")]
    public FbThrift.LoadBalancing.EndPointInfo? SelectRandom()
    {
        _loadBalancer = new FbThrift.LoadBalancing.LoadBalancer
        {
            Algorithm = FbThrift.LoadBalancing.LoadBalancingAlgorithm.Random
        };
        foreach (var addr in _addresses)
            _loadBalancer.AddEndpoint(addr, weight: 1.0);
        return _loadBalancer.SelectEndpoint();
    }

    [Benchmark(BenchmarkMarkdown = "RecordSuccess")]
    public void RecordSuccess()
    {
        foreach (var addr in _addresses)
        {
            _loadBalancer.RecordSuccess(addr, TimeSpan.FromMilliseconds(10));
        }
    }

    [Benchmark(BenchmarkMarkdown = "RecordError")]
    public void RecordError()
    {
        foreach (var addr in _addresses)
        {
            _loadBalancer.RecordError(addr, new Exception("Test error"));
        }
    }
}

public sealed class TestStruct : IThriftSerializable
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public System.Collections.Generic.List<string> Tags { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, string> Metadata { get; set; } = new();

    public void Write(TProtocolWriter writer)
    {
        writer.WriteStructBegin("TestStruct");
        writer.WriteFieldBegin(1, ThriftWireType.I32, 1);
        writer.WriteI32(Id);
        writer.WriteFieldEnd(1);
        writer.WriteFieldBegin(2, ThriftWireType.String, 2);
        writer.WriteString(Name);
        writer.WriteFieldEnd(2);
        writer.WriteFieldBegin(3, ThriftWireType.String, 3);
        writer.WriteString(Description);
        writer.WriteFieldEnd(3);
        writer.WriteFieldBegin(4, ThriftWireType.List, 4);
        writer.WriteListBegin(TProtocolWriter.ListType.String, Tags.Count);
        foreach (var tag in Tags)
            writer.WriteString(tag);
        writer.WriteListEnd();
        writer.WriteFieldEnd(4);
        writer.WriteFieldBegin(5, ThriftWireType.Map, 5);
        writer.WriteMapBegin(TProtocolWriter.MapType.String, TProtocolWriter.MapType.String, Metadata.Count);
        foreach (var kvp in Metadata)
        {
            writer.WriteString(kvp.Key);
            writer.WriteString(kvp.Value);
        }
        writer.WriteMapEnd();
        writer.WriteFieldEnd(5);
        writer.WriteFieldStop();
        writer.WriteStructEnd();
    }

    public void Read(TProtocolReader reader)
    {
        reader.ReadStructBegin();
        while (true)
        {
            var (fieldId, fieldType) = reader.ReadFieldBegin();
            if (fieldType == ThriftWireType.Stop) break;

            switch (fieldId)
            {
                case 1:
                    Id = reader.ReadI32();
                    break;
                case 2:
                    Name = reader.ReadString();
                    break;
                case 3:
                    Description = reader.ReadString();
                    break;
                case 4:
                    var (_, listSize) = reader.ReadListBegin();
                    Tags = new System.Collections.Generic.List<string>(listSize);
                    for (int i = 0; i < listSize; i++)
                        Tags.Add(reader.ReadString());
                    break;
                case 5:
                    var (_, mapSize) = reader.ReadMapBegin();
                    Metadata = new System.Collections.Generic.Dictionary<string, string>(mapSize);
                    for (int i = 0; i < mapSize; i++)
                    {
                        var key = reader.ReadString();
                        var value = reader.ReadString();
                        Metadata[key] = value;
                    }
                    break;
            }
            reader.ReadFieldEnd();
        }
        reader.ReadStructEnd();
    }
}
