# Multi-Compiler, Task Parameter Extraction, and Project Evaluation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Support gcc/g++/clang/clang++/nvcc compilers, extract compile commands from MSBuild task parameters when command-line events are absent, and add `--evaluate` mode to read ClCompile items directly from `.vcxproj` files.

**Architecture:** Three layered improvements to the extraction pipeline. (1) Replace the monolithic `ClCommandParser` with an `ICommandParser` abstraction dispatched by `CommandParserFactory`. (2) Handle `TaskStartedEventArgs`/`TaskParameterEventArgs`/`TaskFinishedEventArgs` in the collector to synthesize commands from task inputs when no `TaskCommandLineEventArgs` fires. (3) Add `ProjectEvaluator` in the CLI project (net8.0 only) that reads `.vcxproj` via `Microsoft.Build.Evaluation.Project`.

**Tech Stack:** C# / netstandard2.0 (core + logger), net8.0 (CLI + tests), xunit, Microsoft.Build.Framework 17.11.4, Microsoft.Build + Microsoft.Build.Locator (CLI only)

**Important constraints:**
- Core targets netstandard2.0: no `IReadOnlySet<T>`, no `Split(char)` single-char overload, no `using` declarations, no `Range`/`Index`. Use `HashSet<string>`, `Split(new[] { ';' }, ...)`, braced `using` blocks.
- `TreatWarningsAsErrors` is enabled globally.
- Logger embeds all core sources via `<Compile Include="..\core\**\*.cs" />` — every new file in `src/core/` is automatically included in the logger build.

---

## File Structure

### New files

| File | Responsibility |
|------|---------------|
| `src/core/Extraction/ICommandParser.cs` | Parser interface |
| `src/core/Extraction/CompilerConstants.cs` | Shared source extensions, compiler name sets |
| `src/core/Extraction/MsvcCommandParser.cs` | cl.exe / clang-cl parsing (moved from ClCommandParser) |
| `src/core/Extraction/GccClangCommandParser.cs` | gcc/g++/clang/clang++ parsing |
| `src/core/Extraction/NvccCommandParser.cs` | nvcc parsing (host flag extraction) |
| `src/core/Extraction/CommandParserFactory.cs` | Selects parser by command line inspection |
| `src/core/Extraction/ITaskMapper.cs` | Interface for mapping task parameters → CompileCommands |
| `src/core/Extraction/TaskMapperRegistry.cs` | Ordered list of mappers + fallback |
| `src/core/Extraction/ClCompileTaskMapper.cs` | Maps CL-task-shaped parameters |
| `src/core/Extraction/CudaCompileTaskMapper.cs` | Maps NVIDIA CudaCompile task parameters |
| `src/core/Extraction/GenericTaskMapper.cs` | Best-effort fallback for unknown tasks |
| `src/cli/Evaluation/ProjectEvaluator.cs` | Reads .vcxproj via MSBuild evaluation API |
| `src/cli/Evaluation/ClCompileItemMapper.cs` | Maps ClCompile item metadata → flags (unit-testable) |
| `tests/tests/GccClangCommandParserTests.cs` | Tests for GCC/Clang parser |
| `tests/tests/NvccCommandParserTests.cs` | Tests for nvcc parser |
| `tests/tests/CommandParserFactoryTests.cs` | Tests for factory dispatch |
| `tests/tests/TaskMapperTests.cs` | Tests for all task mappers |
| `tests/tests/ClCompileItemMapperTests.cs` | Tests for project evaluation metadata mapping |

### Modified files

| File | Change |
|------|--------|
| `src/core/Extraction/ClCommandParser.cs` | Thin `[Obsolete]` wrapper delegating to `MsvcCommandParser` |
| `src/core/Extraction/CompileCommandCollector.cs` | Use `CommandParserFactory`, add task parameter event handling |
| `src/logger/CompileCommandsLogger.cs` | Subscribe to `TaskStarted`, `TaskFinished`; handle `TaskParameterEventArgs` in `OnMessageRaised` |
| `src/cli/Program.cs` | Add `--evaluate` flag, call `ProjectEvaluator` |
| `src/cli/cli.csproj` | Add `Microsoft.Build` + `Microsoft.Build.Locator` dependencies |
| `README.md` | Update supported compilers, limitations, CLI options, roadmap |
| `CLAUDE.md` | Update architecture, data flow, key conventions |

---

## Task 1: ICommandParser Interface, CompilerConstants, and MsvcCommandParser

Extract the parser interface and shared constants, then move ClCommandParser logic into MsvcCommandParser.

**Files:**
- Create: `src/core/Extraction/ICommandParser.cs`
- Create: `src/core/Extraction/CompilerConstants.cs`
- Create: `src/core/Extraction/MsvcCommandParser.cs`
- Modify: `src/core/Extraction/ClCommandParser.cs`

- [ ] **Step 1: Create `ICommandParser` interface**

```csharp
// src/core/Extraction/ICommandParser.cs
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Models;

namespace MsBuildCompileCommands.Core.Extraction
{
    public interface ICommandParser
    {
        bool IsCompilerInvocation(string commandLine);
        List<CompileCommand> Parse(string commandLine, string directory, IList<string>? diagnostics = null);
    }
}
```

- [ ] **Step 2: Create `CompilerConstants`**

```csharp
// src/core/Extraction/CompilerConstants.cs
using System;
using System.Collections.Generic;

namespace MsBuildCompileCommands.Core.Extraction
{
    internal static class CompilerConstants
    {
        public static readonly HashSet<string> SourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".c", ".cc", ".cpp", ".cxx", ".c++", ".cp", ".ixx", ".cppm",
            ".cu"
        };
    }
}
```

- [ ] **Step 3: Create `MsvcCommandParser`**

Copy the entire body of `ClCommandParser` into a new `MsvcCommandParser` class that implements `ICommandParser`. The only changes:
- Class name: `MsvcCommandParser`
- Implements `ICommandParser`
- Replace the private `SourceExtensions` field with `CompilerConstants.SourceExtensions`
- Make `IsCompilerInvocation` an instance method (to satisfy the interface), keep the static version as well for backward compat

```csharp
// src/core/Extraction/MsvcCommandParser.cs
using System;
using System.Collections.Generic;
using System.IO;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;
using MsBuildCompileCommands.Core.Utils;

namespace MsBuildCompileCommands.Core.Extraction
{
    public sealed class MsvcCommandParser : ICommandParser
    {
        private static readonly HashSet<string> CompilerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cl", "cl.exe", "clang-cl", "clang-cl.exe"
        };

        private static readonly HashSet<string> OptionsWithSeparateValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/Fo", "/Fe", "/Fd", "/Fp", "/FR", "/Fr", "/Fa", "/Fm", "/Fi", "/doc"
        };

        private static readonly string[] ExcludedExactFlags = new[]
        {
            "/nologo", "-nologo", "/showIncludes", "-showIncludes", "/doc", "-doc"
        };

        private static readonly string[] ExcludedPrefixes = new[]
        {
            "/Fo", "/Fe", "/Fd", "/Fa", "/Fm", "/FR", "/Fr", "/Fp",
            "-Fo", "-Fe", "-Fd", "-Fa", "-Fm", "-FR", "-Fr", "-Fp"
        };

        private readonly ResponseFileParser _responseFileParser;

        public MsvcCommandParser() : this(new ResponseFileParser()) { }

        public MsvcCommandParser(ResponseFileParser responseFileParser)
        {
            _responseFileParser = responseFileParser ?? throw new ArgumentNullException(nameof(responseFileParser));
        }

        public bool IsCompilerInvocation(string commandLine)
        {
            return IsCompilerInvocationStatic(commandLine);
        }

        public static bool IsCompilerInvocationStatic(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return false;
            return FindCompilerEnd(commandLine) >= 0;
        }

        public List<CompileCommand> Parse(string commandLine, string directory, IList<string>? diagnostics = null)
        {
            int compilerEnd = FindCompilerEnd(commandLine);
            if (compilerEnd < 0)
                return new List<CompileCommand>();

            string compiler = commandLine.Substring(0, compilerEnd).Trim('"');
            string rest = commandLine.Substring(compilerEnd).TrimStart();

            List<string> tokens = CommandLineTokenizer.Tokenize(rest);
            tokens = _responseFileParser.Expand(tokens, directory);

            var flags = new List<string>();
            var sourceFiles = new List<string>();

            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];

                if (token.Length > 1 && token[0] == '@')
                {
                    diagnostics?.Add($"Warning: could not expand response file '{token.Substring(1).Trim('"')}'; flags from this file will be missing");
                    continue;
                }

                if (IsOutputOption(token))
                {
                    if (IsOptionWithSeparateValue(token, i, tokens))
                        i++;
                    continue;
                }

                string? pchAction = GetPchAction(token);
                if (pchAction != null)
                {
                    string? headerName = ExtractPchHeader(token, pchAction, ref i, tokens);
                    if (pchAction == "Yu" && headerName != null)
                        flags.Add("/FI" + headerName);
                    continue;
                }

                if (IsFlag(token))
                {
                    if (!ShouldExcludeFlag(token))
                    {
                        flags.Add(token);
                        if (IsFlagWithPossibleSeparateValue(token) && i + 1 < tokens.Count && !IsFlag(tokens[i + 1]))
                        {
                            flags.Add(tokens[++i]);
                        }
                    }
                }
                else
                {
                    string ext = GetExtension(token);
                    if (CompilerConstants.SourceExtensions.Contains(ext))
                    {
                        sourceFiles.Add(token);
                    }
                    else if (string.IsNullOrEmpty(ext))
                    {
                        // No extension — skip
                    }
                    else
                    {
                        sourceFiles.Add(token);
                    }
                }
            }

            string normalizedDir = PathNormalizer.NormalizeDirectory(directory);
            var commands = new List<CompileCommand>(sourceFiles.Count);

            foreach (string sourceFile in sourceFiles)
            {
                string normalizedFile = PathNormalizer.Normalize(sourceFile, directory);
                var args = new List<string>(flags.Count + 3) { compiler };
                args.AddRange(flags);
                args.Add("/c");
                args.Add(normalizedFile);
                commands.Add(new CompileCommand(normalizedDir, normalizedFile, args));
            }

            return commands;
        }

        // --- All private helper methods identical to current ClCommandParser ---

        private static int FindCompilerEnd(string commandLine)
        {
            string[] names = { "clang-cl.exe", "clang-cl", "cl.exe", "cl" };
            foreach (string name in names)
            {
                int idx = 0;
                while (idx < commandLine.Length)
                {
                    int pos = commandLine.IndexOf(name, idx, StringComparison.OrdinalIgnoreCase);
                    if (pos < 0) break;
                    int end = pos + name.Length;
                    bool atEnd = end >= commandLine.Length || commandLine[end] == ' ' || commandLine[end] == '\t';
                    bool atStart = pos == 0 || commandLine[pos - 1] == '\\' || commandLine[pos - 1] == '/' || commandLine[pos - 1] == '"';
                    if (atEnd && atStart) return end;
                    idx = end;
                }
            }
            return -1;
        }

        private static bool IsFlag(string token)
        {
            return token.StartsWith("/", StringComparison.Ordinal)
                || token.StartsWith("-", StringComparison.Ordinal);
        }

        private static bool IsOutputOption(string token)
        {
            foreach (string exact in ExcludedExactFlags)
            {
                if (string.Equals(token, exact, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            foreach (string prefix in ExcludedPrefixes)
            {
                if (token.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool ShouldExcludeFlag(string token) => IsOutputOption(token);

        private static bool IsOptionWithSeparateValue(string token, int index, List<string> tokens)
        {
            foreach (string opt in OptionsWithSeparateValue)
            {
                if (string.Equals(token, opt, StringComparison.OrdinalIgnoreCase))
                    return index + 1 < tokens.Count;
            }
            return false;
        }

        private static bool IsFlagWithPossibleSeparateValue(string token)
        {
            return string.Equals(token, "/I", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-I", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "/D", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-D", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "/U", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-U", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "/FI", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-FI", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-include", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "/external:I", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-external:I", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-isystem", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-imsvc", StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetPchAction(string token)
        {
            foreach (string prefix in new[] { "/", "-" })
            {
                if (token.StartsWith(prefix + "Yc", StringComparison.Ordinal)) return "Yc";
                if (token.StartsWith(prefix + "Yu", StringComparison.Ordinal)) return "Yu";
            }
            return null;
        }

        private static string? ExtractPchHeader(string token, string action, ref int index, List<string> tokens)
        {
            string rest = token.Substring(token[0] == '/' || token[0] == '-' ? 1 : 0);
            if (rest.Length > action.Length)
                return rest.Substring(action.Length).Trim('"');
            if (index + 1 < tokens.Count && !IsFlag(tokens[index + 1]))
                return tokens[++index].Trim('"');
            return null;
        }

        private static string GetExtension(string path)
        {
            try { return Path.GetExtension(path); }
            catch (Exception) { return string.Empty; }
        }
    }
}
```

- [ ] **Step 4: Convert `ClCommandParser` to obsolete wrapper**

Replace the body of `ClCommandParser` with delegation to `MsvcCommandParser`:

```csharp
// src/core/Extraction/ClCommandParser.cs
using System;
using System.Collections.Generic;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;

namespace MsBuildCompileCommands.Core.Extraction
{
    [Obsolete("Use MsvcCommandParser or CommandParserFactory instead.")]
    public sealed class ClCommandParser
    {
        private readonly MsvcCommandParser _inner;

        public ClCommandParser() : this(new ResponseFileParser()) { }

        public ClCommandParser(ResponseFileParser responseFileParser)
        {
            _inner = new MsvcCommandParser(responseFileParser);
        }

        public static bool IsCompilerInvocation(string commandLine)
        {
            return MsvcCommandParser.IsCompilerInvocationStatic(commandLine);
        }

        public List<CompileCommand> Parse(string commandLine, string directory, IList<string>? diagnostics = null)
        {
            return _inner.Parse(commandLine, directory, diagnostics);
        }
    }
}
```

- [ ] **Step 5: Run tests to verify backward compatibility**

Run: `dotnet test`
Expected: All existing tests pass — `ClCommandParserTests` exercises the wrapper which delegates to `MsvcCommandParser`.

- [ ] **Step 6: Build the solution (including logger) to verify no compile errors**

Run: `dotnet build`
Expected: Clean build. The logger project embeds all core sources including the new files.

- [ ] **Step 7: Commit**

```bash
git add src/core/Extraction/ICommandParser.cs src/core/Extraction/CompilerConstants.cs src/core/Extraction/MsvcCommandParser.cs src/core/Extraction/ClCommandParser.cs
git commit -m "refactor: extract ICommandParser interface and MsvcCommandParser from ClCommandParser"
```

---

## Task 2: GccClangCommandParser

**Files:**
- Create: `tests/tests/GccClangCommandParserTests.cs`
- Create: `src/core/Extraction/GccClangCommandParser.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/tests/GccClangCommandParserTests.cs
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Extraction;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class GccClangCommandParserTests
    {
        private readonly GccClangCommandParser _parser;

        public GccClangCommandParserTests()
        {
            var rsp = new ResponseFileParser(_ => null);
            _parser = new GccClangCommandParser(rsp);
        }

        [Theory]
        [InlineData("gcc -c main.c", true)]
        [InlineData("g++ -c main.cpp", true)]
        [InlineData("cc -c main.c", true)]
        [InlineData("c++ -c main.cpp", true)]
        [InlineData("clang -c main.c", true)]
        [InlineData("clang++ -c main.cpp", true)]
        [InlineData("gcc.exe -c main.c", true)]
        [InlineData("gcc-12 -c main.c", true)]
        [InlineData("clang++-17 -c main.cpp", true)]
        [InlineData(@"C:\msys2\mingw64\bin\g++.exe -c main.cpp", true)]
        [InlineData("/usr/bin/gcc -c main.c", true)]
        [InlineData("x86_64-linux-gnu-gcc -c main.c", true)]
        [InlineData("cl.exe /c main.cpp", false)]
        [InlineData("clang-cl.exe /c main.cpp", false)]
        [InlineData("link.exe /OUT:main.exe main.obj", false)]
        [InlineData("nvcc -c main.cu", false)]
        [InlineData("", false)]
        public void IsCompilerInvocation_detects_gcc_and_clang(string commandLine, bool expected)
        {
            Assert.Equal(expected, _parser.IsCompilerInvocation(commandLine));
        }

        [Fact]
        public void Parse_simple_gcc_command()
        {
            string cmd = "gcc -c -Wall -std=c11 main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            CompileCommand entry = commands[0];
            Assert.Contains("main.c", entry.File);
            Assert.Equal("gcc", entry.Arguments[0]);
            Assert.Contains("-Wall", entry.Arguments);
            Assert.Contains("-std=c11", entry.Arguments);
            Assert.Contains("-c", entry.Arguments);
        }

        [Fact]
        public void Parse_clang_with_includes_and_defines()
        {
            string cmd = "clang++ -c -std=c++17 -I/usr/include -I /opt/include -DFOO -DBAR=1 main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.Contains("-std=c++17", commands[0].Arguments);
            Assert.Contains("-I/usr/include", commands[0].Arguments);
            Assert.Contains("-I", commands[0].Arguments);
            Assert.Contains("-DFOO", commands[0].Arguments);
            Assert.Contains("-DBAR=1", commands[0].Arguments);
        }

        [Fact]
        public void Output_flag_is_excluded()
        {
            string cmd = "gcc -c -o main.o main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.DoesNotContain("-o", commands[0].Arguments);
            Assert.DoesNotContain("main.o", commands[0].Arguments);
        }

        [Fact]
        public void Dependency_flags_are_excluded()
        {
            string cmd = "g++ -c -MMD -MP -MF main.d -MT main.o main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.DoesNotContain(commands[0].Arguments, a => a.StartsWith("-M"));
            Assert.DoesNotContain("main.d", commands[0].Arguments);
            Assert.DoesNotContain("main.o", commands[0].Arguments);
        }

        [Fact]
        public void Warning_and_optimization_flags_are_preserved()
        {
            string cmd = "g++ -c -Wall -Wextra -Werror -O2 -fPIC main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.Contains("-Wall", commands[0].Arguments);
            Assert.Contains("-Wextra", commands[0].Arguments);
            Assert.Contains("-Werror", commands[0].Arguments);
            Assert.Contains("-O2", commands[0].Arguments);
            Assert.Contains("-fPIC", commands[0].Arguments);
        }

        [Fact]
        public void Isystem_and_iquote_are_preserved()
        {
            string cmd = "clang++ -c -isystem /usr/include/boost -iquote ../include main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.Contains("-isystem", commands[0].Arguments);
            Assert.Contains("/usr/include/boost", commands[0].Arguments);
            Assert.Contains("-iquote", commands[0].Arguments);
            Assert.Contains("../include", commands[0].Arguments);
        }

        [Fact]
        public void Include_flag_with_include_is_preserved()
        {
            string cmd = "gcc -c -include pch.h main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.Contains("-include", commands[0].Arguments);
            Assert.Contains("pch.h", commands[0].Arguments);
        }

        [Fact]
        public void Multiple_source_files()
        {
            string cmd = "g++ -c -Wall foo.cpp bar.cpp baz.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Equal(3, commands.Count);
            Assert.Contains(commands, c => c.File.Contains("foo.cpp"));
            Assert.Contains(commands, c => c.File.Contains("bar.cpp"));
            Assert.Contains(commands, c => c.File.Contains("baz.c"));
        }

        [Fact]
        public void Versioned_compiler_path_preserved()
        {
            string cmd = "gcc-12 -c main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.Equal("gcc-12", commands[0].Arguments[0]);
        }

        [Fact]
        public void Cross_compiler_detected()
        {
            string cmd = "x86_64-linux-gnu-g++ -c -std=c++17 main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.Equal("x86_64-linux-gnu-g++", commands[0].Arguments[0]);
        }

        [Fact]
        public void Windows_path_with_spaces()
        {
            string cmd = @"""C:\Program Files\LLVM\bin\clang++.exe"" -c -std=c++17 main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");

            Assert.Single(commands);
            Assert.Equal(@"C:\Program Files\LLVM\bin\clang++.exe", commands[0].Arguments[0]);
        }

        [Fact]
        public void Linker_flags_are_excluded()
        {
            string cmd = "gcc -c -lfoo -L/usr/lib main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.DoesNotContain(commands[0].Arguments, a => a.StartsWith("-l"));
            Assert.DoesNotContain(commands[0].Arguments, a => a.StartsWith("-L"));
        }

        [Fact]
        public void Cu_source_files_recognized()
        {
            string cmd = "clang++ -c -std=c++17 kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.Contains("kernel.cu", commands[0].File);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter GccClangCommandParserTests`
Expected: Compilation error — `GccClangCommandParser` class does not exist.

- [ ] **Step 3: Implement `GccClangCommandParser`**

```csharp
// src/core/Extraction/GccClangCommandParser.cs
using System;
using System.Collections.Generic;
using System.IO;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;
using MsBuildCompileCommands.Core.Utils;

namespace MsBuildCompileCommands.Core.Extraction
{
    public sealed class GccClangCommandParser : ICommandParser
    {
        // Ordered longest first to avoid partial matches (clang++ before clang, g++ before gcc)
        private static readonly string[] BaseNames = { "clang++", "clang", "g++", "gcc", "c++", "cc" };

        private static readonly HashSet<string> ExcludedExactFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-M", "-MM", "-MD", "-MMD", "-MP"
        };

        // Flags that consume the next token (their value)
        private static readonly HashSet<string> ExcludedFlagsWithValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-o", "-MF", "-MT", "-MQ"
        };

        // Flags with a separate value that should be KEPT
        private static readonly HashSet<string> KeptFlagsWithSeparateValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-I", "-D", "-U", "-isystem", "-iquote", "-idirafter", "-include"
        };

        private readonly ResponseFileParser _responseFileParser;

        public GccClangCommandParser() : this(new ResponseFileParser()) { }

        public GccClangCommandParser(ResponseFileParser responseFileParser)
        {
            _responseFileParser = responseFileParser ?? throw new ArgumentNullException(nameof(responseFileParser));
        }

        public bool IsCompilerInvocation(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return false;
            return FindCompilerEnd(commandLine) >= 0;
        }

        public List<CompileCommand> Parse(string commandLine, string directory, IList<string>? diagnostics = null)
        {
            int compilerEnd = FindCompilerEnd(commandLine);
            if (compilerEnd < 0)
                return new List<CompileCommand>();

            string compiler = commandLine.Substring(0, compilerEnd).Trim('"');
            string rest = commandLine.Substring(compilerEnd).TrimStart();

            List<string> tokens = CommandLineTokenizer.Tokenize(rest);
            tokens = _responseFileParser.Expand(tokens, directory);

            var flags = new List<string>();
            var sourceFiles = new List<string>();

            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];

                // Skip unexpanded response file references
                if (token.Length > 1 && token[0] == '@')
                {
                    diagnostics?.Add($"Warning: could not expand response file '{token.Substring(1).Trim('"')}'; flags from this file will be missing");
                    continue;
                }

                if (!IsFlag(token))
                {
                    string ext = GetExtension(token);
                    if (CompilerConstants.SourceExtensions.Contains(ext))
                    {
                        sourceFiles.Add(token);
                    }
                    else if (!string.IsNullOrEmpty(ext))
                    {
                        sourceFiles.Add(token);
                    }
                    continue;
                }

                // Excluded flags that stand alone (no value)
                if (ExcludedExactFlags.Contains(token))
                    continue;

                // Excluded flags that consume the next token
                if (ExcludedFlagsWithValue.Contains(token))
                {
                    if (i + 1 < tokens.Count)
                        i++; // skip value
                    continue;
                }

                // Linker flags: -l<lib>, -L<path> (shouldn't appear but filter if present)
                if (token.StartsWith("-l", StringComparison.Ordinal) ||
                    token.StartsWith("-L", StringComparison.Ordinal))
                    continue;

                // Keep the flag
                flags.Add(token);

                // Flags with a possible separate value
                if (KeptFlagsWithSeparateValue.Contains(token) && i + 1 < tokens.Count && !IsFlag(tokens[i + 1]))
                {
                    flags.Add(tokens[++i]);
                }
            }

            string normalizedDir = PathNormalizer.NormalizeDirectory(directory);
            var commands = new List<CompileCommand>(sourceFiles.Count);

            foreach (string sourceFile in sourceFiles)
            {
                string normalizedFile = PathNormalizer.Normalize(sourceFile, directory);
                var args = new List<string>(flags.Count + 3) { compiler };
                args.AddRange(flags);
                if (!flags.Contains("-c"))
                    args.Add("-c");
                args.Add(normalizedFile);
                commands.Add(new CompileCommand(normalizedDir, normalizedFile, args));
            }

            return commands;
        }

        private static int FindCompilerEnd(string commandLine)
        {
            foreach (string name in BaseNames)
            {
                int idx = 0;
                while (idx < commandLine.Length)
                {
                    int pos = commandLine.IndexOf(name, idx, StringComparison.OrdinalIgnoreCase);
                    if (pos < 0)
                        break;

                    // Start boundary: preceded by path separator, quote, hyphen (cross-compiler), or start
                    bool atStart = pos == 0
                        || commandLine[pos - 1] == '\\'
                        || commandLine[pos - 1] == '/'
                        || commandLine[pos - 1] == '"'
                        || commandLine[pos - 1] == '-';

                    if (!atStart)
                    {
                        idx = pos + name.Length;
                        continue;
                    }

                    int end = pos + name.Length;

                    // Optional version suffix: -<digits>
                    if (end < commandLine.Length && commandLine[end] == '-')
                    {
                        int vEnd = end + 1;
                        while (vEnd < commandLine.Length && char.IsDigit(commandLine[vEnd]))
                            vEnd++;
                        if (vEnd > end + 1)
                            end = vEnd;
                    }

                    // Optional .exe extension
                    if (end + 4 <= commandLine.Length &&
                        string.Compare(commandLine, end, ".exe", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                        end += 4;

                    // End boundary: followed by whitespace or end of string
                    bool atEnd = end >= commandLine.Length
                        || commandLine[end] == ' '
                        || commandLine[end] == '\t';

                    if (atEnd)
                        return end;

                    idx = end;
                }
            }

            return -1;
        }

        private static bool IsFlag(string token)
        {
            return token.StartsWith("-", StringComparison.Ordinal);
        }

        private static string GetExtension(string path)
        {
            try { return Path.GetExtension(path); }
            catch (Exception) { return string.Empty; }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter GccClangCommandParserTests`
Expected: All tests PASS.

- [ ] **Step 5: Run full test suite**

Run: `dotnet test`
Expected: All tests pass (existing + new).

- [ ] **Step 6: Commit**

```bash
git add src/core/Extraction/GccClangCommandParser.cs tests/tests/GccClangCommandParserTests.cs
git commit -m "feat: add GCC/Clang command parser with cross-compiler and version detection"
```

---

## Task 3: NvccCommandParser

**Files:**
- Create: `tests/tests/NvccCommandParserTests.cs`
- Create: `src/core/Extraction/NvccCommandParser.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/tests/NvccCommandParserTests.cs
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Extraction;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class NvccCommandParserTests
    {
        private readonly NvccCommandParser _parser;

        public NvccCommandParserTests()
        {
            var rsp = new ResponseFileParser(_ => null);
            _parser = new NvccCommandParser(rsp);
        }

        [Theory]
        [InlineData("nvcc -c kernel.cu", true)]
        [InlineData("nvcc.exe -c kernel.cu", true)]
        [InlineData(@"C:\CUDA\bin\nvcc.exe -c kernel.cu", true)]
        [InlineData("/usr/local/cuda/bin/nvcc -c kernel.cu", true)]
        [InlineData("gcc -c main.c", false)]
        [InlineData("cl.exe /c main.cpp", false)]
        [InlineData("", false)]
        public void IsCompilerInvocation_detects_nvcc(string commandLine, bool expected)
        {
            Assert.Equal(expected, _parser.IsCompilerInvocation(commandLine));
        }

        [Fact]
        public void Parse_simple_nvcc_command()
        {
            string cmd = "nvcc -c kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            CompileCommand entry = commands[0];
            Assert.Contains("kernel.cu", entry.File);
            Assert.Equal("nvcc", entry.Arguments[0]);
            Assert.Contains("-c", entry.Arguments);
        }

        [Fact]
        public void Include_and_define_flags_preserved()
        {
            string cmd = "nvcc -c -I/usr/include -I /opt/include -DFOO -DBAR=1 kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.Contains("-I/usr/include", commands[0].Arguments);
            Assert.Contains("-I", commands[0].Arguments);
            Assert.Contains("-DFOO", commands[0].Arguments);
            Assert.Contains("-DBAR=1", commands[0].Arguments);
        }

        [Fact]
        public void Xcompiler_flags_extracted()
        {
            string cmd = "nvcc -c -Xcompiler \"-O2,-Wall,-fPIC\" kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.Contains("-O2", commands[0].Arguments);
            Assert.Contains("-Wall", commands[0].Arguments);
            Assert.Contains("-fPIC", commands[0].Arguments);
            Assert.DoesNotContain("-Xcompiler", commands[0].Arguments);
        }

        [Fact]
        public void Compiler_options_long_form_extracted()
        {
            string cmd = "nvcc -c --compiler-options \"-O2,-Wall\" kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.Contains("-O2", commands[0].Arguments);
            Assert.Contains("-Wall", commands[0].Arguments);
        }

        [Fact]
        public void Gpu_flags_excluded()
        {
            string cmd = "nvcc -c --gpu-architecture=sm_75 -gencode arch=compute_75,code=sm_75 kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.DoesNotContain(commands[0].Arguments, a => a.Contains("gpu-architecture"));
            Assert.DoesNotContain(commands[0].Arguments, a => a.Contains("-gencode"));
            Assert.DoesNotContain(commands[0].Arguments, a => a.Contains("arch="));
        }

        [Fact]
        public void Output_flags_excluded()
        {
            string cmd = "nvcc -c -o kernel.o kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.DoesNotContain("-o", commands[0].Arguments);
            Assert.DoesNotContain("kernel.o", commands[0].Arguments);
        }

        [Fact]
        public void Std_flag_preserved()
        {
            string cmd = "nvcc -c -std=c++17 kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.Contains("-std=c++17", commands[0].Arguments);
        }

        [Fact]
        public void Dependency_flags_excluded()
        {
            string cmd = "nvcc -c -MD -MF kernel.d kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.DoesNotContain("-MD", commands[0].Arguments);
            Assert.DoesNotContain("-MF", commands[0].Arguments);
            Assert.DoesNotContain("kernel.d", commands[0].Arguments);
        }

        [Fact]
        public void Multiple_source_files()
        {
            string cmd = "nvcc -c kernel1.cu kernel2.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Equal(2, commands.Count);
            Assert.Contains(commands, c => c.File.Contains("kernel1.cu"));
            Assert.Contains(commands, c => c.File.Contains("kernel2.cu"));
        }

        [Fact]
        public void Cpp_source_files_also_recognized()
        {
            string cmd = "nvcc -c main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, "/home/user/project");

            Assert.Single(commands);
            Assert.Contains("main.cpp", commands[0].File);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter NvccCommandParserTests`
Expected: Compilation error — `NvccCommandParser` class does not exist.

- [ ] **Step 3: Implement `NvccCommandParser`**

```csharp
// src/core/Extraction/NvccCommandParser.cs
using System;
using System.Collections.Generic;
using System.IO;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;
using MsBuildCompileCommands.Core.Utils;

namespace MsBuildCompileCommands.Core.Extraction
{
    public sealed class NvccCommandParser : ICommandParser
    {
        private static readonly HashSet<string> NvccNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nvcc", "nvcc.exe"
        };

        // Flags that stand alone (no value) and should be excluded
        private static readonly HashSet<string> ExcludedExactFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-M", "-MM", "-MD", "-MMD", "-MP",
            "--generate-dependencies", "--generate-nonsystem-dependencies"
        };

        // Flags that consume the next token and should be excluded
        private static readonly HashSet<string> ExcludedFlagsWithValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-o", "--output-file",
            "-odir", "--output-directory",
            "-MF", "-MT", "-MQ",
            "--gpu-architecture", "-arch",
            "--gpu-code", "-code",
            "-gencode", "--generate-code",
            "--relocatable-device-code", "-rdc",
            "--device-c", "-dc",
            "--device-link", "-dlink",
            "--maxrregcount", "-maxrregcount"
        };

        private readonly ResponseFileParser _responseFileParser;

        public NvccCommandParser() : this(new ResponseFileParser()) { }

        public NvccCommandParser(ResponseFileParser responseFileParser)
        {
            _responseFileParser = responseFileParser ?? throw new ArgumentNullException(nameof(responseFileParser));
        }

        public bool IsCompilerInvocation(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return false;
            return FindCompilerEnd(commandLine) >= 0;
        }

        public List<CompileCommand> Parse(string commandLine, string directory, IList<string>? diagnostics = null)
        {
            int compilerEnd = FindCompilerEnd(commandLine);
            if (compilerEnd < 0)
                return new List<CompileCommand>();

            string compiler = commandLine.Substring(0, compilerEnd).Trim('"');
            string rest = commandLine.Substring(compilerEnd).TrimStart();

            List<string> tokens = CommandLineTokenizer.Tokenize(rest);
            tokens = _responseFileParser.Expand(tokens, directory);

            var flags = new List<string>();
            var sourceFiles = new List<string>();

            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];

                if (token.Length > 1 && token[0] == '@')
                {
                    diagnostics?.Add($"Warning: could not expand response file '{token.Substring(1).Trim('"')}'; flags from this file will be missing");
                    continue;
                }

                if (!IsFlag(token))
                {
                    string ext = GetExtension(token);
                    if (CompilerConstants.SourceExtensions.Contains(ext) || !string.IsNullOrEmpty(ext))
                    {
                        sourceFiles.Add(token);
                    }
                    continue;
                }

                // -Xcompiler / --compiler-options: extract host compiler flags
                if (string.Equals(token, "-Xcompiler", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(token, "--compiler-options", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < tokens.Count)
                    {
                        string hostFlags = tokens[++i].Trim('"');
                        foreach (string flag in hostFlags.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string trimmed = flag.Trim();
                            if (trimmed.Length > 0)
                                flags.Add(trimmed);
                        }
                    }
                    continue;
                }

                // GPU-specific flags with = syntax: --gpu-architecture=sm_75
                if (IsGpuFlagWithEquals(token))
                    continue;

                // Excluded exact flags
                if (ExcludedExactFlags.Contains(token))
                    continue;

                // Excluded flags with a separate value
                if (ExcludedFlagsWithValue.Contains(token))
                {
                    if (i + 1 < tokens.Count)
                        i++;
                    continue;
                }

                // Keep the flag (includes -I, -D, -std=, etc.)
                flags.Add(token);

                // Flags with possible separate value
                if (IsFlagWithPossibleSeparateValue(token) && i + 1 < tokens.Count && !IsFlag(tokens[i + 1]))
                {
                    flags.Add(tokens[++i]);
                }
            }

            string normalizedDir = PathNormalizer.NormalizeDirectory(directory);
            var commands = new List<CompileCommand>(sourceFiles.Count);

            foreach (string sourceFile in sourceFiles)
            {
                string normalizedFile = PathNormalizer.Normalize(sourceFile, directory);
                var args = new List<string>(flags.Count + 3) { compiler };
                args.AddRange(flags);
                if (!flags.Contains("-c"))
                    args.Add("-c");
                args.Add(normalizedFile);
                commands.Add(new CompileCommand(normalizedDir, normalizedFile, args));
            }

            return commands;
        }

        private static int FindCompilerEnd(string commandLine)
        {
            string[] names = { "nvcc.exe", "nvcc" };
            foreach (string name in names)
            {
                int idx = 0;
                while (idx < commandLine.Length)
                {
                    int pos = commandLine.IndexOf(name, idx, StringComparison.OrdinalIgnoreCase);
                    if (pos < 0) break;
                    int end = pos + name.Length;
                    bool atEnd = end >= commandLine.Length || commandLine[end] == ' ' || commandLine[end] == '\t';
                    bool atStart = pos == 0 || commandLine[pos - 1] == '\\' || commandLine[pos - 1] == '/' || commandLine[pos - 1] == '"';
                    if (atEnd && atStart) return end;
                    idx = end;
                }
            }
            return -1;
        }

        private static bool IsGpuFlagWithEquals(string token)
        {
            return token.StartsWith("--gpu-architecture=", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("--gpu-code=", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("-gencode=", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("--generate-code=", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("--relocatable-device-code=", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("-arch=", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("-code=", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFlag(string token)
        {
            return token.StartsWith("-", StringComparison.Ordinal);
        }

        private static bool IsFlagWithPossibleSeparateValue(string token)
        {
            return string.Equals(token, "-I", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-D", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-U", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-isystem", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "-include", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetExtension(string path)
        {
            try { return Path.GetExtension(path); }
            catch (Exception) { return string.Empty; }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter NvccCommandParserTests`
Expected: All tests PASS.

- [ ] **Step 5: Run full test suite**

Run: `dotnet test`
Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/core/Extraction/NvccCommandParser.cs tests/tests/NvccCommandParserTests.cs
git commit -m "feat: add nvcc command parser with host flag extraction via -Xcompiler"
```

---

## Task 4: CommandParserFactory and CompileCommandCollector Update

**Files:**
- Create: `src/core/Extraction/CommandParserFactory.cs`
- Create: `tests/tests/CommandParserFactoryTests.cs`
- Modify: `src/core/Extraction/CompileCommandCollector.cs`

- [ ] **Step 1: Write failing factory tests**

```csharp
// tests/tests/CommandParserFactoryTests.cs
using MsBuildCompileCommands.Core.Extraction;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class CommandParserFactoryTests
    {
        [Theory]
        [InlineData("cl.exe /c main.cpp", typeof(MsvcCommandParser))]
        [InlineData("clang-cl.exe /c main.cpp", typeof(MsvcCommandParser))]
        [InlineData("gcc -c main.c", typeof(GccClangCommandParser))]
        [InlineData("g++ -c main.cpp", typeof(GccClangCommandParser))]
        [InlineData("clang++ -c main.cpp", typeof(GccClangCommandParser))]
        [InlineData("clang -c main.c", typeof(GccClangCommandParser))]
        [InlineData("nvcc -c kernel.cu", typeof(NvccCommandParser))]
        public void FindParser_returns_correct_parser_type(string commandLine, System.Type expectedType)
        {
            ICommandParser? parser = CommandParserFactory.FindParser(commandLine);

            Assert.NotNull(parser);
            Assert.IsType(expectedType, parser);
        }

        [Theory]
        [InlineData("link.exe /OUT:main.exe main.obj")]
        [InlineData("lib.exe /OUT:static.lib obj1.obj")]
        [InlineData("")]
        public void FindParser_returns_null_for_non_compilers(string commandLine)
        {
            Assert.Null(CommandParserFactory.FindParser(commandLine));
        }

        [Fact]
        public void Nvcc_checked_before_gcc()
        {
            // nvcc could theoretically match something if gcc were checked first
            ICommandParser? parser = CommandParserFactory.FindParser("nvcc -c kernel.cu");
            Assert.IsType<NvccCommandParser>(parser);
        }

        [Fact]
        public void Clang_cl_matched_as_msvc_not_gcc()
        {
            ICommandParser? parser = CommandParserFactory.FindParser("clang-cl /c main.cpp");
            Assert.IsType<MsvcCommandParser>(parser);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter CommandParserFactoryTests`
Expected: Compilation error — `CommandParserFactory` does not exist.

- [ ] **Step 3: Implement `CommandParserFactory`**

```csharp
// src/core/Extraction/CommandParserFactory.cs
namespace MsBuildCompileCommands.Core.Extraction
{
    public static class CommandParserFactory
    {
        // Order: nvcc (most specific), Msvc (clang-cl before bare clang), GccClang (last)
        private static readonly ICommandParser[] Parsers =
        {
            new NvccCommandParser(),
            new MsvcCommandParser(),
            new GccClangCommandParser()
        };

        public static ICommandParser? FindParser(string commandLine)
        {
            for (int i = 0; i < Parsers.Length; i++)
            {
                if (Parsers[i].IsCompilerInvocation(commandLine))
                    return Parsers[i];
            }
            return null;
        }
    }
}
```

- [ ] **Step 4: Run factory tests**

Run: `dotnet test --filter CommandParserFactoryTests`
Expected: All tests PASS.

- [ ] **Step 5: Update `CompileCommandCollector` to use factory**

Replace the `ClCommandParser` dependency with `CommandParserFactory`:

```csharp
// src/core/Extraction/CompileCommandCollector.cs
// Changes to the existing file:

// 1. Remove the _parser field:
//    REMOVE: private readonly ClCommandParser _parser;

// 2. Update constructors — remove ClCommandParser parameter, keep filter-only:
public CompileCommandCollector() : this(null) { }

public CompileCommandCollector(CompileCommandFilter? filter)
{
    _filter = filter;
}

// Keep obsolete constructors for backward compatibility:
[Obsolete("Use CompileCommandCollector(CompileCommandFilter?) instead.")]
public CompileCommandCollector(ClCommandParser parser) : this(parser, null) { }

[Obsolete("Use CompileCommandCollector(CompileCommandFilter?) instead.")]
public CompileCommandCollector(ClCommandParser parser, CompileCommandFilter? filter) : this(filter) { }

// 3. Update HandleTaskCommandLine:
private void HandleTaskCommandLine(TaskCommandLineEventArgs e)
{
    string? commandLine = e.CommandLine;
    if (string.IsNullOrWhiteSpace(commandLine))
        return;

    ICommandParser? parser = CommandParserFactory.FindParser(commandLine);
    if (parser == null)
        return;

    if (!PassesFilter(e.BuildEventContext))
        return;

    string directory = ResolveDirectory(e.BuildEventContext);

    try
    {
        List<CompileCommand> commands = parser.Parse(commandLine, directory, _diagnostics);

        foreach (CompileCommand cmd in commands)
        {
            _commands[cmd.DeduplicationKey] = cmd;
        }

        if (commands.Count == 0)
        {
            _diagnostics.Add($"Warning: compiler invocation produced no entries: {Truncate(commandLine, 200)}");
        }
    }
    catch (Exception ex)
    {
        _diagnostics.Add($"Error parsing command line: {ex.Message} | {Truncate(commandLine, 200)}");
    }
}

// 4. Add method to expose project paths (needed later for --evaluate):
public List<string> GetProjectPaths()
{
    var paths = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var kvp in _projectDirectories)
    {
        // Reconstruct the project file path from the directory and project name
        // We need to store the full path instead — update HandleProjectStarted
    }
    return paths;
}
```

Actually, to expose project paths cleanly, also store the full project file path:

```csharp
// Add field:
private readonly Dictionary<int, string> _projectFiles = new Dictionary<int, string>();

// In HandleProjectStarted, add:
_projectFiles[contextId] = e.ProjectFile;

// GetProjectPaths implementation:
public List<string> GetProjectPaths()
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var paths = new List<string>();
    foreach (string projectFile in _projectFiles.Values)
    {
        if (projectFile.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase) && seen.Add(projectFile))
        {
            paths.Add(projectFile);
        }
    }
    paths.Sort(StringComparer.OrdinalIgnoreCase);
    return paths;
}
```

- [ ] **Step 6: Run full test suite**

Run: `dotnet test`
Expected: All tests pass. The existing `CompileCommandCollectorTests` still work because `TaskCommandLineEventArgs` for `cl.exe` commands are now handled by `CommandParserFactory` → `MsvcCommandParser`.

- [ ] **Step 7: Add a test for multi-compiler collection**

Add to `CompileCommandCollectorTests.cs`:

```csharp
[Fact]
public void Collects_gcc_commands_via_factory()
{
    var collector = new CompileCommandCollector();

    var projectStarted = CreateProjectStartedEvent(@"C:\project\myapp.vcxproj", projectContextId: 1);
    collector.HandleEvent(projectStarted);

    var taskCmd = CreateTaskCommandLineEvent(
        "gcc -c -Wall main.c",
        projectContextId: 1);
    collector.HandleEvent(taskCmd);

    List<CompileCommand> commands = collector.GetCommands();
    Assert.Single(commands);
    Assert.Contains("main.c", commands[0].File);
    Assert.Contains("-Wall", commands[0].Arguments);
}

[Fact]
public void Collects_nvcc_commands_via_factory()
{
    var collector = new CompileCommandCollector();

    var projectStarted = CreateProjectStartedEvent(@"C:\project\cuda.vcxproj", projectContextId: 1);
    collector.HandleEvent(projectStarted);

    var taskCmd = CreateTaskCommandLineEvent(
        "nvcc -c -I/usr/include kernel.cu",
        projectContextId: 1);
    collector.HandleEvent(taskCmd);

    List<CompileCommand> commands = collector.GetCommands();
    Assert.Single(commands);
    Assert.Contains("kernel.cu", commands[0].File);
}
```

- [ ] **Step 8: Run tests**

Run: `dotnet test`
Expected: All tests pass.

- [ ] **Step 9: Build entire solution including logger**

Run: `dotnet build`
Expected: Clean build. Logger picks up new files automatically via `<Compile Include="..\core\**\*.cs" />`.

- [ ] **Step 10: Commit**

```bash
git add src/core/Extraction/CommandParserFactory.cs tests/tests/CommandParserFactoryTests.cs src/core/Extraction/CompileCommandCollector.cs tests/tests/CompileCommandCollectorTests.cs
git commit -m "feat: add CommandParserFactory and wire multi-compiler support into collector"
```

---

## Task 5: Task Parameter Extraction

**Files:**
- Create: `src/core/Extraction/ITaskMapper.cs`
- Create: `src/core/Extraction/TaskMapperRegistry.cs`
- Create: `src/core/Extraction/ClCompileTaskMapper.cs`
- Create: `src/core/Extraction/CudaCompileTaskMapper.cs`
- Create: `src/core/Extraction/GenericTaskMapper.cs`
- Create: `tests/tests/TaskMapperTests.cs`
- Modify: `src/core/Extraction/CompileCommandCollector.cs`
- Modify: `src/logger/CompileCommandsLogger.cs`

- [ ] **Step 1: Write failing mapper tests**

```csharp
// tests/tests/TaskMapperTests.cs
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Extraction;
using MsBuildCompileCommands.Core.Models;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class TaskMapperTests
    {
        // --- ClCompileTaskMapper tests ---

        [Fact]
        public void ClCompileMapper_can_map_CL_task()
        {
            var mapper = new ClCompileTaskMapper();
            Assert.True(mapper.CanMap("CL"));
            Assert.True(mapper.CanMap("cl"));
        }

        [Fact]
        public void ClCompileMapper_maps_sources_with_defines_and_includes()
        {
            var mapper = new ClCompileTaskMapper();
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sources"] = new List<string> { @"C:\project\main.cpp", @"C:\project\util.cpp" },
                ["PreprocessorDefinitions"] = new List<string> { "WIN32;_DEBUG;VERSION=42" },
                ["AdditionalIncludeDirectories"] = new List<string> { @"C:\sdk\include;C:\project\src" },
                ["AdditionalOptions"] = new List<string> { "/W4 /EHsc" }
            };

            List<CompileCommand> commands = mapper.Map("CL", parameters, @"C:\project");

            Assert.Equal(2, commands.Count);

            CompileCommand mainCmd = commands.Find(c => c.File.Contains("main.cpp"))!;
            Assert.NotNull(mainCmd);
            Assert.Contains("/DWIN32", mainCmd.Arguments);
            Assert.Contains("/D_DEBUG", mainCmd.Arguments);
            Assert.Contains("/DVERSION=42", mainCmd.Arguments);
            Assert.Contains(@"/IC:\sdk\include", mainCmd.Arguments);
            Assert.Contains(@"/IC:\project\src", mainCmd.Arguments);
            Assert.Contains("/W4", mainCmd.Arguments);
            Assert.Contains("/EHsc", mainCmd.Arguments);
        }

        [Fact]
        public void ClCompileMapper_handles_runtime_library()
        {
            var mapper = new ClCompileTaskMapper();
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sources"] = new List<string> { "main.cpp" },
                ["RuntimeLibrary"] = new List<string> { "MultiThreadedDebugDLL" }
            };

            List<CompileCommand> commands = mapper.Map("CL", parameters, @"C:\project");

            Assert.Single(commands);
            Assert.Contains("/MDd", commands[0].Arguments);
        }

        [Fact]
        public void ClCompileMapper_handles_exception_handling()
        {
            var mapper = new ClCompileTaskMapper();
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sources"] = new List<string> { "main.cpp" },
                ["ExceptionHandling"] = new List<string> { "Sync" }
            };

            List<CompileCommand> commands = mapper.Map("CL", parameters, @"C:\project");

            Assert.Single(commands);
            Assert.Contains("/EHsc", commands[0].Arguments);
        }

        [Fact]
        public void ClCompileMapper_no_sources_returns_empty()
        {
            var mapper = new ClCompileTaskMapper();
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["PreprocessorDefinitions"] = new List<string> { "FOO" }
            };

            List<CompileCommand> commands = mapper.Map("CL", parameters, @"C:\project");
            Assert.Empty(commands);
        }

        // --- CudaCompileTaskMapper tests ---

        [Fact]
        public void CudaCompileMapper_can_map_CudaCompile_task()
        {
            var mapper = new CudaCompileTaskMapper();
            Assert.True(mapper.CanMap("CudaCompile"));
            Assert.True(mapper.CanMap("cudacompile"));
            Assert.False(mapper.CanMap("CL"));
        }

        [Fact]
        public void CudaCompileMapper_maps_cuda_sources()
        {
            var mapper = new CudaCompileTaskMapper();
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sources"] = new List<string> { @"C:\project\kernel.cu" },
                ["Defines"] = new List<string> { "USE_CUDA;BLOCK_SIZE=256" },
                ["AdditionalIncludeDirectories"] = new List<string> { @"C:\cuda\include" },
                ["AdditionalOptions"] = new List<string> { "-std=c++17" }
            };

            List<CompileCommand> commands = mapper.Map("CudaCompile", parameters, @"C:\project");

            Assert.Single(commands);
            Assert.Contains("kernel.cu", commands[0].File);
            Assert.Contains("-DUSE_CUDA", commands[0].Arguments);
            Assert.Contains("-DBLOCK_SIZE=256", commands[0].Arguments);
            Assert.Contains(@"-IC:\cuda\include", commands[0].Arguments);
            Assert.Contains("-std=c++17", commands[0].Arguments);
        }

        // --- GenericTaskMapper tests ---

        [Fact]
        public void GenericMapper_maps_task_with_Sources_parameter()
        {
            var mapper = new GenericTaskMapper();
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sources"] = new List<string> { "main.cpp" },
                ["AdditionalIncludeDirectories"] = new List<string> { @"C:\include" },
                ["PreprocessorDefinitions"] = new List<string> { "FOO" }
            };

            List<CompileCommand> commands = mapper.Map("CustomCompiler", parameters, @"C:\project");

            Assert.Single(commands);
            Assert.Contains("main.cpp", commands[0].File);
        }

        [Fact]
        public void GenericMapper_tries_alternative_source_parameter_names()
        {
            var mapper = new GenericTaskMapper();
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["SourceFiles"] = new List<string> { "main.cpp" }
            };

            List<CompileCommand> commands = mapper.Map("CustomCompiler", parameters, @"C:\project");

            Assert.Single(commands);
        }

        [Fact]
        public void GenericMapper_no_source_parameters_returns_empty()
        {
            var mapper = new GenericTaskMapper();
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["OutputFile"] = new List<string> { "out.exe" }
            };

            List<CompileCommand> commands = mapper.Map("CustomCompiler", parameters, @"C:\project");
            Assert.Empty(commands);
        }

        // --- TaskMapperRegistry tests ---

        [Fact]
        public void Registry_selects_ClCompile_mapper_for_CL()
        {
            var registry = new TaskMapperRegistry();
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sources"] = new List<string> { "main.cpp" }
            };

            List<CompileCommand> commands = registry.TryMap("CL", parameters, @"C:\project");
            Assert.Single(commands);
        }

        [Fact]
        public void Registry_selects_CudaCompile_mapper_for_CudaCompile()
        {
            var registry = new TaskMapperRegistry();
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sources"] = new List<string> { "kernel.cu" }
            };

            List<CompileCommand> commands = registry.TryMap("CudaCompile", parameters, @"C:\project");
            Assert.Single(commands);
        }

        [Fact]
        public void Registry_falls_back_to_generic_for_unknown_task()
        {
            var registry = new TaskMapperRegistry();
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sources"] = new List<string> { "main.cpp" }
            };

            List<CompileCommand> commands = registry.TryMap("MyCustomCompiler", parameters, @"C:\project");
            Assert.Single(commands);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter TaskMapperTests`
Expected: Compilation errors — mapper classes don't exist.

- [ ] **Step 3: Create `ITaskMapper` interface**

```csharp
// src/core/Extraction/ITaskMapper.cs
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Models;

namespace MsBuildCompileCommands.Core.Extraction
{
    public interface ITaskMapper
    {
        bool CanMap(string taskName);
        List<CompileCommand> Map(string taskName, IDictionary<string, List<string>> parameters, string directory);
    }
}
```

- [ ] **Step 4: Implement `ClCompileTaskMapper`**

```csharp
// src/core/Extraction/ClCompileTaskMapper.cs
using System;
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Models;
using MsBuildCompileCommands.Core.Utils;

namespace MsBuildCompileCommands.Core.Extraction
{
    public sealed class ClCompileTaskMapper : ITaskMapper
    {
        private static readonly HashSet<string> TaskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CL"
        };

        private static readonly Dictionary<string, string> RuntimeLibraryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MultiThreaded"] = "/MT",
            ["MultiThreadedDebug"] = "/MTd",
            ["MultiThreadedDLL"] = "/MD",
            ["MultiThreadedDebugDLL"] = "/MDd"
        };

        private static readonly Dictionary<string, string> ExceptionHandlingMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sync"] = "/EHsc",
            ["Async"] = "/EHa",
            ["SyncCThrow"] = "/EHs",
            ["false"] = ""
        };

        public bool CanMap(string taskName) => TaskNames.Contains(taskName);

        public List<CompileCommand> Map(string taskName, IDictionary<string, List<string>> parameters, string directory)
        {
            List<string>? sources = GetParameter(parameters, "Sources");
            if (sources == null || sources.Count == 0)
                return new List<CompileCommand>();

            var flags = new List<string>();

            // Preprocessor definitions
            string? defines = GetScalar(parameters, "PreprocessorDefinitions");
            if (defines != null)
            {
                foreach (string def in defines.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = def.Trim();
                    if (trimmed.Length > 0)
                        flags.Add("/D" + trimmed);
                }
            }

            // Include directories
            string? includes = GetScalar(parameters, "AdditionalIncludeDirectories");
            if (includes != null)
            {
                foreach (string inc in includes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = inc.Trim();
                    if (trimmed.Length > 0)
                        flags.Add("/I" + trimmed);
                }
            }

            // Forced includes
            string? forcedIncludes = GetScalar(parameters, "ForcedIncludeFiles");
            if (forcedIncludes != null)
            {
                foreach (string fi in forcedIncludes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = fi.Trim();
                    if (trimmed.Length > 0)
                        flags.Add("/FI" + trimmed);
                }
            }

            // Runtime library
            string? rtl = GetScalar(parameters, "RuntimeLibrary");
            if (rtl != null && RuntimeLibraryMap.TryGetValue(rtl, out string? rtlFlag) && rtlFlag.Length > 0)
                flags.Add(rtlFlag);

            // Exception handling
            string? eh = GetScalar(parameters, "ExceptionHandling");
            if (eh != null && ExceptionHandlingMap.TryGetValue(eh, out string? ehFlag) && ehFlag.Length > 0)
                flags.Add(ehFlag);

            // Additional options (pass through)
            string? additionalOptions = GetScalar(parameters, "AdditionalOptions");
            if (additionalOptions != null)
            {
                foreach (string opt in CommandLineTokenizer.Tokenize(additionalOptions))
                {
                    flags.Add(opt);
                }
            }

            string normalizedDir = PathNormalizer.NormalizeDirectory(directory);
            var commands = new List<CompileCommand>(sources.Count);

            foreach (string source in sources)
            {
                string normalizedFile = PathNormalizer.Normalize(source, directory);
                var args = new List<string>(flags.Count + 3) { "cl.exe" };
                args.AddRange(flags);
                args.Add("/c");
                args.Add(normalizedFile);
                commands.Add(new CompileCommand(normalizedDir, normalizedFile, args));
            }

            return commands;
        }

        private static List<string>? GetParameter(IDictionary<string, List<string>> parameters, string name)
        {
            if (parameters.TryGetValue(name, out List<string>? values))
                return values;
            return null;
        }

        private static string? GetScalar(IDictionary<string, List<string>> parameters, string name)
        {
            List<string>? values = GetParameter(parameters, name);
            if (values != null && values.Count > 0)
                return values[0];
            return null;
        }
    }
}
```

- [ ] **Step 5: Implement `CudaCompileTaskMapper`**

```csharp
// src/core/Extraction/CudaCompileTaskMapper.cs
using System;
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Models;
using MsBuildCompileCommands.Core.Utils;

namespace MsBuildCompileCommands.Core.Extraction
{
    public sealed class CudaCompileTaskMapper : ITaskMapper
    {
        private static readonly HashSet<string> TaskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CudaCompile"
        };

        public bool CanMap(string taskName) => TaskNames.Contains(taskName);

        public List<CompileCommand> Map(string taskName, IDictionary<string, List<string>> parameters, string directory)
        {
            List<string>? sources = GetParameter(parameters, "Sources");
            if (sources == null || sources.Count == 0)
                return new List<CompileCommand>();

            var flags = new List<string>();

            // Defines (CUDA uses "Defines" not "PreprocessorDefinitions")
            string? defines = GetScalar(parameters, "Defines");
            if (defines != null)
            {
                foreach (string def in defines.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = def.Trim();
                    if (trimmed.Length > 0)
                        flags.Add("-D" + trimmed);
                }
            }

            // Include directories
            string? includes = GetScalar(parameters, "AdditionalIncludeDirectories");
            if (includes != null)
            {
                foreach (string inc in includes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = inc.Trim();
                    if (trimmed.Length > 0)
                        flags.Add("-I" + trimmed);
                }
            }

            // Additional options
            string? additionalOptions = GetScalar(parameters, "AdditionalOptions");
            if (additionalOptions != null)
            {
                foreach (string opt in CommandLineTokenizer.Tokenize(additionalOptions))
                {
                    flags.Add(opt);
                }
            }

            string normalizedDir = PathNormalizer.NormalizeDirectory(directory);
            var commands = new List<CompileCommand>(sources.Count);

            foreach (string source in sources)
            {
                string normalizedFile = PathNormalizer.Normalize(source, directory);
                var args = new List<string>(flags.Count + 3) { "nvcc" };
                args.AddRange(flags);
                args.Add("-c");
                args.Add(normalizedFile);
                commands.Add(new CompileCommand(normalizedDir, normalizedFile, args));
            }

            return commands;
        }

        private static List<string>? GetParameter(IDictionary<string, List<string>> parameters, string name)
        {
            if (parameters.TryGetValue(name, out List<string>? values))
                return values;
            return null;
        }

        private static string? GetScalar(IDictionary<string, List<string>> parameters, string name)
        {
            List<string>? values = GetParameter(parameters, name);
            if (values != null && values.Count > 0)
                return values[0];
            return null;
        }
    }
}
```

- [ ] **Step 6: Implement `GenericTaskMapper`**

```csharp
// src/core/Extraction/GenericTaskMapper.cs
using System;
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Models;
using MsBuildCompileCommands.Core.Utils;

namespace MsBuildCompileCommands.Core.Extraction
{
    public sealed class GenericTaskMapper : ITaskMapper
    {
        private static readonly string[] SourceParameterNames = { "Sources", "SourceFiles", "InputFiles", "Inputs" };
        private static readonly string[] IncludeParameterNames = { "AdditionalIncludeDirectories", "IncludePaths", "Includes" };
        private static readonly string[] DefineParameterNames = { "PreprocessorDefinitions", "Defines" };

        public bool CanMap(string taskName) => true; // Fallback mapper — always willing to try

        public List<CompileCommand> Map(string taskName, IDictionary<string, List<string>> parameters, string directory)
        {
            List<string>? sources = FindParameter(parameters, SourceParameterNames);
            if (sources == null || sources.Count == 0)
                return new List<CompileCommand>();

            // Filter to only recognized source file extensions
            var filteredSources = new List<string>();
            foreach (string source in sources)
            {
                string ext = GetExtension(source);
                if (CompilerConstants.SourceExtensions.Contains(ext))
                    filteredSources.Add(source);
            }

            if (filteredSources.Count == 0)
                return new List<CompileCommand>();

            var flags = new List<string>();

            // Defines
            string? defines = FindScalar(parameters, DefineParameterNames);
            if (defines != null)
            {
                foreach (string def in defines.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = def.Trim();
                    if (trimmed.Length > 0)
                        flags.Add("/D" + trimmed);
                }
            }

            // Include directories
            string? includes = FindScalar(parameters, IncludeParameterNames);
            if (includes != null)
            {
                foreach (string inc in includes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = inc.Trim();
                    if (trimmed.Length > 0)
                        flags.Add("/I" + trimmed);
                }
            }

            // Additional options
            string? additionalOptions = FindScalar(parameters, new[] { "AdditionalOptions" });
            if (additionalOptions != null)
            {
                foreach (string opt in CommandLineTokenizer.Tokenize(additionalOptions))
                {
                    flags.Add(opt);
                }
            }

            string normalizedDir = PathNormalizer.NormalizeDirectory(directory);
            var commands = new List<CompileCommand>(filteredSources.Count);

            foreach (string source in filteredSources)
            {
                string normalizedFile = PathNormalizer.Normalize(source, directory);
                var args = new List<string>(flags.Count + 3) { "cl.exe" };
                args.AddRange(flags);
                args.Add("/c");
                args.Add(normalizedFile);
                commands.Add(new CompileCommand(normalizedDir, normalizedFile, args));
            }

            return commands;
        }

        private static List<string>? FindParameter(IDictionary<string, List<string>> parameters, string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (parameters.TryGetValue(names[i], out List<string>? values) && values.Count > 0)
                    return values;
            }
            return null;
        }

        private static string? FindScalar(IDictionary<string, List<string>> parameters, string[] names)
        {
            List<string>? values = FindParameter(parameters, names);
            if (values != null && values.Count > 0)
                return values[0];
            return null;
        }

        private static string GetExtension(string path)
        {
            try { return System.IO.Path.GetExtension(path); }
            catch (Exception) { return string.Empty; }
        }
    }
}
```

- [ ] **Step 7: Implement `TaskMapperRegistry`**

```csharp
// src/core/Extraction/TaskMapperRegistry.cs
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Models;

namespace MsBuildCompileCommands.Core.Extraction
{
    public sealed class TaskMapperRegistry
    {
        private readonly ITaskMapper[] _mappers =
        {
            new ClCompileTaskMapper(),
            new CudaCompileTaskMapper()
        };

        private readonly GenericTaskMapper _fallback = new GenericTaskMapper();

        public List<CompileCommand> TryMap(string taskName, IDictionary<string, List<string>> parameters, string directory)
        {
            for (int i = 0; i < _mappers.Length; i++)
            {
                if (_mappers[i].CanMap(taskName))
                    return _mappers[i].Map(taskName, parameters, directory);
            }
            return _fallback.Map(taskName, parameters, directory);
        }
    }
}
```

- [ ] **Step 8: Run mapper tests**

Run: `dotnet test --filter TaskMapperTests`
Expected: All tests PASS.

- [ ] **Step 9: Wire task parameter handling into `CompileCommandCollector`**

Add to `CompileCommandCollector.cs`:

```csharp
// Add these using statements:
using System.Collections;

// Add fields:
private readonly TaskMapperRegistry _taskMapperRegistry = new TaskMapperRegistry();
private readonly Dictionary<int, TaskState> _activeTasks = new Dictionary<int, TaskState>();

private sealed class TaskState
{
    public string TaskName;
    public bool HadCommandLine;
    public Dictionary<string, List<string>> Parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    public TaskState(string taskName)
    {
        TaskName = taskName;
    }
}

// Update HandleEvent to add new cases:
public void HandleEvent(BuildEventArgs e)
{
    switch (e)
    {
        case ProjectStartedEventArgs projectStarted:
            HandleProjectStarted(projectStarted);
            break;
        case TaskStartedEventArgs taskStarted:
            HandleTaskStarted(taskStarted);
            break;
        case TaskCommandLineEventArgs taskCommandLine:
            HandleTaskCommandLine(taskCommandLine);
            break;
        case TaskFinishedEventArgs taskFinished:
            HandleTaskFinished(taskFinished);
            break;
        default:
            // TaskParameterEventArgs inherits BuildMessageEventArgs
            // Check dynamically to avoid compile-time dependency on specific version
            HandlePossibleTaskParameter(e);
            break;
    }
}

private void HandleTaskStarted(TaskStartedEventArgs e)
{
    if (e.TaskName == null || e.BuildEventContext == null)
        return;

    int nodeId = e.BuildEventContext.NodeId;
    _activeTasks[nodeId] = new TaskState(e.TaskName);
}

private void HandleTaskFinished(TaskFinishedEventArgs e)
{
    if (e.BuildEventContext == null)
        return;

    int nodeId = e.BuildEventContext.NodeId;
    if (!_activeTasks.TryGetValue(nodeId, out TaskState? state))
        return;

    _activeTasks.Remove(nodeId);

    // Only synthesize if no TaskCommandLineEventArgs was seen for this task
    if (state.HadCommandLine || state.Parameters.Count == 0)
        return;

    if (!PassesFilter(e.BuildEventContext))
        return;

    string directory = ResolveDirectory(e.BuildEventContext);

    try
    {
        List<CompileCommand> commands = _taskMapperRegistry.TryMap(state.TaskName, state.Parameters, directory);
        foreach (CompileCommand cmd in commands)
        {
            // Only add if not already captured by a command-line event
            if (!_commands.ContainsKey(cmd.DeduplicationKey))
            {
                _commands[cmd.DeduplicationKey] = cmd;
            }
        }
    }
    catch (Exception ex)
    {
        _diagnostics.Add($"Error mapping task parameters for '{state.TaskName}': {ex.Message}");
    }
}

private void HandlePossibleTaskParameter(BuildEventArgs e)
{
    // TaskParameterEventArgs is available in Microsoft.Build.Framework 16.8+
    // Use type checking to handle gracefully when not available
    if (e is TaskParameterEventArgs taskParam)
    {
        HandleTaskParameter(taskParam);
    }
}

private void HandleTaskParameter(TaskParameterEventArgs e)
{
    if (e.BuildEventContext == null)
        return;

    // Only process input parameters
    if (e.Kind != TaskParameterMessageKind.TaskInput)
        return;

    int nodeId = e.BuildEventContext.NodeId;
    if (!_activeTasks.TryGetValue(nodeId, out TaskState? state))
        return;

    string paramName = e.ItemType;
    if (string.IsNullOrEmpty(paramName))
        return;

    var values = new List<string>();
    if (e.Items != null)
    {
        foreach (object? item in e.Items)
        {
            if (item is ITaskItem taskItem)
            {
                values.Add(taskItem.ItemSpec);
            }
            else if (item != null)
            {
                string? str = item.ToString();
                if (str != null)
                    values.Add(str);
            }
        }
    }

    if (values.Count > 0)
        state.Parameters[paramName] = values;
}

// Update HandleTaskCommandLine to mark the active task:
private void HandleTaskCommandLine(TaskCommandLineEventArgs e)
{
    // Mark active task as having a command line
    if (e.BuildEventContext != null)
    {
        int nodeId = e.BuildEventContext.NodeId;
        if (_activeTasks.TryGetValue(nodeId, out TaskState? state))
            state.HadCommandLine = true;
    }

    // ... rest of existing HandleTaskCommandLine logic unchanged
}
```

Note: `TaskParameterEventArgs`, `TaskParameterMessageKind`, `TaskStartedEventArgs`, `TaskFinishedEventArgs`, and `ITaskItem` are all in `Microsoft.Build.Framework` which is already referenced. Verify at build time that these types are available in the 17.11.4 package for netstandard2.0.

- [ ] **Step 10: Update logger to forward new event types**

In `src/logger/CompileCommandsLogger.cs`, update `Initialize`:

```csharp
public void Initialize(IEventSource eventSource)
{
    ParseParameters();
    _collector = new CompileCommandCollector(BuildFilter());

    eventSource.MessageRaised += OnMessageRaised;
    eventSource.ProjectStarted += OnProjectStarted;
    eventSource.BuildFinished += OnBuildFinished;
    eventSource.TaskStarted += OnTaskStarted;
    eventSource.TaskFinished += OnTaskFinished;
}

private void OnTaskStarted(object sender, TaskStartedEventArgs e)
{
    _collector?.HandleEvent(e);
}

private void OnTaskFinished(object sender, TaskFinishedEventArgs e)
{
    _collector?.HandleEvent(e);
}

// Update OnMessageRaised to also forward TaskParameterEventArgs:
private void OnMessageRaised(object sender, BuildMessageEventArgs e)
{
    // TaskCommandLineEventArgs and TaskParameterEventArgs both inherit BuildMessageEventArgs
    _collector?.HandleEvent(e);
}
```

- [ ] **Step 11: Build entire solution**

Run: `dotnet build`
Expected: Clean build. If `TaskParameterEventArgs` or related types are not available in the netstandard2.0 target of `Microsoft.Build.Framework 17.11.4`, the build will fail here. In that case, wrap the `TaskParameterEventArgs` handling in a try/catch with reflection, or conditionally compile. Check the build output.

- [ ] **Step 12: Run full test suite**

Run: `dotnet test`
Expected: All tests pass.

- [ ] **Step 13: Commit**

```bash
git add src/core/Extraction/ITaskMapper.cs src/core/Extraction/TaskMapperRegistry.cs src/core/Extraction/ClCompileTaskMapper.cs src/core/Extraction/CudaCompileTaskMapper.cs src/core/Extraction/GenericTaskMapper.cs tests/tests/TaskMapperTests.cs src/core/Extraction/CompileCommandCollector.cs src/logger/CompileCommandsLogger.cs
git commit -m "feat: add task parameter extraction with CL, CudaCompile, and generic mappers"
```

---

## Task 6: Project Evaluation Mode (`--evaluate`)

**Files:**
- Create: `src/cli/Evaluation/ClCompileItemMapper.cs`
- Create: `src/cli/Evaluation/ProjectEvaluator.cs`
- Create: `tests/tests/ClCompileItemMapperTests.cs`
- Modify: `src/cli/cli.csproj`
- Modify: `src/cli/Program.cs`

- [ ] **Step 1: Add NuGet dependencies to CLI project**

Edit `src/cli/cli.csproj`:

```xml
<ItemGroup>
    <ProjectReference Include="..\core\core.csproj" />
</ItemGroup>

<ItemGroup>
    <PackageReference Include="MSBuild.StructuredLogger" Version="2.3.154" />
    <PackageReference Include="Microsoft.Build" Version="17.11.4" ExcludeAssets="runtime" />
    <PackageReference Include="Microsoft.Build.Locator" Version="1.7.8" />
</ItemGroup>
```

- [ ] **Step 2: Write failing `ClCompileItemMapper` tests**

```csharp
// tests/tests/ClCompileItemMapperTests.cs
using System.Collections.Generic;
using Xunit;

// This test file tests the metadata-to-flags mapping logic without requiring MSBuild evaluation.
// We simulate ClCompile item metadata as a dictionary.
namespace MsBuildCompileCommands.Tests
{
    public class ClCompileItemMapperTests
    {
        [Fact]
        public void Maps_preprocessor_definitions()
        {
            var metadata = new Dictionary<string, string>
            {
                ["PreprocessorDefinitions"] = "WIN32;_DEBUG;VERSION=42"
            };

            List<string> flags = MsBuildCompileCommands.Cli.Evaluation.ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Contains("/DWIN32", flags);
            Assert.Contains("/D_DEBUG", flags);
            Assert.Contains("/DVERSION=42", flags);
        }

        [Fact]
        public void Maps_additional_include_directories()
        {
            var metadata = new Dictionary<string, string>
            {
                ["AdditionalIncludeDirectories"] = @"C:\sdk\include;C:\project\src"
            };

            List<string> flags = MsBuildCompileCommands.Cli.Evaluation.ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Contains(@"/IC:\sdk\include", flags);
            Assert.Contains(@"/IC:\project\src", flags);
        }

        [Fact]
        public void Maps_forced_include_files()
        {
            var metadata = new Dictionary<string, string>
            {
                ["ForcedIncludeFiles"] = "pch.h;config.h"
            };

            List<string> flags = MsBuildCompileCommands.Cli.Evaluation.ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Contains("/FIpch.h", flags);
            Assert.Contains("/FIconfig.h", flags);
        }

        [Fact]
        public void Maps_runtime_library()
        {
            var metadata = new Dictionary<string, string>
            {
                ["RuntimeLibrary"] = "MultiThreadedDebugDLL"
            };

            List<string> flags = MsBuildCompileCommands.Cli.Evaluation.ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Contains("/MDd", flags);
        }

        [Fact]
        public void Maps_exception_handling()
        {
            var metadata = new Dictionary<string, string>
            {
                ["ExceptionHandling"] = "Sync"
            };

            List<string> flags = MsBuildCompileCommands.Cli.Evaluation.ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Contains("/EHsc", flags);
        }

        [Fact]
        public void Maps_language_standard()
        {
            var metadata = new Dictionary<string, string>
            {
                ["LanguageStandard"] = "stdcpp17"
            };

            List<string> flags = MsBuildCompileCommands.Cli.Evaluation.ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Contains("/std:c++17", flags);
        }

        [Fact]
        public void Maps_language_standard_c()
        {
            var metadata = new Dictionary<string, string>
            {
                ["LanguageStandard_C"] = "stdc11"
            };

            List<string> flags = MsBuildCompileCommands.Cli.Evaluation.ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Contains("/std:c11", flags);
        }

        [Fact]
        public void Maps_conformance_mode()
        {
            var metadata = new Dictionary<string, string>
            {
                ["ConformanceMode"] = "true"
            };

            List<string> flags = MsBuildCompileCommands.Cli.Evaluation.ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Contains("/permissive-", flags);
        }

        [Fact]
        public void Maps_compile_as()
        {
            var metadata = new Dictionary<string, string>
            {
                ["CompileAs"] = "CompileAsCpp"
            };

            List<string> flags = MsBuildCompileCommands.Cli.Evaluation.ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Contains("/TP", flags);
        }

        [Fact]
        public void Maps_additional_options()
        {
            var metadata = new Dictionary<string, string>
            {
                ["AdditionalOptions"] = "/W4 /Zc:__cplusplus"
            };

            List<string> flags = MsBuildCompileCommands.Cli.Evaluation.ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Contains("/W4", flags);
            Assert.Contains("/Zc:__cplusplus", flags);
        }

        [Fact]
        public void Empty_metadata_returns_empty_flags()
        {
            var metadata = new Dictionary<string, string>();
            List<string> flags = MsBuildCompileCommands.Cli.Evaluation.ClCompileItemMapper.MapMetadataToFlags(metadata);
            Assert.Empty(flags);
        }

        [Fact]
        public void Ignores_inherited_value_markers()
        {
            var metadata = new Dictionary<string, string>
            {
                ["PreprocessorDefinitions"] = "FOO;%(PreprocessorDefinitions)"
            };

            List<string> flags = MsBuildCompileCommands.Cli.Evaluation.ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Contains("/DFOO", flags);
            Assert.DoesNotContain(flags, f => f.Contains("%("));
        }
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter ClCompileItemMapperTests`
Expected: Compilation error — `ClCompileItemMapper` does not exist.

- [ ] **Step 4: Implement `ClCompileItemMapper`**

```csharp
// src/cli/Evaluation/ClCompileItemMapper.cs
using System;
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Extraction;

namespace MsBuildCompileCommands.Cli.Evaluation
{
    /// <summary>
    /// Maps ClCompile item metadata to compiler flags.
    /// Separated from ProjectEvaluator for unit testability without MSBuild installation.
    /// </summary>
    public static class ClCompileItemMapper
    {
        private static readonly Dictionary<string, string> RuntimeLibraryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MultiThreaded"] = "/MT",
            ["MultiThreadedDebug"] = "/MTd",
            ["MultiThreadedDLL"] = "/MD",
            ["MultiThreadedDebugDLL"] = "/MDd"
        };

        private static readonly Dictionary<string, string> ExceptionHandlingMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sync"] = "/EHsc",
            ["Async"] = "/EHa",
            ["SyncCThrow"] = "/EHs"
        };

        private static readonly Dictionary<string, string> LanguageStandardMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stdcpplatest"] = "/std:c++latest",
            ["stdcpp23"] = "/std:c++23",
            ["stdcpp20"] = "/std:c++20",
            ["stdcpp17"] = "/std:c++17",
            ["stdcpp14"] = "/std:c++14"
        };

        private static readonly Dictionary<string, string> LanguageStandardCMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stdc17"] = "/std:c17",
            ["stdc11"] = "/std:c11"
        };

        private static readonly Dictionary<string, string> CompileAsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CompileAsC"] = "/TC",
            ["CompileAsCpp"] = "/TP"
        };

        /// <summary>
        /// Convert ClCompile item metadata dictionary into compiler flag list.
        /// </summary>
        public static List<string> MapMetadataToFlags(IDictionary<string, string> metadata)
        {
            var flags = new List<string>();

            MapSemicolonList(metadata, "PreprocessorDefinitions", "/D", flags);
            MapSemicolonList(metadata, "AdditionalIncludeDirectories", "/I", flags);
            MapSemicolonList(metadata, "ForcedIncludeFiles", "/FI", flags);

            MapLookup(metadata, "RuntimeLibrary", RuntimeLibraryMap, flags);
            MapLookup(metadata, "ExceptionHandling", ExceptionHandlingMap, flags);
            MapLookup(metadata, "LanguageStandard", LanguageStandardMap, flags);
            MapLookup(metadata, "LanguageStandard_C", LanguageStandardCMap, flags);
            MapLookup(metadata, "CompileAs", CompileAsMap, flags);

            // ConformanceMode
            if (metadata.TryGetValue("ConformanceMode", out string? conformance) &&
                string.Equals(conformance, "true", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add("/permissive-");
            }

            // TreatWChar_tAsBuiltInType
            if (metadata.TryGetValue("TreatWChar_tAsBuiltInType", out string? wchar))
            {
                if (string.Equals(wchar, "false", StringComparison.OrdinalIgnoreCase))
                    flags.Add("/Zc:wchar_t-");
            }

            // AdditionalOptions — pass through as tokenized flags
            if (metadata.TryGetValue("AdditionalOptions", out string? additionalOptions) &&
                !string.IsNullOrWhiteSpace(additionalOptions))
            {
                foreach (string opt in CommandLineTokenizer.Tokenize(additionalOptions))
                {
                    flags.Add(opt);
                }
            }

            return flags;
        }

        private static void MapSemicolonList(IDictionary<string, string> metadata, string key, string prefix, List<string> flags)
        {
            if (!metadata.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
                return;

            foreach (string item in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = item.Trim();
                // Skip MSBuild inherited-value markers like %(PreprocessorDefinitions)
                if (trimmed.Length > 0 && !trimmed.StartsWith("%(", StringComparison.Ordinal))
                    flags.Add(prefix + trimmed);
            }
        }

        private static void MapLookup(IDictionary<string, string> metadata, string key,
            Dictionary<string, string> map, List<string> flags)
        {
            if (metadata.TryGetValue(key, out string? value) &&
                value != null &&
                map.TryGetValue(value, out string? flag))
            {
                flags.Add(flag);
            }
        }
    }
}
```

- [ ] **Step 5: Run `ClCompileItemMapper` tests**

Run: `dotnet test --filter ClCompileItemMapperTests`
Expected: All tests PASS.

- [ ] **Step 6: Implement `ProjectEvaluator`**

```csharp
// src/cli/Evaluation/ProjectEvaluator.cs
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Evaluation;
using MsBuildCompileCommands.Core.Models;
using MsBuildCompileCommands.Core.Utils;

namespace MsBuildCompileCommands.Cli.Evaluation
{
    public sealed class ProjectEvaluator
    {
        private readonly List<string> _diagnostics = new List<string>();

        public IReadOnlyList<string> Diagnostics => _diagnostics;

        /// <summary>
        /// Evaluate a .vcxproj and synthesize compile commands from ClCompile items.
        /// </summary>
        public List<CompileCommand> Evaluate(string projectPath, string? configuration = null, string? platform = null)
        {
            var commands = new List<CompileCommand>();

            try
            {
                var globalProperties = new Dictionary<string, string>();
                if (configuration != null)
                    globalProperties["Configuration"] = configuration;
                if (platform != null)
                    globalProperties["Platform"] = platform;

                using (var collection = new ProjectCollection())
                {
                    var project = new Project(projectPath, globalProperties, null, collection);
                    string directory = Path.GetDirectoryName(projectPath) ?? Directory.GetCurrentDirectory();

                    // Get CLToolExe if set, otherwise default to cl.exe
                    string compiler = project.GetPropertyValue("CLToolExe");
                    if (string.IsNullOrWhiteSpace(compiler))
                        compiler = "cl.exe";

                    foreach (ProjectItem item in project.GetItems("ClCompile"))
                    {
                        string sourceFile = item.EvaluatedInclude;
                        if (string.IsNullOrWhiteSpace(sourceFile))
                            continue;

                        // Build metadata dictionary
                        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (ProjectMetadata meta in item.Metadata)
                        {
                            if (!string.IsNullOrWhiteSpace(meta.EvaluatedValue))
                                metadata[meta.Name] = meta.EvaluatedValue;
                        }

                        List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

                        string normalizedFile = PathNormalizer.Normalize(sourceFile, directory);
                        string normalizedDir = PathNormalizer.NormalizeDirectory(directory);

                        var args = new List<string>(flags.Count + 3) { compiler };
                        args.AddRange(flags);
                        args.Add("/c");
                        args.Add(normalizedFile);

                        commands.Add(new CompileCommand(normalizedDir, normalizedFile, args));
                    }
                }
            }
            catch (Exception ex)
            {
                _diagnostics.Add($"Error evaluating project '{projectPath}': {ex.Message}");
            }

            return commands;
        }
    }
}
```

- [ ] **Step 7: Update `Program.cs` with `--evaluate` flag**

Add to `Program.cs`:

```csharp
// 1. Add MSBuild locator registration at the very start of Main, before any other code:
private static int Main(string[] args)
{
    // Register MSBuild locator before any MSBuild types are used
    try
    {
        Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
    }
    catch (Exception)
    {
        // MSBuild not found — --evaluate will fail gracefully later
    }

    // ... existing argument parsing ...
}

// 2. Add evaluate flag parsing:
bool evaluate = false;

// In the argument parsing loop, add:
else if (string.Equals(arg, "--evaluate", StringComparison.OrdinalIgnoreCase))
{
    evaluate = true;
}

// 3. Pass evaluate to GenerateFromBinlog:
return GenerateFromBinlog(binlogPath, outputPath, overwrite, projectFilter, configFilter, evaluate);

// 4. Update GenerateFromBinlog signature and add evaluation logic:
private static int GenerateFromBinlog(string binlogPath, string outputPath, bool overwrite,
    string? projectFilter, string? configFilter, bool evaluate)
{
    Console.Error.WriteLine($"Reading {binlogPath}...");

    CompileCommandFilter? filter = BuildFilter(projectFilter, configFilter);
    var collector = new CompileCommandCollector(filter);

    try
    {
        var reader = new BinLogReader();
        reader.AnyEventRaised += (sender, e) => collector.HandleEvent(e);
        reader.Replay(binlogPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error reading binlog: {ex.Message}");
        return 1;
    }

    List<CompileCommand> commands = collector.GetCommands();

    // Project evaluation: fill in source files not captured by events
    if (evaluate)
    {
        var capturedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CompileCommand cmd in commands)
            capturedFiles.Add(cmd.DeduplicationKey);

        var evaluator = new MsBuildCompileCommands.Cli.Evaluation.ProjectEvaluator();
        List<string> projectPaths = collector.GetProjectPaths();

        int evalCount = 0;
        foreach (string projectPath in projectPaths)
        {
            if (!File.Exists(projectPath))
            {
                Console.Error.WriteLine($"  Warning: project file not found for evaluation: {projectPath}");
                continue;
            }

            List<CompileCommand> evalCommands = evaluator.Evaluate(
                projectPath,
                configFilter?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)[0]);

            foreach (CompileCommand cmd in evalCommands)
            {
                if (capturedFiles.Add(cmd.DeduplicationKey))
                {
                    commands.Add(cmd);
                    evalCount++;
                }
            }
        }

        if (evalCount > 0)
            Console.Error.WriteLine($"  Evaluation added {evalCount} entries not captured by build events");

        foreach (string diag in evaluator.Diagnostics)
            Console.Error.WriteLine($"  {diag}");
    }

    // ... rest of existing write logic ...
    try
    {
        string fullOutputPath = Path.GetFullPath(outputPath);
        CompileCommandsWriter.Write(fullOutputPath, commands, overwrite);
        Console.Error.WriteLine($"Wrote {commands.Count} new entries to {fullOutputPath}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error writing output: {ex.Message}");
        return 1;
    }

    foreach (string diag in collector.Diagnostics)
    {
        Console.Error.WriteLine($"  {diag}");
    }

    return 0;
}

// 5. Update PrintUsage to include --evaluate:
// Add to OPTIONS section:
//     --evaluate              After binlog replay, evaluate .vcxproj files to fill in
//                             source files not captured by build events
```

- [ ] **Step 8: Build the CLI**

Run: `dotnet build src/cli/cli.csproj`
Expected: Clean build. If `Microsoft.Build.Locator` fails to resolve, check NuGet feed access.

- [ ] **Step 9: Run full test suite**

Run: `dotnet test`
Expected: All tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/cli/Evaluation/ClCompileItemMapper.cs src/cli/Evaluation/ProjectEvaluator.cs tests/tests/ClCompileItemMapperTests.cs src/cli/cli.csproj src/cli/Program.cs
git commit -m "feat: add --evaluate mode for project file evaluation"
```

---

## Task 7: Documentation Updates

**Files:**
- Modify: `README.md`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update README.md**

Key changes:
1. **Supported compilers section** (replace lines 244-248):

```markdown
## Supported compilers

- **cl.exe** (MSVC) - primary target
- **clang-cl** - MSVC-compatible Clang driver
- **gcc / g++** - GNU Compiler Collection (including cross-compiler and versioned variants like `x86_64-linux-gnu-gcc`, `gcc-12`)
- **clang / clang++** - LLVM Clang (native mode, not clang-cl)
- **nvcc** - NVIDIA CUDA compiler (extracts host compiler flags via `-Xcompiler`/`--compiler-options`)
```

2. **Limitations section** (replace lines 249-256):

```markdown
## Limitations

- The first build must be a clean build to populate `compile_commands.json`; subsequent incremental builds automatically merge new entries and prune deleted files
- Response file expansion requires the response files to exist on disk at parse time; a warning is emitted when a response file cannot be read (common when replaying `.binlog` files after the build's temporary files have been cleaned up)
- PCH: `/Yc` (create) is stripped and `/Yu` (use) is converted to `/FI` (forced include) so clangd sees the implicit PCH header; the `.pch` file itself is not used
- Custom MSBuild tasks that invoke compilers via `System.Diagnostics.Process` without logging and without standard task parameters are not captured by event-based collection; use `--evaluate` to extract flags from project files as a fallback
- Project evaluation (`--evaluate`) requires MSBuild/Visual Studio to be installed and only works with the CLI tool
- Path normalization assumes Windows drive-letter paths
- Generated source files are captured if they appear in the compiler command line, but the files must exist for clangd to use them
```

3. **CLI options section** — add `--evaluate` after `--configuration`:

```markdown
- `--evaluate` - After binlog replay, evaluate `.vcxproj` files to fill in source files not captured by build events (requires MSBuild installation)
```

4. **What gets captured section** — add at the end:

```markdown
- Source files and flags from custom MSBuild task parameters (when no command-line event is logged)
- ClCompile items from project files via `--evaluate` mode
```

5. **Architecture section** — update data flow:

```markdown
## Architecture

```
MsBuildCompileCommands.Core (netstandard2.0)
  Models/CompileCommand           Compile entry model
  Models/CompileCommandFilter     Project/configuration filter
  Extraction/ICommandParser       Parser interface
  Extraction/CommandParserFactory  Selects parser by compiler detection
  Extraction/MsvcCommandParser    cl.exe/clang-cl argument parser
  Extraction/GccClangCommandParser gcc/g++/clang/clang++ argument parser
  Extraction/NvccCommandParser    nvcc argument parser (host flag extraction)
  Extraction/CommandLineTokenizer Windows command line tokenizer
  Extraction/CompileCommandCollector  MSBuild event processor
  Extraction/ITaskMapper          Task parameter → CompileCommand mapper interface
  Extraction/TaskMapperRegistry   Dispatches to known + fallback mappers
  Extraction/ClCompileTaskMapper  Maps CL task parameters
  Extraction/CudaCompileTaskMapper Maps CudaCompile task parameters
  Extraction/GenericTaskMapper    Best-effort fallback for unknown tasks
  IO/ResponseFileParser           @response-file expansion
  IO/CompileCommandsWriter        JSON output
  Utils/PathNormalizer            Path normalization

MsBuildCompileCommands (netstandard2.0, logger)
  Logger                          ILogger implementation for live builds

MsBuildCompileCommands (net8.0, CLI)
  Program                         Binlog replay CLI
  Evaluation/ProjectEvaluator     .vcxproj evaluation via MSBuild API
  Evaluation/ClCompileItemMapper  ClCompile metadata → flags mapping
```
```

6. **Roadmap** — update:

```markdown
## Roadmap

- [ ] Custom flag translation rules
- [ ] ETW-based process tracing for compilers invoked without MSBuild events
- [ ] Linux/macOS support for offline binlog parsing
```

- [ ] **Step 2: Update CLAUDE.md**

Key changes:

1. **Data flow** — update:

```markdown
### Data flow

```
MSBuild events → CompileCommandCollector → CommandParserFactory → [Msvc|GccClang|Nvcc]Parser → CompileCommand[] → CompileCommandsWriter → JSON
                                         → TaskMapperRegistry → [ClCompile|CudaCompile|Generic]Mapper ↗ (fallback when no command-line event)
```
```

2. **Key conventions** — add:

```markdown
- `ICommandParser` is the parser interface; `CommandParserFactory` dispatches by compiler detection
- Parser priority: nvcc → MSVC (clang-cl) → GCC/Clang
- Task parameter extraction synthesizes commands when `TaskCommandLineEventArgs` is absent; event-captured commands take precedence
- `--evaluate` mode lives in CLI only (requires `Microsoft.Build`, not available in core/logger)
- `ClCommandParser` is an `[Obsolete]` wrapper around `MsvcCommandParser` for backward compatibility
```

- [ ] **Step 3: Build and test**

Run: `dotnet build && dotnet test`
Expected: Clean build, all tests pass.

- [ ] **Step 4: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: update README and CLAUDE.md for multi-compiler and evaluation support"
```

---

## Summary

| Task | What it delivers |
|------|-----------------|
| 1 | `ICommandParser` interface, `CompilerConstants`, `MsvcCommandParser`, `ClCommandParser` backward-compat wrapper |
| 2 | `GccClangCommandParser` with cross-compiler/versioned detection |
| 3 | `NvccCommandParser` with `-Xcompiler` host flag extraction |
| 4 | `CommandParserFactory` + `CompileCommandCollector` update to use factory |
| 5 | Task parameter extraction: `ITaskMapper`, registry, CL/CudaCompile/Generic mappers, collector integration |
| 6 | `--evaluate` CLI flag: `ProjectEvaluator`, `ClCompileItemMapper`, CLI wiring |
| 7 | README + CLAUDE.md documentation updates |

Tasks 1-4 can be parallelized with subagent-driven development (they're independent new parsers). Tasks 5-7 are sequential.
