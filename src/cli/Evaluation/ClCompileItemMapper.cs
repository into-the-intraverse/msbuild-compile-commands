using System;
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Extraction;

namespace MsBuildCompileCommands.Cli.Evaluation
{
    /// <summary>
    /// Converts MSBuild ClCompile item metadata into MSVC compiler flags.
    /// Separated from <see cref="ProjectEvaluator"/> for unit testability without MSBuild installation.
    /// </summary>
    public static class ClCompileItemMapper
    {
        private static readonly Dictionary<string, string> RuntimeLibraryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MultiThreaded"] = "/MT",
            ["MultiThreadedDebug"] = "/MTd",
            ["MultiThreadedDLL"] = "/MD",
            ["MultiThreadedDebugDLL"] = "/MDd",
        };

        private static readonly Dictionary<string, string> ExceptionHandlingMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sync"] = "/EHsc",
            ["Async"] = "/EHa",
            ["SyncCThrow"] = "/EHs",
        };

        private static readonly Dictionary<string, string> LanguageStandardMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stdcpplatest"] = "/std:c++latest",
            ["stdcpp23"] = "/std:c++23",
            ["stdcpp20"] = "/std:c++20",
            ["stdcpp17"] = "/std:c++17",
            ["stdcpp14"] = "/std:c++14",
        };

        private static readonly Dictionary<string, string> LanguageStandardCMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stdc17"] = "/std:c17",
            ["stdc11"] = "/std:c11",
            ["stdclatest"] = "/std:clatest",
        };

        private static readonly Dictionary<string, string> CompileAsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CompileAsC"] = "/TC",
            ["CompileAsCpp"] = "/TP",
        };

        public static List<string> MapMetadataToFlags(IDictionary<string, string> metadata)
        {
            var flags = new List<string>();

            if (metadata.TryGetValue("PreprocessorDefinitions", out string? defs) && !string.IsNullOrEmpty(defs))
            {
                foreach (string def in defs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = def.Trim();
                    if (trimmed.Length > 0 && !IsInheritedMarker(trimmed))
                        flags.Add("/D" + trimmed);
                }
            }

            if (metadata.TryGetValue("AdditionalIncludeDirectories", out string? includes) && !string.IsNullOrEmpty(includes))
            {
                foreach (string dir in includes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = dir.Trim();
                    if (trimmed.Length > 0 && !IsInheritedMarker(trimmed))
                        flags.Add("/I" + trimmed);
                }
            }

            if (metadata.TryGetValue("ForcedIncludeFiles", out string? forcedIncludes) && !string.IsNullOrEmpty(forcedIncludes))
            {
                foreach (string file in forcedIncludes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = file.Trim();
                    if (trimmed.Length > 0 && !IsInheritedMarker(trimmed))
                        flags.Add("/FI" + trimmed);
                }
            }

            if (metadata.TryGetValue("RuntimeLibrary", out string? rtl) && !string.IsNullOrEmpty(rtl))
            {
                if (RuntimeLibraryMap.TryGetValue(rtl, out string? flag))
                    flags.Add(flag);
            }

            if (metadata.TryGetValue("ExceptionHandling", out string? eh) && !string.IsNullOrEmpty(eh))
            {
                if (ExceptionHandlingMap.TryGetValue(eh, out string? flag))
                    flags.Add(flag);
            }

            if (metadata.TryGetValue("LanguageStandard", out string? langStd) && !string.IsNullOrEmpty(langStd))
            {
                if (LanguageStandardMap.TryGetValue(langStd, out string? flag))
                    flags.Add(flag);
            }

            if (metadata.TryGetValue("LanguageStandard_C", out string? langStdC) && !string.IsNullOrEmpty(langStdC))
            {
                if (LanguageStandardCMap.TryGetValue(langStdC, out string? flag))
                    flags.Add(flag);
            }

            if (metadata.TryGetValue("ConformanceMode", out string? conformance) &&
                string.Equals(conformance, "true", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add("/permissive-");
            }

            if (metadata.TryGetValue("CompileAs", out string? compileAs) && !string.IsNullOrEmpty(compileAs))
            {
                if (CompileAsMap.TryGetValue(compileAs, out string? flag))
                    flags.Add(flag);
            }

            if (metadata.TryGetValue("TreatWChar_tAsBuiltInType", out string? wchar) &&
                string.Equals(wchar, "false", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add("/Zc:wchar_t-");
            }

            if (metadata.TryGetValue("AdditionalOptions", out string? additional) && !string.IsNullOrEmpty(additional))
            {
                List<string> tokens = CommandLineTokenizer.Tokenize(additional);
                flags.AddRange(tokens);
            }

            return flags;
        }

        private static bool IsInheritedMarker(string value)
        {
            return value.StartsWith("%(", StringComparison.Ordinal) && value.EndsWith(")", StringComparison.Ordinal);
        }
    }
}
