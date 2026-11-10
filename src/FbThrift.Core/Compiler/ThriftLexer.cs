// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Compiler;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

/// <summary>
/// Thrift IDL Token types.
/// </summary>
public enum ThriftTokenType
{
    EOF,
    Identifier,
    StringLiteral,
    IntegerLiteral,
    DoubleLiteral,
    Keyword,
    Symbol,
    Comment,
    Annotation,
}

/// <summary>
/// Represents a token in the Thrift IDL.
/// </summary>
public readonly struct ThriftToken
{
    public ThriftTokenType Type { get; init; }
    public string Text { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    
    public override string ToString() => $"{Type}: '{Text}' at {Line}:{Column}";
}

/// <summary>
/// Thrift IDL Lexer - converts .thrift source into tokens.
/// Supports annotations, includes, namespaces, and cpp2 annotations.
/// </summary>
public sealed class ThriftLexer
{
    private readonly string _input;
    private readonly string _sourceName;
    private int _position = 0;
    private int _line = 1;
    private int _column = 1;
    
    private static readonly HashSet<string> _keywords = new()
    {
        "namespace", "include", "cpp_namespace", "java_namespace", "py_namespace",
        "perl_namespace", "php_namespace", "rb_namespace", "cocoa_namespace",
        "struct", "union", "exception", "enum", "typedef", "service", "extends",
        "throws", "oneway", "optional", "required", "const", "list", "set", "map",
        "bool", "byte", "i16", "i32", "i64", "double", "string", "binary", "void",
        "true", "false", "enum", "struct", "union", "exception", "service", "extends",
        "cpp_namespace", "java_namespace", "py_namespace", "perl_namespace",
        "php_namespace", "rb_namespace", "cocoa_namespace", "hs_namespace",
        "go_namespace", "swift_namespace", "rust_namespace", "dart_namespace",
        "cpp2", "cpp2_ref", "cpp2_terse_write", "cpp2_force_include", "cpp2_template",
        "hack", "java", "python", "go", "rust", "dart", "swift", "cpp", "java",
        "namespace", "include", "struct", "union", "exception", "enum", "typedef", 
        "service", "extends", "throws", "oneway", "optional", "required", "const",
        "list", "set", "map", "bool", "byte", "i16", "i32", "i64", "double", 
        "string", "binary", "void",
        "true", "false", "enum", "struct", "union", "exception", "service", "extends",
        "cpp_namespace", "java_namespace", "py_namespace", "perl_namespace",
        "php_namespace", "rb_namespace", "cocoa_namespace", "hs_namespace",
        "go_namespace", "swift_namespace", "rust_namespace", "dart_namespace",
        "cpp2", "cpp2_ref", "cpp2_terse_write", "cpp2_force_include", "cpp2_template",
        "hack", "java", "python", "go", "rust", "dart", "swift", "cpp", "java",
        "namespace", "include", "struct", "union", "exception", "enum", "typedef", 
        "service", "extends", "throws", "oneway", "optional", "required", "const",
        "list", "set", "map", "bool", "byte", "i16", "i32", "i64", "double", 
        "string", "binary", "void",
        "true", "false", "enum", "struct", "union", "exception", "service", "extends",
        "throws", "oneway", "optional", "required", "const",
        "list", "set", "map", "enum", "struct", "union", "exception", "service",
        "cpp", "java", "python", "go", "rust", "dart", "swift", "cpp2", "cpp2_ref",
        "cpp2_terse_write", "cpp2_force_include", "cpp2_template",
        "hack", "java", "python", "go", "rust", "dart", "swift", "cpp", "java"
    };
    
    // Annotation keywords that start with @
    private static readonly HashSet<string> _annotationKeywords = new()
    {
        "cpp.Ref", "cpp.Ref{}", "cpp.TerseWrite", "cpp.ForceInclude", "cpp2.Template",
        "cpp.Ref", "cpp.ForceInclude", "cpp.TerseWrite", "cpp.Box", "cpp.Reference",
        "hack.MigrationBlockingLegacyJSONSerialization", "hack.MigrationBlockingLegacyJSONSerialization",
        "java.Type", "java.Final", "java.Nullable", "java.NonNull",
        "py.Dynamic", "py.Async", "go.Tag", "rust.Async", "rust.Box",
        "dart.Nullable", "swift.Optional", "cpp2.Ref", "cpp2.TerseWrite",
        "cpp2.ForceInclude", "cpp2.Template"
    };

    public ThriftLexer(string input, string sourceName = "<string>")
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _sourceName = sourceName;
    }
    
    public ThriftToken NextToken()
    {
        SkipWhitespaceAndComments();
        
        if (_position >= _input.Length)
            return new ThriftToken { Type = ThriftTokenType.EOF, Text = "", Line = _line, Column = _column };
        
        char c = _input[_position];
        
        // String literal
        if (c == '"' || c == '\'')
            return ReadStringLiteral();
        
        // Number
        if (char.IsDigit(c) || (c == '-' && _position + 1 < _input.Length && char.IsDigit(_input[_position + 1])))
            return ReadNumber();
        
        // Annotation (starts with @)
        if (c == '@')
            return ReadAnnotation();
        
        // Identifier or keyword
        if (char.IsLetter(c) || c == '_')
            return ReadIdentifier();
        
        // Symbols
        return ReadSymbol();
    }
    
    private void SkipWhitespaceAndComments()
    {
        while (_position < _input.Length)
        {
            char c = _input[_position];
            
            if (char.IsWhiteSpace(c))
            {
                if (c == '\n')
                {
                    _line++;
                    _column = 1;
                }
                else
                {
                    _column++;
                }
                _position++;
            }
            else if (c == '/' && _position + 1 < _input.Length)
            {
                char next = _input[_position + 1];
                if (next == '/')
                {
                    // Line comment
                    _position += 2;
                    _column += 2;
                    while (_position < _input.Length && _input[_position] != '\n')
                    {
                        if (_input[_position] == '\n')
                        {
                            _line++;
                            _column = 1;
                        }
                        else
                        {
                            _column++;
                        }
                        _position++;
                    }
                }
                else if (next == '*')
                {
                    // Block comment
                    _position += 2;
                    _column += 2;
                    while (_position + 1 < _input.Length)
                    {
                        if (_input[_position] == '*' && _input[_position + 1] == '/')
                        {
                            _position += 2;
                            _column += 2;
                            break;
                        }
                        if (_input[_position] == '\n')
                        {
                            _line++;
                            _column = 1;
                        }
                        else
                        {
                            _column++;
                        }
                        _position++;
                    }
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }
    }
    
    private ThriftToken ReadAnnotation()
    {
        int start = _position;
        int line = _line;
        int col = _column;
        
        _position++; // Skip @
        _column++;
        
        var sb = new StringBuilder();
        
        // Read annotation name (can include dots like cpp.Ref)
        while (_position < _input.Length && (char.IsLetterOrDigit(_input[_position]) || _input[_position] == '.' || _input[_position] == '_'))
        {
            sb.Append(_input[_position]);
            _position++;
            _column++;
        }
        
        // Check for annotation parameters like @cpp.Ref{...}
        if (_position < _input.Length && _input[_position] == '{')
        {
            sb.Append('{');
            _position++;
            _column++;
            int braceCount = 1;
            while (_position < _input.Length && braceCount > 0)
            {
                char c = _input[_position];
                sb.Append(c);
                if (c == '{') braceCount++;
                else if (c == '}') braceCount--;
                _position++;
                if (c == '\n') { _line++; _column = 1; } else _column++;
                if (braceCount == 0) break;
            }
        }
        
        string text = sb.ToString();
        
        return new ThriftToken
        {
            Type = ThriftTokenType.Annotation,
            Text = text,
            Line = line,
            Column = col
        };
    }
    
    private ThriftToken ReadStringLiteral()
    {
        char quote = _input[_position];
        int start = _position;
        int line = _line;
        int col = _column;
        _position++;
        _column++;
        
        var sb = new StringBuilder();
        while (_position < _input.Length && _input[_position] != quote)
        {
            if (_input[_position] == '\\' && _position + 1 < _input.Length)
            {
                _position++;
                _column++;
                char escaped = _input[_position];
                sb.Append(escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '\\' => '\\',
                    '"' => '"',
                    '\'' => '\'',
                    _ => escaped
                });
            }
            else
            {
                sb.Append(_input[_position]);
            }
            _position++;
            _column++;
        }
        
        if (_position >= _input.Length)
            throw new InvalidOperationException("Unterminated string literal");
        
        _position++; // Skip closing quote
        
        return new ThriftToken
        {
            Type = ThriftTokenType.StringLiteral,
            Text = sb.ToString(),
            Line = line,
            Column = col
        };
    }
    
    private ThriftToken ReadNumber()
    {
        int start = _position;
        int line = _line;
        int col = _column;
        bool isDouble = false;
        
        if (_input[_position] == '-')
        {
            _position++;
            _column++;
        }
        
        while (_position < _input.Length && char.IsDigit(_input[_position]))
        {
            _position++;
            _column++;
        }
        
        if (_position < _input.Length && _input[_position] == '.')
        {
            isDouble = true;
            _position++;
            _column++;
            while (_position < _input.Length && char.IsDigit(_input[_position]))
            {
                _position++;
                _column++;
            }
        }
        
        string text = _input.Substring(start, _position - start);
        
        return new ThriftToken
        {
            Type = isDouble ? ThriftTokenType.DoubleLiteral : ThriftTokenType.IntegerLiteral,
            Text = text,
            Line = _line,
            Column = col
        };
    }
    
    private ThriftToken ReadIdentifier()
    {
        int start = _position;
        int line = _line;
        int col = _column;
        
        while (_position < _input.Length && (char.IsLetterOrDigit(_input[_position]) || _input[_position] == '_' || _input[_position] == '.'))
        {
            _position++;
            _column++;
        }
        
        string text = _input.Substring(start, _position - start);
        
        return new ThriftToken
        {
            Type = _keywords.Contains(text) ? ThriftTokenType.Keyword : ThriftTokenType.Identifier,
            Text = text,
            Line = line,
            Column = col
        };
    }
    
    private ThriftToken ReadSymbol()
    {
        int line = _line;
        int col = _column;
        
        // Multi-character symbols
        if (_position + 1 < _input.Length)
        {
            string twoChar = _input.Substring(_position, 2);
            if (twoChar is "::" or "<=" or ">=" or "==" or "!=" or "->")
            {
                _position += 2;
                _column += 2;
                return new ThriftToken { Type = ThriftTokenType.Symbol, Text = twoChar, Line = line, Column = col };
            }
        }
        
        // Single character symbols
        char c = _input[_position];
        _position++;
        _column++;
        
        return new ThriftToken
        {
            Type = ThriftTokenType.Symbol,
            Text = c.ToString(),
            Line = line,
            Column = col
        };
    }
}