// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Transports.Rocket;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

/// <summary>
/// Extension methods for RocketFrame using ArrayPool for zero-GC frame encoding.
/// </summary>
public static class RocketFrameExtensions
{
    public static RocketFrame With(this RocketFrame frame, RocketFrameHeader header)
    {
        return new RocketFrame(header, frame.Metadata, frame.Data);
    }

    /// <summary>
    /// Encodes a RocketFrame to a buffer using pooled memory.
    /// </summary>
    public static ReadOnlyMemory<byte> EncodePooled(this RocketFrame frame)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var writer = new ArrayBufferWriter<byte>(buffer);
            frame.Write(writer);
            return writer.WrittenMemory;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Encodes multiple RocketFrames to a single buffer using pooled memory.
    /// </summary>
    public static ReadOnlyMemory<byte> EncodeMultiplePooled(this IEnumerable<RocketFrame> frames)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            var writer = new ArrayBufferWriter<byte>(buffer);
            foreach (var frame in frames)
            {
                frame.Write(writer);
            }
            return writer.WrittenMemory;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Creates a SETUP frame with pooled encoding.
    /// </summary>
    public static ReadOnlyMemory<byte> CreateSetupFramePooled(
        int streamId,
        uint version,
        uint keepAliveTimeoutMs,
        uint maxLifetimeMs,
        Dictionary<string, string> parameters,
        string token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            var writer = new ArrayBufferWriter<byte>(buffer);
            
            // Write frame header
            var header = new RocketFrameHeader(
                0, // Will be updated after writing metadata
                RocketFrameType.Setup,
                RocketFrameFlags.Metadata,
                streamId);
            
            // Reserve space for header (will be overwritten)
            var headerSpan = writer.GetSpan(RocketFrameHeader.HeaderSize);
            writer.Advance(RocketFrameHeader.HeaderSize);
            
            // Write metadata: version, keepalive, max lifetime, params, token
            var metadataWriter = new SetupFrameWriter(writer);
            metadataWriter.WriteVersion(version);
            metadataWriter.WriteKeepAlive(keepAliveTimeoutMs);
            metadataWriter.WriteMaxLifetime(maxLifetimeMs);
            metadataWriter.WriteParameters(parameters);
            metadataWriter.WriteToken(token);
            
            int metadataLength = metadataWriter.Written;
            
            // Calculate total length and write actual header
            var totalLength = metadataLength;
            var frameHeader = new RocketFrameHeader(
                totalLength,
                RocketFrameType.Setup,
                RocketFrameFlags.Metadata,
                streamId);
            
            // Now write the actual header with correct length
            var finalSpan = writer.GetSpan(RocketFrameHeader.HeaderSize);
            frameHeader.Write(finalSpan);
            writer.Advance(RocketFrameHeader.HeaderSize);
            
            return writer.WrittenMemory;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Creates a SETUP frame writer helper.
    /// </summary>
    private sealed class SetupFrameWriter
    {
        private readonly ArrayBufferWriter<byte> _output;
        
        public SetupFrameWriter(ArrayBufferWriter<byte> writer)
        {
            _output = writer;
        }
        
        public int Written => _output.WrittenCount;
        
        public void WriteVersion(uint version)
        {
            WriteVarint(version);
        }
        
        public void WriteKeepAlive(uint ms)
        {
            WriteVarint(ms);
        }
        
        public void WriteMaxLifetime(uint ms)
        {
            WriteVarint(ms);
        }
        
        public void WriteParameters(Dictionary<string, string> parameters)
        {
            WriteVarint((uint)parameters.Count);
            foreach (var kvp in parameters)
            {
                WriteString(kvp.Key);
                WriteString(kvp.Value);
            }
        }
        
        public void WriteToken(string token)
        {
            WriteString(token);
        }
        
        private void WriteVarint(uint value)
        {
            while (value >= 0x80)
            {
                _output.Write(new byte[] { (byte)(value | 0x80) });
                value >>= 7;
            }
            _output.Write(new byte[] { (byte)value });
        }
        
        private void WriteString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                _output.Write(new byte[] { 0 });
                return;
            }
            
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            WriteVarint((uint)bytes.Length);
            _output.Write(bytes);
        }
    }
}
