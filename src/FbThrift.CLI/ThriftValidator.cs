// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.CLI;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// Thrift validator implementation.
/// </summary>
public sealed class ThriftValidator : IThriftValidator
{
    private readonly ILogger<ThriftValidator> _logger;

    public ThriftValidator(ILogger<ThriftValidator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<ThriftValidationResult>> ValidateAsync(
        string[] inputFiles,
        ThriftValidateOptions options,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ThriftValidationResult>();

        foreach (var inputFile in inputFiles)
        {
            var result = new ThriftValidationResult
            {
                File = inputFile
            };

            try
            {
                _logger.LogInformation("Validating {InputFile}", inputFile);

                if (!File.Exists(inputFile))
                {
                    result.IsValid = false;
                    result.Errors = new[] { $"Input file not found: {inputFile}" };
                    results.Add(result);
                    continue;
                }

                var content = await File.ReadAllTextAsync(inputFile, cancellationToken);
                
                // Parse the Thrift file
                var parser = new ThriftParser();
                var ast = parser.Parse(content, Path.GetFileName(inputFile));

                // Check for parse errors
                if (parser.Errors.Any())
                {
                    result.IsValid = false;
                    result.Errors = parser.Errors.ToArray();
                }
                else
                {
                    result.IsValid = true;
                    
                    // Run additional validation rules
                    var validationErrors = ValidateAst(ast, options);
                    if (validationErrors.Any())
                    {
                        result.IsValid = false;
                        result.Errors = validationErrors.ToArray();
                    }
                    
                    // Run warnings
                    var warningErrors = ValidateWarnings(ast);
                    if (warningErrors.Any())
                    {
                        result.Warnings = warningErrors.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Errors = new[] { ex.Message };
            }

            results.Add(result);
        }

        return results;
    }

    private List<string> ValidateAst(ThriftAst ast, ThriftValidateOptions options)
    {
        var errors = new List<string>();

        // Check for duplicate struct names
        var structNames = ast.Structs.GroupBy(s => s.Name).Where(g => g.Count() > 1);
        foreach (var group in structNames)
        {
            errors.Add($"Duplicate struct name: {group.Key}");
        }

        // Check for duplicate service names
        var serviceNames = ast.Services.GroupBy(s => s.Name).Where(g => g.Count() > 1);
        foreach (var group in serviceNames)
        {
            errors.Add($"Duplicate service name: {group.Key}");
        }

        // Check for duplicate enum names
        var enumNames = ast.Enums.GroupBy(e => e.Name).Where(g => g.Count() > 1);
        foreach (var group in enumNames)
        {
            errors.Add($"Duplicate enum name: {group.Key}");
        }

        // Check for duplicate exception names
        var exceptionNames = ast.Exceptions.GroupBy(e => e.Name).Where(g => g.Count() > 1);
        foreach (var group in exceptionNames)
        {
            errors.Add($"Duplicate exception name: {group.Key}");
        }

        // Validate struct fields
        foreach (var structDef in ast.Structs)
        {
            var fieldIds = structDef.Fields.GroupBy(f => f.Id).Where(g => g.Count() > 1);
            foreach (var group in fieldIds)
            {
                errors.Add($"Duplicate field ID {group.Key} in struct {structDef.Name}");
            }

            var fieldNames = structDef.Fields.GroupBy(f => f.Name).Where(g => g.Count() > 1);
            foreach (var group in fieldNames)
            {
                errors.Add($"Duplicate field name {group.Key} in struct {structDef.Name}");
            }
        }

        // Validate service methods
        foreach (var serviceDef in ast.Services)
        {
            var methodNames = serviceDef.Methods.GroupBy(m => m.Name).Where(g => g.Count() > 1);
            foreach (var group in methodNames)
            {
                errors.Add($"Duplicate method name {group.Key} in service {serviceDef.Name}");
            }

            foreach (var method in serviceDef.Methods)
            {
                if (method.Parameters.Count != 1)
                {
                    // Thrift methods should have exactly one parameter (the request struct)
                    // This is a warning, not an error
                }
            }
        }

        // Validate enum values
        foreach (var enumDef in ast.Enums)
        {
            var valueNames = enumDef.Values.GroupBy(v => v.Name).Where(g => g.Count() > 1);
            foreach (var group in valueNames)
            {
                errors.Add($"Duplicate enum value name {group.Key} in enum {enumDef.Name}");
            }

            var valueValues = enumDef.Values.GroupBy(v => v.Value).Where(g => g.Count() > 1);
            foreach (var group in valueValues)
            {
                errors.Add($"Duplicate enum value {group.Key} in enum {enumDef.Name}");
            }
        }

        // Check for breaking changes if baseline provided
        if (options.CheckBreakingChanges && !string.IsNullOrEmpty(options.BaselineFile))
        {
            if (File.Exists(options.BaselineFile))
            {
                var baselineContent = File.ReadAllText(options.BaselineFile);
                var baselineParser = new ThriftParser();
                var baselineAst = baselineParser.Parse(baselineContent, Path.GetFileName(options.BaselineFile));

                var breakingChanges = DetectBreakingChanges(baselineAst, ast);
                errors.AddRange(breakingChanges);
            }
        }

        return errors;
    }

    private List<string> ValidateWarnings(ThriftAst ast)
    {
        var warnings = new List<string>();

        // Warn about empty structs
        foreach (var structDef in ast.Structs)
        {
            if (!structDef.Fields.Any())
            {
                warnings.Add($"Struct {structDef.Name} has no fields");
            }
        }

        // Warn about empty services
        foreach (var serviceDef in ast.Services)
        {
            if (!serviceDef.Methods.Any())
            {
                warnings.Add($"Service {serviceDef.Name} has no methods");
            }
        }

        // Warn about empty enums
        foreach (var enumDef in ast.Enums)
        {
            if (!enumDef.Values.Any())
            {
                warnings.Add($"Enum {enumDef.Name} has no values");
            }
        }

        // Warn about missing doc comments
        foreach (var structDef in ast.Structs)
        {
            if (string.IsNullOrEmpty(structDef.DocComment))
            {
                warnings.Add($"Struct {structDef.Name} is missing documentation");
            }
        }

        foreach (var serviceDef in ast.Services)
        {
            if (string.IsNullOrEmpty(serviceDef.DocComment))
            {
                warnings.Add($"Service {serviceDef.Name} is missing documentation");
            }
        }

        foreach (var enumDef in ast.Enums)
        {
            if (string.IsNullOrEmpty(enumDef.DocComment))
            {
                warnings.Add($"Enum {enumDef.Name} is missing documentation");
            }
        }

        return warnings;
    }

    private List<string> DetectBreakingChanges(ThriftAst baseline, ThriftAst current)
    {
        var breakingChanges = new List<string>();

        // Check for removed structs
        foreach (var baselineStruct in baseline.Structs)
        {
            if (!current.Structs.Any(s => s.Name == baselineStruct.Name))
            {
                breakingChanges.Add($"Struct {baselineStruct.Name} was removed (breaking change)");
            }
        }

        // Check for removed struct fields
        foreach (var baselineStruct in baseline.Structs)
        {
            var currentStruct = current.Structs.FirstOrDefault(s => s.Name == baselineStruct.Name);
            if (currentStruct != null)
            {
                foreach (var baselineField in baselineStruct.Fields)
                {
                    if (!currentStruct.Fields.Any(f => f.Id == baselineField.Id))
                    {
                        breakingChanges.Add($"Field {baselineField.Name} (ID: {baselineField.Id}) was removed from struct {baselineStruct.Name} (breaking change)");
                    }
                }

                // Check for field ID changes
                foreach (var currentField in currentStruct.Fields)
                {
                    var baselineField = baselineStruct.Fields.FirstOrDefault(f => f.Name == currentField.Name);
                    if (baselineField != null && baselineField.Id != currentField.Id)
                    {
                        breakingChanges.Add($"Field {currentField.Name} in struct {currentStruct.Name} changed ID from {baselineField.Id} to {currentField.Id} (breaking change)");
                    }
                }
            }
        }

        // Check for removed services
        foreach (var baselineService in baseline.Services)
        {
            if (!current.Services.Any(s => s.Name == baselineService.Name))
            {
                breakingChanges.Add($"Service {baselineService.Name} was removed (breaking change)");
            }
        }

        // Check for removed service methods
        foreach (var baselineService in baseline.Services)
        {
            var currentService = current.Services.FirstOrDefault(s => s.Name == baselineService.Name);
            if (currentService != null)
            {
                foreach (var baselineMethod in baselineService.Methods)
                {
                    if (!currentService.Methods.Any(m => m.Name == baselineMethod.Name))
                    {
                        breakingChanges.Add($"Method {baselineMethod.Name} was removed from service {baselineService.Name} (breaking change)");
                    }
                }

                // Check for method signature changes
                foreach (var currentMethod in currentService.Methods)
                {
                    var baselineMethod = baselineService.Methods.FirstOrDefault(m => m.Name == currentMethod.Name);
                    if (baselineMethod != null)
                    {
                        if (baselineMethod.ReturnType.Name != currentMethod.ReturnType.Name)
                        {
                            breakingChanges.Add($"Method {currentMethod.Name} in service {currentService.Name} changed return type from {baselineMethod.ReturnType.Name} to {currentMethod.ReturnType.Name} (breaking change)");
                        }

                        if (baselineMethod.Oneway != currentMethod.Oneway)
                        {
                            breakingChanges.Add($"Method {currentMethod.Name} in service {currentService.Name} changed oneway status (breaking change)");
                        }
                    }
                }
            }
        }

        // Check for removed enums
        foreach (var baselineEnum in baseline.Enums)
        {
            if (!current.Enums.Any(e => e.Name == baselineEnum.Name))
            {
                breakingChanges.Add($"Enum {baselineEnum.Name} was removed (breaking change)");
            }
        }

        // Check for removed enum values
        foreach (var baselineEnum in baseline.Enums)
        {
            var currentEnum = current.Enums.FirstOrDefault(e => e.Name == baselineEnum.Name);
            if (currentEnum != null)
            {
                foreach (var baselineValue in baselineEnum.Values)
                {
                    if (!currentEnum.Values.Any(v => v.Name == baselineValue.Name))
                    {
                        breakingChanges.Add($"Enum value {baselineValue.Name} was removed from enum {baselineEnum.Name} (breaking change)");
                    }
                }

                // Check for enum value changes
                foreach (var currentValue in currentEnum.Values)
                {
                    var baselineValue = baselineEnum.Values.FirstOrDefault(v => v.Name == currentValue.Name);
                    if (baselineValue != null && baselineValue.Value != currentValue.Value)
                    {
                        breakingChanges.Add($"Enum value {currentValue.Name} in enum {currentEnum.Name} changed value from {baselineValue.Value} to {currentValue.Value} (breaking change)");
                    }
                }
            }
        }

        return breakingChanges;
    }
}