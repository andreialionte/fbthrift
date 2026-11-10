// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.CLI;

using System.Collections.Generic;

/// <summary>
/// Options for Thrift code generation.
/// </summary>
public sealed class ThriftGeneratorOptions
{
    public string Language { get; set; } = "csharp";
    public string? Namespace { get; set; }
    public bool GenerateAsync { get; set; } = true;
    public bool Nullable { get; set; } = true;
    public string[] IncludePaths { get; set; } = Array.Empty<string>();
    public string? TemplateDirectory { get; set; }
    public bool ForceOverwrite { get; set; } = false;
}

/// <summary>
/// Result of code generation.
/// </summary>
public sealed class ThriftGenerationResult
{
    public string OutputFile { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Interface for Thrift code generator.
/// </summary>
public interface IThriftGenerator
{
    Task<IReadOnlyList<ThriftGenerationResult>> GenerateAsync(
        string[] inputFiles,
        string outputDirectory,
        ThriftGeneratorOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for Thrift validation.
/// </summary>
public sealed class ThriftValidateOptions
{
    public string[] IncludePaths { get; set; } = Array.Empty<string>();
    public bool CheckBreakingChanges { get; set; } = false;
    public string? BaselineFile { get; set; }
}

/// <summary>
/// Result of Thrift validation.
/// </summary>
public sealed class ThriftValidationResult
{
    public string File { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Interface for Thrift validator.
/// </summary>
public interface IThriftValidator
{
    Task<IReadOnlyList<ThriftValidationResult>> ValidateAsync(
        string[] inputFiles,
        ThriftValidateOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for Thrift compilation.
/// </summary>
public sealed class ThriftCompileOptions
{
    public string OutputDirectory { get; set; } = string.Empty;
    public bool Debug { get; set; } = false;
    public string[] IncludePaths { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Result of Thrift compilation.
/// </summary>
public sealed class ThriftCompileResult
{
    public bool Success { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
    public IReadOnlyList<string> Artifacts { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Interface for Thrift compiler.
/// </summary>
public interface IThriftCompiler
{
    Task<ThriftCompileResult> CompileAsync(
        string[] inputFiles,
        ThriftCompileOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for Thrift server.
/// </summary>
public sealed class ThriftServerOptions
{
    public Uri Endpoint { get; set; } = new Uri("tcp://localhost:9090");
    public string Transport { get; set; } = "tcp";
    public string Protocol { get; set; } = "compact";
    public int MaxConnections { get; set; } = 10000;
    public bool UseTls { get; set; } = false;
    public string? CertificateFile { get; set; }
    public string? ServiceAssembly { get; set; }
}

/// <summary>
/// Interface for Thrift server factory.
/// </summary>
public interface IThriftServerFactory
{
    Task<IThriftServer> CreateServerAsync(ThriftServerOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for Thrift client.
/// </summary>
public sealed class ThriftClientOptions
{
    public Uri Endpoint { get; set; } = new Uri("tcp://localhost:9090");
    public string Transport { get; set; } = "tcp";
    public string Protocol { get; set; } = "compact";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Interface for Thrift client factory.
/// </summary>
public interface IThriftClientFactory
{
    Task<IThriftClient> CreateClientAsync(ThriftClientOptions options, CancellationToken cancellationToken = default);
}