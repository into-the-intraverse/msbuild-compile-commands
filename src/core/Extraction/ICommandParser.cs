using System.Collections.Generic;
using MsBuildCompileCommands.Core.Models;

namespace MsBuildCompileCommands.Core.Extraction
{
    public interface ICommandParser
    {
        bool IsCompilerInvocation(string commandLine);
        List<CompileCommand> Parse(string commandLine, string directory, IList<string>? diagnostics = null);
    }
}
