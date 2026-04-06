# Flag Translation Rules Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add built-in MSVC→clang flag translation (on by default) and user-defined translation rules to make compile_commands.json directly usable by clangd.

**Architecture:** New `ParserKind` enum tags each `CompileCommand` with its source compiler. A `FlagTranslator` runs translation rules (built-in or user-provided) in `CompileCommandCollector` after parsing, before dedup. Config file replaces all built-ins; `--no-translate` disables everything.

**Tech Stack:** C# / netstandard2.0 (core), net8.0 (CLI), System.Text.Json, xunit

**Spec:** `docs/superpowers/specs/2026-04-07-flag-translation-rules-design.md`

---

### Task 1: Add ParserKind enum and update CompileCommand model

**Files:**
- Create: `src/core/Models/ParserKind.cs`
- Modify: `src/core/Models/CompileCommand.cs`
- Modify: `tests/tests/CompileCommandCollectorTests.cs`

- [ ] **Step 1: Create ParserKind enum**

Create `src/core/Models/ParserKind.cs`:

```csharp
namespace MsBuildCompileCommands.Core.Models
{
    public enum ParserKind
    {
        Unknown,
        Msvc,
        GccClang,
        Nvcc
    }
}
```

- [ ] **Step 2: Add ParserKind to CompileCommand**

In `src/core/Models/CompileCommand.cs`, add a `ParserKind` property. The constructor gets a new optional parameter defaulting to `Unknown` so existing call sites don't break yet:

```csharp
public ParserKind ParserKind { get; }

public CompileCommand(string directory, string file, IReadOnlyList<string> arguments, ParserKind parserKind = ParserKind.Unknown)
{
    Directory = directory ?? throw new ArgumentNullException(nameof(directory));
    File = file ?? throw new ArgumentNullException(nameof(file));
    Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
    ParserKind = parserKind;
}
```

`ParserKind` does NOT participate in `Equals` or `GetHashCode` — dedup is by `File` only.

- [ ] **Step 3: Build and verify existing tests pass**

Run: `dotnet build && dotnet test`
Expected: All existing tests pass. No call sites break because the parameter defaults to `Unknown`.

- [ ] **Step 4: Commit**

```bash
git add src/core/Models/ParserKind.cs src/core/Models/CompileCommand.cs
git commit -m "feat: add ParserKind enum to CompileCommand model"
```

---

### Task 2: Set ParserKind in all parsers and task mappers

**Files:**
- Modify: `src/core/Extraction/MsvcCommandParser.cs:174` (the `new CompileCommand(...)` call)
- Modify: `src/core/Extraction/GccClangCommandParser.cs:156` (the `new CompileCommand(...)` call)
- Modify: `src/core/Extraction/NvccCommandParser.cs:189` (the `new CompileCommand(...)` call)
- Modify: `src/core/Extraction/ClCompileTaskMapper.cs:104` (the `new CompileCommand(...)` call)
- Modify: `src/core/Extraction/CudaCompileTaskMapper.cs:67` (the `new CompileCommand(...)` call)
- Modify: `src/core/Extraction/GenericTaskMapper.cs:102` (the `new CompileCommand(...)` call)

- [ ] **Step 1: Update MsvcCommandParser**

In `src/core/Extraction/MsvcCommandParser.cs`, add the `using` for `Models` namespace (already present), then change line 174:

```csharp
commands.Add(new CompileCommand(normalizedDir, normalizedFile, args, ParserKind.Msvc));
```

- [ ] **Step 2: Update GccClangCommandParser**

In `src/core/Extraction/GccClangCommandParser.cs`, change line 156:

```csharp
commands.Add(new CompileCommand(normalizedDir, normalizedFile, args, ParserKind.GccClang));
```

- [ ] **Step 3: Update NvccCommandParser**

In `src/core/Extraction/NvccCommandParser.cs`, change line 189:

```csharp
commands.Add(new CompileCommand(normalizedDir, normalizedFile, args, ParserKind.Nvcc));
```

- [ ] **Step 4: Update ClCompileTaskMapper**

In `src/core/Extraction/ClCompileTaskMapper.cs`, change line 104:

```csharp
results.Add(new CompileCommand(normalizedDir, normalizedFile, args, ParserKind.Msvc));
```

- [ ] **Step 5: Update CudaCompileTaskMapper**

In `src/core/Extraction/CudaCompileTaskMapper.cs`, change line 67:

```csharp
results.Add(new CompileCommand(normalizedDir, normalizedFile, args, ParserKind.Nvcc));
```

- [ ] **Step 6: Update GenericTaskMapper**

In `src/core/Extraction/GenericTaskMapper.cs`, change line 102:

```csharp
results.Add(new CompileCommand(normalizedDir, normalizedFile, args, ParserKind.Unknown));
```

- [ ] **Step 7: Build and run tests**

Run: `dotnet build && dotnet test`
Expected: All tests pass. ParserKind is now set everywhere.

- [ ] **Step 8: Commit**

```bash
git add src/core/Extraction/MsvcCommandParser.cs src/core/Extraction/GccClangCommandParser.cs src/core/Extraction/NvccCommandParser.cs src/core/Extraction/ClCompileTaskMapper.cs src/core/Extraction/CudaCompileTaskMapper.cs src/core/Extraction/GenericTaskMapper.cs
git commit -m "feat: set ParserKind in all parsers and task mappers"
```

---

### Task 3: Create TranslationRule model with built-in MSVC rules

**Files:**
- Create: `src/core/Models/TranslationRule.cs`
- Create: `tests/tests/BuiltinRulesTests.cs`

- [ ] **Step 1: Write test for built-in rules existence**

Create `tests/tests/BuiltinRulesTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~BuiltinRulesTests`
Expected: FAIL — `TranslationRule` type does not exist.

- [ ] **Step 3: Create TranslationRule with built-in rules**

Create `src/core/Models/TranslationRule.cs`:

```csharp
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

                // RTTI — /GR- must precede /GR for prefix-safety if rules were prefix-matched,
                // but both are exact so order doesn't matter. Keep specific-first for readability.
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~BuiltinRulesTests`
Expected: All PASS.

- [ ] **Step 5: Commit**

```bash
git add src/core/Models/TranslationRule.cs tests/tests/BuiltinRulesTests.cs
git commit -m "feat: add TranslationRule model with built-in MSVC rules"
```

---

### Task 4: Implement FlagTranslator

**Files:**
- Create: `src/core/Extraction/FlagTranslator.cs`
- Create: `tests/tests/FlagTranslatorTests.cs`

- [ ] **Step 1: Write FlagTranslator tests**

Create `tests/tests/FlagTranslatorTests.cs`:

```csharp
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
            // Rule that would match the compiler name if applied to it
            var rules = new[] { new TranslationRule(ParserKind.Msvc, "cl.exe", "clang") };
            var translator = new FlagTranslator(rules);

            var cmd = MakeCommand(ParserKind.Msvc, "cl.exe", "/c", "C:/project/main.cpp");
            var result = translator.Translate(cmd);

            Assert.Equal("cl.exe", result.Arguments[0]);
        }

        [Fact]
        public void Source_file_is_never_translated()
        {
            // Rule that would match a path prefix
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~FlagTranslatorTests`
Expected: FAIL — `FlagTranslator` does not exist.

- [ ] **Step 3: Implement FlagTranslator**

Create `src/core/Extraction/FlagTranslator.cs`:

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~FlagTranslatorTests`
Expected: All PASS.

- [ ] **Step 5: Commit**

```bash
git add src/core/Extraction/FlagTranslator.cs tests/tests/FlagTranslatorTests.cs
git commit -m "feat: implement FlagTranslator with exact and prefix matching"
```

---

### Task 5: Integrate FlagTranslator into CompileCommandCollector

**Files:**
- Modify: `src/core/Extraction/CompileCommandCollector.cs`
- Modify: `tests/tests/CompileCommandCollectorTests.cs`

- [ ] **Step 1: Write integration test**

Add to `tests/tests/CompileCommandCollectorTests.cs`:

```csharp
[Fact]
public void Translates_flags_when_translator_is_provided()
{
    var rules = new[] { new TranslationRule(ParserKind.Msvc, "/EHsc", "-fexceptions") };
    var translator = new FlagTranslator(rules);
    var collector = new CompileCommandCollector(filter: null, translator: translator);

    var projectStarted = CreateProjectStartedEvent(@"C:\project\myapp.vcxproj", projectContextId: 1);
    collector.HandleEvent(projectStarted);

    var taskCmd = CreateTaskCommandLineEvent("cl.exe /c /EHsc main.cpp", projectContextId: 1);
    collector.HandleEvent(taskCmd);

    List<CompileCommand> commands = collector.GetCommands();
    Assert.Single(commands);
    Assert.Contains("-fexceptions", commands[0].Arguments);
    Assert.DoesNotContain("/EHsc", commands[0].Arguments);
}

[Fact]
public void No_translator_passes_flags_through_unchanged()
{
    var collector = new CompileCommandCollector();

    var projectStarted = CreateProjectStartedEvent(@"C:\project\myapp.vcxproj", projectContextId: 1);
    collector.HandleEvent(projectStarted);

    var taskCmd = CreateTaskCommandLineEvent("cl.exe /c /EHsc main.cpp", projectContextId: 1);
    collector.HandleEvent(taskCmd);

    List<CompileCommand> commands = collector.GetCommands();
    Assert.Single(commands);
    Assert.Contains("/EHsc", commands[0].Arguments);
}
```

Add the required `using` at the top of the file:

```csharp
using MsBuildCompileCommands.Core.Models;
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~CompileCommandCollectorTests.Translates_flags`
Expected: FAIL — `CompileCommandCollector` has no constructor overload accepting `FlagTranslator`.

- [ ] **Step 3: Add FlagTranslator to CompileCommandCollector**

In `src/core/Extraction/CompileCommandCollector.cs`:

Add a field after `_filter`:

```csharp
private readonly FlagTranslator? _translator;
```

Add a new constructor overload (keep existing ones for backward compatibility):

```csharp
public CompileCommandCollector(CompileCommandFilter? filter, FlagTranslator? translator)
{
    _filter = filter;
    _translator = translator;
}
```

Update the existing constructors to chain:

```csharp
public CompileCommandCollector() : this(null, null) { }

public CompileCommandCollector(CompileCommandFilter? filter) : this(filter, null) { }
```

In `HandleTaskCommandLine`, after parsing commands and before storing, add translation. Replace the existing storage loop (lines 215-218):

```csharp
List<CompileCommand> commands = parser.Parse(commandLine, directory, _diagnostics);
foreach (CompileCommand cmd in commands)
{
    CompileCommand final = _translator != null ? _translator.Translate(cmd) : cmd;
    _commands[final.DeduplicationKey] = final;
}
```

In `HandleTaskFinished`, after mapping commands and before storing, add translation. Replace the existing storage loop (lines 108-112):

```csharp
List<CompileCommand> commands = _taskMapperRegistry.TryMap(state.TaskName, state.Parameters, directory);
foreach (CompileCommand cmd in commands)
{
    CompileCommand final = _translator != null ? _translator.Translate(cmd) : cmd;
    if (!_commands.ContainsKey(final.DeduplicationKey))
        _commands[final.DeduplicationKey] = final;
}
```

- [ ] **Step 4: Run all tests**

Run: `dotnet test`
Expected: All tests pass, including the two new integration tests.

- [ ] **Step 5: Commit**

```bash
git add src/core/Extraction/CompileCommandCollector.cs tests/tests/CompileCommandCollectorTests.cs
git commit -m "feat: integrate FlagTranslator into CompileCommandCollector"
```

---

### Task 6: Add TranslationRuleLoader for JSON config files

**Files:**
- Create: `src/core/IO/TranslationRuleLoader.cs`
- Create: `tests/tests/TranslationRuleLoaderTests.cs`

- [ ] **Step 1: Write tests for config loading**

Create `tests/tests/TranslationRuleLoaderTests.cs`:

```csharp
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

            Assert.Throws<System.Text.Json.JsonException>(() => TranslationRuleLoader.Load(path));
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~TranslationRuleLoaderTests`
Expected: FAIL — `TranslationRuleLoader` does not exist.

- [ ] **Step 3: Implement TranslationRuleLoader**

Create `src/core/IO/TranslationRuleLoader.cs`:

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~TranslationRuleLoaderTests`
Expected: All PASS.

- [ ] **Step 5: Commit**

```bash
git add src/core/IO/TranslationRuleLoader.cs tests/tests/TranslationRuleLoaderTests.cs
git commit -m "feat: add TranslationRuleLoader for JSON config files"
```

---

### Task 7: Wire up CLI with --flag-rules, --no-translate, --dump-rules

**Files:**
- Modify: `src/cli/Program.cs`

- [ ] **Step 1: Add new CLI options to argument parsing**

In `src/cli/Program.cs`, add variables after `bool evaluate = false;` (line 45):

```csharp
string? flagRulesPath = null;
bool noTranslate = false;
bool dumpRules = false;
```

Add parsing cases inside the `for` loop, before the unknown-option `else` branch (before line 97):

```csharp
else if (string.Equals(arg, "--flag-rules", StringComparison.OrdinalIgnoreCase))
{
    if (i + 1 < args.Length)
        flagRulesPath = args[++i];
    else
    {
        Console.Error.WriteLine("Error: --flag-rules requires a path argument.");
        return 1;
    }
}
else if (arg.StartsWith("--flag-rules=", StringComparison.OrdinalIgnoreCase))
{
    flagRulesPath = arg.Substring("--flag-rules=".Length);
}
else if (string.Equals(arg, "--no-translate", StringComparison.OrdinalIgnoreCase))
{
    noTranslate = true;
}
else if (string.Equals(arg, "--dump-rules", StringComparison.OrdinalIgnoreCase))
{
    dumpRules = true;
}
```

Add early exit for `--dump-rules` after the `--version` check (after line 37):

```csharp
if (dumpRules)
{
    Console.WriteLine(TranslationRuleLoader.Serialize(TranslationRule.MsvcBuiltins()));
    return 0;
}
```

Note: The `dumpRules` variable must be parsed before this check. Move the `--dump-rules` handling: parse it in the loop, but handle the early-exit after the full loop completes and before the binlog-null check. Restructure:

After the for loop and before the `binlogPath == null` check, add:

```csharp
if (dumpRules)
{
    Console.WriteLine(TranslationRuleLoader.Serialize(TranslationRule.MsvcBuiltins()));
    return 0;
}
```

Add required usings at top of `Program.cs`:

```csharp
using MsBuildCompileCommands.Core.IO;
```

- [ ] **Step 2: Build FlagTranslator from options and pass to collector**

In `GenerateFromBinlog`, add a `flagRulesPath` and `noTranslate` parameter. Update the method signature:

```csharp
private static int GenerateFromBinlog(string binlogPath, string outputPath, bool overwrite,
    string? projectFilter, string? configFilter, bool evaluate, string? flagRulesPath, bool noTranslate)
```

Update the call site in `Main`:

```csharp
return GenerateFromBinlog(binlogPath, outputPath, overwrite, projectFilter, configFilter, evaluate, flagRulesPath, noTranslate);
```

In `GenerateFromBinlog`, after building the filter and before constructing the collector, build the translator:

```csharp
FlagTranslator? translator = null;
if (!noTranslate)
{
    IReadOnlyList<TranslationRule> rules;
    if (flagRulesPath != null)
    {
        if (!File.Exists(flagRulesPath))
        {
            Console.Error.WriteLine($"Error: Flag rules file not found: {flagRulesPath}");
            return 1;
        }
        try
        {
            rules = TranslationRuleLoader.Load(flagRulesPath);
            Console.Error.WriteLine($"Loaded {rules.Count} translation rules from {flagRulesPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error loading flag rules: {ex.Message}");
            return 1;
        }
    }
    else
    {
        rules = TranslationRule.MsvcBuiltins();
    }
    translator = new FlagTranslator(rules);
}
var collector = new CompileCommandCollector(filter, translator);
```

Add required usings:

```csharp
using MsBuildCompileCommands.Core.Extraction;
```

(May already be present — verify.)

- [ ] **Step 3: Update PrintUsage**

Add these lines to the OPTIONS section of `PrintUsage()`:

```
    --flag-rules <path>     Path to custom flag translation rules JSON file
                            (replaces built-in MSVC→clang rules entirely)
    --no-translate          Disable all flag translation (built-in and custom)
    --dump-rules            Print built-in translation rules as JSON and exit
```

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build && dotnet test`
Expected: All pass. CLI compiles with new options.

- [ ] **Step 5: Commit**

```bash
git add src/cli/Program.cs
git commit -m "feat: add --flag-rules, --no-translate, --dump-rules CLI options"
```

---

### Task 8: Wire up Logger with MSBuild properties

**Files:**
- Modify: `src/logger/CompileCommandsLogger.cs`

- [ ] **Step 1: Add translation parameters to logger**

In `src/logger/CompileCommandsLogger.cs`, add fields after `_configFilter` (line 30):

```csharp
private string? _flagRulesPath;
private bool _noTranslate;
```

In `ParseParameters()`, add cases inside the `foreach` loop (after the `configuration` case):

```csharp
else if (string.Equals(key, "flagrules", StringComparison.OrdinalIgnoreCase))
{
    _flagRulesPath = value;
}
else if (string.Equals(key, "translate", StringComparison.OrdinalIgnoreCase))
{
    _noTranslate = string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Build translator in Initialize**

In `Initialize`, replace the collector creation line:

```csharp
FlagTranslator? translator = null;
if (!_noTranslate)
{
    IReadOnlyList<TranslationRule> rules;
    if (_flagRulesPath != null && System.IO.File.Exists(_flagRulesPath))
    {
        try
        {
            rules = TranslationRuleLoader.Load(_flagRulesPath);
        }
        catch (Exception)
        {
            rules = TranslationRule.MsvcBuiltins();
        }
    }
    else
    {
        rules = TranslationRule.MsvcBuiltins();
    }
    translator = new FlagTranslator(rules);
}

_collector = new CompileCommandCollector(BuildFilter(), translator);
```

Add required usings at top:

```csharp
using System.Collections.Generic;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;
```

- [ ] **Step 3: Update logger XML doc comment**

Update the summary doc comment at the top of the class to include the new parameters:

```
///   flagrules=&lt;path&gt;       Path to custom flag translation rules JSON
///   translate=false         Disable all flag translation
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: Builds successfully.

- [ ] **Step 5: Commit**

```bash
git add src/logger/CompileCommandsLogger.cs
git commit -m "feat: add flag translation support to MSBuild logger"
```

---

### Task 9: Add built-in rules end-to-end test

**Files:**
- Modify: `tests/tests/BuiltinRulesTests.cs`

- [ ] **Step 1: Write end-to-end test using built-in rules with FlagTranslator**

Add to `tests/tests/BuiltinRulesTests.cs`:

```csharp
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
```

Add the required using at top:

```csharp
using MsBuildCompileCommands.Core.Extraction;
```

- [ ] **Step 2: Run tests**

Run: `dotnet test --filter FullyQualifiedName~BuiltinRulesTests`
Expected: All PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/tests/BuiltinRulesTests.cs
git commit -m "test: add end-to-end tests for built-in translation rules"
```

---

### Task 10: Run full test suite and final verification

- [ ] **Step 1: Run all tests**

Run: `dotnet test`
Expected: All tests pass.

- [ ] **Step 2: Run release build**

Run: `dotnet build -c Release`
Expected: No warnings or errors.

- [ ] **Step 3: Verify --dump-rules output**

Run: `dotnet run --project src/cli/cli.csproj -- --dump-rules`
Expected: Prints formatted JSON array of all built-in translation rules to stdout. The JSON should be valid and parseable.

- [ ] **Step 4: Commit any fixes needed**

If any fixes were needed, commit them here.
