// Copyright (c) 2024 FbThrift Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace FbThrift.Core.Compiler;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

/// <summary>
/// Thrift IDL Parser - parses tokens into AST.
/// </summary>
public sealed class ThriftParser
{
    private readonly ThriftLexer _lexer;
    private ThriftToken _currentToken;
    private readonly ThriftCompiler _compiler;
    
    public ThriftParser(ThriftLexer lexer, ThriftCompiler compiler)
    {
        _lexer = lexer;
        _compiler = compiler;
        _currentToken = _lexer.NextToken();
    }
    
    public ThriftModule ParseModule()
    {
        var module = new ThriftModule();
        
        while (_currentToken.Type != ThriftTokenType.EOF)
        {
            switch (_currentToken.Type)
            {
                case ThriftTokenType.Keyword when _currentToken.Text == "namespace":
                    ParseNamespace(module);
                    break;
                case ThriftTokenType.Keyword when _currentToken.Text == "include":
                    ParseInclude(module);
                    break;
                case ThriftTokenType.Keyword when _currentToken.Text == "struct":
                    ParseStruct();
                    break;
                case ThriftTokenType.Keyword when _currentToken.Text == "exception":
                    ParseException();
                    break;
                case ThriftTokenType.Keyword when _currentToken.Text == "union":
                    ParseUnion();
                    break;
                case ThriftTokenType.Keyword when _currentToken.Text == "enum":
                    ParseEnum();
                    break;
                case ThriftTokenType.Keyword when _currentToken.Text == "typedef":
                    ParseTypedef();
                    break;
                case ThriftTokenType.Keyword when _currentToken.Text == "service":
                    ParseService();
                    break;
                case ThriftTokenType.Keyword when _currentToken.Text == "const":
                    ParseConst();
                    break;
                case ThriftTokenType.Keyword when _currentToken.Text == "enum":
                    ParseEnum();
                    break;
                case ThriftTokenType.Keyword when _currentToken.Text == "typedef":
                    ParseTypedef();
                    break;
                default:
                    Advance();
                    break;
            }
        }
        
        return module;
    }
    
    private void Advance()
    {
        _currentToken = _lexer.NextToken();
    }
    
    private void Expect(ThriftTokenType type, string text = null)
    {
        if (_currentToken.Type != type || (text != null && _currentToken.Text != text))
        {
            throw new InvalidOperationException(
                $"Expected {type} {(text != null ? text : "")}, got {_currentToken.Type} '{_currentToken.Text}' at line {_currentToken.Line}");
        }
        Advance();
    }
    
    private void ExpectKeyword(string keyword)
    {
        if (_currentToken.Type != ThriftTokenType.Keyword || _currentToken.Text != keyword)
        {
            throw new InvalidOperationException(
                $"Expected keyword '{keyword}', got '{_currentToken.Text}' at line {_currentToken.Line}");
        }
        Advance();
    }
    
    private void ExpectSymbol(string symbol)
    {
        if (_currentToken.Type != ThriftTokenType.Symbol || _currentToken.Text != symbol)
        {
            throw new InvalidOperationException(
                $"Expected symbol '{symbol}', got '{_currentToken.Text}' at line {_currentToken.Line}");
        }
        Advance();
    }
    
    private void ParseNamespace(ThriftModule module)
    {
        ExpectKeyword("namespace");
        
        string language = _currentToken.Text;
        Advance();
        
        string ns = _currentToken.Text;
        Expect(ThriftTokenType.Identifier);
        
        module.Namespaces[language] = ns;
    }
    
    private void ParseInclude(ThriftModule module)
    {
        ExpectKeyword("include");
        string file = _currentToken.Text;
        Expect(ThriftTokenType.StringLiteral);
        module.Includes.Add(file);
    }
    
    private void ParseStruct()
    {
        ExpectKeyword("struct");
        string name = _currentToken.Text;
        Expect(ThriftTokenType.Identifier);
        
        var structDef = new ThriftStruct { Name = name };
        
        ExpectSymbol("{");
        while (_currentToken.Text != "}")
        {
            if (_currentToken.Type == ThriftTokenType.Keyword && _currentToken.Text == "optional")
            {
                Advance();
            }
            else if (_currentToken.Type == ThriftTokenType.Keyword && _currentToken.Text == "required")
            {
                Advance();
            }
            
            ParseField();
        }
        ExpectSymbol("}");
        
        _compiler.RegisterStruct(new ThriftStruct { Name = name });
    }
    
    private void ParseException()
    {
        ExpectKeyword("exception");
        // Similar to struct but IsException = true
        ExpectSymbol("{");
        while (_currentToken.Text != "}")
        {
            ParseField();
        }
        ExpectSymbol("}");
    }
    
    private void ParseUnion()
    {
        ExpectKeyword("union");
        string name = _currentToken.Text;
        Expect(ThriftTokenType.Identifier);
        
        ExpectSymbol("{");
        while (_currentToken.Text != "}")
        {
            ParseField();
        }
        ExpectSymbol("}");
    }
    
    private void ParseEnum()
    {
        ExpectKeyword("enum");
        string name = _currentToken.Text;
        Expect(ThriftTokenType.Identifier);
        
        ExpectSymbol("{");
        var enumDef = new ThriftEnum { Name = name };
        
        while (_currentToken.Text != "}")
        {
            string valueName = _currentToken.Text;
            Expect(ThriftTokenType.Identifier);
            
            int value = 0;
            if (_currentToken.Type == ThriftTokenType.Symbol && _currentToken.Text == "=")
            {
                Advance();
                if (_currentToken.Type == ThriftTokenType.IntegerLiteral)
                {
                    value = int.Parse(_currentToken.Text);
                    Advance();
                }
            }
            
            enumDef.Values.Add(new ThriftEnumValue { Name = valueName, Value = value });
            
            if (_currentToken.Type == ThriftTokenType.Symbol && _currentToken.Text == ",")
            {
                Advance();
            }
        }
        ExpectSymbol("}");
        
        _compiler.RegisterEnum(enumDef);
    }
    
    private void ParseTypedef()
    {
        ExpectKeyword("typedef");
        // Simplified typedef parsing
        Advance(); // type
        ExpectSymbol(";");
    }
    
    private void ParseService()
    {
        ExpectKeyword("service");
        string name = _currentToken.Text;
        Expect(ThriftTokenType.Identifier);
        
        if (_currentToken.Type == ThriftTokenType.Keyword && _currentToken.Text == "extends")
        {
            Advance();
            // Parse extends
        }
        
        ExpectSymbol("{");
        var service = new ThriftService { Name = name };
        
        while (_currentToken.Text != "}")
        {
            ParseFunction();
        }
        ExpectSymbol("}");
        
        _compiler.RegisterService(service);
    }
    
    private void ParseFunction()
    {
        string returnType = _currentToken.Text;
        Advance();
        
        string name = _currentToken.Text;
        Expect(ThriftTokenType.Identifier);
        
        ExpectSymbol("(");
        while (_currentToken.Text != ")")
        {
            ParseField();
            if (_currentToken.Type == ThriftTokenType.Symbol && _currentToken.Text == ",")
            {
                Advance();
            }
        }
        ExpectSymbol(")");
        
        if (_currentToken.Type == ThriftTokenType.Keyword && _currentToken.Text == "throws")
        {
            Advance();
            ExpectSymbol("(");
            while (_currentToken.Text != ")")
            {
                // Parse exception type
                Advance();
                if (_currentToken.Type == ThriftTokenType.Symbol && _currentToken.Text == ",")
                {
                    Advance();
                }
            }
            ExpectSymbol(")");
        }
        
        if (_currentToken.Type == ThriftTokenType.Keyword && _currentToken.Text == "oneway")
        {
            Advance();
        }
        
        ExpectSymbol(";");
    }
    
    private void ParseField()
    {
        int id = 0;
        if (_currentToken.Type == ThriftTokenType.IntegerLiteral)
        {
            id = int.Parse(_currentToken.Text);
            Advance();
            ExpectSymbol(":");
        }
        
        string type = _currentToken.Text;
        Advance();
        
        string name = _currentToken.Text;
        Expect(ThriftTokenType.Identifier);
        
        if (_currentToken.Type == ThriftTokenType.Symbol && _currentToken.Text == "=")
        {
            Advance();
            // Parse default value
        }
        
        if (_currentToken.Type == ThriftTokenType.Symbol && _currentToken.Text == ",")
        {
            Advance();
        }
        else if (_currentToken.Type == ThriftTokenType.Symbol && _currentToken.Text == ";")
        {
            Advance();
        }
    }
    
    private void ParseConst()
    {
        ExpectKeyword("const");
        // Simplified const parsing
        string type = _currentToken.Text;
        Advance();
        string name = _currentToken.Text;
        Expect(ThriftTokenType.Identifier);
        ExpectSymbol("=");
        // Parse value
        ExpectSymbol(";");
    }
}