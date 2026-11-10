// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FBThrift;

using System;

/// <summary>
/// Void type for oneway (fire-and-forget) Thrift methods.
/// </summary>
public sealed class ThriftVoid : IThriftSerializable
{
    public void __fbthrift_clear() { }
    
    public bool __fbthrift_is_empty() => true;
    
    public void __fbthrift_write(IThriftProtocolWriter writer)
    {
        // Write nothing for void
    }
    
    public void __fbthrift_read(IThriftProtocolReader reader)
    {
        // Read nothing for void
    }
    
    public override bool Equals(object? obj) => obj is ThriftVoid;
    
    public override int GetHashCode() => 0;
    
    public override string ToString() => "void";
}