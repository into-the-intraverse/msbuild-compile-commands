using System;
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Models;
using MsBuildCompileCommands.Core.Utils;

namespace MsBuildCompileCommands.Core.Extraction
{
    /// <summary>
    /// Maps CudaCompile task parameters to compile commands (nvcc).
    /// </summary>
    public sealed class CudaCompileTaskMapper : ITaskMapper
    {
        public bool CanMap(string taskName)
        {
            return string.Equals(taskName, "CudaCompile", StringComparison.OrdinalIgnoreCase);
        }

        public List<CompileCommand> Map(string taskName, IDictionary<string, List<string>> parameters, string directory)
        {
            var results = new List<CompileCommand>();
            List<string>? sources = ClCompileTaskMapper.GetParameter(parameters, "Sources");
            if (sources == null || sources.Count == 0)
                return results;

            var flags = new List<string>();
            flags.Add("nvcc");

            // Defines (NOT PreprocessorDefinitions) → -D
            string? defs = ClCompileTaskMapper.GetScalar(parameters, "Defines");
            if (defs != null)
            {
                foreach (string def in defs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = def.Trim();
                    if (trimmed.Length > 0)
                        flags.Add("-D" + trimmed);
                }
            }

            // AdditionalIncludeDirectories → -I
            string? includes = ClCompileTaskMapper.GetScalar(parameters, "AdditionalIncludeDirectories");
            if (includes != null)
            {
                foreach (string inc in includes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = inc.Trim();
                    if (trimmed.Length > 0)
                        flags.Add("-I" + trimmed);
                }
            }

            // AdditionalOptions → tokenize
            string? additionalOptions = ClCompileTaskMapper.GetScalar(parameters, "AdditionalOptions");
            if (additionalOptions != null)
            {
                List<string> tokens = CommandLineTokenizer.Tokenize(additionalOptions);
                flags.AddRange(tokens);
            }

            string normalizedDir = PathNormalizer.NormalizeDirectory(directory);

            foreach (string source in sources)
            {
                string normalizedFile = PathNormalizer.Normalize(source, directory);
                var args = new List<string>(flags);
                args.Add(normalizedFile);
                results.Add(new CompileCommand(normalizedDir, normalizedFile, args));
            }

            return results;
        }
    }
}
