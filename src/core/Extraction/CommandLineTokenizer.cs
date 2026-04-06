using System.Collections.Generic;

namespace MsBuildCompileCommands.Core.Extraction
{
    /// <summary>
    /// Splits a Windows command line into tokens, handling double-quote escaping.
    /// Follows the MSVC CRT argument parsing convention (CommandLineToArgvW rules).
    /// </summary>
    public static class CommandLineTokenizer
    {
        public static List<string> Tokenize(string commandLine)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(commandLine))
                return tokens;

            int i = 0;
            int len = commandLine.Length;

            while (i < len)
            {
                // Skip whitespace
                while (i < len && (commandLine[i] == ' ' || commandLine[i] == '\t'))
                    i++;

                if (i >= len)
                    break;

                var token = new System.Text.StringBuilder();
                bool inQuotes = false;

                while (i < len)
                {
                    char c = commandLine[i];

                    if (c == '"')
                    {
                        inQuotes = !inQuotes;
                        i++;
                    }
                    else if (!inQuotes && (c == ' ' || c == '\t'))
                    {
                        break;
                    }
                    else if (c == '\\')
                    {
                        // Count consecutive backslashes
                        int backslashCount = 0;
                        while (i < len && commandLine[i] == '\\')
                        {
                            backslashCount++;
                            i++;
                        }

                        if (i < len && commandLine[i] == '"')
                        {
                            // Backslashes before a quote: each pair becomes one backslash,
                            // odd trailing backslash escapes the quote
                            token.Append('\\', backslashCount / 2);
                            if (backslashCount % 2 == 1)
                            {
                                token.Append('"');
                                i++;
                            }
                            else
                            {
                                inQuotes = !inQuotes;
                                i++;
                            }
                        }
                        else
                        {
                            // Backslashes not before a quote: literal
                            token.Append('\\', backslashCount);
                        }
                        continue;
                    }
                    else
                    {
                        token.Append(c);
                        i++;
                    }
                }

                if (token.Length > 0)
                    tokens.Add(token.ToString());
            }

            return tokens;
        }
    }
}
