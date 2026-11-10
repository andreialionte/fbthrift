// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.CLI;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Thrift AST (Abstract Syntax Tree) definitions.
/// </summary>
public sealed class ThriftAst
{
    public string SourceFile { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public List<ThriftInclude> Includes { get; } = new();
    public List<ThriftStructDefinition> Structs { get; } = new();
    public List<ThriftServiceDefinition> Services { get; } = new();
    public List<ThriftEnumDefinition> Enums { get; } = new();
    public List<ThriftExceptionDefinition> Exceptions { get; } = new();
    public List<ThriftTypedefDefinition> Typedefs { get; } = new();
    public List<ThriftConstDefinition> Constants { get; } = new();
}

/// <summary>
/// Thrift include statement.
/// </summary>
public sealed class ThriftInclude
{
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// Thrift struct definition.
/// </summary>
public sealed class ThriftStructDefinition
{
    public string Name { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string DocComment { get; set; } = string.Empty;
    public List<ThriftFieldDefinition> Fields { get; } = new();
}

/// <summary>
/// Thrift field definition.
/// </summary>
public sealed class ThriftFieldDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ThriftTypeReference Type { get; set; } = new();
    public bool Required { get; set; }
    public bool Optional { get; set; }
    public string DefaultValue { get; set; } = string.Empty;
    public string DocComment { get; set; } = string.Empty;
}

/// <summary>
/// Thrift type reference.
/// </summary>
public sealed class ThriftTypeReference
{
    public string Name { get; set; } = string.Empty;
    public bool IsContainer { get; set; }
    public ThriftTypeReference? KeyType { get; set; }
    public ThriftTypeReference? ValueType { get; set; }
    public ThriftTypeReference? ElementType { get; set; }
}

/// <summary>
/// Thrift service definition.
/// </summary>
public sealed class ThriftServiceDefinition
{
    public string Name { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string DocComment { get; set; } = string.Empty;
    public string Extends { get; set; } = string.Empty;
    public List<ThriftMethodDefinition> Methods { get; } = new();
}

/// <summary>
/// Thrift method definition.
/// </summary>
public sealed class ThriftMethodDefinition
{
    public string Name { get; set; } = string.Empty;
    public ThriftTypeReference ReturnType { get; set; } = new();
    public List<ThriftParameterDefinition> Parameters { get; } = new();
    public bool Oneway { get; set; }
    public List<ThriftExceptionReference> Throws { get; } = new();
    public string DocComment { get; set; } = string.Empty;
}

/// <summary>
/// Thrift method parameter.
/// </summary>
public sealed class ThriftParameterDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ThriftTypeReference Type { get; set; } = new();
    public bool Required { get; set; }
    public bool Optional { get; set; }
}

/// <summary>
/// Thrift exception reference in throws clause.
/// </summary>
public sealed class ThriftExceptionReference
{
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// Thrift enum definition.
/// </summary>
public sealed class ThriftEnumDefinition
{
    public string Name { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string DocComment { get; set; } = string.Empty;
    public List<ThriftEnumValue> Values { get; } = new();
}

/// <summary>
/// Thrift enum value.
/// </summary>
public sealed class ThriftEnumValue
{
    public string Name { get; set; } = string.Empty;
    public long Value { get; set; }
    public string DocComment { get; set; } = string.Empty;
}

/// <summary>
/// Thrift exception definition.
/// </summary>
public sealed class ThriftExceptionDefinition
{
    public string Name { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string DocComment { get; set; } = string.Empty;
    public List<ThriftFieldDefinition> Fields { get; } = new();
}

/// <summary>
/// Thrift typedef definition.
/// </summary>
public sealed class ThriftTypedefDefinition
{
    public string Name { get; set; } = string.Empty;
    public ThriftTypeReference Type { get; set; } = new();
    public string DocComment { get; set; } = string.Empty;
}

/// <summary>
/// Thrift constant definition.
/// </summary>
public sealed class ThriftConstDefinition
{
    public string Name { get; set; } = string.Empty;
    public ThriftTypeReference Type { get; set; } = new();
    public string Value { get; set; } = string.Empty;
    public string DocComment { get; set; } = string.Empty;
}

/// <summary>
/// Thrift parser for parsing .thrift files.
/// </summary>
public sealed class ThriftParser
{
    private readonly string _content;
    private int _position;
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Warnings => _warnings;

    public ThriftAst Parse(string content, string sourceFile)
    {
        _content = content;
        _position = 0;
        _errors.Clear();
        _warnings.Clear();

        var ast = new ThriftAst { SourceFile = sourceFile };

        while (!IsAtEnd())
        {
            SkipWhitespaceAndComments();
            
            if (IsAtEnd()) break;

            var token = PeekToken();
            
            if (token.StartsWith("namespace"))
            {
                ParseNamespace(ast);
            }
            else if (token.StartsWith("include"))
            {
                ParseInclude(ast);
            }
            else if (token.StartsWith("struct"))
            {
                ParseStruct(ast);
            }
            else if (token.StartsWith("service"))
            {
                ParseService(ast);
            }
            else if (token.StartsWith("enum"))
            {
                ParseEnum(ast);
            }
            else if (token.StartsWith("exception"))
            {
                ParseException(ast);
            }
            else if (token.StartsWith("typedef"))
            {
                ParseTypedef(ast);
            }
            else if (token.StartsWith("const"))
            {
                ParseConst(ast);
            }
            else
            {
                // Skip unknown tokens
                ReadToken();
            }
        }

        return ast;
    }

    private void ParseNamespace(ThriftAst ast)
    {
        ReadToken(); // consume "namespace"
        var lang = ReadToken(); // language (e.g., "csharp")
        var ns = ReadToken(); // namespace name
        ast.Namespace = ns;
    }

    private void ParseInclude(ThriftAst ast)
    {
        ReadToken(); // consume "include"
        var path = ReadToken(); // include path
        ast.Includes.Add(new ThriftInclude { Path = path });
    }

    private void ParseStruct(ThriftAst ast)
    {
        ReadToken(); // consume "struct"
        var name = ReadToken();
        
        var structDef = new ThriftStructDefinition
        {
            Name = name,
            SourceFile = string.Empty
        };

        ReadToken(); // consume "{"
        
        while (!IsAtEnd() && PeekToken() != "}")
        {
            var field = ParseField();
            structDef.Fields.Add(field);
        }
        
        ReadToken(); // consume "}"
        ast.Structs.Add(structDef);
    }

    private void ParseService(ThriftAst ast)
    {
        ReadToken(); // consume "service"
        var name = ReadToken();
        
        var serviceDef = new ThriftServiceDefinition
        {
            Name = name,
            SourceFile = string.Empty
        };

        if (PeekToken() == "extends")
        {
            ReadToken(); // consume "extends"
            serviceDef.Extends = ReadToken();
        }

        ReadToken(); // consume "{"
        
        while (!IsAtEnd() && PeekToken() != "}")
        {
            var method = ParseMethod();
            serviceDef.Methods.Add(method);
        }
        
        ReadToken(); // consume "}"
        ast.Services.Add(serviceDef);
    }

    private ThriftMethodDefinition ParseMethod()
    {
        var returnType = ParseTypeRef();
        var name = ReadToken();
        
        var method = new ThriftMethodDefinition
        {
            Name = name,
            ReturnType = returnType
        };

        ReadToken(); // consume "("
        
        if (PeekToken() != ")")
        {
            while (!IsAtEnd() && PeekToken() != ")")
            {
                var param = ParseParameter();
                method.Parameters.Add(param);
                
                if (PeekToken() == ",")
                {
                    ReadToken(); // consume ","
                }
                else
                {
                    break;
                }
            }
        }
        
        ReadToken(); // consume ")"

        if (PeekToken() == "throws")
        {
            ReadToken(); // consume "throws"
            ReadToken(); // consume "("
            
            while (!IsAtEnd() && PeekToken() != ")")
            {
                var excType = ReadToken();
                method.Throws.Add(new ThriftExceptionReference { Type = excType });
                
                if (PeekToken() == ",")
                {
                    ReadToken(); // consume ","
                }
                else
                {
                    break;
                }
            }
            
            ReadToken(); // consume ")"
        }

        if (PeekToken() == "oneway")
        {
            ReadToken(); // consume "oneway"
            method.Oneway = true;
        }

        ReadToken(); // consume ";"
        
        return method;
    }

    private ThriftParameterDefinition ParseParameter()
    {
        var id = int.Parse(ReadToken()); // field ID
        ReadToken(); // consume ":"
        var type = ParseTypeRef();
        var name = ReadToken();
        
        var param = new ThriftParameterDefinition
        {
            Id = id,
            Name = name,
            Type = type
        };

        if (PeekToken() == "=")
        {
            ReadToken(); // consume "="
            param.DefaultValue = ReadToken();
        }

        return param;
    }

    private void ParseEnum(ThriftAst ast)
    {
        ReadToken(); // consume "enum"
        var name = ReadToken();
        
        var enumDef = new ThriftEnumDefinition
        {
            Name = name,
            SourceFile = string.Empty
        };

        ReadToken(); // consume "{"
        
        while (!IsAtEnd() && PeekToken() != "}")
        {
            var valueName = ReadToken();
            long value = 0;
            
            if (PeekToken() == "=")
            {
                ReadToken(); // consume "="
                value = long.Parse(ReadToken());
            }
            
            enumDef.Values.Add(new ThriftEnumValue
            {
                Name = valueName,
                Value = value
            });

            if (PeekToken() == ",")
            {
                ReadToken(); // consume ","
            }
        }
        
        ReadToken(); // consume "}"
        ast.Enums.Add(enumDef);
    }

    private void ParseException(ThriftAst ast)
    {
        ReadToken(); // consume "exception"
        var name = ReadToken();
        
        var exceptionDef = new ThriftExceptionDefinition
        {
            Name = name,
            SourceFile = string.Empty
        };

        ReadToken(); // consume "{"
        
        while (!IsAtEnd() && PeekToken() != "}")
        {
            var field = ParseField();
            exceptionDef.Fields.Add(field);
        }
        
        ReadToken(); // consume "}"
        ast.Exceptions.Add(exceptionDef);
    }

    private void ParseTypedef(ThriftAst ast)
    {
        ReadToken(); // consume "typedef"
        var type = ParseTypeRef();
        var name = ReadToken();
        
        ast.Typedefs.Add(new ThriftTypedefDefinition
        {
            Name = name,
            Type = type
        });

        ReadToken(); // consume ";"
    }

    private void ParseConst(ThriftAst ast)
    {
        ReadToken(); // consume "const"
        var type = ParseTypeRef();
        var name = ReadToken();
        ReadToken(); // consume "="
        var value = ReadToken();
        ReadToken(); // consume ";"

        ast.Constants.Add(new ThriftConstDefinition
        {
            Name = name,
            Type = ParseTypeRef(), // Simplified
            Value = value
        });
    }

    private ThriftFieldDefinition ParseField()
    {
        var required = false;
        var optional = false;
        
        if (PeekToken() == "required")
        {
            ReadToken();
            required = true;
        }
        else if (PeekToken() == "optional")
        {
            ReadToken();
            optional = true;
        }

        var id = int.Parse(ReadToken());
        ReadToken(); // consume ":"
        var type = ParseTypeRef();
        var name = ReadToken();
        
        var field = new ThriftFieldDefinition
        {
            Id = id,
            Name = name,
            Type = type,
            Required = required,
            Optional = optional
        };

        if (PeekToken() == "=")
        {
            ReadToken(); // consume "="
            field.DefaultValue = ReadToken();
        }

        ReadToken(); // consume ";"
        
        return field;
    }

    private ThriftTypeReference ParseTypeRef()
    {
        var token = ReadToken();
        
        var typeRef = new ThriftTypeReference
        {
            Name = token
        };

        if (token == "list" || token == "set")
        {
            typeRef.IsContainer = true;
            ReadToken(); // consume "<"
            typeRef.ElementType = ParseTypeRef();
            ReadToken(); // consume ">"
        }
        else if (token == "map")
        {
            typeRef.IsContainer = true;
            ReadToken(); // consume "<"
            typeRef.KeyType = ParseTypeRef();
            ReadToken(); // consume ","
            typeRef.ValueType = ParseTypeRef();
            ReadToken(); // consume ">"
        }

        return typeRef;
    }

    private string PeekToken()
    {
        var savedPos = _position;
        var token = ReadToken();
        _position = savedPos;
        return token;
    }

    private string ReadToken()
    {
        SkipWhitespaceAndComments();
        
        if (IsAtEnd()) return string.Empty;

        var start = _position;
        
        // Handle string literals
        if (_content[_position] == '"')
        {
            _position++;
            while (!IsAtEnd() && _content[_position] != '"')
            {
                if (_content[_position] == '\\') _position += 2;
                else _position++;
            }
            if (!IsAtEnd()) _position++; // consume closing quote
            return _content.Substring(start, _position - start);
        }

        // Handle identifiers, keywords, numbers, punctuation
        while (!IsAtEnd() && !char.IsWhiteSpace(_content[_position]) && 
               _content[_position] != '{' && _content[_position] != '}' &&
               _content[_position] != '(' && _content[_position] != ')' &&
               _content[_position] != '<' && _content[_position] != '>' &&
               _content[_position] != ',' && _content[_position] != ';' &&
               _content[_position] != ':' && _content[_position] != '=')
        {
            _position++;
        }

        return _content.Substring(start, _position - start);
    }

    private void SkipWhitespaceAndComments()
    {
        while (!IsAtEnd())
        {
            if (char.IsWhiteSpace(_content[_position]))
            {
                _position++;
            }
            else if (_position + 1 < _content.Length && _content[_position] == '/' && _content[_position + 1] == '/')
            {
                // Single-line comment
                _position += 2;
                while (!IsAtEnd() && _content[_position] != '\n')
                {
                    _position++;
                }
            }
            else if (_position + 1 < _content.Length && _content[_position] == '/' && _content[_position + 1] == '*')
            {
                // Multi-line comment
                _position += 2;
                while (!IsAtEnd() && !(_content[_position] == '*' && _position + 1 < _content.Length && _content[_position + 1] == '/'))
                {
                    _position++;
                }
                if (!IsAtEnd()) _position += 2;
            }
            else
            {
                break;
            }
        }
    }

    private bool IsAtEnd()
    {
        return _position >= _content.Length;
    }
}