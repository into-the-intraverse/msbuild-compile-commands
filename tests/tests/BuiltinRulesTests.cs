using System.Collections.Generic;
using System.Linq;
using MsBuildCompileCommands.Core.Extraction;
using MsBuildCompileCommands.Core.Models;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class BuiltinRulesTests
    {
        [Fact]
        public void MsvcBuiltins_returns_non_empty_list()
        {
            IReadOnlyList<TranslationRule> rules = TranslationRule.MsvcBuiltins();
            Assert.NotEmpty(rules);
        }

        [Fact]
        public void MsvcBuiltins_all_rules_target_msvc()
        {
            IReadOnlyList<TranslationRule> rules = TranslationRule.MsvcBuiltins();
            Assert.All(rules, r => Assert.Equal(ParserKind.Msvc, r.When));
        }

        [Theory]
        [InlineData("/std:c++17", "-std=c++17")]
        [InlineData("/std:c++20", "-std=c++20")]
        [InlineData("/std:c++latest", "-std=c++2c")]
        [InlineData("/std:c11", "-std=c11")]
        [InlineData("/EHsc", "-fexceptions")]
        [InlineData("/EHa", "-fexceptions")]
        [InlineData("/GR-", "-fno-rtti")]
        [InlineData("/GR", "-frtti")]
        [InlineData("/W0", "-w")]
        [InlineData("/W4", "-Wall -Wextra")]
        [InlineData("/WX", "-Werror")]
        [InlineData("/J", "-funsigned-char")]
        [InlineData("/permissive-", "-fno-ms-extensions")]
        [InlineData("/c", "-c")]
        public void MsvcBuiltins_contains_exact_rule(string from, string to)
        {
            IReadOnlyList<TranslationRule> rules = TranslationRule.MsvcBuiltins();
            Assert.Contains(rules, r => r.From == from && r.To == to && !r.Prefix);
        }

        [Theory]
        [InlineData("/D", "-D")]
        [InlineData("/U", "-U")]
        [InlineData("/I", "-I")]
        [InlineData("/FI", "-include ")]
        [InlineData("/external:I", "-isystem ")]
        public void MsvcBuiltins_contains_prefix_rule(string from, string to)
        {
            IReadOnlyList<TranslationRule> rules = TranslationRule.MsvcBuiltins();
            Assert.Contains(rules, r => r.From == from && r.To == to && r.Prefix);
        }

        [Fact]
        public void Builtin_rules_translate_typical_msvc_command()
        {
            var translator = new FlagTranslator(TranslationRule.MsvcBuiltins());

            var cmd = new CompileCommand(
                "C:/project",
                "C:/project/main.cpp",
                new[] { "cl.exe", "/std:c++20", "/EHsc", "/GR", "/W4", "/WX", "/DFOO=1", "/IC:/inc", "/FIstdafx.h", "/c", "C:/project/main.cpp" },
                ParserKind.Msvc);

            CompileCommand result = translator.Translate(cmd);

            // Exact translations
            Assert.Contains("-std=c++20", result.Arguments);
            Assert.Contains("-fexceptions", result.Arguments);
            Assert.Contains("-frtti", result.Arguments);
            Assert.Contains("-Wall", result.Arguments);
            Assert.Contains("-Wextra", result.Arguments);
            Assert.Contains("-Werror", result.Arguments);
            Assert.Contains("-c", result.Arguments);

            // Prefix translations
            Assert.Contains("-DFOO=1", result.Arguments);
            Assert.Contains("-IC:/inc", result.Arguments);

            // /FI → -include (separate args)
            var argsList = new System.Collections.Generic.List<string>(result.Arguments);
            int includeIdx = argsList.IndexOf("-include");
            Assert.True(includeIdx >= 0);
            Assert.Equal("stdafx.h", argsList[includeIdx + 1]);

            // Original MSVC flags should be gone
            Assert.DoesNotContain("/std:c++20", result.Arguments);
            Assert.DoesNotContain("/EHsc", result.Arguments);
            Assert.DoesNotContain("/DFOO=1", result.Arguments);

            // Compiler and source file preserved
            Assert.Equal("cl.exe", result.Arguments[0]);
            Assert.Equal("C:/project/main.cpp", result.Arguments[result.Arguments.Count - 1]);
        }

        [Fact]
        public void Builtin_rules_do_not_affect_gcc_commands()
        {
            var translator = new FlagTranslator(TranslationRule.MsvcBuiltins());

            var cmd = new CompileCommand(
                "C:/project",
                "C:/project/main.cpp",
                new[] { "gcc", "-std=c++20", "-Wall", "-DFOO=1", "-c", "C:/project/main.cpp" },
                ParserKind.GccClang);

            CompileCommand result = translator.Translate(cmd);

            // All flags should pass through unchanged
            Assert.Contains("-std=c++20", result.Arguments);
            Assert.Contains("-Wall", result.Arguments);
            Assert.Contains("-DFOO=1", result.Arguments);
            Assert.Contains("-c", result.Arguments);
        }
    }
}
