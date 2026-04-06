using System;
using System.Collections.Generic;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;

namespace MsBuildCompileCommands.Core.Extraction
{
    [Obsolete("Use MsvcCommandParser or CommandParserFactory instead.")]
    public sealed class ClCommandParser
    {
        private readonly MsvcCommandParser _inner;

        public ClCommandParser() : this(new ResponseFileParser()) { }

        public ClCommandParser(ResponseFileParser responseFileParser)
        {
            _inner = new MsvcCommandParser(responseFileParser);
        }

        public static bool IsCompilerInvocation(string commandLine)
        {
            return MsvcCommandParser.IsCompilerInvocationStatic(commandLine);
        }

        public List<CompileCommand> Parse(string commandLine, string directory, IList<string>? diagnostics = null)
        {
            return _inner.Parse(commandLine, directory, diagnostics);
        }
    }
}
