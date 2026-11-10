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
/// Thrift compiler implementation.
/// </summary>
public sealed class ThriftCompiler : IThriftCompiler
{
    private readonly ILogger<ThriftCompiler> _logger;

    public ThriftCompiler(ILogger<ThriftCompiler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ThriftCompileResult> CompileAsync(
        string[] inputFiles,
        ThriftCompileOptions options,
        CancellationToken cancellationToken = default)
    {
        var result = new ThriftCompileResult();
        var artifacts = new List<string>();
        var errors = new List<string>();

        try
        {
            var outputDir = options.OutputDirectory;
            Directory.CreateDirectory(outputDir);

            foreach (var inputFile in inputFiles)
            {
                _logger.LogInformation("Compiling {InputFile}", inputFile);

                if (!File.Exists(inputFile))
                {
                    errors.Add($"Input file not found: {inputFile}");
                    continue;
                }

                var content = await File.ReadAllTextAsync(inputFile, cancellationToken);
                
                // Parse the Thrift file
                var parser = new ThriftParser();
                var ast = parser.Parse(content, Path.GetFileName(inputFile));

                if (parser.Errors.Any())
                {
                    errors.AddRange(parser.Errors.Select(e => $"{inputFile}: {e}"));
                    continue;
                }

                // Generate compiled artifacts
                var generatedFiles = await GenerateArtifactsAsync(ast, outputDir, options, cancellationToken);
                artifacts.AddRange(generatedFiles);
            }

            if (errors.Any())
            {
                result.Success = false;
                result.Errors = errors.ToArray();
            }
            else
            {
                result.Success = true;
                result.OutputDirectory = options.OutputDirectory;
                result.Artifacts = artifacts.ToArray();
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors = new[] { ex.Message };
        }

        return result;
    }

    private async Task<List<string>> GenerateArtifactsAsync(
        ThriftAst ast,
        string outputDirectory,
        ThriftCompileOptions options,
        CancellationToken cancellationToken)
    {
        var artifacts = new List<string>();

        // Generate C# code for each definition type
        foreach (var structDef in ast.Structs)
        {
            var code = GenerateStructCode(structDef);
            var fileName = Path.Combine(outputDirectory, $"{structDef.Name}.cs");
            await File.WriteAllTextAsync(fileName, code, cancellationToken);
            artifacts.Add(fileName);
        }

        foreach (var serviceDef in ast.Services)
        {
            var code = GenerateServiceCode(serviceDef);
            var fileName = Path.Combine(outputDirectory, $"{serviceDef.Name}.cs");
            await File.WriteAllTextAsync(fileName, code, cancellationToken);
            artifacts.Add(fileName);
        }

        foreach (var enumDef in ast.Enums)
        {
            var code = GenerateEnumCode(enumDef);
            var fileName = Path.Combine(outputDirectory, $"{enumDef.Name}.cs");
            await File.WriteAllTextAsync(fileName, code, cancellationToken);
            artifacts.Add(fileName);
        }

        foreach (var exceptionDef in ast.Exceptions)
        {
            var code = GenerateExceptionCode(exceptionDef);
            var fileName = Path.Combine(outputDirectory, $"{exceptionDef.Name}.cs");
            await File.WriteAllTextAsync(fileName, code, cancellationToken);
            artifacts.Add(fileName);
        }

        // Generate a .thrift file with all definitions (for debugging/archival)
        var thriftFile = Path.Combine(outputDirectory, "compiled.thrift");
        var thriftContent = GenerateThriftFile(ast);
        await File.WriteAllTextAsync(thriftFile, thriftContent, cancellationToken);
        artifacts.Add(thriftFile);

        // Generate a metadata JSON file
        var metadataFile = Path.Combine(outputDirectory, "metadata.json");
        var metadata = GenerateMetadata(ast);
        await File.WriteAllTextAsync(metadataFile, metadata, cancellationToken);
        artifacts.Add(metadataFile);

        return artifacts;
    }

    private string GenerateStructCode(ThriftStructDefinition structDef)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("// Generated by FbThrift Compiler");
        sb.AppendLine($"// Source: {structDef.SourceFile}");
        sb.AppendLine();
        
        sb.AppendLine("namespace Generated;");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using FbThrift.Core.Abstractions;");
        sb.AppendLine();
        
        sb.AppendLine($"[ThriftStruct]");
        sb.AppendLine($"public partial class {structDef.Name} : IThriftSerializable");
        sb.AppendLine("{");
        
        foreach (var field in structDef.Fields)
        {
            var csType = GetCSharpType(field.Type);
            
            sb.AppendLine($"    [ThriftField({field.Id})]");
            sb.AppendLine($"    public {csType} {field.Name} {{ get; set; }}");
            sb.AppendLine();
        }
        
        sb.AppendLine("    public void Write(IProtocolWriter writer)");
        sb.AppendLine("    {");
        sb.AppendLine("        writer.WriteStructBegin(null);");
        
        foreach (var field in structDef.Fields)
        {
            sb.AppendLine($"        writer.WriteFieldBegin(\"{field.Name}\", TType.{field.Type.Name}, {field.Id});");
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

    private string GenerateServiceCode(ThriftServiceDefinition serviceDef)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("// Generated by FbThrift Compiler");
        sb.AppendLine();
        sb.AppendLine("namespace Generated;");
        sb.AppendLine();
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

    private string GenerateEnumCode(ThriftEnumDefinition enumDef)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("// Generated by FbThrift Compiler");
        sb.AppendLine();
        sb.AppendLine("namespace Generated;");
        sb.AppendLine();
        
        sb.AppendLine($"public enum {enumDef.Name}");
        sb.AppendLine("{");
        
        foreach (var value in enumDef.Values)
        {
            sb.AppendLine($"    {value.Name} = {value.Value},");
        }
        
        sb.AppendLine("}");
        
        return sb.ToString();
    }

    private string GenerateExceptionCode(ThriftExceptionDefinition exceptionDef)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("// Generated by FbThrift Compiler");
        sb.AppendLine();
        sb.AppendLine("namespace Generated;");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using FbThrift.Core.Abstractions;");
        sb.AppendLine();
        
        sb.AppendLine($"[ThriftException]");
        sb.AppendLine($"public partial class {exceptionDef.Name} : ThriftApplicationException");
        sb.AppendLine("{");
        
        foreach (var field in exceptionDef.Fields)
        {
            var csType = GetCSharpType(field.Type);
            
            sb.AppendLine($"    [ThriftField({field.Id})]");
            sb.AppendLine($"    public {csType} {field.Name} {{ get; set; }}");
            sb.AppendLine();
        }
        
        sb.AppendLine("    public override string Message => base.Message;");
        sb.AppendLine("}");
        
        return sb.ToString();
    }

    private string GenerateThriftFile(ThriftAst ast)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("# Generated by FbThrift Compiler");
        sb.AppendLine();
        
        if (!string.IsNullOrEmpty(ast.Namespace))
        {
            sb.AppendLine($"namespace csharp {ast.Namespace}");
            sb.AppendLine();
        }

        foreach (var include in ast.Includes)
        {
            sb.AppendLine($"include \"{include.Path}\"");
        }
        sb.AppendLine();

        foreach (var structDef in ast.Structs)
        {
            sb.AppendLine($"struct {structDef.Name} {{");
            foreach (var field in structDef.Fields)
            {
                var required = field.Required ? "required " : (field.Optional ? "optional " : "");
                sb.AppendLine($"  {required}{field.Type.Name} {field.Name} = {field.Id};");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        foreach (var serviceDef in ast.Services)
        {
            if (!string.IsNullOrEmpty(serviceDef.Extends))
            {
                sb.AppendLine($"service {serviceDef.Name} extends {serviceDef.Extends} {{");
            }
            else
            {
                sb.AppendLine($"service {serviceDef.Name} {{");
            }
            
            foreach (var method in serviceDef.Methods)
            {
                var returnType = method.ReturnType.Name;
                var paramType = method.Parameters[0].Type.Name;
                var oneway = method.Oneway ? "oneway " : "";
                
                if (method.Throws.Any())
                {
                    var throws = string.Join(", ", method.Throws.Select(t => t.Type));
                    sb.AppendLine($"  {oneway}{returnType} {method.Name}(1: {paramType} request) throws ({throws});");
                }
                else
                {
                    sb.AppendLine($"  {oneway}{returnType} {method.Name}(1: {paramType} request);");
                }
            }
            
            sb.AppendLine("}");
            sb.AppendLine();
        }

        foreach (var enumDef in ast.Enums)
        {
            sb.AppendLine($"enum {enumDef.Name} {{");
            foreach (var value in enumDef.Values)
            {
                sb.AppendLine($"  {value.Name} = {value.Value},");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        foreach (var exceptionDef in ast.Exceptions)
        {
            sb.AppendLine($"exception {exceptionDef.Name} {{");
            foreach (var field in exceptionDef.Fields)
            {
                sb.AppendLine($"  1: {field.Type.Name} {field.Name}");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string GenerateMetadata(ThriftAst ast)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("{");
        sb.AppendLine($"  \"namespace\": \"{ast.Namespace}\",");
        sb.AppendLine($"  \"structs\": {ast.Structs.Count},");
        sb.AppendLine($"  \"services\": {ast.Services.Count},");
        sb.AppendLine($"  \"enums\": {ast.Enums.Count},");
        sb.AppendLine($"  \"exceptions\": {ast.Exceptions.Count},");
        sb.AppendLine($"  \"typedefs\": {ast.Typedefs.Count},");
        sb.AppendLine($"  \"constants\": {ast.Constants.Count}");
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
            _ => typeRef.Name
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