// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.CLI;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Thrift code generator implementation.
/// </summary>
public sealed class ThriftGenerator : IThriftGenerator
{
    private readonly ILogger<ThriftGenerator> _logger;
    private readonly IServiceProvider _serviceProvider;

    public ThriftGenerator(ILogger<ThriftGenerator> logger, IServiceProvider serviceProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public async Task<IReadOnlyList<ThriftGenerationResult>> GenerateAsync(
        string[] inputFiles,
        string outputDirectory,
        ThriftGeneratorOptions options,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ThriftGenerationResult>();

        foreach (var inputFile in inputFiles)
        {
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                _logger.LogInformation("Generating code for {InputFile}", inputFile);

                if (!File.Exists(inputFile))
                {
                    results.Add(new ThriftGenerationResult
                    {
                        OutputFile = inputFile,
                        Success = false,
                        Error = $"Input file not found: {inputFile}"
                    });
                    continue;
                }

                var content = await File.ReadAllTextAsync(inputFile, cancellationToken);
                
                // Parse the Thrift file
                var parser = new ThriftParser();
                var ast = parser.Parse(content, Path.GetFileName(inputFile));

                // Generate code based on language
                var generatedFiles = new List<string>();
                
                if (options.Language.Equals("csharp", StringComparison.OrdinalIgnoreCase))
                {
                    generatedFiles = await GenerateCSharpAsync(ast, outputDirectory, options, cancellationToken);
                }
                else
                {
                    throw new NotSupportedException($"Language '{options.Language}' not yet supported");
                }

                stopwatch.Stop();
                
                foreach (var file in generatedFiles)
                {
                    results.Add(new ThriftGenerationResult
                    {
                        OutputFile = file,
                        Success = true,
                        Error = null
                    });
                }

                _logger.LogInformation("Generated {Count} files for {InputFile} in {Elapsed}ms", 
                    generatedFiles.Count, inputFile, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Failed to generate code for {InputFile}", inputFile);
                
                results.Add(new ThriftGenerationResult
                {
                    OutputFile = inputFile,
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        return results;
    }

    private async Task<List<string>> GenerateCSharpAsync(
        ThriftAst ast,
        string outputDirectory,
        ThriftGeneratorOptions options,
        CancellationToken cancellationToken)
    {
        var generatedFiles = new List<string>();
        
        Directory.CreateDirectory(outputDirectory);

        // Generate structs/classes
        foreach (var structDef in ast.Structs)
        {
            var code = GenerateStruct(structDef, options);
            var fileName = Path.Combine(outputDirectory, $"{structDef.Name}.cs");
            
            if (options.ForceOverwrite || !File.Exists(fileName))
            {
                await File.WriteAllTextAsync(fileName, code, cancellationToken);
                generatedFiles.Add(fileName);
            }
        }

        // Generate services
        foreach (var serviceDef in ast.Services)
        {
            var code = GenerateService(serviceDef, options);
            var fileName = Path.Combine(outputDirectory, $"{serviceDef.Name}.cs");
            
            if (options.ForceOverwrite || !File.Exists(fileName))
            {
                await File.WriteAllTextAsync(fileName, code, cancellationToken);
                generatedFiles.Add(fileName);
            }
        }

        // Generate enums
        foreach (var enumDef in ast.Enums)
        {
            var code = GenerateEnum(enumDef, options);
            var fileName = Path.Combine(outputDirectory, $"{enumDef.Name}.cs");
            
            if (options.ForceOverwrite || !File.Exists(fileName))
            {
                await File.WriteAllTextAsync(fileName, code, cancellationToken);
                generatedFiles.Add(fileName);
            }
        }

        // Generate exceptions
        foreach (var exceptionDef in ast.Exceptions)
        {
            var code = GenerateException(exceptionDef, options);
            var fileName = Path.Combine(outputDirectory, $"{exceptionDef.Name}.cs");
            
            if (options.ForceOverwrite || !File.Exists(fileName))
            {
                await File.WriteAllTextAsync(fileName, code, cancellationToken);
                generatedFiles.Add(fileName);
            }
        }

        return generatedFiles;
    }

    private string GenerateStruct(ThriftStructDefinition structDef, ThriftGeneratorOptions options)
    {
        var sb = new System.Text.StringBuilder();
        
        sb.AppendLine("// Generated by FbThrift Generator");
        sb.AppendLine($"// Source: {structDef.SourceFile}");
        sb.AppendLine();
        
        if (!string.IsNullOrEmpty(options.Namespace))
        {
            sb.AppendLine($"namespace {options.Namespace};");
            sb.AppendLine();
        }
        
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using FbThrift.Core.Abstractions;");
        sb.AppendLine();
        
        // Attributes
        sb.AppendLine($"[ThriftStruct]");
        sb.AppendLine($"public partial class {structDef.Name} : IThriftSerializable");
        sb.AppendLine("{");
        
        // Fields
        foreach (var field in structDef.Fields)
        {
            var csType = GetCSharpType(field.Type);
            var nullable = options.Nullable && field.Required != true ? "?" : "";
            
            sb.AppendLine($"    /// <summary>");
            sb.AppendLine($"    /// Field ID: {field.Id}");
            if (!string.IsNullOrEmpty(field.DocComment))
            {
                foreach (var line in field.DocComment.Split('\n'))
                {
                    sb.AppendLine($"    /// {line.Trim()}");
                }
            }
            sb.AppendLine($"    /// </summary>");
            sb.AppendLine($"    [ThriftField({field.Id})]");
            sb.AppendLine($"    public {csType}{nullable} {field.Name} {{ get; set; }}");
            sb.AppendLine();
        }
        
        // IThriftSerializable implementation
        sb.AppendLine("    public void Write(IProtocolWriter writer)");
        sb.AppendLine("    {");
        sb.AppendLine("        writer.WriteStructBegin(null);");
        
        foreach (var field in structDef.Fields)
        {
            sb.AppendLine($"        writer.WriteFieldBegin(\"{field.Name}\", TType.{field.Type}, {field.Id});");
            sb.AppendLine($"        writer.Write{GetWriteMethod(field.Type)}({field.Name});");
            sb.AppendLine("        writer.WriteFieldEnd();");
        }
        
        sb.AppendLine("        writer.WriteFieldStop();");
        sb.AppendLine("        writer.WriteStructEnd();");
        sb.AppendLine("    }");
        sb.AppendLine();
        
        sb.AppendLine("    public void Read(IProtocolReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine("        reader.ReadStructBegin(out _);");
        sb.AppendLine("        while (true)");
        sb.AppendLine("        {");
        sb.AppendLine("            reader.ReadFieldBegin(out string name, out TType fieldType, out int fieldId);");
        sb.AppendLine("            if (fieldType == TType.Stop) break;");
        
        foreach (var field in structDef.Fields)
        {
            sb.AppendLine($"            if (fieldId == {field.Id})");
            sb.AppendLine("            {");
            sb.AppendLine($"                {field.Name} = reader.Read{GetReadMethod(field.Type)}();");
            sb.AppendLine("            }");
        }
        
        sb.AppendLine("            reader.ReadFieldEnd();");
        sb.AppendLine("        }");
        sb.AppendLine("        reader.ReadStructEnd();");
        sb.AppendLine("    }");
        
        sb.AppendLine("}");
        
        return sb.ToString();
    }

    private string GenerateService(ThriftServiceDefinition serviceDef, ThriftGeneratorOptions options)
    {
        var sb = new System.Text.StringBuilder();
        
        sb.AppendLine("// Generated by FbThrift Generator");
        sb.AppendLine();
        
        if (!string.IsNullOrEmpty(options.Namespace))
        {
            sb.AppendLine($"namespace {options.Namespace};");
            sb.AppendLine();
        }
        
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using FbThrift.Core.Abstractions;");
        sb.AppendLine();
        
        sb.AppendLine($"public interface I{serviceDef.Name}");
        sb.AppendLine("{");
        
        foreach (var method in serviceDef.Methods)
        {
            var returnType = GetCSharpType(method.ReturnType);
            var paramType = GetCSharpType(method.Parameters[0].Type);
            
            if (method.Oneway)
            {
                sb.AppendLine($"    ValueTask {method.Name}Async({paramType} request, RpcOptions? options = null, CancellationToken cancellationToken = default);");
            }
            else
            {
                sb.AppendLine($"    ValueTask<{returnType}> {method.Name}Async({paramType} request, RpcOptions? options = null, CancellationToken cancellationToken = default);");
            }
        }
        
        sb.AppendLine("}");
        
        return sb.ToString();
    }

    private string GenerateEnum(ThriftEnumDefinition enumDef, ThriftGeneratorOptions options)
    {
        var sb = new System.Text.StringBuilder();
        
        sb.AppendLine("// Generated by FbThrift Generator");
        sb.AppendLine();
        
        if (!string.IsNullOrEmpty(options.Namespace))
        {
            sb.AppendLine($"namespace {options.Namespace};");
            sb.AppendLine();
        }
        
        sb.AppendLine($"public enum {enumDef.Name}");
        sb.AppendLine("{");
        
        foreach (var value in enumDef.Values)
        {
            sb.AppendLine($"    {value.Name} = {value.Value},");
        }
        
        sb.AppendLine("}");
        
        return sb.ToString();
    }

    private string GenerateException(ThriftExceptionDefinition exceptionDef, ThriftGeneratorOptions options)
    {
        var sb = new System.Text.StringBuilder();
        
        sb.AppendLine("// Generated by FbThrift Generator");
        sb.AppendLine();
        
        if (!string.IsNullOrEmpty(options.Namespace))
        {
            sb.AppendLine($"namespace {options.Namespace};");
            sb.AppendLine();
        }
        
        sb.AppendLine("using System;");
        sb.AppendLine("using FbThrift.Core.Abstractions;");
        sb.AppendLine();
        
        sb.AppendLine($"[ThriftException]");
        sb.AppendLine($"public partial class {exceptionDef.Name} : ThriftApplicationException");
        sb.AppendLine("{");
        
        foreach (var field in exceptionDef.Fields)
        {
            var csType = GetCSharpType(field.Type);
            var nullable = options.Nullable && field.Required != true ? "?" : "";
            
            sb.AppendLine($"    [ThriftField({field.Id})]");
            sb.AppendLine($"    public {csType}{nullable} {field.Name} {{ get; set; }}");
            sb.AppendLine();
        }
        
        sb.AppendLine("    public override string Message => base.Message;");
        sb.AppendLine("}");
        
        return sb.ToString();
    }

    private string GetCSharpType(ThriftTypeReference typeRef)
    {
        return typeRef.Name switch
        {
            "bool" => "bool",
            "byte" => "sbyte",
            "i16" => "short",
            "i32" => "int",
            "i64" => "long",
            "double" => "double",
            "float" => "float",
            "string" => "string",
            "binary" => "byte[]",
            _ => typeRef.Name // User-defined types
        };
    }

    private string GetWriteMethod(string thriftType)
    {
        return thriftType switch
        {
            "bool" => "Bool",
            "byte" => "Byte",
            "i16" => "I16",
            "i32" => "I32",
            "i64" => "I64",
            "double" => "Double",
            "float" => "Float",
            "string" => "String",
            "binary" => "Binary",
            _ => "Write"
        };
    }

    private string GetReadMethod(string thriftType)
    {
        return thriftType switch
        {
            "bool" => "Bool",
            "byte" => "Byte",
            "i16" => "I16",
            "i32" => "I32",
            "i64" => "I64",
            "double" => "Double",
            "float" => "Float",
            "string" => "String",
            "binary" => "Binary",
            _ => "Read"
        };
    }
}