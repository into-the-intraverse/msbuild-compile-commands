using System;
using System.Collections.Generic;
using System.IO;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;
using MsBuildCompileCommands.Core.Utils;

namespace MsBuildCompileCommands.Core.Extraction
{
    /// <summary>
    /// Parses NVIDIA nvcc command lines into <see cref="CompileCommand"/> entries.
    /// One entry per source file found on the command line.
    /// Implements <see cref="ICommandParser"/> for the nvcc compiler.
    /// </summary>
    /// <remarks>
    /// Key feature: <c>-Xcompiler</c> / <c>--compiler-options</c> values are split on commas
    /// and each part is emitted as an individual flag, making host-compiler options visible
    /// to clangd.
    /// </remarks>
    public sealed class NvccCommandParser : ICommandParser
    {
        // Flags excluded entirely (standalone, no value consumed after them).
        private static readonly HashSet<string> ExcludedStandaloneFlags = new HashSet<string>(StringComparer.Ordinal)
        {
            "-M", "-MM", "-MD", "-MMD", "-MP", "--generate-dependencies", "--generate-nonsystem-dependencies"
        };

        // Flags that take a separate next-token value and should be excluded together with that value.
        private static readonly HashSet<string> ExcludedFlagsWithValue = new HashSet<string>(StringComparer.Ordinal)
        {
            "-o", "--output-file",
            "-odir", "--output-directory",
            "-MF", "-MT", "-MQ",
            "--gpu-architecture", "-arch",
            "--gpu-code", "-code",
            "-gencode", "--generate-code",
            "--relocatable-device-code", "-rdc",
            "--device-c", "-dc",
            "--device-link", "-dlink",
            "--maxrregcount", "-maxrregcount"
        };

        // Token prefixes for GPU flags used in the `--flag=value` form.
        // The entire token is excluded when it starts with one of these.
        private static readonly string[] ExcludedPrefixes = new string[]
        {
            "--gpu-architecture=",
            "--gpu-code=",
            "-gencode=",
            "--generate-code=",
            "--relocatable-device-code=",
            "-arch=",
            "-code="
        };

        // Flags that always take the next token as a separate value and must be kept.
        private static readonly HashSet<string> KeptFlagsWithSeparateValue = new HashSet<string>(StringComparer.Ordinal)
        {
            "-I", "-D", "-U", "-isystem", "-include"
        };

        // Flags that trigger host-compiler flag expansion (value split on commas).
        private static readonly HashSet<string> XcompilerFlags = new HashSet<string>(StringComparer.Ordinal)
        {
            "-Xcompiler", "--compiler-options"
        };

        private readonly ResponseFileParser _responseFileParser;

        public NvccCommandParser() : this(new ResponseFileParser()) { }

        public NvccCommandParser(ResponseFileParser responseFileParser)
        {
            _responseFileParser = responseFileParser ?? throw new ArgumentNullException(nameof(responseFileParser));
        }

        /// <inheritdoc/>
        public bool IsCompilerInvocation(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return false;

            return FindCompilerEnd(commandLine) >= 0;
        }

        /// <inheritdoc/>
        public List<CompileCommand> Parse(string commandLine, string directory, IList<string>? diagnostics = null)
        {
            int compilerEnd = FindCompilerEnd(commandLine);
            if (compilerEnd < 0)
                return new List<CompileCommand>();

            // Extract compiler token — strip surrounding quotes (Windows quoted paths).
            string compiler = commandLine.Substring(0, compilerEnd).Trim('"');
            string rest = commandLine.Substring(compilerEnd).TrimStart();

            List<string> tokens = CommandLineTokenizer.Tokenize(rest);
            tokens = _responseFileParser.Expand(tokens, directory);

            var flags = new List<string>();
            var sourceFiles = new List<string>();
            bool hasExplicitCompileFlag = false;

            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];

                // Skip unexpanded response-file references.
                if (token.Length > 1 && token[0] == '@')
                {
                    diagnostics?.Add(
                        $"Warning: could not expand response file '{token.Substring(1).Trim('"')}'; flags from this file will be missing");
                    continue;
                }

                // -c is the compile-only flag; track it and keep it.
                if (string.Equals(token, "-c", StringComparison.Ordinal))
                {
                    hasExplicitCompileFlag = true;
                    flags.Add(token);
                    continue;
                }

                // -Xcompiler / --compiler-options: consume next token and expand comma-separated parts.
                if (XcompilerFlags.Contains(token))
                {
                    if (i + 1 < tokens.Count)
                    {
                        i++;
                        string value = tokens[i];
                        ExpandXcompilerValue(value, flags);
                    }
                    continue;
                }

                // Excluded standalone flags.
                if (ExcludedStandaloneFlags.Contains(token))
                    continue;

                // Excluded flags that consume the next token as a value.
                if (ExcludedFlagsWithValue.Contains(token))
                {
                    if (i + 1 < tokens.Count)
                        i++; // skip value token
                    continue;
                }

                // Excluded GPU flags in `--flag=value` form.
                if (HasExcludedPrefix(token))
                    continue;

                if (token.StartsWith("-", StringComparison.Ordinal))
                {
                    flags.Add(token);

                    // Kept flags that take a separate value token.
                    if (KeptFlagsWithSeparateValue.Contains(token) && i + 1 < tokens.Count)
                    {
                        string next = tokens[i + 1];
                        // Only consume separately when the next token doesn't look like a new flag.
                        if (!next.StartsWith("-", StringComparison.Ordinal))
                            flags.Add(tokens[++i]);
                    }
                }
                else
                {
                    // Not a flag — check if it is a source file.
                    string ext = GetExtension(token);
                    if (CompilerConstants.SourceExtensions.Contains(ext))
                        sourceFiles.Add(token);
                    // Other non-flag tokens (e.g. values already consumed by a preceding flag) are ignored.
                }
            }

            string normalizedDir = PathNormalizer.NormalizeDirectory(directory);
            var commands = new List<CompileCommand>(sourceFiles.Count);

            foreach (string sourceFile in sourceFiles)
            {
                string normalizedFile = PathNormalizer.Normalize(sourceFile, directory);

                var args = new List<string>(flags.Count + 3);
                args.Add(compiler);
                args.AddRange(flags);
                if (!hasExplicitCompileFlag)
                    args.Add("-c");
                args.Add(normalizedFile);

                commands.Add(new CompileCommand(normalizedDir, normalizedFile, args, ParserKind.Nvcc));
            }

            return commands;
        }

        /// <summary>
        /// Scans <paramref name="commandLine"/> for an nvcc executable and returns the index
        /// past its last character, or -1 if none found.
        /// <para>
        /// Detection rules:
        /// <list type="bullet">
        ///   <item>Base name: <c>nvcc</c> (case-insensitive)</item>
        ///   <item>Start boundary: pos==0, or preceded by \, /, or "</item>
        ///   <item>Optional .exe extension (case-insensitive)</item>
        ///   <item>End boundary: end-of-string, space, tab, or closing "</item>
        /// </list>
        /// </para>
        /// </summary>
        private static int FindCompilerEnd(string commandLine)
        {
            const string baseName = "nvcc";
            int idx = 0;

            while (idx < commandLine.Length)
            {
                int pos = commandLine.IndexOf(baseName, idx, StringComparison.OrdinalIgnoreCase);
                if (pos < 0)
                    break;

                int end = pos + baseName.Length;

                // Start boundary: start-of-string, path separator, or opening quote.
                bool atStart = pos == 0
                    || commandLine[pos - 1] == '\\'
                    || commandLine[pos - 1] == '/'
                    || commandLine[pos - 1] == '"';

                if (!atStart)
                {
                    idx = end;
                    continue;
                }

                // Optionally consume .exe extension (case-insensitive).
                if (end + 4 <= commandLine.Length
                    && string.Compare(commandLine, end, ".exe", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    end += 4;
                }

                // End boundary: end-of-string, whitespace, or closing quote.
                bool atEnd = end >= commandLine.Length
                    || commandLine[end] == ' '
                    || commandLine[end] == '\t'
                    || commandLine[end] == '"';

                if (atEnd)
                {
                    // Advance past the closing quote so the caller gets a clean split point.
                    if (end < commandLine.Length && commandLine[end] == '"')
                        end++;
                    return end;
                }

                idx = end;
            }

            return -1;
        }

        /// <summary>
        /// Splits a -Xcompiler / --compiler-options value on commas and adds each
        /// non-empty part to <paramref name="flags"/>.
        /// </summary>
        private static void ExpandXcompilerValue(string value, List<string> flags)
        {
            // The value may be quoted: already unquoted by the tokenizer.
            string[] parts = value.Split(',');
            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0)
                    flags.Add(trimmed);
            }
        }

        private static bool HasExcludedPrefix(string token)
        {
            foreach (string prefix in ExcludedPrefixes)
            {
                if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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
