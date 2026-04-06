using System;
using System.Collections.Generic;
using System.IO;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;
using MsBuildCompileCommands.Core.Utils;

namespace MsBuildCompileCommands.Core.Extraction
{
    /// <summary>
    /// Parses cl.exe / clang-cl command lines into <see cref="CompileCommand"/> entries.
    /// One entry per source file found on the command line.
    /// </summary>
    public sealed class ClCommandParser
    {
        private static readonly HashSet<string> CompilerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cl", "cl.exe", "clang-cl", "clang-cl.exe"
        };

        // cl.exe options that take a separate argument (next token)
        private static readonly HashSet<string> OptionsWithSeparateValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/Fo", "/Fe", "/Fd", "/Fp", "/FR", "/Fr", "/Fa", "/Fm", "/Fi", "/doc"
        };

        // Flags to exclude from compile_commands.json output (output-path / cosmetic flags).
        // Note: /FI (forced include) must NOT be excluded — only /Fi (preprocessor output) should be,
        // but /Fi is not a real cl.exe flag, so we omit it. Comparison is case-sensitive on purpose
        // to avoid /FI matching /Fi patterns.
        private static readonly string[] ExcludedExactFlags = new[]
        {
            "/nologo", "-nologo", "/showIncludes", "-showIncludes", "/doc", "-doc"
        };

        private static readonly string[] ExcludedPrefixes = new[]
        {
            "/Fo", "/Fe", "/Fd", "/Fa", "/Fm", "/FR", "/Fr", "/Fp",
            "-Fo", "-Fe", "-Fd", "-Fa", "-Fm", "-FR", "-Fr", "-Fp"
        };

        private static readonly HashSet<string> SourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".c", ".cc", ".cpp", ".cxx", ".c++", ".cp", ".ixx", ".cppm"
        };

        private readonly ResponseFileParser _responseFileParser;

        public ClCommandParser() : this(new ResponseFileParser()) { }

        public ClCommandParser(ResponseFileParser responseFileParser)
        {
            _responseFileParser = responseFileParser ?? throw new ArgumentNullException(nameof(responseFileParser));
        }

        /// <summary>
        /// Returns true if the command line looks like a cl.exe or clang-cl invocation.
        /// </summary>
        public static bool IsCompilerInvocation(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return false;

            // Extract the first token (compiler path)
            List<string> tokens = CommandLineTokenizer.Tokenize(commandLine);
            if (tokens.Count == 0)
                return false;

            string compilerName = Path.GetFileName(tokens[0]);
            return CompilerNames.Contains(compilerName);
        }

        /// <summary>
        /// Parse a cl.exe / clang-cl command line into compile command entries.
        /// Returns one entry per source file.
        /// </summary>
        public List<CompileCommand> Parse(string commandLine, string directory)
        {
            List<string> tokens = CommandLineTokenizer.Tokenize(commandLine);
            if (tokens.Count == 0)
                return new List<CompileCommand>();

            // Expand response files
            tokens = _responseFileParser.Expand(tokens, directory);

            string compiler = tokens[0];
            string compilerName = Path.GetFileName(compiler);
            if (!CompilerNames.Contains(compilerName))
                return new List<CompileCommand>();

            // Collect flags and source files
            var flags = new List<string>();
            var sourceFiles = new List<string>();

            for (int i = 1; i < tokens.Count; i++)
            {
                string token = tokens[i];

                if (IsOutputOption(token))
                {
                    // Skip output-path options and their values
                    if (IsOptionWithSeparateValue(token, i, tokens))
                        i++; // skip next token too
                    continue;
                }

                if (IsFlag(token))
                {
                    if (!ShouldExcludeFlag(token))
                    {
                        flags.Add(token);

                        // Some flags like /I, /D can have a separate value
                        if (IsFlagWithPossibleSeparateValue(token) && i + 1 < tokens.Count && !IsFlag(tokens[i + 1]))
                        {
                            flags.Add(tokens[++i]);
                        }
                    }
                }
                else
                {
                    // Potential source file
                    string ext = GetExtension(token);
                    if (SourceExtensions.Contains(ext))
                    {
                        sourceFiles.Add(token);
                    }
                    else if (string.IsNullOrEmpty(ext))
                    {
                        // No extension, might be an argument to a preceding flag - skip
                    }
                    else
                    {
                        // Unknown extension, could be a source file with unusual extension
                        // Include it to avoid losing entries
                        sourceFiles.Add(token);
                    }
                }
            }

            // Produce one CompileCommand per source file
            string normalizedDir = PathNormalizer.NormalizeDirectory(directory);
            var commands = new List<CompileCommand>(sourceFiles.Count);

            foreach (string sourceFile in sourceFiles)
            {
                string normalizedFile = PathNormalizer.Normalize(sourceFile, directory);

                var args = new List<string>(flags.Count + 3);
                args.Add(compiler);
                args.AddRange(flags);
                args.Add("/c");
                args.Add(normalizedFile);

                commands.Add(new CompileCommand(normalizedDir, normalizedFile, args));
            }

            return commands;
        }

        private static bool IsFlag(string token)
        {
            return token.StartsWith("/", StringComparison.Ordinal)
                || token.StartsWith("-", StringComparison.Ordinal);
        }

        private static bool IsOutputOption(string token)
        {
            foreach (string exact in ExcludedExactFlags)
            {
                if (string.Equals(token, exact, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            foreach (string prefix in ExcludedPrefixes)
            {
                // Case-sensitive prefix match to avoid /FI matching /Fo-style exclusions.
                // The prefixes list uses exact casing (/Fo, /Fe, etc.).
                if (token.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool ShouldExcludeFlag(string token)
        {
            return IsOutputOption(token);
        }

        private static bool IsOptionWithSeparateValue(string token, int index, List<string> tokens)
        {
            // Check if this output option is just the flag prefix without a value appended,
            // meaning the value is in the next token
            foreach (string opt in OptionsWithSeparateValue)
            {
                if (string.Equals(token, opt, StringComparison.OrdinalIgnoreCase))
                    return index + 1 < tokens.Count;
            }
            return false;
        }

        /// <summary>
        /// Flags like /I and /D can appear as "/I dir" (separate) or "/Idir" (joined).
        /// When the token is exactly "/I" or "/D" (no value appended), the value is in the next token.
        /// </summary>
        private static bool IsFlagWithPossibleSeparateValue(string token)
        {
            return string.Equals(token, "/I", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-I", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "/D", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-D", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "/U", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-U", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "/FI", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-FI", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-include", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "/external:I", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-external:I", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-isystem", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-imsvc", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetExtension(string path)
        {
            try
            {
                return Path.GetExtension(path);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
