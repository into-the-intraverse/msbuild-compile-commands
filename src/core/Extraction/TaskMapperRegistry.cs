using System.Collections.Generic;
using MsBuildCompileCommands.Core.Models;

namespace MsBuildCompileCommands.Core.Extraction
{
    /// <summary>
    /// Selects the appropriate <see cref="ITaskMapper"/> for a given MSBuild task name
    /// and delegates command synthesis.
    /// </summary>
    public sealed class TaskMapperRegistry
    {
        private readonly ITaskMapper[] _mappers = { new ClCompileTaskMapper(), new CudaCompileTaskMapper() };
        private readonly GenericTaskMapper _fallback = new GenericTaskMapper();

        public List<CompileCommand> TryMap(string taskName, IDictionary<string, List<string>> parameters, string directory)
        {
            for (int i = 0; i < _mappers.Length; i++)
            {
                if (_mappers[i].CanMap(taskName))
                    return _mappers[i].Map(taskName, parameters, directory);
            }
            return _fallback.Map(taskName, parameters, directory);
        }
    }
}
