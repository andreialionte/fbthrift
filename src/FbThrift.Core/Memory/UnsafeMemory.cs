// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Memory;

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Buffers.Binary;

/// <summary>
/// High-performance memory operations using <see cref="System.Runtime.CompilerServices.Unsafe"/>.
/// Provides bounds-check-free operations with Debug.Assert guards for safety.
/// </summary>
public static class UnsafeMemory
{
    /// <summary>
    /// Reads a primitive value from a byte span without bounds checks.
    /// Caller MUST ensure span has sufficient length (Debug.Assert in debug builds).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ReadUnaligned<T>(ReadOnlySpan<byte> source) where T : unmanaged
    {
        Debug.Assert(source.Length >= Unsafe.SizeOf<T>(), "Insufficient buffer size for read");
        ref byte srcRef = ref MemoryMarshal.GetReference(source);
        return Unsafe.ReadUnaligned<T>(ref srcRef);
    }

    /// <summary>
    /// Writes a primitive value to a byte span without bounds checks.
    /// Caller MUST ensure span has sufficient length (Debug.Assert in debug builds).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUnaligned<T>(Span<byte> destination, T value) where T : unmanaged
    {
        Debug.Assert(destination.Length >= Unsafe.SizeOf<T>(), "Insufficient buffer size for write");
        ref byte destRef = ref MemoryMarshal.GetReference(destination);
        Unsafe.WriteUnaligned(ref destRef, value);
    }

    /// <summary>
    /// Reads a big-endian Int16 from a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short ReadInt16BE(ReadOnlySpan<byte> source)
    {
        Debug.Assert(source.Length >= 2);
        return BinaryPrimitives.ReadInt16BigEndian(source);
    }

    /// <summary>
    /// Reads a big-endian Int32 from a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadInt32BE(ReadOnlySpan<byte> source)
    {
        Debug.Assert(source.Length >= 4);
        return BinaryPrimitives.ReadInt32BigEndian(source);
    }

    /// <summary>
    /// Reads a big-endian Int64 from a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadInt64BE(ReadOnlySpan<byte> source)
    {
        Debug.Assert(source.Length >= 8);
        return BinaryPrimitives.ReadInt64BigEndian(source);
    }

    /// <summary>
    /// Reads a big-endian UInt16 from a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadUInt16BE(ReadOnlySpan<byte> source)
    {
        Debug.Assert(source.Length >= 2);
        return BinaryPrimitives.ReadUInt16BigEndian(source);
    }

    /// <summary>
    /// Reads a big-endian UInt32 from a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUInt32BE(ReadOnlySpan<byte> source)
    {
        Debug.Assert(source.Length >= 4);
        return BinaryPrimitives.ReadUInt32BigEndian(source);
    }

    /// <summary>
    /// Reads a big-endian UInt64 from a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadUInt64BE(ReadOnlySpan<byte> source)
    {
        Debug.Assert(source.Length >= 8);
        return BinaryPrimitives.ReadUInt64BigEndian(source);
    }

    /// <summary>
    /// Reads a big-endian Double from a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ReadDoubleBE(ReadOnlySpan<byte> source)
    {
        Debug.Assert(source.Length >= 8);
        return BinaryPrimitives.ReadDoubleBigEndian(source);
    }

    /// <summary>
    /// Reads a big-endian Single from a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ReadSingleBE(ReadOnlySpan<byte> source)
    {
        Debug.Assert(source.Length >= 4);
        return BinaryPrimitives.ReadSingleBigEndian(source);
    }

    /// <summary>
    /// Writes a big-endian Int16 to a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInt16BE(Span<byte> destination, short value)
    {
        Debug.Assert(destination.Length >= 2);
        BinaryPrimitives.WriteInt16BigEndian(destination, value);
    }

    /// <summary>
    /// Writes a big-endian Int32 to a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInt32BE(Span<byte> destination, int value)
    {
        Debug.Assert(destination.Length >= 4);
        BinaryPrimitives.WriteInt32BigEndian(destination, value);
    }

    /// <summary>
    /// Writes a big-endian Int64 to a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInt64BE(Span<byte> destination, long value)
    {
        Debug.Assert(destination.Length >= 8);
        BinaryPrimitives.WriteInt64BigEndian(destination, value);
    }

    /// <summary>
    /// Writes a big-endian UInt16 to a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt16BE(Span<byte> destination, ushort value)
    {
        Debug.Assert(destination.Length >= 2);
        BinaryPrimitives.WriteUInt16BigEndian(destination, value);
    }

    /// <summary>
    /// Writes a big-endian UInt32 to a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt32BE(Span<byte> destination, uint value)
    {
        Debug.Assert(destination.Length >= 4);
        BinaryPrimitives.WriteUInt32BigEndian(destination, value);
    }

    /// <summary>
    /// Writes a big-endian UInt64 to a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt64BE(Span<byte> destination, ulong value)
    {
        Debug.Assert(destination.Length >= 8);
        BinaryPrimitives.WriteUInt64BigEndian(destination, value);
    }

    /// <summary>
    /// Writes a big-endian Double to a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDoubleBE(Span<byte> destination, double value)
    {
        Debug.Assert(destination.Length >= 8);
        BinaryPrimitives.WriteDoubleBigEndian(destination, value);
    }

    /// <summary>
    /// Writes a big-endian Single to a byte span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSingleBE(Span<byte> destination, float value)
    {
        Debug.Assert(destination.Length >= 4);
        BinaryPrimitives.WriteSingleBigEndian(destination, value);
    }

    /// <summary>
    /// Fast memory copy using Unsafe.CopyBlockUnaligned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyBlockUnaligned(Span<byte> destination, ReadOnlySpan<byte> source, int byteCount)
    {
        Debug.Assert(destination.Length >= byteCount);
        Debug.Assert(source.Length >= byteCount);
        Debug.Assert(byteCount >= 0);

        if (byteCount == 0) return;

        ref byte destRef = ref MemoryMarshal.GetReference(destination);
        ref byte srcRef = ref MemoryMarshal.GetReference(source);
        Unsafe.CopyBlockUnaligned(ref destRef, ref srcRef, (uint)byteCount);
    }

    /// <summary>
    /// Fast memory copy for blittable types.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyBlock<T>(Span<T> destination, ReadOnlySpan<T> source) where T : unmanaged
    {
        int byteCount = source.Length * Unsafe.SizeOf<T>();
        Debug.Assert(destination.Length >= source.Length);
        Debug.Assert(byteCount >= 0);

        if (byteCount == 0) return;

        ref byte destRef = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(destination));
        ref byte srcRef = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(source));
        Unsafe.CopyBlockUnaligned(ref destRef, ref srcRef, (uint)byteCount);
    }

    /// <summary>
    /// Fast memory initialization using Unsafe.InitBlockUnaligned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InitBlockUnaligned(Span<byte> destination, byte value, int byteCount)
    {
        Debug.Assert(destination.Length >= byteCount);
        Debug.Assert(byteCount >= 0);

        if (byteCount == 0) return;

        ref byte destRef = ref MemoryMarshal.GetReference(destination);
        Unsafe.InitBlockUnaligned(ref destRef, value, (uint)byteCount);
    }

    /// <summary>
    /// Gets the size of a blittable type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SizeOf<T>() where T : unmanaged
    {
        return Unsafe.SizeOf<T>();
    }

    /// <summary>
    /// Adds an element offset to a reference.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T Add<T>(ref T source, int elementOffset) where T : unmanaged
    {
        return ref Unsafe.Add(ref source, elementOffset);
    }

    /// <summary>
    /// Subtracts an element offset from a reference.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T Subtract<T>(ref T source, int elementOffset) where T : unmanaged
    {
        return ref Unsafe.Subtract(ref source, elementOffset);
    }

    /// <summary>
    /// Reinterprets a reference as a different type.
    /// Use with caution - only for blittable types with identical layout.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref TTo As<TFrom, TTo>(ref TFrom source) where TFrom : unmanaged where TTo : unmanaged
    {
        Debug.Assert(Unsafe.SizeOf<TFrom>() == Unsafe.SizeOf<TTo>(), "Type size mismatch");
        return ref Unsafe.As<TFrom, TTo>(ref source);
    }

    /// <summary>
    /// Reinterprets a value as a different type (bit-cast).
    /// Safer than As as it provides some compile-time checks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TTo BitCast<TFrom, TTo>(TFrom value) where TFrom : unmanaged where TTo : unmanaged
    {
        Debug.Assert(Unsafe.SizeOf<TFrom>() == Unsafe.SizeOf<TTo>(), "Type size mismatch");
        return Unsafe.BitCast<TFrom, TTo>(value);
    }

    /// <summary>
    /// Checks if two references point to the same location.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AreSame<T>(ref T left, ref T right)
    {
        return Unsafe.AreSame(ref left, ref right);
    }

    /// <summary>
    /// Checks if left reference address is less than right reference address.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAddressLessThan<T>(ref T left, ref T right)
    {
        return Unsafe.IsAddressLessThan(ref left, ref right);
    }

    /// <summary>
    /// Creates a null reference (use with caution).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T NullRef<T>()
    {
        return ref Unsafe.NullRef<T>();
    }

    /// <summary>
    /// Checks if a reference is a null reference.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNullRef<T>(ref T source)
    {
        return Unsafe.IsNullRef(ref source);
    }

    /// <summary>
    /// Skips initialization of a local variable (use with caution).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SkipInit<T>(out T value) where T : unmanaged
    {
        Unsafe.SkipInit(out value);
    }
}