namespace FBThrift;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Represents a universal name for a Thrift definition.
/// Format: domain_path/identifier (fbthrift:// is implied)
/// </summary>
public readonly struct UniversalName : IEquatable<UniversalName>
{
    public string Value { get; }

    public UniversalName(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public bool Equals(UniversalName other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is UniversalName other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value;

    public static implicit operator string(UniversalName name) => name.Value;
    public static implicit operator UniversalName(string value) => new(value);

    /// <summary>
    /// Computes the SHA2-256 hash of this universal name (prefixed with "fbthrift://").
    /// </summary>
    public byte[] ComputeHash()
    {
        using var sha256 = SHA256.Create();
        var prefixed = "fbthrift://" + Value;
        return sha256.ComputeHash(Encoding.UTF8.GetBytes(prefixed));
    }

    /// <summary>
    /// Gets the 8-byte prefix of the SHA2-256 hash (used for quick identification).
    /// </summary>
    public ulong GetHashPrefix8()
    {
        var hash = ComputeHash();
        return BitConverter.ToUInt64(hash, 0);
    }
}