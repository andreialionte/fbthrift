// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.CLI.Options;

using System.CommandLine;

/// <summary>
/// Base options for all FbThrift CLI commands.
/// </summary>
public abstract class CommandOptions
{
    /// <summary>
    /// Gets or sets the verbosity level.
    /// </summary>
    public Verbosity Verbosity { get; set; } = Verbosity.Normal;

    /// <summary>
    /// Gets or sets whether to output JSON format.
    /// </summary>
    public bool JsonOutput { get; set; } = false;
}

/// <summary>
/// Verbosity levels for CLI output.
/// </summary>
public enum Verbosity
{
    Quiet = 0,
    Minimal = 1,
    Normal = 2,
    Detailed = 3,
    Diagnostic = 4
}

/// <summary>
/// Options for the generate command.
/// </summary>
public sealed class GenerateOptions : CommandOptions
{
    /// <summary>
    /// Gets or sets the input Thrift file(s).
    /// </summary>
    public FileInfo[] InputFiles { get; set; } = Array.Empty<FileInfo>();

    /// <summary>
    /// Gets or sets the output directory.
    /// </summary>
    public DirectoryInfo? OutputDirectory { get; set; }

    /// <summary>
    /// Gets or sets the target language (csharp, java, python, etc.).
    /// </summary>
    public string Language { get; set; } = "csharp";

    /// <summary>
    /// Gets or sets the namespace for generated code.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Gets or sets whether to generate async methods.
    /// </summary>
    public bool GenerateAsync { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to generate nullable annotations.
    /// </summary>
    public bool Nullable { get; set; } = true;

    /// <summary>
    /// Gets or sets additional include paths.
    /// </summary>
    public DirectoryInfo[] IncludePaths { get; set; } = Array.Empty<DirectoryInfo>();

    /// <summary>
    /// Gets or sets the template directory for custom templates.
    /// </summary>
    public DirectoryInfo? TemplateDirectory { get; set; }

    /// <summary>
    /// Gets or sets whether to overwrite existing files.
    /// </summary>
    public bool Force { get; set; } = false;
}

/// <summary>
/// Options for the validate command.
/// </summary>
public sealed class ValidateOptions : CommandOptions
{
    /// <summary>
    /// Gets or sets the input Thrift file(s) to validate.
    /// </summary>
    public FileInfo[] InputFiles { get; set; } = Array.Empty<FileInfo>();

    /// <summary>
    /// Gets or sets additional include paths.
    /// </summary>
    public DirectoryInfo[] IncludePaths { get; set; } = Array.Empty<DirectoryInfo>();

    /// <summary>
    /// Gets or sets whether to check for breaking changes.
    /// </summary>
    public bool CheckBreakingChanges { get; set; } = false;

    /// <summary>
    /// Gets or sets the baseline file for breaking change detection.
    /// </summary>
    public FileInfo? BaselineFile { get; set; }
}

/// <summary>
/// Options for the compile command.
/// </summary>
public sealed class CompileOptions : CommandOptions
{
    /// <summary>
    /// Gets or sets the input Thrift file(s).
    /// </summary>
    public FileInfo[] InputFiles { get; set; } = Array.Empty<FileInfo>();

    /// <summary>
    /// Gets or sets the output directory for compiled artifacts.
    /// </summary>
    public DirectoryInfo? OutputDirectory { get; set; }

    /// <summary>
    /// Gets or sets whether to emit debug information.
    /// </summary>
    public bool Debug { get; set; } = false;

    /// <summary>
    /// Gets or sets additional include paths.
    /// </summary>
    public DirectoryInfo[] IncludePaths { get; set; } = Array.Empty<DirectoryInfo>();
}

/// <summary>
/// Options for the server command.
/// </summary>
public sealed class ServerOptions : CommandOptions
{
    /// <summary>
    /// Gets or sets the endpoint to listen on.
    /// </summary>
    public string Endpoint { get; set; } = "tcp://localhost:9090";

    /// <summary>
    /// Gets or sets the service assembly to load.
    /// </summary>
    public FileInfo? ServiceAssembly { get; set; }

    /// <summary>
    /// Gets or sets the transport type (tcp, http2, websocket, rocket).
    /// </summary>
    public string Transport { get; set; } = "tcp";

    /// <summary>
    /// Gets or sets the protocol type (binary, compact, json).
    /// </summary>
    public string Protocol { get; set; } = "compact";

    /// <summary>
    /// Gets or sets the maximum number of connections.
    /// </summary>
    public int MaxConnections { get; set; } = 10000;

    /// <summary>
    /// Gets or sets whether to enable TLS.
    /// </summary>
    public bool UseTls { get; set; } = false;

    /// <summary>
    /// Gets or sets the certificate file path.
    /// </summary>
    public FileInfo? CertificateFile { get; set; }
}

/// <summary>
/// Options for the client command.
/// </summary>
public sealed class ClientOptions : CommandOptions
{
    /// <summary>
    /// Gets or sets the endpoint to connect to.
    /// </summary>
    public string Endpoint { get; set; } = "tcp://localhost:9090";

    /// <summary>
    /// Gets or sets the service name to call.
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the method name to call.
    /// </summary>
    public string MethodName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the request data (JSON).
    /// </summary>
    public string? RequestJson { get; set; }

    /// <summary>
    /// Gets or sets the request file path.
    /// </summary>
    public FileInfo? RequestFile { get; set; }

    /// <summary>
    /// Gets or sets the transport type.
    /// </summary>
    public string Transport { get; set; } = "tcp";

    /// <summary>
    /// Gets or sets the protocol type.
    /// </summary>
    public string Protocol { get; set; } = "compact";

    /// <summary>
    /// Gets or sets the request timeout.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}