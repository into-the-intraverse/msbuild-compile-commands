using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using MsBuildCompileCommands.Core.Models;

namespace MsBuildCompileCommands.Core.IO
{
    public static class TranslationRuleLoader
    {
        private static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static IReadOnlyList<TranslationRule> Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (JsonDocument doc = JsonDocument.Parse(bytes))
            {
                return ParseRules(doc.RootElement);
            }
        }

        public static string Serialize(IReadOnlyList<TranslationRule> rules)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream, WriterOptions))
                {
                    writer.WriteStartArray();

                    foreach (TranslationRule rule in rules)
                    {
                        writer.WriteStartObject();

                        if (rule.When != null)
                            writer.WriteString("when", ParserKindToString(rule.When.Value));

                        writer.WriteString("from", rule.From);

                        if (rule.To != null)
                            writer.WriteString("to", rule.To);
                        else
                            writer.WriteNull("to");

                        if (rule.Prefix)
                            writer.WriteBoolean("prefix", true);

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static IReadOnlyList<TranslationRule> ParseRules(JsonElement root)
        {
            var rules = new List<TranslationRule>();

            foreach (JsonElement element in root.EnumerateArray())
            {
                ParserKind? when = null;
                if (element.TryGetProperty("when", out JsonElement whenEl) && whenEl.ValueKind == JsonValueKind.String)
                {
                    when = ParseParserKind(whenEl.GetString()!);
                }

                if (!element.TryGetProperty("from", out JsonElement fromEl) || fromEl.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("Translation rule is missing required 'from' field.");

                string from = fromEl.GetString()!;

                string? to = null;
                if (element.TryGetProperty("to", out JsonElement toEl))
                {
                    if (toEl.ValueKind == JsonValueKind.String)
                        to = toEl.GetString();
                    // null (JsonValueKind.Null) keeps to = null
                }

                bool prefix = false;
                if (element.TryGetProperty("prefix", out JsonElement prefixEl) && prefixEl.ValueKind == JsonValueKind.True)
                    prefix = true;

                rules.Add(new TranslationRule(when, from, to, prefix));
            }

            return rules;
        }

        private static ParserKind ParseParserKind(string value)
        {
            if (string.Equals(value, "msvc", StringComparison.OrdinalIgnoreCase))
                return ParserKind.Msvc;
            if (string.Equals(value, "gcc-clang", StringComparison.OrdinalIgnoreCase))
                return ParserKind.GccClang;
            if (string.Equals(value, "nvcc", StringComparison.OrdinalIgnoreCase))
                return ParserKind.Nvcc;

            throw new InvalidOperationException($"Unknown parser kind '{value}'. Expected: msvc, gcc-clang, nvcc.");
        }

        private static string ParserKindToString(ParserKind kind)
        {
            switch (kind)
            {
                case ParserKind.Msvc: return "msvc";
                case ParserKind.GccClang: return "gcc-clang";
                case ParserKind.Nvcc: return "nvcc";
                default: return "unknown";
            }
        }
    }
}
