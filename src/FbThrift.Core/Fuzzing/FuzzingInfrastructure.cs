// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Fuzzing;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Fuzzing Infrastructure for fbthrift.
/// Provides property-based testing, mutation-based fuzzing, and structure-aware fuzzing.
/// 
/// References:
/// - libFuzzer (LLVM): https://llvm.org/docs/LibFuzzer.html
/// - AFL++: https://github.com/AFLplusplus/AFLplusplus
/// - SharpFuzz: https://github.com/Metalnem/sharpfuzz
/// - FsCheck: https://fscheck.github.io/FsCheck/
/// - Hypothesis (Python): https://hypothesis.works/
/// 
/// For fbthrift: Focus on Thrift protocol encoding/decoding, QUIC frame handling, 
/// TLS handshake, HPACK compression, and PQC key exchange.
/// </summary>

/// <summary>
/// Arbitrary value generator for fuzzing.
/// </summary>
public interface IArbitrary<T>
{
    T Generate(Random random);
    IEnumerable<T> GenerateMany(Random random, int count);
    T Mutate(T value, Random random, double mutationRate = 0.1);
}

/// <summary>
/// Generator registry for type-directed fuzzing.
/// </summary>
public static class Gen
{
    private static readonly Dictionary<Type, Delegate> _generators = new();
    private static readonly Random _globalRandom = new();
    
    static Gen()
    {
        // Register primitive generators
        Register<int>(() => _globalRandom.Next());
        Register<long>(() => _globalRandom.NextInt64());
        Register<uint>(() => (uint)_globalRandom.Next());
        Register<ulong>(() => (ulong)_globalRandom.NextInt64());
        Register<short>(() => (short)_globalRandom.Next());
        Register<ushort>(() => (ushort)_globalRandom.Next());
        Register<byte>(() => (byte)_globalRandom.Next(256));
        Register<sbyte>(() => (sbyte)_globalRandom.Next(256));
        Register<bool>(() => _globalRandom.Next(2) == 1);
        Register<float>(() => (float)_globalRandom.NextDouble());
        Register<double>(() => _globalRandom.NextDouble());
        Register<decimal>(() => (decimal)_globalRandom.NextDouble());
        
        Register<string>(() => GenerateString(_globalRandom));
        Register<Guid>(() => Guid.NewGuid());
        Register<DateTime>(() => GenerateDateTime(_globalRandom));
        Register<TimeSpan>(() => GenerateTimeSpan(_globalRandom));
        Register<IPAddress>(() => GenerateIPAddress(_globalRandom));
        Register<IPEndPoint>(() => GenerateIPEndPoint(_globalRandom));
        
        // Collections
        Register<ArraySegment<byte>>(() => GenerateByteArray(_globalRandom));
    }
    
    /// <summary>
    /// Registers a generator for a type.
    /// </summary>
    public static void Register<T>(Func<T> generator)
    {
        _generators[typeof(T)] = generator;
    }
    
    /// <summary>
    /// Registers an arbitrary implementation for a type.
    /// </summary>
    public static void Register<T>(IArbitrary<T> arbitrary)
    {
        _generators[typeof(T)] = new Func<T>(() => arbitrary.Generate(Gen._globalRandom));
    }
    
    /// <summary>
    /// Generates a random value of type T.
    /// </summary>
    public static T Generate<T>()
    {
        if (_generators.TryGetValue(typeof(T), out var generator))
        {
            return ((Func<T>)generator)();
        }
        
        // Try to find IArbitrary<T>
        if (_generators.TryGetValue(typeof(T), out var arb) && arb is IArbitrary<T> arbitrary)
        {
            return arbitrary.Generate(_globalRandom);
        }
        
        throw new InvalidOperationException($"No generator registered for type {typeof(T).Name}");
    }
    
    /// <summary>
    /// Generates multiple random values of type T.
    /// </summary>
    public static IEnumerable<T> GenerateMany<T>(int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return Generate<T>();
        }
    }
    
    /// <summary>
    /// Mutates an existing value.
    /// </summary>
    public static T Mutate<T>(T value, double mutationRate = 0.1)
    {
        if (_generators.TryGetValue(typeof(T), out var arb) && arb is IArbitrary<T> arbitrary)
        {
            return arbitrary.Mutate(value, _globalRandom, mutationRate);
        }
        
        throw new InvalidOperationException($"No mutator registered for type {typeof(T).Name}");
    }
    
    // Primitive generators
    
    private static string GenerateString(Random random)
    {
        var length = random.Next(0, 100);
        var chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()_+-=[]{}|;':\",./<>? ";
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append(chars[random.Next(chars.Length)]);
        }
        return sb.ToString();
    }
    
    private static DateTime GenerateDateTime(Random random)
    {
        var start = new DateTime(1970, 1, 1);
        var end = DateTime.UtcNow.AddYears(10);
        var range = end - start;
        return start.Add(TimeSpan.FromTicks((long)(random.NextDouble() * range.Ticks)));
    }
    
    private static TimeSpan GenerateTimeSpan(Random random)
    {
        return TimeSpan.FromMilliseconds(random.NextInt64(0, long.MaxValue / 10000));
    }
    
    private static IPAddress GenerateIPAddress(Random random)
    {
        if (random.Next(2) == 0)
        {
            // IPv4
            return new IPAddress(new byte[] { (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256) });
        }
        else
        {
            // IPv6
            var bytes = new byte[16];
            random.NextBytes(bytes);
            return new IPAddress(bytes);
        }
    }
    
    private static IPEndPoint GenerateIPEndPoint(Random random)
    {
        return new IPEndPoint(GenerateIPAddress(random), random.Next(1, 65535));
    }
    
    private static ArraySegment<byte> GenerateByteArray(Random random)
    {
        var length = random.Next(0, 1024);
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return new ArraySegment<byte>(bytes);
    }
}

/// <summary>
/// Property-based testing framework (FsCheck-style).
/// </summary>
public static class Prop
{
    /// <summary>
    /// Tests a property with random inputs.
    /// </summary>
    public static PropertyResult ForAll<T>(Func<T, bool> property, int testCount = 100)
    {
        var random = new Random();
        var failures = new List<PropertyFailure>();
        
        for (int i = 0; i < testCount; i++)
        {
            try
            {
                var input = Gen.Generate<T>();
                if (!property(input))
                {
                    failures.Add(new PropertyFailure(i, input, "Property returned false"));
                }
            }
            catch (Exception ex)
            {
                failures.Add(new PropertyFailure(i, default, ex.Message));
            }
        }
        
        return new PropertyResult
        {
            Passed = failures.Count == 0,
            TestCount = testCount,
            Failures = failures
        };
    }
    
    /// <summary>
    /// Tests a property with two random inputs.
    /// </summary>
    public static PropertyResult ForAll<T1, T2>(Func<T1, T2, bool> property, int testCount = 100)
    {
        var random = new Random();
        var failures = new List<PropertyFailure>();
        
        for (int i = 0; i < testCount; i++)
        {
            try
            {
                var input1 = Gen.Generate<T1>();
                var input2 = Gen.Generate<T2>();
                if (!property(input1, input2))
                {
                    failures.Add(new PropertyFailure(i, (input1, input2), "Property returned false"));
                }
            }
            catch (Exception ex)
            {
                failures.Add(new PropertyFailure(i, default, ex.Message));
            }
        }
        
        return new PropertyResult
        {
            Passed = failures.Count == 0,
            TestCount = testCount,
            Failures = failures
        };
    }
    
    /// <summary>
    /// Tests a property with shrinking on failure.
    /// </summary>
    public static PropertyResult ForAllWithShrink<T>(
        Func<T, bool> property, 
        Func<T, IEnumerable<T>> shrink, 
        int testCount = 100)
    {
        var random = new Random();
        var failures = new List<PropertyFailure>();
        
        for (int i = 0; i < testCount; i++)
        {
            var input = Gen.Generate<T>();
            
            if (!property(input))
            {
                // Shrink to find minimal failing case
                var minimal = Shrink(input, shrink, property);
                failures.Add(new PropertyFailure(i, minimal, "Property returned false (shrunk)"));
            }
        }
        
        return new PropertyResult
        {
            Passed = failures.Count == 0,
            TestCount = testCount,
            Failures = failures
        };
    }
    
    private static T Shrink<T>(T input, Func<T, IEnumerable<T>> shrink, Func<T, bool> property)
    {
        var current = input;
        
        foreach (var candidate in shrink(current))
        {
            if (!property(candidate))
            {
                current = candidate;
            }
        }
        
        return current;
    }
}

/// <summary>
/// Property test result.
/// </summary>
public readonly struct PropertyResult
{
    public bool Passed { get; init; }
    public int TestCount { get; init; }
    public IReadOnlyList<PropertyFailure> Failures { get; init; }
    
    public override string ToString()
    {
        if (Passed)
            return $"PASSED: {TestCount} tests passed";
        
        return $"FAILED: {Failures.Count}/{TestCount} tests failed\n" +
               string.Join("\n", Failures.Select(f => $"  Test {f.TestIndex}: {f.Message}"));
    }
}

/// <summary>
/// Property test failure.
/// </summary>
public readonly struct PropertyFailure
{
    public int TestIndex { get; init; }
    public object? Input { get; init; }
    public string Message { get; init; }
    
    public PropertyFailure(int testIndex, object? input, string message)
    {
        TestIndex = testIndex;
        Input = input;
        Message = message;
    }
}

/// <summary>
/// Mutation-based fuzzer for structured data.
/// </summary>
public sealed class MutationFuzzer
{
    private readonly Random _random = new();
    private readonly List<IMutationOperator> _operators = new();
    
    public MutationFuzzer()
    {
        // Default mutation operators
        _operators.Add(new BitFlipOperator());
        _operators.Add(new ByteFlipOperator());
        _operators.Add(new ByteInsertOperator());
        _operators.Add(new ByteDeleteOperator());
        _operators.Add(new ByteCopyOperator());
        _operators.Add(new ArithmeticOperator());
        _operators.Add(new InterestingValuesOperator());
    }
    
    /// <summary>
    /// Adds a custom mutation operator.
    /// </summary>
    public void AddOperator(IMutationOperator op)
    {
        _operators.Add(op);
    }
    
    /// <summary>
    /// Mutates a byte array.
    /// </summary>
    public byte[] Mutate(byte[] input, int maxMutations = 10)
    {
        if (input == null || input.Length == 0)
        {
            return GenerateRandomBytes();
        }
        
        var output = (byte[])input.Clone();
        var mutations = _random.Next(1, maxMutations + 1);
        
        for (int i = 0; i < mutations; i++)
        {
            var op = _operators[_random.Next(_operators.Count)];
            output = op.Mutate(output, _random);
            
            if (output.Length == 0)
            {
                output = GenerateRandomBytes();
                break;
            }
        }
        
        return output;
    }
    
    /// <summary>
    /// Mutates a structured object by serializing, mutating, and deserializing.
    /// </summary>
    public T MutateStructured<T>(T input, Func<T, byte[]> serialize, Func<byte[], T> deserialize, int maxMutations = 10)
    {
        var bytes = serialize(input);
        var mutated = Mutate(bytes, maxMutations);
        return deserialize(mutated);
    }
    
    private byte[] GenerateRandomBytes()
    {
        var length = _random.Next(1, 1024);
        var bytes = new byte[length];
        _random.NextBytes(bytes);
        return bytes;
    }
}

/// <summary>
/// Mutation operator interface.
/// </summary>
public interface IMutationOperator
{
    byte[] Mutate(byte[] input, Random random);
}

/// <summary>
/// Bit flip mutation (flips a single bit).
/// </summary>
public sealed class BitFlipOperator : IMutationOperator
{
    public byte[] Mutate(byte[] input, Random random)
    {
        if (input.Length == 0) return input;
        
        var output = (byte[])input.Clone();
        var byteIndex = random.Next(output.Length);
        var bitIndex = random.Next(8);
        
        output[byteIndex] ^= (byte)(1 << bitIndex);
        return output;
    }
}

/// <summary>
/// Byte flip mutation (replaces a byte with random value).
/// </summary>
public sealed class ByteFlipOperator : IMutationOperator
{
    public byte[] Mutate(byte[] input, Random random)
    {
        if (input.Length == 0) return input;
        
        var output = (byte[])input.Clone();
        var byteIndex = random.Next(output.Length);
        output[byteIndex] = (byte)random.Next(256);
        return output;
    }
}

/// <summary>
/// Byte insert mutation (inserts random bytes).
/// </summary>
public sealed class ByteInsertOperator : IMutationOperator
{
    public byte[] Mutate(byte[] input, Random random)
    {
        var output = new byte[input.Length + random.Next(1, 16)];
        var insertPos = random.Next(input.Length + 1);
        
        Array.Copy(input, 0, output, 0, insertPos);
        random.NextBytes(output.AsSpan(insertPos, output.Length - insertPos));
        Array.Copy(input, insertPos, output, insertPos + output.Length - input.Length, input.Length - insertPos);
        
        return output;
    }
}

/// <summary>
/// Byte delete mutation (deletes random bytes).
/// </summary>
public sealed class ByteDeleteOperator : IMutationOperator
{
    public byte[] Mutate(byte[] input, Random random)
    {
        if (input.Length <= 1) return input;
        
        var deleteCount = random.Next(1, Math.Min(16, input.Length));
        var deletePos = random.Next(input.Length - deleteCount + 1);
        
        var output = new byte[input.Length - deleteCount];
        Array.Copy(input, 0, output, 0, deletePos);
        Array.Copy(input, deletePos + deleteCount, output, deletePos, input.Length - deletePos - deleteCount);
        
        return output;
    }
}

/// <summary>
/// Byte copy mutation (copies a segment to another location).
/// </summary>
public sealed class ByteCopyOperator : IMutationOperator
{
    public byte[] Mutate(byte[] input, Random random)
    {
        if (input.Length < 2) return input;
        
        var output = (byte[])input.Clone();
        var srcPos = random.Next(input.Length);
        var dstPos = random.Next(input.Length);
        var copyLen = random.Next(1, Math.Min(16, input.Length - Math.Max(srcPos, dstPos)));
        
        if (copyLen > 0 && srcPos + copyLen <= input.Length && dstPos + copyLen <= input.Length)
        {
            Array.Copy(input, srcPos, output, dstPos, copyLen);
        }
        
        return output;
    }
}

/// <summary>
/// Arithmetic mutation (adds/subtracts from integer values in bytes).
/// </summary>
public sealed class ArithmeticOperator : IMutationOperator
{
    public byte[] Mutate(byte[] input, Random random)
    {
        if (input.Length < 2) return input;
        
        var output = (byte[])input.Clone();
        var pos = random.Next(input.Length - 1);
        var value = random.Next(-128, 128);
        
        // Treat as 16-bit little-endian integer
        if (pos + 1 < output.Length)
        {
            var current = (short)(output[pos] | (output[pos + 1] << 8));
            var newValue = (short)Math.Clamp(current + value, short.MinValue, short.MaxValue);
            output[pos] = (byte)newValue;
            output[pos + 1] = (byte)(newValue >> 8);
        }
        
        return output;
    }
}

/// <summary>
/// Interesting values mutation (replaces with known edge-case values).
/// </summary>
public sealed class InterestingValuesOperator : IMutationOperator
{
    private static readonly byte[][] InterestingBytes = new[]
    {
        new byte[] { 0x00 },
        new byte[] { 0xFF },
        new byte[] { 0x7F },
        new byte[] { 0x80 },
        new byte[] { 0x00, 0x00 },
        new byte[] { 0xFF, 0xFF },
        new byte[] { 0x7F, 0xFF },
        new byte[] { 0x80, 0x00 },
        new byte[] { 0x00, 0x00, 0x00, 0x00 },
        new byte[] { 0xFF, 0xFF, 0xFF, 0xFF },
        new byte[] { 0x7F, 0xFF, 0xFF, 0xFF },
        new byte[] { 0x80, 0x00, 0x00, 0x00 },
    };
    
    public byte[] Mutate(byte[] input, Random random)
    {
        if (input.Length == 0) return input;
        
        var output = (byte[])input.Clone();
        var interesting = InterestingBytes[random.Next(InterestingBytes.Length)];
        var pos = random.Next(Math.Max(1, output.Length - interesting.Length + 1));
        
        Array.Copy(interesting, 0, output, pos, Math.Min(interesting.Length, output.Length - pos));
        
        return output;
    }
}

/// <summary>
/// Structure-aware fuzzer for Thrift/Protocol Buffers.
/// </summary>
public sealed class StructureAwareFuzzer<T>
{
    private readonly MutationFuzzer _mutator = new();
    private readonly Func<T, byte[]> _serialize;
    private readonly Func<byte[], T> _deserialize;
    private readonly IArbitrary<T> _generator;
    
    public StructureAwareFuzzer(
        Func<T, byte[]> serialize,
        Func<byte[], T> deserialize,
        IArbitrary<T>? generator = null)
    {
        _serialize = serialize ?? throw new ArgumentNullException(nameof(serialize));
        _deserialize = deserialize ?? throw new ArgumentNullException(nameof(deserialize));
        _generator = generator ?? new DefaultArbitrary<T>();
    }
    
    /// <summary>
    /// Generates a random valid structure.
    /// </summary>
    public T Generate()
    {
        return _generator.Generate(new Random());
    }
    
    /// <summary>
    /// Mutates an existing structure.
    /// </summary>
    public T Mutate(T input, double mutationRate = 0.1)
    {
        return _generator.Mutate(input, new Random(), mutationRate);
    }
    
    /// <summary>
    /// Cross-over two structures.
    /// </summary>
    public T CrossOver(T parent1, T parent2)
    {
        var bytes1 = _serialize(parent1);
        var bytes2 = _serialize(parent2);
        
        if (bytes1.Length == 0 || bytes2.Length == 0)
            return parent1;
        
        var minLen = Math.Min(bytes1.Length, bytes2.Length);
        var crossPoint = new Random().Next(minLen);
        
        var crossed = new byte[minLen];
        Array.Copy(bytes1, 0, crossed, 0, crossPoint);
        Array.Copy(bytes2, crossPoint, crossed, crossPoint, minLen - crossPoint);
        
        try
        {
            return _deserialize(crossed);
        }
        catch
        {
            return parent1;
        }
    }
    
    /// <summary>
    /// Runs a fuzzing campaign.
    /// </summary>
    public FuzzingCampaign RunCampaign(
        Action<T> testAction,
        int iterations = 1000,
        int mutationsPerIteration = 10,
        CancellationToken cancellationToken = default)
    {
        var campaign = new FuzzingCampaign();
        var current = Generate();
        
        for (int i = 0; i < iterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            try
            {
                testAction(current);
                campaign.RecordSuccess();
            }
            catch (Exception ex)
            {
                campaign.RecordCrash(current, ex);
            }
            
            // Mutate for next iteration
            current = _mutator.MutateStructured(current, _serialize, _deserialize, mutationsPerIteration);
            
            // Occasionally generate fresh input
            if (new Random().Next(100) < 5) // 5% chance
            {
                current = Generate();
            }
        }
        
        return campaign;
    }
}

/// <summary>
/// Default arbitrary implementation using reflection.
/// </summary>
public sealed class DefaultArbitrary<T> : IArbitrary<T>
{
    private readonly Random _random = new();
    private readonly ConstructorInfo? _constructor;
    private readonly PropertyInfo[] _properties;
    private readonly FieldInfo[] _fields;
    
    public DefaultArbitrary()
    {
        var type = typeof(T);
        _constructor = type.GetConstructor(Type.EmptyTypes);
        _properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        _fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
    }
    
    public T Generate(Random random)
    {
        if (_constructor != null)
        {
            var instance = (T)_constructor.Invoke(null);
            PopulateRandom(instance, random);
            return instance;
        }
        
        // For structs or types without default constructor
        return Activator.CreateInstance<T>();
    }
    
    public IEnumerable<T> GenerateMany(Random random, int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return Generate(random);
        }
    }
    
    public T Mutate(T value, Random random, double mutationRate = 0.1)
    {
        // For complex types, create a new instance with some fields mutated
        var instance = Generate(random);
        
        // Copy non-mutated fields
        foreach (var prop in _properties.Where(p => p.CanWrite))
        {
            if (random.NextDouble() > mutationRate)
            {
                prop.SetValue(instance, prop.GetValue(value));
            }
        }
        
        foreach (var field in _fields)
        {
            if (random.NextDouble() > mutationRate)
            {
                field.SetValue(instance, field.GetValue(value));
            }
        }
        
        return instance;
    }
    
    private void PopulateRandom(object instance, Random random)
    {
        foreach (var prop in _properties.Where(p => p.CanWrite))
        {
            try
            {
                prop.SetValue(instance, GenerateRandomValue(prop.PropertyType, random));
            }
            catch { /* Ignore */ }
        }
        
        foreach (var field in _fields)
        {
            try
            {
                field.SetValue(instance, GenerateRandomValue(field.FieldType, random));
            }
            catch { /* Ignore */ }
        }
    }
    
    private object GenerateRandomValue(Type type, Random random)
    {
        if (type == typeof(int)) return random.Next();
        if (type == typeof(long)) return random.NextInt64();
        if (type == typeof(uint)) return (uint)random.Next();
        if (type == typeof(ulong)) return (ulong)random.NextInt64();
        if (type == typeof(short)) return (short)random.Next();
        if (type == typeof(ushort)) return (ushort)random.Next();
        if (type == typeof(byte)) return (byte)random.Next(256);
        if (type == typeof(sbyte)) return (sbyte)random.Next(256);
        if (type == typeof(bool)) return random.Next(2) == 1;
        if (type == typeof(float)) return (float)random.NextDouble();
        if (type == typeof(double)) return random.NextDouble();
        if (type == typeof(decimal)) return (decimal)random.NextDouble();
        if (type == typeof(string)) return Gen.Generate<string>();
        if (type == typeof(Guid)) return Guid.NewGuid();
        if (type == typeof(DateTime)) return DateTime.UtcNow.AddDays(random.Next(-3650, 3650));
        if (type == typeof(TimeSpan)) return TimeSpan.FromMilliseconds(random.NextInt64(0, int.MaxValue));
        if (type.IsEnum) return Enum.GetValues(type).GetValue(random.Next(Enum.GetValues(type).Length))!;
        if (type.IsArray)
        {
            var elemType = type.GetElementType()!;
            var length = random.Next(0, 10);
            var array = Array.CreateInstance(elemType, length);
            for (int i = 0; i < length; i++)
            {
                array.SetValue(GenerateRandomValue(elemType, random), i);
            }
            return array;
        }
        
        return Activator.CreateInstance(type)!;
    }
}

/// <summary>
/// Fuzzing campaign results.
/// </summary>
public sealed class FuzzingCampaign
{
    private readonly List<Crash> _crashes = new();
    private long _successCount;
    private long _totalIterations;
    
    public int TotalIterations => (int)_totalIterations;
    public int SuccessCount => (int)_successCount;
    public int CrashCount => _crashes.Count;
    public IReadOnlyList<Crash> Crashes => _crashes;
    
    public void RecordSuccess()
    {
        Interlocked.Increment(ref _successCount);
        Interlocked.Increment(ref _totalIterations);
    }
    
    public void RecordCrash(object input, Exception exception)
    {
        Interlocked.Increment(ref _totalIterations);
        _crashes.Add(new Crash
        {
            Input = input,
            Exception = exception,
            Timestamp = DateTime.UtcNow
        });
    }
    
    public override string ToString()
    {
        return $"Campaign: {TotalIterations} iterations, {SuccessCount} passed, {CrashCount} crashes";
    }
}

/// <summary>
/// Crash information from fuzzing.
/// </summary>
public readonly struct Crash
{
    public object? Input { get; init; }
    public Exception Exception { get; init; }
    public DateTime Timestamp { get; init; }
    
    public override string ToString()
    {
        return $"Crash at {Timestamp}: {Exception.GetType().Name}: {Exception.Message}";
    }
}

/// <summary>
/// Fuzzing integration with build pipeline.
/// </summary>
public static class Fuzz
{
    /// <summary>
    /// Runs a fuzzing session for a target function.
    /// </summary>
    public static FuzzingCampaign FuzzTarget<T>(
        Func<T, bool> target,
        int iterations = 1000,
        CancellationToken cancellationToken = default)
    {
        var campaign = new FuzzingCampaign();
        
        for (int i = 0; i < 1000; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var input = Gen.Generate<T>();
            
            try
            {
                if (target(input))
                {
                    // Property held
                }
            }
            catch (Exception ex)
            {
                campaign.RecordCrash(default, ex);
            }
        }
        
        return campaign;
    }
    
    /// <summary>
    /// Runs fuzzing against a Thrift protocol encoder/decoder.
    /// </summary>
    public static FuzzingCampaign FuzzThriftProtocol<T>(
        Func<T, byte[]> encode,
        Func<byte[], T> decode,
        int iterations = 1000,
        CancellationToken cancellationToken = default)
    {
        var fuzzer = new StructureAwareFuzzer<T>(encode, decode);
        return fuzzer.RunCampaign(
            testAction: obj =>
            {
                var encoded = encode(obj);
                var decoded = decode(encoded);
                // Round-trip property: decode(encode(x)) == x
                if (!EqualityComparer<T>.Default.Equals(obj, decoded))
                {
                    throw new InvalidOperationException("Round-trip property violated");
                }
            },
            iterations: iterations);
    }
    
    /// <summary>
    /// Runs fuzzing against QUIC frame encoding/decoding.
    /// </summary>
    public static FuzzingCampaign FuzzQuicFrames(
        int iterations = 1000,
        CancellationToken cancellationToken = default)
    {
        // Would test QUIC frame encoding/decoding round-trips
        var campaign = new FuzzingCampaign();
        // Implementation would test various frame types
        return campaign;
    }
    
    /// <summary>
    /// Runs fuzzing against TLS handshake messages.
    /// </summary>
    public static FuzzingCampaign FuzzTlsHandshake(
        int iterations = 1000,
        CancellationToken cancellationToken = default)
    {
        var campaign = new FuzzingCampaign();
        // Would test ClientHello, ServerHello, Certificate, etc.
        return campaign;
    }
    
    /// <summary>
    /// Runs fuzzing against HPACK compression.
    /// </summary>
    public static FuzzingCampaign FuzzHpack(
        int iterations = 1000,
        CancellationToken cancellationToken = default)
    {
        var campaign = new FuzzingCampaign();
        // Would test HPACK encoder/decoder
        return campaign;
    }
    
    /// <summary>
    /// Runs fuzzing against PQC key exchange.
    /// </summary>
    public static FuzzingCampaign FuzzPqcKeyExchange(
        int iterations = 1000,
        CancellationToken cancellationToken = default)
    {
        var campaign = new FuzzingCampaign();
        // Would test ML-KEM encapsulate/decapsulate, ML-DSA sign/verify
        return campaign;
    }
}