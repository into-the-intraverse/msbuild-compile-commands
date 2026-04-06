using System;
using System.Collections.Generic;
using System.IO;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;
using MsBuildCompileCommands.Core.Utils;

namespace MsBuildCompileCommands.Core.Extraction
{
    /// <summary>
    /// Parses gcc/g++/cc/c++/clang/clang++ command lines into <see cref="CompileCommand"/> entries.
    /// One entry per source file found on the command line.
    /// Implements <see cref="ICommandParser"/> for the GCC/Clang compiler family.
    /// </summary>
    public sealed class GccClangCommandParser : ICommandParser
    {
        // Base names tried longest-first so clang++ matches before clang,
        // and c++ matches before cc.
        private static readonly string[] BaseNames = { "clang++", "clang", "g++", "gcc", "c++", "cc" };

        // Flags excluded entirely (no value consumed after them).
        private static readonly HashSet<string> ExcludedStandaloneFlags = new HashSet<string>(StringComparer.Ordinal)
        {
            "-M", "-MM", "-MD", "-MMD", "-MP"
        };

        // Flags that take a separate next-token value and should be excluded together with that value.
        private static readonly HashSet<string> ExcludedFlagsWithValue = new HashSet<string>(StringComparer.Ordinal)
        {
            "-o", "-MF", "-MT", "-MQ"
        };

        // Flag prefixes that should be excluded (the whole token starting with this prefix is dropped).
        // -l<lib> and -L<path> are linker flags.
        private static readonly string[] ExcludedPrefixes = { "-l", "-L" };

        // Flags that always take the next token as a value and must be kept.
        private static readonly HashSet<string> KeptFlagsWithSeparateValue = new HashSet<string>(StringComparer.Ordinal)
        {
            "-I", "-D", "-U", "-isystem", "-iquote", "-idirafter", "-include"
        };

        private readonly ResponseFileParser _responseFileParser;

        public GccClangCommandParser() : this(new ResponseFileParser()) { }

        public GccClangCommandParser(ResponseFileParser responseFileParser)
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

            // Extract the compiler token — strip surrounding quotes (Windows quoted paths).
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

                // Excluded standalone flags.
                if (ExcludedStandaloneFlags.Contains(token))
                    continue;

                // Excluded flags that consume the next token.
                if (ExcludedFlagsWithValue.Contains(token))
                {
                    if (i + 1 < tokens.Count)
                        i++; // skip value token
                    continue;
                }

                // Excluded prefix flags (-l<lib>, -L<path>).
                if (HasExcludedPrefix(token))
                    continue;

                if (token.StartsWith("-", StringComparison.Ordinal))
                {
                    flags.Add(token);

                    // Kept flags that take a separate value token.
                    if (KeptFlagsWithSeparateValue.Contains(token) && i + 1 < tokens.Count)
                    {
                        // Only consume the next token as the value when it does not start with '-'
                        // (a joined form like -DFOO has no separate token).
                        string next = tokens[i + 1];
                        if (!next.StartsWith("-", StringComparison.Ordinal))
                            flags.Add(tokens[++i]);
                    }
                }
                else
                {
                    // Not a flag — check if it's a source file.
                    string ext = GetExtension(token);
                    if (CompilerConstants.SourceExtensions.Contains(ext))
                    {
                        sourceFiles.Add(token);
                    }
                    // Tokens with no extension or unknown extension are silently ignored;
                    // they may be values already consumed by a preceding flag, or object files.
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

                commands.Add(new CompileCommand(normalizedDir, normalizedFile, args));
            }

            return commands;
        }

        /// <summary>
        /// Scans the command line for a GCC/Clang compiler executable and returns the
        /// index past its last character, or -1 if none found.
        /// <para>
        /// Detection rules:
        /// <list type="bullet">
        ///   <item>Base names (longest first): clang++, clang, g++, gcc, c++, cc</item>
        ///   <item>Start boundary: pos==0, or preceded by \, /, ", or - (cross-compiler prefixes)</item>
        ///   <item>Optional version suffix: -&lt;digits&gt; (e.g. gcc-12)</item>
        ///   <item>Optional .exe extension (case-insensitive)</item>
        ///   <item>End boundary: end-of-string, space, or tab</item>
        /// </list>
        /// </para>
        /// <para>
        /// clang-cl must NOT match: after matching "clang" the next character is '-', the version
        /// suffix requires a digit which 'c' is not, .exe does not follow, and '-' is not a valid
        /// end boundary, so the candidate is rejected.
        /// </para>
        /// </summary>
        private static int FindCompilerEnd(string commandLine)
        {
            foreach (string baseName in BaseNames)
            {
                int idx = 0;
                while (idx < commandLine.Length)
                {
                    int pos = commandLine.IndexOf(baseName, idx, StringComparison.OrdinalIgnoreCase);
                    if (pos < 0)
                        break;

                    int end = pos + baseName.Length;

                    // Start boundary check.
                    bool atStart = pos == 0
                        || commandLine[pos - 1] == '\\'
                        || commandLine[pos - 1] == '/'
                        || commandLine[pos - 1] == '"'
                        || commandLine[pos - 1] == '-';

                    if (!atStart)
                    {
                        idx = end;
                        continue;
                    }

                    // Optionally consume version suffix: -<one-or-more-digits>
                    if (end < commandLine.Length && commandLine[end] == '-')
                    {
                        int digitStart = end + 1;
                        int digitEnd = digitStart;
                        while (digitEnd < commandLine.Length && char.IsDigit(commandLine[digitEnd]))
                            digitEnd++;

                        if (digitEnd > digitStart) // at least one digit consumed
                            end = digitEnd;
                        // else: leave end unchanged; the '-' is NOT consumed
                    }

                    // Optionally consume .exe extension (case-insensitive).
                    if (end + 4 <= commandLine.Length
                        && string.Compare(commandLine, end, ".exe", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        end += 4;
                    }

                    // End boundary check. A closing quote is also a valid end boundary
                    // because the compiler token may be quoted: "C:\...\clang++.exe" -c ...
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
            }

            return -1;
        }

        private static bool HasExcludedPrefix(string token)
        {
            foreach (string prefix in ExcludedPrefixes)
            {
                if (token.StartsWith(prefix, StringComparison.Ordinal) && token.Length > prefix.Length)
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
