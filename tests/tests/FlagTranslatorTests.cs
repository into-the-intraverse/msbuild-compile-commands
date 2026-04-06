using System.Collections.Generic;
using System.Linq;
using MsBuildCompileCommands.Core.Extraction;
using MsBuildCompileCommands.Core.Models;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class FlagTranslatorTests
    {
        private static CompileCommand MakeCommand(ParserKind kind, params string[] args)
        {
            return new CompileCommand("C:/project", "C:/project/main.cpp", args, kind);
        }

        [Fact]
        public void Exact_match_replaces_flag()
        {
            var rules = new[] { new TranslationRule(ParserKind.Msvc, "/EHsc", "-fexceptions") };
            var translator = new FlagTranslator(rules);

            var cmd = MakeCommand(ParserKind.Msvc, "cl.exe", "/EHsc", "/c", "C:/project/main.cpp");
            var result = translator.Translate(cmd);

            Assert.Contains("-fexceptions", result.Arguments);
            Assert.DoesNotContain("/EHsc", result.Arguments);
        }

        [Fact]
        public void Exact_match_drop_removes_flag()
        {
            var rules = new[] { new TranslationRule(ParserKind.Msvc, "/nologo", null) };
            var translator = new FlagTranslator(rules);

            var cmd = MakeCommand(ParserKind.Msvc, "cl.exe", "/nologo", "/c", "C:/project/main.cpp");
            var result = translator.Translate(cmd);

            Assert.DoesNotContain("/nologo", result.Arguments);
            Assert.Equal(3, result.Arguments.Count); // cl.exe, /c, main.cpp
        }

        [Fact]
        public void Prefix_match_preserves_suffix()
        {
            var rules = new[] { new TranslationRule(ParserKind.Msvc, "/D", "-D", prefix: true) };
            var translator = new FlagTranslator(rules);

            var cmd = MakeCommand(ParserKind.Msvc, "cl.exe", "/DFOO=bar", "C:/project/main.cpp");
            var result = translator.Translate(cmd);

            Assert.Contains("-DFOO=bar", result.Arguments);
            Assert.DoesNotContain("/DFOO=bar", result.Arguments);
        }

        [Fact]
        public void Prefix_match_with_trailing_space_splits_into_separate_args()
        {
            var rules = new[] { new TranslationRule(ParserKind.Msvc, "/FI", "-include ", prefix: true) };
            var translator = new FlagTranslator(rules);

            var cmd = MakeCommand(ParserKind.Msvc, "cl.exe", "/FIstdafx.h", "C:/project/main.cpp");
            var result = translator.Translate(cmd);

            var argsList = result.Arguments.ToList();
            int idx = argsList.IndexOf("-include");
            Assert.True(idx >= 0, "Should contain -include as separate arg");
            Assert.Equal("stdafx.h", argsList[idx + 1]);
        }

        [Fact]
        public void Multi_arg_expansion_splits_on_spaces()
        {
            var rules = new[] { new TranslationRule(ParserKind.Msvc, "/W4", "-Wall -Wextra") };
            var translator = new FlagTranslator(rules);

            var cmd = MakeCommand(ParserKind.Msvc, "cl.exe", "/W4", "C:/project/main.cpp");
            var result = translator.Translate(cmd);

            Assert.Contains("-Wall", result.Arguments);
            Assert.Contains("-Wextra", result.Arguments);
            Assert.DoesNotContain("/W4", result.Arguments);
        }

        [Fact]
        public void Parser_scoping_skips_non_matching_commands()
        {
            var rules = new[] { new TranslationRule(ParserKind.Msvc, "/c", "-c") };
            var translator = new FlagTranslator(rules);

            // GccClang command — rule targets Msvc, should not match
            var cmd = MakeCommand(ParserKind.GccClang, "gcc", "/c", "C:/project/main.cpp");
            var result = translator.Translate(cmd);

            Assert.Contains("/c", result.Arguments);
        }

        [Fact]
        public void Global_rule_applies_to_all_parsers()
        {
            var rules = new[] { new TranslationRule(null, "--remove-me", null) };
            var translator = new FlagTranslator(rules);

            var msvc = MakeCommand(ParserKind.Msvc, "cl.exe", "--remove-me", "C:/project/main.cpp");
            var gcc = MakeCommand(ParserKind.GccClang, "gcc", "--remove-me", "C:/project/main.cpp");

            Assert.DoesNotContain("--remove-me", translator.Translate(msvc).Arguments);
            Assert.DoesNotContain("--remove-me", translator.Translate(gcc).Arguments);
        }

        [Fact]
        public void No_match_passes_through_unchanged()
        {
            var rules = new[] { new TranslationRule(ParserKind.Msvc, "/EHsc", "-fexceptions") };
            var translator = new FlagTranslator(rules);

            var cmd = MakeCommand(ParserKind.Msvc, "cl.exe", "/O2", "C:/project/main.cpp");
            var result = translator.Translate(cmd);

            Assert.Contains("/O2", result.Arguments);
        }

        [Fact]
        public void Compiler_executable_is_never_translated()
        {
            var rules = new[] { new TranslationRule(ParserKind.Msvc, "cl.exe", "clang") };
            var translator = new FlagTranslator(rules);

            var cmd = MakeCommand(ParserKind.Msvc, "cl.exe", "/c", "C:/project/main.cpp");
            var result = translator.Translate(cmd);

            Assert.Equal("cl.exe", result.Arguments[0]);
        }

        [Fact]
        public void Source_file_is_never_translated()
        {
            var rules = new[] { new TranslationRule(ParserKind.Msvc, "C:", "X:", prefix: true) };
            var translator = new FlagTranslator(rules);

            var cmd = MakeCommand(ParserKind.Msvc, "cl.exe", "/c", "C:/project/main.cpp");
            var result = translator.Translate(cmd);

            Assert.Equal("C:/project/main.cpp", result.Arguments[result.Arguments.Count - 1]);
        }

        [Fact]
        public void Empty_rules_is_passthrough()
        {
            var translator = new FlagTranslator(new TranslationRule[0]);

            var cmd = MakeCommand(ParserKind.Msvc, "cl.exe", "/EHsc", "/c", "C:/project/main.cpp");
            var result = translator.Translate(cmd);

            Assert.Equal(cmd.Arguments, result.Arguments);
        }

        [Fact]
        public void Preserves_directory_file_and_parser_kind()
        {
            var rules = new[] { new TranslationRule(ParserKind.Msvc, "/EHsc", "-fexceptions") };
            var translator = new FlagTranslator(rules);

            var cmd = MakeCommand(ParserKind.Msvc, "cl.exe", "/EHsc", "C:/project/main.cpp");
            var result = translator.Translate(cmd);

            Assert.Equal(cmd.Directory, result.Directory);
            Assert.Equal(cmd.File, result.File);
            Assert.Equal(cmd.ParserKind, result.ParserKind);
        }

        [Fact]
        public void First_matching_rule_wins()
        {
            var rules = new[]
            {
                new TranslationRule(ParserKind.Msvc, "/W4", "-Wall -Wextra"),
                new TranslationRule(ParserKind.Msvc, "/W4", "-Wdifferent"),
            };
            var translator = new FlagTranslator(rules);

            var cmd = MakeCommand(ParserKind.Msvc, "cl.exe", "/W4", "C:/project/main.cpp");
            var result = translator.Translate(cmd);

            Assert.Contains("-Wall", result.Arguments);
            Assert.DoesNotContain("-Wdifferent", result.Arguments);
        }
    }
}
