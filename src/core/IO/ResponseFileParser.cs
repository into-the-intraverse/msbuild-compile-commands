using System;
using System.Collections.Generic;
using System.IO;
using MsBuildCompileCommands.Core.Extraction;

namespace MsBuildCompileCommands.Core.IO
{
    /// <summary>
    /// Expands @response-file references in command-line token lists.
    /// Handles nested response files up to a configurable depth to prevent infinite recursion.
    /// </summary>
    public sealed class ResponseFileParser
    {
        private const int MaxDepth = 10;

        private readonly Func<string, string?> _fileReader;

        /// <summary>
        /// Creates a parser that reads response files from disk.
        /// </summary>
        public ResponseFileParser() : this(ReadFileOrNull) { }

        /// <summary>
        /// Creates a parser with a custom file reader (for testing).
        /// </summary>
        public ResponseFileParser(Func<string, string?> fileReader)
        {
            _fileReader = fileReader ?? throw new ArgumentNullException(nameof(fileReader));
        }

        /// <summary>
        /// Expand all @file tokens in the given list, returning a new list with response file
        /// contents inlined. Non-response-file tokens are passed through unchanged.
        /// </summary>
        public List<string> Expand(IReadOnlyList<string> tokens, string? baseDirectory = null)
        {
            return ExpandCore(tokens, baseDirectory, depth: 0);
        }

        private List<string> ExpandCore(IReadOnlyList<string> tokens, string? baseDirectory, int depth)
        {
            if (depth > MaxDepth)
                return new List<string>(tokens);

            var result = new List<string>();

            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];

                if (token.StartsWith("@", StringComparison.Ordinal) && token.Length > 1)
                {
                    string filePath = token.Substring(1).Trim('"');

                    if (!Path.IsPathRooted(filePath) && !string.IsNullOrEmpty(baseDirectory))
                    {
                        filePath = Path.Combine(baseDirectory, filePath);
                    }

                    string? content = _fileReader(filePath);
                    if (content != null)
                    {
                        List<string> innerTokens = CommandLineTokenizer.Tokenize(content);
                        List<string> expanded = ExpandCore(
                            innerTokens,
                            Path.GetDirectoryName(filePath),
                            depth + 1);
                        result.AddRange(expanded);
                    }
                    else
                    {
                        // Could not read response file; keep the token as-is and let the caller decide
                        result.Add(token);
                    }
                }
                else
                {
                    result.Add(token);
                }
            }

            return result;
        }

        private static string? ReadFileOrNull(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
