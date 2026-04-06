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
    /// Implements <see cref="ICommandParser"/> for the MSVC compiler family.
    /// </summary>
    public sealed class MsvcCommandParser : ICommandParser
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

        private readonly ResponseFileParser _responseFileParser;

        public MsvcCommandParser() : this(new ResponseFileParser()) { }

        public MsvcCommandParser(ResponseFileParser responseFileParser)
        {
            _responseFileParser = responseFileParser ?? throw new ArgumentNullException(nameof(responseFileParser));
        }

        /// <summary>
        /// Returns true if the command line looks like a cl.exe or clang-cl invocation.
        /// Satisfies <see cref="ICommandParser.IsCompilerInvocation"/>.
        /// </summary>
        public bool IsCompilerInvocation(string commandLine)
        {
            return IsCompilerInvocationStatic(commandLine);
        }

        /// <summary>
        /// Static helper — used by the <see cref="ClCommandParser"/> backward-compatibility wrapper.
        /// </summary>
        public static bool IsCompilerInvocationStatic(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return false;

            return FindCompilerEnd(commandLine) >= 0;
        }

        /// <summary>
        /// Parse a cl.exe / clang-cl command line into compile command entries.
        /// Returns one entry per source file.
        /// </summary>
        public List<CompileCommand> Parse(string commandLine, string directory, IList<string>? diagnostics = null)
        {
            int compilerEnd = FindCompilerEnd(commandLine);
            if (compilerEnd < 0)
                return new List<CompileCommand>();

            string compiler = commandLine.Substring(0, compilerEnd).Trim('"');
            string rest = commandLine.Substring(compilerEnd).TrimStart();

            List<string> tokens = CommandLineTokenizer.Tokenize(rest);

            // Expand response files
            tokens = _responseFileParser.Expand(tokens, directory);

            // Collect flags and source files
            var flags = new List<string>();
            var sourceFiles = new List<string>();

            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];

                // Skip unexpanded response-file references (@file.rsp tokens that
                // ResponseFileParser could not read). Without this guard, the token
                // would be misclassified as a source file with an unknown extension.
                if (token.Length > 1 && token[0] == '@')
                {
                    diagnostics?.Add($"Warning: could not expand response file '{token.Substring(1).Trim('"')}'; flags from this file will be missing");
                    continue;
                }

                if (IsOutputOption(token))
                {
                    // Skip output-path options and their values
                    if (IsOptionWithSeparateValue(token, i, tokens))
                        i++; // skip next token too
                    continue;
                }

                // PCH flags: /Yc creates the PCH (strip entirely), /Yu uses the PCH
                // (convert to /FI forced-include so clangd sees the implicit header).
                string? pchAction = GetPchAction(token);
                if (pchAction != null)
                {
                    string? headerName = ExtractPchHeader(token, pchAction, ref i, tokens);
                    if (pchAction == "Yu" && headerName != null)
                        flags.Add("/FI" + headerName);
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
                    if (CompilerConstants.SourceExtensions.Contains(ext))
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

        /// <summary>
        /// Finds the end position of the compiler executable in the raw command line.
        /// Handles both quoted paths ("C:\Program Files\...\cl.exe") and unquoted paths
        /// with spaces (C:\Program Files\...\CL.exe) that MSBuild emits.
        /// Returns the index past the compiler name, or -1 if no compiler found.
        /// </summary>
        private static int FindCompilerEnd(string commandLine)
        {
            // Try each known compiler name (longest first to match clang-cl.exe before cl.exe)
            string[] names = { "clang-cl.exe", "clang-cl", "cl.exe", "cl" };

            foreach (string name in names)
            {
                int idx = 0;
                while (idx < commandLine.Length)
                {
                    int pos = commandLine.IndexOf(name, idx, StringComparison.OrdinalIgnoreCase);
                    if (pos < 0)
                        break;

                    int end = pos + name.Length;

                    // Must be at end of string or followed by whitespace (not part of a longer path segment)
                    bool atEnd = end >= commandLine.Length || commandLine[end] == ' ' || commandLine[end] == '\t';
                    // Must be preceded by a path separator or start of string (not a substring of another word)
                    bool atStart = pos == 0 || commandLine[pos - 1] == '\\' || commandLine[pos - 1] == '/' || commandLine[pos - 1] == '"';

                    if (atEnd && atStart)
                        return end;

                    idx = end;
                }
            }

            return -1;
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

        /// <summary>
        /// Returns "Yc" or "Yu" if the token is a PCH flag, null otherwise.
        /// </summary>
        private static string? GetPchAction(string token)
        {
            foreach (string prefix in new[] { "/", "-" })
            {
                if (token.StartsWith(prefix + "Yc", StringComparison.Ordinal))
                    return "Yc";
                if (token.StartsWith(prefix + "Yu", StringComparison.Ordinal))
                    return "Yu";
            }
            return null;
        }

        /// <summary>
        /// Extracts the header name from a /Yu or /Yc flag.
        /// Handles joined form (/Yuheader.h) and separate form (/Yu header.h).
        /// Advances <paramref name="index"/> past the separate value when consumed.
        /// Returns null when no header name is present.
        /// </summary>
        private static string? ExtractPchHeader(string token, string action, ref int index, List<string> tokens)
        {
            // Strip leading / or -
            string rest = token.Substring(token[0] == '/' || token[0] == '-' ? 1 : 0);

            // Joined form: /Yuheader.h or /Yu"header.h"
            if (rest.Length > action.Length)
                return rest.Substring(action.Length).Trim('"');

            // Separate form: /Yu header.h — next token is the value if it isn't another flag
            if (index + 1 < tokens.Count && !IsFlag(tokens[index + 1]))
                return tokens[++index].Trim('"');

            return null;
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
