using System;
using System.Collections.Generic;
using System.IO;
using MsBuildCompileCommands.Core.Models;
using MsBuildCompileCommands.Core.Utils;

namespace MsBuildCompileCommands.Core.Extraction
{
    /// <summary>
    /// Fallback mapper that attempts to extract compile commands from any task
    /// by probing well-known parameter names.
    /// </summary>
    public sealed class GenericTaskMapper : ITaskMapper
    {
        private static readonly string[] SourceParamNames = { "Sources", "SourceFiles", "InputFiles", "Inputs" };
        private static readonly string[] IncludeParamNames = { "AdditionalIncludeDirectories", "IncludePaths", "Includes" };
        private static readonly string[] DefineParamNames = { "PreprocessorDefinitions", "Defines" };

        public bool CanMap(string taskName)
        {
            return true;
        }

        public List<CompileCommand> Map(string taskName, IDictionary<string, List<string>> parameters, string directory)
        {
            var results = new List<CompileCommand>();

            // Find source files from known parameter names
            List<string>? sources = null;
            foreach (string name in SourceParamNames)
            {
                sources = ClCompileTaskMapper.GetParameter(parameters, name);
                if (sources != null && sources.Count > 0)
                    break;
            }

            if (sources == null || sources.Count == 0)
                return results;

            // Filter to recognized source extensions only
            var filteredSources = new List<string>();
            foreach (string source in sources)
            {
                string ext = Path.GetExtension(source);
                if (!string.IsNullOrEmpty(ext) && CompilerConstants.SourceExtensions.Contains(ext))
                    filteredSources.Add(source);
            }

            if (filteredSources.Count == 0)
                return results;

            var flags = new List<string>();
            flags.Add("cl.exe");

            // Defines → /D
            foreach (string name in DefineParamNames)
            {
                string? defs = ClCompileTaskMapper.GetScalar(parameters, name);
                if (defs != null)
                {
                    foreach (string def in defs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string trimmed = def.Trim();
                        if (trimmed.Length > 0)
                            flags.Add("/D" + trimmed);
                    }
                    break;
                }
            }

            // Includes → /I
            foreach (string name in IncludeParamNames)
            {
                string? includes = ClCompileTaskMapper.GetScalar(parameters, name);
                if (includes != null)
                {
                    foreach (string inc in includes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string trimmed = inc.Trim();
                        if (trimmed.Length > 0)
                            flags.Add("/I" + trimmed);
                    }
                    break;
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

            foreach (string source in filteredSources)
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
