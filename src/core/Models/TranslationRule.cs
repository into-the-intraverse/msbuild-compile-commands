using System.Collections.Generic;

namespace MsBuildCompileCommands.Core.Models
{
    public sealed class TranslationRule
    {
        /// <summary>Which parser this rule applies to. Null means all parsers.</summary>
        public ParserKind? When { get; }

        /// <summary>Flag (or flag prefix) to match.</summary>
        public string From { get; }

        /// <summary>
        /// Replacement flag(s). Null means drop the matched flag.
        /// For exact match: space-separated values expand to multiple arguments.
        /// For prefix match: concatenated with suffix, or split if To ends with a space.
        /// </summary>
        public string? To { get; }

        /// <summary>
        /// When true, From is matched as a prefix of the argument and the suffix is preserved.
        /// When false, From must match the full argument exactly.
        /// </summary>
        public bool Prefix { get; }

        public TranslationRule(ParserKind? when, string from, string? to, bool prefix = false)
        {
            When = when;
            From = from;
            To = to;
            Prefix = prefix;
        }

        /// <summary>
        /// Returns the built-in MSVC → clang translation rules.
        /// Covers flags that affect clangd's semantic analysis.
        /// </summary>
        public static IReadOnlyList<TranslationRule> MsvcBuiltins()
        {
            return new[]
            {
                // Language standard
                new TranslationRule(ParserKind.Msvc, "/std:c++14", "-std=c++14"),
                new TranslationRule(ParserKind.Msvc, "/std:c++17", "-std=c++17"),
                new TranslationRule(ParserKind.Msvc, "/std:c++20", "-std=c++20"),
                new TranslationRule(ParserKind.Msvc, "/std:c++latest", "-std=c++2c"),
                new TranslationRule(ParserKind.Msvc, "/std:c11", "-std=c11"),
                new TranslationRule(ParserKind.Msvc, "/std:c17", "-std=c17"),

                // Exceptions
                new TranslationRule(ParserKind.Msvc, "/EHsc", "-fexceptions"),
                new TranslationRule(ParserKind.Msvc, "/EHa", "-fexceptions"),
                new TranslationRule(ParserKind.Msvc, "/EHs", "-fexceptions"),

                // RTTI
                new TranslationRule(ParserKind.Msvc, "/GR-", "-fno-rtti"),
                new TranslationRule(ParserKind.Msvc, "/GR", "-frtti"),

                // Warning levels
                new TranslationRule(ParserKind.Msvc, "/Wall", "-Weverything"),
                new TranslationRule(ParserKind.Msvc, "/W4", "-Wall -Wextra"),
                new TranslationRule(ParserKind.Msvc, "/W3", "-Wall"),
                new TranslationRule(ParserKind.Msvc, "/W2", "-Wall"),
                new TranslationRule(ParserKind.Msvc, "/W1", "-Wall"),
                new TranslationRule(ParserKind.Msvc, "/W0", "-w"),

                // Warnings as errors
                new TranslationRule(ParserKind.Msvc, "/WX-", "-Wno-error"),
                new TranslationRule(ParserKind.Msvc, "/WX", "-Werror"),

                // Char signedness
                new TranslationRule(ParserKind.Msvc, "/J", "-funsigned-char"),

                // Conformance
                new TranslationRule(ParserKind.Msvc, "/permissive-", "-fno-ms-extensions"),

                // Compile only
                new TranslationRule(ParserKind.Msvc, "/c", "-c"),

                // Prefix rules — defines, includes
                new TranslationRule(ParserKind.Msvc, "/D", "-D", prefix: true),
                new TranslationRule(ParserKind.Msvc, "/U", "-U", prefix: true),
                new TranslationRule(ParserKind.Msvc, "/I", "-I", prefix: true),
                new TranslationRule(ParserKind.Msvc, "/FI", "-include ", prefix: true),
                new TranslationRule(ParserKind.Msvc, "/external:I", "-isystem ", prefix: true),
            };
        }
    }
}
