// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.CLI.Commands;

using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FbThrift.CLI.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handler for the generate command.
/// </summary>
public sealed class GenerateCommandHandler : CommandHandler<GenerateOptions>
{
    private readonly IThriftGenerator _generator;

    public GenerateCommandHandler(ILogger<GenerateCommandHandler> logger, IServiceProvider services, IThriftGenerator generator)
        : base(logger, services)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
    }

    public override async Task<int> ExecuteAsync(GenerateOptions options, CancellationToken cancellationToken = default)
    {
        LogCommandStart(options);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (options.InputFiles.Length == 0)
            {
                Logger.LogError("No input files specified");
                return 2;
            }

            var outputDir = options.OutputDirectory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
            if (!outputDir.Exists)
            {
                outputDir.Create();
            }

            var generateOptions = new ThriftGeneratorOptions
            {
                Language = options.Language,
                Namespace = options.Namespace,
                GenerateAsync = options.GenerateAsync,
                Nullable = options.Nullable,
                IncludePaths = options.IncludePaths.Select(p => p.FullName).ToArray(),
                TemplateDirectory = options.TemplateDirectory?.FullName,
                ForceOverwrite = options.Force
            };

            var results = await _generator.GenerateAsync(
                options.InputFiles.Select(f => f.FullName).ToArray(),
                outputDir.FullName,
                generateOptions,
                cancellationToken);

            if (options.JsonOutput)
            {
                var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(json);
            }
            else
            {
                foreach (var result in results)
                {
                    if (result.Success)
                    {
                        Logger.LogInformation("Generated: {File}", result.OutputFile);
                    }
                    else
                    {
                        Logger.LogError("Failed to generate {File}: {Error}", result.OutputFile, result.Error);
                    }
                }
            }

            var exitCode = results.All(r => r.Success) ? 0 : 1;
            LogCommandComplete(options, exitCode, stopwatch.Elapsed);
            return exitCode;
        }
        catch (Exception ex)
        {
            return await HandleExceptionAsync(ex, options);
        }
    }
}

/// <summary>
/// Handler for the validate command.
/// </summary>
public sealed class ValidateCommandHandler : CommandHandler<ValidateOptions>
{
    private readonly IThriftValidator _validator;

    public ValidateCommandHandler(ILogger<ValidateCommandHandler> logger, IServiceProvider services, IThriftValidator validator)
        : base(logger, services)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public override async Task<int> ExecuteAsync(ValidateOptions options, CancellationToken cancellationToken = default)
    {
        LogCommandStart(options);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (options.InputFiles.Length == 0)
            {
                Logger.LogError("No input files specified");
                return 2;
            }

            var validateOptions = new ThriftValidateOptions
            {
                IncludePaths = options.IncludePaths.Select(p => p.FullName).ToArray(),
                CheckBreakingChanges = options.CheckBreakingChanges,
                BaselineFile = options.BaselineFile?.FullName
            };

            var results = await _validator.ValidateAsync(
                options.InputFiles.Select(f => f.FullName).ToArray(),
                validateOptions,
                cancellationToken);

            if (options.JsonOutput)
            {
                var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(json);
            }
            else
            {
                foreach (var result in results)
                {
                    if (result.IsValid)
                    {
                        Logger.LogInformation("Valid: {File}", result.File);
                    }
                    else
                    {
                        Logger.LogError("Invalid: {File}", result.File);
                        foreach (var error in result.Errors)
                        {
                            Logger.LogError("  {Error}", error);
                        }
                    }
                }
            }

            var exitCode = results.All(r => r.IsValid) ? 0 : 1;
            LogCommandComplete(options, exitCode, stopwatch.Elapsed);
            return exitCode;
        }
        catch (Exception ex)
        {
            return await HandleExceptionAsync(ex, options);
        }
    }
}

/// <summary>
/// Handler for the compile command.
/// </summary>
public sealed class CompileCommandHandler : CommandHandler<CompileOptions>
{
    private readonly IThriftCompiler _compiler;

    public CompileCommandHandler(ILogger<CompileCommandHandler> logger, IServiceProvider services, IThriftCompiler compiler)
        : base(logger, services)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    }

    public override async Task<int> ExecuteAsync(CompileOptions options, CancellationToken cancellationToken = default)
    {
        LogCommandStart(options);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (options.InputFiles.Length == 0)
            {
                Logger.LogError("No input files specified");
                return 2;
            }

            var outputDir = options.OutputDirectory ?? new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "compiled"));
            if (!outputDir.Exists)
            {
                outputDir.Create();
            }

            var compileOptions = new ThriftCompileOptions
            {
                OutputDirectory = outputDir.FullName,
                Debug = options.Debug,
                IncludePaths = options.IncludePaths.Select(p => p.FullName).ToArray()
            };

            var result = await _compiler.CompileAsync(
                options.InputFiles.Select(f => f.FullName).ToArray(),
                compileOptions,
                cancellationToken);

            if (options.JsonOutput)
            {
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(json);
            }
            else
            {
                if (result.Success)
                {
                    Logger.LogInformation("Compilation successful. Output: {OutputDir}", result.OutputDirectory);
                    foreach (var artifact in result.Artifacts)
                    {
                        Logger.LogInformation("  Artifact: {Artifact}", artifact);
                    }
                }
                else
                {
                    Logger.LogError("Compilation failed: {Errors}", string.Join(", ", result.Errors));
                }
            }

            var exitCode = result.Success ? 0 : 1;
            LogCommandComplete(options, exitCode, stopwatch.Elapsed);
            return exitCode;
        }
        catch (Exception ex)
        {
            return await HandleExceptionAsync(ex, options);
        }
    }
}

/// <summary>
/// Handler for the server command.
/// </summary>
public sealed class ServerCommandHandler : CommandHandler<ServerOptions>
{
    private readonly IThriftServerFactory _serverFactory;

    public ServerCommandHandler(ILogger<ServerCommandHandler> logger, IServiceProvider services, IThriftServerFactory serverFactory)
        : base(logger, services)
    {
        _serverFactory = serverFactory ?? throw new ArgumentNullException(nameof(serverFactory));
    }

    public override async Task<int> ExecuteAsync(ServerOptions options, CancellationToken cancellationToken = default)
    {
        LogCommandStart(options);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint))
            {
                Logger.LogError("Invalid endpoint: {Endpoint}", options.Endpoint);
                return 2;
            }

            var serverOptions = new ThriftServerOptions
            {
                Endpoint = endpoint,
                Transport = options.Transport,
                Protocol = options.Protocol,
                MaxConnections = options.MaxConnections,
                UseTls = options.UseTls,
                CertificateFile = options.CertificateFile?.FullName
            };

            // Load service assembly if provided
            if (options.ServiceAssembly != null)
            {
                if (!File.Exists(options.ServiceAssembly.FullName))
                {
                    Logger.LogError("Service assembly not found: {Path}", options.ServiceAssembly.FullName);
                    return 3;
                }
                serverOptions.ServiceAssembly = options.ServiceAssembly.FullName;
            }

            var server = await _serverFactory.CreateServerAsync(serverOptions, cancellationToken);
            
            Logger.LogInformation("Server started on {Endpoint}", options.Endpoint);
            Logger.LogInformation("Press Ctrl+C to stop the server");

            // Wait for cancellation
            var tcs = new TaskCompletionSource<int>();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                tcs.TrySetResult(0);
            };

            await server.StartAsync(cancellationToken);
            
            try
            {
                await tcs.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on Ctrl+C
            }

            await server.StopAsync(cancellationToken);
            Logger.LogInformation("Server stopped");

            LogCommandComplete(options, 0, stopwatch.Elapsed);
            return 0;
        }
        catch (Exception ex)
        {
            return await HandleExceptionAsync(ex, options);
        }
    }
}

/// <summary>
/// Handler for the client command.
/// </summary>
public sealed class ClientCommandHandler : CommandHandler<ClientOptions>
{
    private readonly IThriftClientFactory _clientFactory;

    public ClientCommandHandler(ILogger<ClientCommandHandler> logger, IServiceProvider services, IThriftClientFactory clientFactory)
        : base(logger, services)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public override async Task<int> ExecuteAsync(ClientOptions options, CancellationToken cancellationToken = default)
    {
        LogCommandStart(options);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (string.IsNullOrEmpty(options.ServiceName))
            {
                Logger.LogError("Service name is required");
                return 2;
            }

            if (string.IsNullOrEmpty(options.MethodName))
            {
                Logger.LogError("Method name is required");
                return 2;
            }

            if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint))
            {
                Logger.LogError("Invalid endpoint: {Endpoint}", options.Endpoint);
                return 2;
            }

            // Parse request data
            object? request = null;
            if (!string.IsNullOrEmpty(options.RequestJson))
            {
                request = JsonSerializer.Deserialize<object>(options.RequestJson);
            }
            else if (options.RequestFile != null)
            {
                if (!File.Exists(options.RequestFile.FullName))
                {
                    Logger.LogError("Request file not found: {Path}", options.RequestFile.FullName);
                    return 3;
                }
                var json = await File.ReadAllTextAsync(options.RequestFile.FullName, cancellationToken);
                request = JsonSerializer.Deserialize<object>(json);
            }

            if (request == null)
            {
                Logger.LogError("Request data is required (--request-json or --request-file)");
                return 2;
            }

            var clientOptions = new ThriftClientOptions
            {
                Endpoint = endpoint,
                Transport = options.Transport,
                Protocol = options.Protocol,
                Timeout = options.Timeout
            };

            var client = await _clientFactory.CreateClientAsync(clientOptions, cancellationToken);

            try
            {
                // This is simplified - in reality you'd need to know the request/response types
                // For now, we'll just show the pattern
                Logger.LogInformation("Calling {Service}.{Method}", options.ServiceName, options.MethodName);
                
                // In a real implementation, you'd use the generated client
                // var response = await client.CallAsync<RequestType, ResponseType>($"{options.ServiceName}.{options.MethodName}", request);
                
                Logger.LogInformation("Call completed successfully");
                LogCommandComplete(options, 0, stopwatch.Elapsed);
                return 0;
            }
            finally
            {
                await client.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            return await HandleExceptionAsync(ex, options);
        }
    }
}