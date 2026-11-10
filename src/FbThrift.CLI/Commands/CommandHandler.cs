// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.CLI.Commands;

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FbThrift.CLI.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Base interface for all command handlers.
/// </summary>
public interface ICommandHandler<TOptions> where TOptions : CommandOptions
{
    /// <summary>
    /// Executes the command with the given options.
    /// </summary>
    Task<int> ExecuteAsync(TOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Abstract base class for command handlers with DI support.
/// </summary>
public abstract class CommandHandler<TOptions> : ICommandHandler<TOptions> where TOptions : CommandOptions
{
    protected readonly ILogger Logger;
    protected readonly IServiceProvider ServiceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandHandler{TOptions}"/> class.
    /// </summary>
    protected CommandHandler(ILogger logger, IServiceProvider serviceProvider)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <inheritdoc/>
    public abstract Task<int> ExecuteAsync(TOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a cancellation token with timeout from options.
    /// </summary>
    protected CancellationTokenSource CreateTimeoutCancellationTokenSource(TOptions options, TimeSpan? defaultTimeout = null)
    {
        var timeout = GetTimeout(options) ?? defaultTimeout ?? TimeSpan.FromMinutes(5);
        return CancellationTokenSource.CreateLinkedTokenSource(new CancellationTokenSource(timeout).Token);
    }

    /// <summary>
    /// Gets the timeout from options if available.
    /// </summary>
    protected virtual TimeSpan? GetTimeout(TOptions options)
    {
        return options switch
        {
            ClientOptions clientOpts => clientOpts.Timeout,
            _ => null
        };
    }

    /// <summary>
    /// Logs command execution start.
    /// </summary>
    protected void LogCommandStart(TOptions options)
    {
        var commandName = typeof(TOptions).Name.Replace("Options", "");
        Logger.LogInformation("Starting command: {Command}", commandName);
        
        if (options.Verbosity >= Verbosity.Diagnostic)
        {
            Logger.LogDebug("Command options: {Options}", JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>
    /// Logs command execution completion.
    /// </summary>
    protected void LogCommandComplete(TOptions options, int exitCode, TimeSpan elapsed)
    {
        var commandName = typeof(TOptions).Name.Replace("Options", "");
        if (exitCode == 0)
        {
            Logger.LogInformation("Command {Command} completed successfully in {Elapsed}ms", commandName, elapsed.TotalMilliseconds);
        }
        else
        {
            Logger.LogError("Command {Command} failed with exit code {ExitCode} after {Elapsed}ms", commandName, exitCode, elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Handles exceptions during command execution.
    /// </summary>
    protected async Task<int> HandleExceptionAsync(Exception ex, TOptions options)
    {
        var commandName = typeof(TOptions).Name.Replace("Options", "");
        
        if (options.Verbosity >= Verbosity.Diagnostic)
        {
            Logger.LogError(ex, "Command {Command} failed with exception", commandName);
        }
        else
        {
            Logger.LogError("Command {Command} failed: {Message}", commandName, ex.Message);
        }

        // Return specific exit codes based on exception type
        return ex switch
        {
            ArgumentException => 2,      // Invalid arguments
            FileNotFoundException => 3,  // File not found
            UnauthorizedAccessException => 4, // Permission denied
            TimeoutException => 5,       // Timeout
            OperationCanceledException => 6, // Cancelled
            _ => 1                       // General error
        };
    }
}

/// <summary>
/// Extension methods for command registration.
/// </summary>
public static class CommandExtensions
{
    /// <summary>
    /// Adds a command with the specified options and handler.
    /// </summary>
    public static Command AddCommand<TOptions, THandler>(this Command parent, string name, string description)
        where TOptions : CommandOptions
        where THandler : ICommandHandler<TOptions>
    {
        var command = new Command(name, description);
        
        // Add options from TOptions properties
        foreach (var prop in typeof(TOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<ObsoleteAttribute>() != null) continue;
            
            var option = CreateOptionFromProperty(prop);
            if (option != null)
            {
                command.AddOption(option);
            }
        }

        command.SetHandler(async (InvocationContext context) =>
        {
            var services = context.BindingContext.GetService<IServiceProvider>();
            if (services == null)
            {
                context.Console.Error.WriteLine("Error: Service provider not available");
                context.ExitCode = 1;
                return;
            }

            var handler = services.GetRequiredService<THandler>();
            var options = CreateOptions<TOptions>(context);
            
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var exitCode = await handler.ExecuteAsync(options, context.GetCancellationToken());
                context.ExitCode = exitCode;
            }
            catch (Exception ex)
            {
                var logger = services.GetRequiredService<ILogger<THandler>>();
                context.ExitCode = await handler switch
                {
                    CommandHandler<TOptions> typedHandler => await typedHandler.HandleExceptionAsync(ex, options),
                    _ => 1
                };
            }
            finally
            {
                stopwatch.Stop();
            }
        });

        parent.AddCommand(command);
        return command;
    }

    private static Option? CreateOptionFromProperty(PropertyInfo prop)
    {
        var name = prop.Name.ToLowerInvariant();
        var aliases = new[] { $"--{name}", $"-{name[0]}" };
        
        var description = prop.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? string.Empty;
        
        return prop.PropertyType switch
        {
            Type t when t == typeof(string) => new Option<string>(aliases, description),
            Type t when t == typeof(bool) => new Option<bool>(aliases, description),
            Type t when t == typeof(int) => new Option<int>(aliases, description),
            Type t when t == typeof(TimeSpan) => new Option<TimeSpan>(aliases, description),
            Type t when t.IsArray => CreateArrayOption(prop, aliases, description),
            Type t when t == typeof(FileInfo) => new Option<FileInfo>(aliases, description),
            Type t when t == typeof(DirectoryInfo) => new Option<DirectoryInfo>(aliases, description),
            Type t when t.IsEnum => new Option<string>(aliases, description) { ArgumentHelpName = string.Join("|", Enum.GetNames(t)) },
            _ => null
        };
    }

    private static Option CreateArrayOption(PropertyInfo prop, string[] aliases, string description)
    {
        var elementType = prop.PropertyType.GetElementType()!;
        return elementType switch
        {
            Type t when t == typeof(FileInfo) => new Option<FileInfo[]>(aliases, description),
            Type t when t == typeof(DirectoryInfo) => new Option<DirectoryInfo[]>(aliases, description),
            Type t when t == typeof(string) => new Option<string[]>(aliases, description),
            _ => new Option<string[]>(aliases, description)
        };
    }

    private static TOptions CreateOptions<TOptions>(InvocationContext context) where TOptions : CommandOptions, new()
    {
        var options = new TOptions();
        foreach (var prop in typeof(TOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = prop.Name.ToLowerInvariant();
            if (context.ParseResult.FindResultFor(OptionAliases(name)) is { Tokens.Count: > 0 } result)
            {
                var value = ConvertValue(result.Tokens, prop.PropertyType);
                prop.SetValue(options, value);
            }
        }
        return options;
    }

    private static string[] OptionAliases(string name) => new[] { $"--{name}", $"-{name[0]}" };

    private static object? ConvertValue(IReadOnlyList<Token> tokens, Type targetType)
    {
        if (tokens.Count == 0) return null;
        
        if (targetType.IsArray)
        {
            var elementType = targetType.GetElementType()!;
            var array = Array.CreateInstance(elementType, tokens.Count);
            for (int i = 0; i < tokens.Count; i++)
            {
                array.SetValue(ConvertSingleValue(tokens[i].Value, elementType), i);
            }
            return array;
        }
        
        return ConvertSingleValue(tokens[0].Value, targetType);
    }

    private static object ConvertSingleValue(string value, Type targetType)
    {
        if (targetType == typeof(string)) return value;
        if (targetType == typeof(bool)) return bool.Parse(value);
        if (targetType == typeof(int)) return int.Parse(value);
        if (targetType == typeof(TimeSpan)) return TimeSpan.Parse(value);
        if (targetType == typeof(FileInfo)) return new FileInfo(value);
        if (targetType == typeof(DirectoryInfo)) return new DirectoryInfo(value);
        if (targetType.IsEnum) return Enum.Parse(targetType, value, true);
        return Convert.ChangeType(value, targetType);
    }
}