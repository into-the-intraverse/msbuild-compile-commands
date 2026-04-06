using System;
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Models;

namespace MsBuildCompileCommands.Core.Extraction
{
    public sealed class FlagTranslator
    {
        private readonly IReadOnlyList<TranslationRule> _rules;

        public FlagTranslator(IReadOnlyList<TranslationRule> rules)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        }

        public CompileCommand Translate(CompileCommand command)
        {
            if (_rules.Count == 0)
                return command;

            IReadOnlyList<string> args = command.Arguments;
            if (args.Count < 2)
                return command;

            var translated = new List<string>(args.Count);

            // Index 0 is always the compiler executable — never translate it
            translated.Add(args[0]);

            // Last element is always the source file — never translate it
            int lastIndex = args.Count - 1;

            for (int i = 1; i < lastIndex; i++)
            {
                string arg = args[i];
                TranslationRule? match = FindMatch(arg, command.ParserKind);

                if (match == null)
                {
                    translated.Add(arg);
                    continue;
                }

                if (match.To == null)
                {
                    // Drop the argument
                    continue;
                }

                if (!match.Prefix)
                {
                    // Exact match: split To on spaces for multi-arg expansion
                    foreach (string part in match.To.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        translated.Add(part);
                    }
                }
                else
                {
                    // Prefix match: preserve suffix
                    string suffix = arg.Substring(match.From.Length);

                    if (match.To.Length > 0 && match.To[match.To.Length - 1] == ' ')
                    {
                        // Trailing space: flag and suffix become separate arguments
                        translated.Add(match.To.TrimEnd());
                        if (suffix.Length > 0)
                            translated.Add(suffix);
                    }
                    else
                    {
                        // No trailing space: concatenate
                        translated.Add(match.To + suffix);
                    }
                }
            }

            // Add source file (last argument)
            translated.Add(args[lastIndex]);

            return new CompileCommand(command.Directory, command.File, translated, command.ParserKind);
        }

        private TranslationRule? FindMatch(string arg, ParserKind parserKind)
        {
            for (int i = 0; i < _rules.Count; i++)
            {
                TranslationRule rule = _rules[i];

                // Check parser scope
                if (rule.When != null && rule.When != parserKind)
                    continue;

                if (rule.Prefix)
                {
                    if (arg.StartsWith(rule.From, StringComparison.Ordinal) && arg.Length >= rule.From.Length)
                        return rule;
                }
                else
                {
                    if (string.Equals(arg, rule.From, StringComparison.Ordinal))
                        return rule;
                }
            }

            return null;
        }
    }
}
