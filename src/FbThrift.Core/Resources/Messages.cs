// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Resources;

using System;
using System.Resources;
using System.Reflection;

/// <summary>
/// Provides access to localized log messages.
/// </summary>
public static class LogMessages
{
    private static readonly ResourceManager _resourceManager = new(
        "FbThrift.Core.Resources.LogMessages",
        Assembly.GetExecutingAssembly());

    /// <summary>
    /// Gets the localized string for the specified key.
    /// </summary>
    public static string GetString(string key)
    {
        return _resourceManager.GetString(key) ?? key;
    }

    /// <summary>
    /// Gets the localized string for the specified key with formatting.
    /// </summary>
    public static string GetString(string key, params object[] args)
    {
        var format = GetString(key);
        return string.Format(format, args);
    }

    // Transport messages
    public static string TransportConnecting => GetString(nameof(TransportConnecting));
    public static string TransportConnected => GetString(nameof(TransportConnected));
    public static string TransportDisconnected => GetString(nameof(TransportDisconnected));
    public static string TransportConnectionFailed => GetString(nameof(TransportConnectionFailed));
    public static string TransportReadTimeout => GetString(nameof(TransportReadTimeout));
    public static string TransportWriteTimeout => GetString(nameof(TransportWriteTimeout));
    public static string TransportMaxFrameSizeExceeded => GetString(nameof(TransportMaxFrameSizeExceeded));

    // Protocol messages
    public static string ProtocolEncoding => GetString(nameof(ProtocolEncoding));
    public static string ProtocolDecoding => GetString(nameof(ProtocolDecoding));
    public static string ProtocolUnsupportedVersion => GetString(nameof(ProtocolUnsupportedVersion));
    public static string ProtocolInvalidFrame => GetString(nameof(ProtocolInvalidFrame));

    // RPC messages
    public static string RpcCallStarted => GetString(nameof(RpcCallStarted));
    public static string RpcCallCompleted => GetString(nameof(RpcCallCompleted));
    public static string RpcCallFailed => GetString(nameof(RpcCallFailed));
    public static string RpcTimeout => GetString(nameof(RpcTimeout));
    public static string RpcCancelled => GetString(nameof(RpcCancelled));
    public static string RpcMethodNotFound => GetString(nameof(RpcMethodNotFound));

    // Serialization messages
    public static string SerializationStarted => GetString(nameof(SerializationStarted));
    public static string SerializationCompleted => GetString(nameof(SerializationCompleted));
    public static string SerializationFailed => GetString(nameof(SerializationFailed));
    public static string DeserializationFailed => GetString(nameof(DeserializationFailed));

    // Server messages
    public static string ServerStarting => GetString(nameof(ServerStarting));
    public static string ServerStarted => GetString(nameof(ServerStarted));
    public static string ServerStopping => GetString(nameof(ServerStopping));
    public static string ServerStopped => GetString(nameof(ServerStopped));
    public static string ServerRequestReceived => GetString(nameof(ServerRequestReceived));
    public static string ServerRequestCompleted => GetString(nameof(ServerRequestCompleted));
    public static string ServerRequestFailed => GetString(nameof(ServerRequestFailed));
}

/// <summary>
/// Provides access to localized error messages.
/// </summary>
public static class ErrorMessages
{
    private static readonly ResourceManager _resourceManager = new(
        "FbThrift.Core.Resources.ErrorMessages",
        Assembly.GetExecutingAssembly());

    /// <summary>
    /// Gets the localized string for the specified key.
    /// </summary>
    public static string GetString(string key)
    {
        return _resourceManager.GetString(key) ?? key;
    }

    /// <summary>
    /// Gets the localized string for the specified key with formatting.
    /// </summary>
    public static string GetString(string key, params object[] args)
    {
        var format = GetString(key);
        return string.Format(format, args);
    }

    // Transport errors
    public static string TransportNotConnected => GetString(nameof(TransportNotConnected));
    public static string TransportAlreadyConnected => GetString(nameof(TransportAlreadyConnected));
    public static string TransportConnectionRefused => GetString(nameof(TransportConnectionRefused));
    public static string TransportCertificateValidationFailed => GetString(nameof(TransportCertificateValidationFailed));
    public static string TransportTlsHandshakeFailed => GetString(nameof(TransportTlsHandshakeFailed));

    // Protocol errors
    public static string ProtocolUnsupportedProtocol => GetString(nameof(ProtocolUnsupportedProtocol));
    public static string ProtocolInvalidMagicNumber => GetString(nameof(ProtocolInvalidMagicNumber));
    public static string ProtocolFrameTooLarge => GetString(nameof(ProtocolFrameTooLarge));
    public static string ProtocolChecksumMismatch => GetString(nameof(ProtocolChecksumMismatch));
    public static string ProtocolInvalidFieldType => GetString(nameof(ProtocolInvalidFieldType));

    // RPC errors
    public static string RpcServiceUnavailable => GetString(nameof(RpcServiceUnavailable));
    public static string RpcMethodNotImplemented => GetString(nameof(RpcMethodNotImplemented));
    public static string RpcInvalidArguments => GetString(nameof(RpcInvalidArguments));
    public static string RpcInternalError => GetString(nameof(RpcInternalError));
    public static string RpcDeadlineExceeded => GetString(nameof(RpcDeadlineExceeded));

    // Serialization errors
    public static string SerializationTypeMismatch => GetString(nameof(SerializationTypeMismatch));
    public static string SerializationMissingRequiredField => GetString(nameof(SerializationMissingRequiredField));
    public static string SerializationUnknownField => GetString(nameof(SerializationUnknownField));
    public static string SerializationBufferOverflow => GetString(nameof(SerializationBufferOverflow));

    // Configuration errors
    public static string ConfigInvalidEndpoint => GetString(nameof(ConfigInvalidEndpoint));
    public static string ConfigMissingCertificate => GetString(nameof(ConfigMissingCertificate));
    public static string ConfigInvalidTimeout => GetString(nameof(ConfigInvalidTimeout));
}