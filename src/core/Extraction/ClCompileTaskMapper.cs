using System;
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Models;
using MsBuildCompileCommands.Core.Utils;

namespace MsBuildCompileCommands.Core.Extraction
{
    /// <summary>
    /// Maps CL task parameters to compile commands (MSVC cl.exe).
    /// </summary>
    public sealed class ClCompileTaskMapper : ITaskMapper
    {
        private static readonly Dictionary<string, string> RuntimeLibraryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "MultiThreaded", "/MT" },
            { "MultiThreadedDebug", "/MTd" },
            { "MultiThreadedDLL", "/MD" },
            { "MultiThreadedDebugDLL", "/MDd" }
        };

        private static readonly Dictionary<string, string> ExceptionHandlingMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Sync", "/EHsc" },
            { "Async", "/EHa" },
            { "SyncCThrow", "/EHs" }
        };

        public bool CanMap(string taskName)
        {
            return string.Equals(taskName, "CL", StringComparison.OrdinalIgnoreCase);
        }

        public List<CompileCommand> Map(string taskName, IDictionary<string, List<string>> parameters, string directory)
        {
            var results = new List<CompileCommand>();
            List<string>? sources = GetParameter(parameters, "Sources");
            if (sources == null || sources.Count == 0)
                return results;

            var flags = new List<string>();
            flags.Add("cl.exe");

            // PreprocessorDefinitions → /D
            string? defs = GetScalar(parameters, "PreprocessorDefinitions");
            if (defs != null)
            {
                foreach (string def in defs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = def.Trim();
                    if (trimmed.Length > 0)
                        flags.Add("/D" + trimmed);
                }
            }

            // AdditionalIncludeDirectories → /I
            string? includes = GetScalar(parameters, "AdditionalIncludeDirectories");
            if (includes != null)
            {
                foreach (string inc in includes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = inc.Trim();
                    if (trimmed.Length > 0)
                        flags.Add("/I" + trimmed);
                }
            }

            // ForcedIncludeFiles → /FI
            string? forcedIncludes = GetScalar(parameters, "ForcedIncludeFiles");
            if (forcedIncludes != null)
            {
                foreach (string fi in forcedIncludes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = fi.Trim();
                    if (trimmed.Length > 0)
                        flags.Add("/FI" + trimmed);
                }
            }

            // RuntimeLibrary
            string? rtl = GetScalar(parameters, "RuntimeLibrary");
            if (rtl != null && RuntimeLibraryMap.TryGetValue(rtl, out string? rtlFlag))
                flags.Add(rtlFlag);

            // ExceptionHandling
            string? eh = GetScalar(parameters, "ExceptionHandling");
            if (eh != null && ExceptionHandlingMap.TryGetValue(eh, out string? ehFlag))
                flags.Add(ehFlag);

            // AdditionalOptions → tokenize
            string? additionalOptions = GetScalar(parameters, "AdditionalOptions");
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

        internal static List<string>? GetParameter(IDictionary<string, List<string>> parameters, string name)
        {
            if (parameters.TryGetValue(name, out List<string>? values))
                return values;
            return null;
        }

        internal static string? GetScalar(IDictionary<string, List<string>> parameters, string name)
        {
            List<string>? values = GetParameter(parameters, name);
            if (values != null && values.Count > 0)
                return values[0];
            return null;
        }
    }
}
