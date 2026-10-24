using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using POrigami = Prowl.OrigamiUI;

namespace V12Studio.Editor
{
    /// <summary>
    /// C# language for the code tab: lexical highlighting from
    /// <see cref="V12.Core.UI.CodeLanguages.CSharp"/> plus a Roslyn session that
    /// provides diagnostics, completion, quick info and go-to-definition.
    /// </summary>
    internal sealed class RoslynCodeLanguage : POrigami.CodeLanguage
    {
        private readonly string _path;
        private readonly string? _projectDir;

        public RoslynCodeLanguage(string path, string? projectDir)
        {
            _path = path;
            _projectDir = projectDir;
        }

        public override string Name => "C#";
        public override POrigami.ICodeLexer? Lexer => V12.Core.UI.V12CodeLanguages.CSharp.Lexer;
        public override string? LineComment => "//";
        public override string? BlockCommentStart => "/*";
        public override string? BlockCommentEnd => "*/";

        public override POrigami.CodeLanguageSession? CreateSession(POrigami.CodeDocument document)
            => new RoslynCodeSession(document, _path, _projectDir);
    }
}
