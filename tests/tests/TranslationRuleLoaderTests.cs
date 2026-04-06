using System;
using System.Collections.Generic;
using System.IO;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class TranslationRuleLoaderTests
    {
        [Fact]
        public void Loads_exact_rule_from_json()
        {
            string json = @"[{ ""when"": ""msvc"", ""from"": ""/EHsc"", ""to"": ""-fexceptions"" }]";
            string path = WriteTempJson(json);

            IReadOnlyList<TranslationRule> rules = TranslationRuleLoader.Load(path);

            Assert.Single(rules);
            Assert.Equal(ParserKind.Msvc, rules[0].When);
            Assert.Equal("/EHsc", rules[0].From);
            Assert.Equal("-fexceptions", rules[0].To);
            Assert.False(rules[0].Prefix);
        }

        [Fact]
        public void Loads_prefix_rule_from_json()
        {
            string json = @"[{ ""when"": ""msvc"", ""from"": ""/D"", ""to"": ""-D"", ""prefix"": true }]";
            string path = WriteTempJson(json);

            IReadOnlyList<TranslationRule> rules = TranslationRuleLoader.Load(path);

            Assert.Single(rules);
            Assert.True(rules[0].Prefix);
        }

        [Fact]
        public void Loads_drop_rule_with_null_to()
        {
            string json = @"[{ ""when"": ""nvcc"", ""from"": ""--expt-relaxed-constexpr"", ""to"": null }]";
            string path = WriteTempJson(json);

            IReadOnlyList<TranslationRule> rules = TranslationRuleLoader.Load(path);

            Assert.Single(rules);
            Assert.Null(rules[0].To);
        }

        [Fact]
        public void Loads_global_rule_without_when()
        {
            string json = @"[{ ""from"": ""--remove"", ""to"": null }]";
            string path = WriteTempJson(json);

            IReadOnlyList<TranslationRule> rules = TranslationRuleLoader.Load(path);

            Assert.Single(rules);
            Assert.Null(rules[0].When);
        }

        [Fact]
        public void Loads_gcc_clang_when_value()
        {
            string json = @"[{ ""when"": ""gcc-clang"", ""from"": ""-old"", ""to"": ""-new"" }]";
            string path = WriteTempJson(json);

            IReadOnlyList<TranslationRule> rules = TranslationRuleLoader.Load(path);

            Assert.Equal(ParserKind.GccClang, rules[0].When);
        }

        [Fact]
        public void Throws_on_missing_from_field()
        {
            string json = @"[{ ""when"": ""msvc"", ""to"": ""-fexceptions"" }]";
            string path = WriteTempJson(json);

            var ex = Assert.Throws<InvalidOperationException>(() => TranslationRuleLoader.Load(path));
            Assert.Contains("from", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Throws_on_unknown_when_value()
        {
            string json = @"[{ ""when"": ""unknown-compiler"", ""from"": ""/x"", ""to"": ""-x"" }]";
            string path = WriteTempJson(json);

            var ex = Assert.Throws<InvalidOperationException>(() => TranslationRuleLoader.Load(path));
            Assert.Contains("unknown-compiler", ex.Message);
        }

        [Fact]
        public void Throws_on_invalid_json()
        {
            string path = WriteTempJson("not valid json");

            Assert.ThrowsAny<System.Text.Json.JsonException>(() => TranslationRuleLoader.Load(path));
        }

        [Fact]
        public void Serializes_rules_to_json_roundtrip()
        {
            IReadOnlyList<TranslationRule> builtins = TranslationRule.MsvcBuiltins();
            string json = TranslationRuleLoader.Serialize(builtins);
            string path = WriteTempJson(json);

            IReadOnlyList<TranslationRule> loaded = TranslationRuleLoader.Load(path);

            Assert.Equal(builtins.Count, loaded.Count);
            for (int i = 0; i < builtins.Count; i++)
            {
                Assert.Equal(builtins[i].When, loaded[i].When);
                Assert.Equal(builtins[i].From, loaded[i].From);
                Assert.Equal(builtins[i].To, loaded[i].To);
                Assert.Equal(builtins[i].Prefix, loaded[i].Prefix);
            }
        }

        private static string WriteTempJson(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), $"test-rules-{Guid.NewGuid()}.json");
            File.WriteAllText(path, content);
            return path;
        }
    }
}
