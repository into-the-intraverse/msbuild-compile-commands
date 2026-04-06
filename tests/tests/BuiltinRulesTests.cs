using System.Collections.Generic;
using System.Linq;
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
    }
}
