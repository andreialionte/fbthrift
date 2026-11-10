// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Exceptions;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FbThrift.Core.Server;

/// <summary>
/// Error kind classification for Thrift exceptions.
/// </summary>
public enum ErrorKind
{
    /// <summary>
    /// Unspecified error kind (defaults to Transient for retry purposes).
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Transient error - might succeed if retried immediately.
    /// </summary>
    Transient = 1,

    /// <summary>
    /// Stateful error - state must be changed before any chance of success.
    /// </summary>
    Stateful = 2,

    /// <summary>
    /// Permanent error - can never succeed.
    /// </summary>
    Permanent = 3
}

/// <summary>
/// Fault attribution for Thrift exceptions.
/// </summary>
public enum FaultAttribution
{
    /// <summary>
    /// Unspecified fault (defaults to Server for retry purposes).
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// The server is at fault.
    /// </summary>
    Server = 1,

    /// <summary>
    /// The client is at fault.
    /// </summary>
    Client = 2
}

/// <summary>
/// Error safety classification for Thrift exceptions.
/// </summary>
public enum ErrorSafety
{
    /// <summary>
    /// Unspecified safety (defaults to Unsafe for retry purposes).
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Safe error - failed RPC is known to have no side effects.
    /// </summary>
    Safe = 1,

    /// <summary>
    /// Unsafe error - may have side effects.
    /// </summary>
    Unsafe = 2
}

/// <summary>
/// RPC idempotency classification.
/// </summary>
public enum RpcIdempotency
{
    /// <summary>
    /// Unspecified (not idempotent).
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Readonly method - no meaningful side effects, can be called anytime.
    /// </summary>
    Readonly = 1,

    /// <summary>
    /// Idempotent method - can be immediately retried after transient failure.
    /// </summary>
    Idempotent = 2
}

/// <summary>
/// Extended TApplicationException with error classification metadata.
/// </summary>
public sealed class ClassifiedApplicationException : TApplicationException
{
    public ErrorKind Kind { get; }
    public FaultAttribution Fault { get; }
    public ErrorSafety Safety { get; }

    public ClassifiedApplicationException(
        ErrorKind kind,
        FaultAttribution fault,
        ErrorSafety safety,
        TApplicationExceptionType type,
        string message)
        : base(type, message)
    {
        Kind = kind;
        Fault = fault;
        Safety = safety;
    }

    public ClassifiedApplicationException(
        ErrorKind kind,
        FaultAttribution fault,
        ErrorSafety safety,
        TApplicationExceptionType type,
        string message,
        Exception innerException)
        : base(type, message, innerException)
    {
        Kind = kind;
        Fault = fault;
        Safety = safety;
    }

    /// <summary>
    /// Determines if this error is retryable based on error classification and RPC idempotency.
    /// </summary>
    /// <param name="rpcIdempotency">The idempotency of the RPC method.</param>
    /// <returns>True if the error is retryable.</returns>
    public bool IsRetryable(RpcIdempotency rpcIdempotency)
    {
        // Retryable = Error.Transient && (RPC.Idempotent || RPC.Readonly || Error.Safe)
        var effectiveKind = Kind == ErrorKind.Unspecified ? ErrorKind.Transient : Kind;
        var effectiveSafety = Safety == ErrorSafety.Unspecified ? ErrorSafety.Unsafe : Safety;

        if (effectiveKind != ErrorKind.Transient)
            return false;

        return rpcIdempotency == RpcIdempotency.Idempotent ||
               rpcIdempotency == RpcIdempotency.Readonly ||
               effectiveSafety == ErrorSafety.Safe;
    }

    /// <summary>
    /// Creates a transient server error.
    /// </summary>
    public static ClassifiedApplicationException TransientServer(string message, ErrorSafety safety = ErrorSafety.Unsafe)
    {
        return new ClassifiedApplicationException(
            ErrorKind.Transient, FaultAttribution.Server, safety,
            TApplicationExceptionType.InternalError, message);
    }

    /// <summary>
    /// Creates a transient client error.
    /// </summary>
    public static ClassifiedApplicationException TransientClient(string message, ErrorSafety safety = ErrorSafety.Unsafe)
    {
        return new ClassifiedApplicationException(
            ErrorKind.Transient, FaultAttribution.Client, safety,
            TApplicationExceptionType.InternalError, message);
    }

    /// <summary>
    /// Creates a stateful server error.
    /// </summary>
    public static ClassifiedApplicationException StatefulServer(string message)
    {
        return new ClassifiedApplicationException(
            ErrorKind.Stateful, FaultAttribution.Server, ErrorSafety.Unsafe,
            TApplicationExceptionType.InternalError, message);
    }

    /// <summary>
    /// Creates a permanent client error.
    /// </summary>
    public static ClassifiedApplicationException PermanentClient(string message)
    {
        return new ClassifiedApplicationException(
            ErrorKind.Permanent, FaultAttribution.Client, ErrorSafety.Unsafe,
            TApplicationExceptionType.InternalError, message);
    }

    /// <summary>
    /// Creates a permanent server error.
    /// </summary>
    public static ClassifiedApplicationException PermanentServer(string message)
    {
        return new ClassifiedApplicationException(
            ErrorKind.Permanent, FaultAttribution.Server, ErrorSafety.Unsafe,
            TApplicationExceptionType.InternalError, message);
    }

    /// <summary>
    /// Creates a safe transient error (always retryable).
    /// </summary>
    public static ClassifiedApplicationException SafeTransient(string message, FaultAttribution fault = FaultAttribution.Server)
    {
        return new ClassifiedApplicationException(
            ErrorKind.Transient, fault, ErrorSafety.Safe,
            TApplicationExceptionType.InternalError, message);
    }
}

/// <summary>
/// Attributes for declaring error classification on Thrift exceptions.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class ThriftErrorKindAttribute : Attribute
{
    public ErrorKind Kind { get; }

    public ThriftErrorKindAttribute(ErrorKind kind)
    {
        Kind = kind;
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class ThriftFaultAttributionAttribute : Attribute
{
    public FaultAttribution Fault { get; }

    public ThriftFaultAttributionAttribute(FaultAttribution fault)
    {
        Fault = fault;
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class ThriftErrorSafetyAttribute : Attribute
{
    public ErrorSafety Safety { get; }

    public ThriftErrorSafetyAttribute(ErrorSafety safety)
    {
        Safety = safety;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ThriftRpcIdempotencyAttribute : Attribute
{
    public RpcIdempotency Idempotency { get; }

    public ThriftRpcIdempotencyAttribute(RpcIdempotency idempotency)
    {
        Idempotency = idempotency;
    }
}

/// <summary>
/// Extension methods for error classification.
/// </summary>
public static class ErrorClassificationExtensions
{
    /// <summary>
    /// Gets the error kind for an exception (from attribute or defaults).
    /// </summary>
    public static ErrorKind GetErrorKind(this Exception exception)
    {
        var type = exception.GetType();
        var attr = type.GetCustomAttribute<ThriftErrorKindAttribute>();
        return attr?.Kind ?? ErrorKind.Unspecified;
    }

    /// <summary>
    /// Gets the fault attribution for an exception.
    /// </summary>
    public static FaultAttribution GetFaultAttribution(this Exception exception)
    {
        var type = exception.GetType();
        var attr = type.GetCustomAttribute<ThriftFaultAttributionAttribute>();
        return attr?.Fault ?? FaultAttribution.Unspecified;
    }

    /// <summary>
    /// Gets the error safety for an exception.
    /// </summary>
    public static ErrorSafety GetErrorSafety(this Exception exception)
    {
        var type = exception.GetType();
        var attr = type.GetCustomAttribute<ThriftErrorSafetyAttribute>();
        return attr?.Safety ?? ErrorSafety.Unspecified;
    }

    /// <summary>
    /// Gets the RPC idempotency for a method.
    /// </summary>
    public static RpcIdempotency GetRpcIdempotency(this MethodInfo method)
    {
        var attr = method.GetCustomAttribute<ThriftRpcIdempotencyAttribute>();
        return attr?.Idempotency ?? RpcIdempotency.Unspecified;
    }

    /// <summary>
    /// Determines if an exception is retryable for a given RPC method.
    /// </summary>
    public static bool IsRetryable(this Exception exception, MethodInfo method)
    {
        var rpcIdempotency = method.GetRpcIdempotency();

        if (exception is ClassifiedApplicationException classified)
        {
            return classified.IsRetryable(rpcIdempotency);
        }

        // For other exceptions, use default classification
        var kind = exception.GetErrorKind();
        var safety = exception.GetErrorSafety();

        var effectiveKind = kind == ErrorKind.Unspecified ? ErrorKind.Transient : kind;
        var effectiveSafety = safety == ErrorSafety.Unspecified ? ErrorSafety.Unsafe : safety;

        if (effectiveKind != ErrorKind.Transient)
            return false;

        return rpcIdempotency == RpcIdempotency.Idempotent ||
               rpcIdempotency == RpcIdempotency.Readonly ||
               effectiveSafety == ErrorSafety.Safe;
    }
}

/// <summary>
/// Retry policy based on error classification.
/// </summary>
public sealed class RetryPolicy
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);
    public double BackoffMultiplier { get; init; } = 2.0;
    public bool Jitter { get; init; } = true;

    public static RetryPolicy Default => new();
    public static RetryPolicy Aggressive => new() { MaxRetries = 5, BaseDelay = TimeSpan.FromMilliseconds(50), BackoffMultiplier = 1.5 };
    public static RetryPolicy Conservative => new() { MaxRetries = 2, BaseDelay = TimeSpan.FromSeconds(1), BackoffMultiplier = 3.0 };
}

/// <summary>
/// Executes an operation with retry logic based on error classification.
/// </summary>
public static class RetryExecutor
{
    /// <summary>
    /// Executes an operation with automatic retry based on error classification.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        MethodInfo method,
        RetryPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= RetryPolicy.Default;
        var random = new Random();

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < policy.MaxRetries && ex.IsRetryable(method))
            {
                var delay = CalculateDelay(attempt, policy);
                if (policy.Jitter)
                {
                    delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * (0.5 + random.NextDouble()));
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Executes an operation with automatic retry (void return).
    /// </summary>
    public static async Task ExecuteAsync(
        Func<Task> operation,
        MethodInfo method,
        RetryPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        }, method, policy, cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan CalculateDelay(int attempt, RetryPolicy policy)
    {
        var delay = TimeSpan.FromMilliseconds(
            policy.BaseDelay.TotalMilliseconds * Math.Pow(policy.BackoffMultiplier, attempt));

        if (delay > policy.MaxDelay)
            delay = policy.MaxDelay;

        return delay;
    }
}