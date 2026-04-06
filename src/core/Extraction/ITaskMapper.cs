using System.Collections.Generic;
using MsBuildCompileCommands.Core.Models;

namespace MsBuildCompileCommands.Core.Extraction
{
    /// <summary>
    /// Maps MSBuild task parameters to compile commands when no TaskCommandLineEventArgs is fired.
    /// </summary>
    public interface ITaskMapper
    {
        /// <summary>Returns true if this mapper handles the given task name.</summary>
        bool CanMap(string taskName);

        /// <summary>Synthesizes compile commands from task parameters.</summary>
        List<CompileCommand> Map(string taskName, IDictionary<string, List<string>> parameters, string directory);
    }
}
