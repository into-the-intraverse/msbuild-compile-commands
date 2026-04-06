# Multi-Compiler Support, Task Parameter Extraction, and Project Evaluation

**Date:** 2026-04-06
**Status:** Draft

## Problem

The tool currently only captures compiler invocations from `TaskCommandLineEventArgs` and only recognizes `cl.exe` / `clang-cl`. This means:

1. **Non-MSVC compilers** (gcc, g++, clang, clang++, nvcc) are invisible even when their command lines are properly logged by MSBuild tasks.
2. **Custom MSBuild tasks** that invoke compilers via `System.Diagnostics.Process` without calling `LogCommandLine()` produce no `TaskCommandLineEventArgs` — their compilations are silently missed.
3. **Projects with standard `ClCompile` items** but non-standard build tasks have all their flag information in the `.vcxproj` file, but the tool has no way to read it.

## Goals

- Support gcc, g++, cc, c++, clang, clang++, and nvcc compiler families
- Extract compile commands from MSBuild task parameters when command-line events are absent
- Add `--evaluate` CLI flag to read `ClCompile` items directly from `.vcxproj` files
- Update README limitations and CLAUDE.md to reflect new capabilities

## Non-Goals

- ETW-based process tracing (remains a stated limitation)
- Full nvcc flag translation to clang equivalents (extract host compiler flags via `-Xcompiler` only)
- Linux/macOS project evaluation (requires MSBuild to be installed)

---

## Design

### 1. Multi-Compiler Command Parser Architecture

**Current state:** `ClCommandParser` is a monolithic class handling MSVC-specific detection, tokenization, flag classification, and PCH logic.

**New design:** Introduce a parser abstraction with compiler-family-specific implementations.

```
ICommandParser
    bool IsCompilerInvocation(string commandLine)
    Parse(string commandLine, string directory, IList<string>? diagnostics) -> List<CompileCommand>

MsvcCommandParser        — existing ClCommandParser logic (cl.exe, clang-cl)
GccClangCommandParser    — gcc, g++, cc, c++, clang, clang++
NvccCommandParser        — nvcc (CUDA)

CommandParserFactory
    static ICommandParser? FindParser(string commandLine)
    — returns the first parser whose IsCompilerInvocation returns true
    — checks in order: Nvcc, GccClang, Msvc (most specific first)
```

All parsers share `CommandLineTokenizer` and `ResponseFileParser` (GCC/Clang also support `@file` response files).

#### 1a. `ICommandParser` Interface

New file: `src/core/Extraction/ICommandParser.cs`

```csharp
public interface ICommandParser
{
    bool IsCompilerInvocation(string commandLine);
    List<CompileCommand> Parse(string commandLine, string directory, IList<string>? diagnostics = null);
}
```

#### 1b. `MsvcCommandParser`

Rename `ClCommandParser` to `MsvcCommandParser`, implement `ICommandParser`. No logic changes — this is a pure rename + interface implementation.

Keep `ClCommandParser` as a thin obsolete wrapper that delegates to `MsvcCommandParser` to avoid breaking the logger (which embeds core sources and may be referenced externally). Mark it `[Obsolete]`.

#### 1c. `GccClangCommandParser`

New file: `src/core/Extraction/GccClangCommandParser.cs`

**Compiler names recognized:**
```
gcc, gcc.exe, g++, g++.exe,
cc, cc.exe, c++, c++.exe,
clang, clang.exe, clang++, clang++.exe
```

Also match versioned variants via suffix pattern: `gcc-12`, `clang++-17`, `x86_64-linux-gnu-gcc`, etc. Detection rule: extract the filename from the last path component (after last `/` or `\`), strip the `.exe` extension if present, then strip a trailing `-<one-or-more-digits>` suffix (e.g., `clang++-17` becomes `clang++`). The result must be in the name set above. Cross-compiler prefixes like `x86_64-linux-gnu-` are handled by also checking the substring after the last `-` that isn't a version number.

**Flag handling differences from MSVC:**

| Category | MSVC | GCC/Clang |
|----------|------|-----------|
| Output file | `/Fo` | `-o` |
| Dependency files | n/a | `-MF`, `-MT`, `-MQ`, `-MP`, `-MMD`, `-MD`, `-M`, `-MM` |
| PCH create | `/Yc` | n/a (different mechanism, not translated) |
| PCH use | `/Yu` -> `/FI` | n/a |
| Include dirs | `/I`, `/external:I` | `-I`, `-isystem`, `-iquote`, `-idirafter` |
| Defines | `/D` | `-D` |
| Forced include | `/FI` | `-include` |
| Cosmetic | `/nologo`, `/showIncludes` | n/a |

**Excluded flags** (not useful for clangd):
- Output: `-o` (and its value)
- Dependency generation: `-MF`, `-MT`, `-MQ`, `-MP`, `-MMD`, `-MD`, `-M`, `-MM`
- Debug info format: `-g` variants are kept (clangd uses them)
- Linking: `-l`, `-L` (shouldn't appear in compile-only commands, but filter if present)

**Kept flags** (all others pass through):
- `-std=c++17`, `-Wall`, `-Wextra`, `-Werror`, `-O2`, etc.
- `-I`, `-D`, `-U`, `-include`, `-isystem`, `-iquote`
- `-f*` flags (e.g., `-fPIC`, `-fno-exceptions`)

**Source extensions:** Same set as MSVC plus `.cu` (shared via constant).

**Compile flag appended:** `-c` (same semantic as MSVC `/c`).

#### 1d. `NvccCommandParser`

New file: `src/core/Extraction/NvccCommandParser.cs`

**Compiler names recognized:**
```
nvcc, nvcc.exe
```

**Strategy:** Extract flags that clangd can use for `.cu` files. nvcc is a compiler driver that passes host compiler flags via `-Xcompiler` / `--compiler-options`. The approach:

1. Collect all `-I`, `-D`, `-U` flags (these are passed to both host and device compilers)
2. Extract host compiler flags from `-Xcompiler <flags>` and `--compiler-options <flags>` (comma-separated list)
3. Collect `-std=c++*` / `--std c++*`
4. Discard GPU-specific flags (`--gpu-architecture`, `-gencode`, `--relocatable-device-code`, `--gpu-code`, `-arch`, `-code`)
5. Discard output flags (`-o`, `-odir`)
6. Discard dependency flags (`-M`, `-MD`, `-MF`, etc.)

**Source extensions:** `.cu`, `.cuh` added to source extension set.

**Compile flag appended:** `-c`

**Compiler in output:** Use `nvcc` as the compiler in arguments[0] (clangd will see it's not clang/gcc but the flags will still be useful for includes/defines).

#### 1e. `CommandParserFactory`

New file: `src/core/Extraction/CommandParserFactory.cs`

```csharp
public static class CommandParserFactory
{
    private static readonly ICommandParser[] Parsers = {
        new NvccCommandParser(),
        new MsvcCommandParser(),
        new GccClangCommandParser()
    };

    public static ICommandParser? FindParser(string commandLine)
    {
        foreach (var parser in Parsers)
            if (parser.IsCompilerInvocation(commandLine))
                return parser;
        return null;
    }
}
```

Order matters: `nvcc` first (most specific), then `Msvc` (so `clang-cl` matches before bare `clang` in `GccClang`), then `GccClang` last. Note: `clang-cl` is not in `GccClangCommandParser`'s name set, so there's no false match — but checking Msvc first is still safer for edge cases like paths containing "clang-cl".

#### 1f. `CompileCommandCollector` Changes

Replace the direct `ClCommandParser` dependency:

```csharp
// Before:
private readonly ClCommandParser _parser;
if (!ClCommandParser.IsCompilerInvocation(commandLine)) return;
List<CompileCommand> commands = _parser.Parse(commandLine, directory, _diagnostics);

// After:
ICommandParser? parser = CommandParserFactory.FindParser(commandLine);
if (parser == null) return;
List<CompileCommand> commands = parser.Parse(commandLine, directory, _diagnostics);
```

The collector no longer owns a parser instance — it asks the factory per command line. This is cheap (string scan) and allows mixed-compiler builds to work correctly.

Constructor changes: Remove `ClCommandParser` parameter. Add a `ResponseFileParser` parameter that gets shared across all parsers (or let each parser create its own — simpler, negligible cost).

For backward compatibility, keep the `ClCommandParser`-accepting constructor as `[Obsolete]` delegating to the new one.

#### 1g. Shared Constants

New file: `src/core/Extraction/CompilerConstants.cs`

```csharp
internal static class CompilerConstants
{
    public static readonly HashSet<string> SourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cc", ".cpp", ".cxx", ".c++", ".cp", ".ixx", ".cppm",
        ".cu"   // CUDA
    };
}
```

All parsers reference this shared set instead of each maintaining their own copy.

---

### 2. Task Parameter Extraction

**Motivation:** Some custom MSBuild tasks invoke compilers without firing `TaskCommandLineEventArgs`. However, the binlog records task input parameters via `TaskParameterEventArgs` (available in MSBuild 16.6+ / `Microsoft.Build.Framework` 16.6+). We can reconstruct compile commands from these parameters.

#### 2a. Event Handling

`TaskParameterEventArgs` fires for each task input parameter. The binlog also contains `TaskStartedEventArgs` (task name, project file) and `TaskFinishedEventArgs`.

New handler in `CompileCommandCollector`:

```csharp
case TaskStartedEventArgs taskStarted:
    HandleTaskStarted(taskStarted);
    break;
case TaskFinishedEventArgs taskFinished:
    HandleTaskFinished(taskFinished);
    break;
```

The collector tracks the "current task" per build context and accumulates parameters.

#### 2b. `ITaskMapper` Interface

New file: `src/core/Extraction/ITaskMapper.cs`

```csharp
public interface ITaskMapper
{
    bool CanMap(string taskName);
    List<CompileCommand> Map(string taskName, IDictionary<string, string> parameters, string directory);
}
```

#### 2c. Known Task Mappers

**`CudaCompileMapper`** — handles NVIDIA's `CudaCompile` MSBuild task:
- `Sources` -> source files
- `AdditionalIncludeDirectories` -> `-I` flags
- `Defines` -> `-D` flags
- `AdditionalOptions` -> pass through
- `CompilerPath` or known nvcc location -> compiler

**`ClCompileMapper`** (generic) — handles any task that has `ClCompile`-shaped parameters:
- `Sources` -> source files
- `PreprocessorDefinitions` -> `/D` flags
- `AdditionalIncludeDirectories` -> `/I` flags
- `AdditionalOptions` -> pass through
- `RuntimeLibrary` -> `/MT`, `/MD`, etc.
- `ExceptionHandling` -> `/EHsc`, `/EHa`, etc.
- `LanguageStandard` -> `/std:c++17`, etc.

This mapper covers custom `ToolTask` subclasses that read standard `ClCompile` metadata.

#### 2d. Generic Fallback Mapper

**`GenericTaskMapper`** — best-effort for unknown tasks:
- Looks for parameters named `Sources`, `SourceFiles`, `InputFiles`, or `Inputs`
- Looks for `AdditionalIncludeDirectories`, `IncludePaths`, `Includes`
- Looks for `PreprocessorDefinitions`, `Defines`
- Looks for `AdditionalOptions`
- Synthesizes a minimal compile command with just includes and defines

#### 2e. `TaskMapperRegistry`

```csharp
public sealed class TaskMapperRegistry
{
    private readonly List<ITaskMapper> _mappers = new List<ITaskMapper>
    {
        new CudaCompileMapper(),
        new ClCompileMapper()
    };
    private readonly GenericTaskMapper _fallback = new GenericTaskMapper();

    public List<CompileCommand> TryMap(string taskName, IDictionary<string, string> parameters, string directory)
    {
        foreach (var mapper in _mappers)
            if (mapper.CanMap(taskName))
                return mapper.Map(taskName, parameters, directory);
        return _fallback.Map(taskName, parameters, directory);
    }
}
```

#### 2f. Priority

Commands from `TaskCommandLineEventArgs` (approach 1) always take priority over task-parameter-synthesized commands (approach 2). The collector applies synthesized commands only for source files not already captured by command-line events. This is enforced by the existing last-wins deduplication — command-line events fire after task parameters, so they naturally win.

If a source file appears in both a command-line event and a task parameter event, the command-line version is kept.

#### 2g. Availability Concern

`TaskParameterEventArgs` was introduced in MSBuild 16.6 (VS 2019 16.6). The core library targets `netstandard2.0` with `Microsoft.Build.Framework 17.11.4`, which includes this type. However, the binlog must have been produced by MSBuild 16.6+ to contain these events. Older binlogs will simply have no task parameter events — graceful degradation.

The structured logger library (`MSBuild.StructuredLogger`) used by the CLI can read these events from binlogs. The live logger receives them via `eventSource.AnyEventRaised` or specific subscriptions.

---

### 3. Project Evaluation Mode (`--evaluate`)

**Motivation:** When custom tasks neither log command lines nor expose standard parameters, the compilation flags may still be available as `ClCompile` item metadata in the `.vcxproj` file. Direct evaluation extracts these.

#### 3a. CLI Flag

New CLI option: `--evaluate`

```
MsBuildCompileCommands build.binlog --evaluate
```

Behavior: After binlog replay (which captures what it can from events), evaluate each `.vcxproj` seen in the binlog to fill in source files not already captured.

#### 3b. Architecture Constraint

Project evaluation requires `Microsoft.Build` (the full evaluation engine, not just `Framework`). This is a heavy dependency (~20 MB of assemblies) and only works on .NET 6+.

**Decision:** Project evaluation lives in the CLI project (`net8.0`), not in core (`netstandard2.0`). This preserves the logger's compatibility with .NET Framework MSBuild.

New class: `src/cli/Evaluation/ProjectEvaluator.cs`

#### 3c. `ProjectEvaluator` Design

```csharp
public sealed class ProjectEvaluator
{
    public List<CompileCommand> Evaluate(
        string projectPath,
        string? configuration = null,
        string? platform = null)
    {
        // 1. Load project with Microsoft.Build.Evaluation.Project
        // 2. Set global properties (Configuration, Platform) if provided
        // 3. Get all ClCompile items
        // 4. For each item, extract metadata and synthesize compile command
        // 5. Return list
    }
}
```

**Metadata extracted from `ClCompile` items:**

| Metadata | Maps to |
|----------|---------|
| `Identity` (item spec) | Source file path |
| `PreprocessorDefinitions` | `/D` flags (semicolon-separated) |
| `AdditionalIncludeDirectories` | `/I` flags (semicolon-separated) |
| `ForcedIncludeFiles` | `/FI` flags (semicolon-separated) |
| `LanguageStandard` / `LanguageStandard_C` | `/std:c++17`, `/std:c11`, etc. |
| `RuntimeLibrary` | `/MT`, `/MTd`, `/MD`, `/MDd` |
| `ExceptionHandling` | `/EHsc`, `/EHa`, etc. |
| `BasicRuntimeChecks` | `/RTC1`, `/RTCs`, `/RTCu` |
| `AdditionalOptions` | Pass through as-is (tokenized) |
| `CompileAs` | `/TC` or `/TP` |
| `TreatWChar_tAsBuiltInType` | `/Zc:wchar_t` or `/Zc:wchar_t-` |
| `ConformanceMode` | `/permissive-` |

**Compiler selection:** Use `$(CLToolExe)` property if set, otherwise default to `cl.exe`.

#### 3d. Integration with CLI

In `Program.cs`:

```csharp
if (evaluate)
{
    var evaluator = new ProjectEvaluator();
    var capturedFiles = new HashSet<string>(
        commands.Select(c => c.DeduplicationKey),
        StringComparer.OrdinalIgnoreCase);

    foreach (string projectPath in collector.GetProjectPaths())
    {
        var evalCommands = evaluator.Evaluate(projectPath, configFilter);
        foreach (var cmd in evalCommands)
        {
            if (!capturedFiles.Contains(cmd.DeduplicationKey))
            {
                commands.Add(cmd);
                capturedFiles.Add(cmd.DeduplicationKey);
            }
        }
    }
}
```

#### 3e. `CompileCommandCollector` Change

Expose project file paths collected during binlog replay:

```csharp
public IEnumerable<string> GetProjectPaths()
```

Returns distinct `.vcxproj` paths from `ProjectStartedEventArgs`. Filters to only C++ projects (`.vcxproj` extension).

#### 3f. Logger Integration

The `--evaluate` flag is **CLI-only**. The live logger does not support it because:
- The logger runs inside MSBuild, which already has the project loaded — there's no need to re-evaluate
- The logger's purpose is to capture commands from the build as it happens

#### 3g. NuGet Dependency

Add `Microsoft.Build` and `Microsoft.Build.Locator` to `cli.csproj`:

```xml
<PackageReference Include="Microsoft.Build" Version="17.11.4" ExcludeAssets="runtime" />
<PackageReference Include="Microsoft.Build.Locator" Version="1.7.8" />
```

`Microsoft.Build.Locator` finds the installed MSBuild/VS instance at runtime. `ExcludeAssets="runtime"` avoids shipping MSBuild DLLs — they come from the installed VS/SDK.

---

### 4. Documentation Updates

#### 4a. README Changes

**Supported compilers section** — replace current content:

```markdown
## Supported compilers

- **cl.exe** (MSVC) - primary target
- **clang-cl** - MSVC-compatible Clang driver
- **gcc / g++** - GNU Compiler Collection
- **clang / clang++** - LLVM Clang (native mode)
- **nvcc** - NVIDIA CUDA compiler (extracts host compiler flags via -Xcompiler)
```

**Limitations section** — update:
- Remove: "Does not handle custom MSBuild tasks that invoke compilers through non-standard mechanisms"
- Remove: "Only captures compilation commands (cl.exe / clang-cl)"
- Add: "Custom MSBuild tasks that invoke compilers via `System.Diagnostics.Process` without logging are not captured by event-based collection; use `--evaluate` to extract flags from project files as a fallback"
- Add: "Project evaluation (`--evaluate`) requires MSBuild/Visual Studio to be installed and only works with the CLI tool"

**CLI options section** — add `--evaluate`:

```markdown
- `--evaluate` - After binlog replay, evaluate .vcxproj files to fill in source files not captured by build events
```

**Roadmap** — update:
- Remove: "Support for CUDA nvcc through MSBuild" (done)
- Add: "ETW-based process tracing for compilers invoked without MSBuild events"

#### 4b. CLAUDE.md Changes

**Architecture section** — update data flow:

```
MSBuild events → CompileCommandCollector → CommandParserFactory → [Msvc|GccClang|Nvcc]Parser → CompileCommand[] → CompileCommandsWriter → JSON
```

**Supported compilers** — add note about multi-compiler support.

**Key conventions** — add:
- `ICommandParser` is the parser interface; `CommandParserFactory` dispatches by compiler detection
- Task parameter extraction synthesizes commands when `TaskCommandLineEventArgs` is absent
- `--evaluate` mode lives in CLI only (requires `Microsoft.Build`, not available in core/logger)

---

## Acceptance Criteria

1. **Multi-compiler detection:** `gcc main.cpp`, `g++ -std=c++17 main.cpp`, `clang++ main.cpp`, and `nvcc main.cu` command lines are correctly parsed into `CompileCommand` entries
2. **GCC/Clang flag handling:** `-o`, `-MF`, `-MD`, `-MMD` are excluded; `-I`, `-D`, `-std=`, `-W*`, `-f*` are kept
3. **nvcc host flag extraction:** `-Xcompiler "-O2,-Wall"` is expanded into individual flags in the output
4. **Task parameter extraction:** A custom task with `Sources` and `AdditionalIncludeDirectories` parameters produces compile commands even without `TaskCommandLineEventArgs`
5. **CudaCompile mapper:** NVIDIA's `CudaCompile` task parameters are correctly mapped
6. **Generic fallback:** Unknown tasks with `Sources`-like parameters produce best-effort entries
7. **Priority:** Event-captured commands take precedence over task-parameter-synthesized commands
8. **Project evaluation:** `--evaluate` reads `ClCompile` items from `.vcxproj` and synthesizes commands for uncaptured source files
9. **Evaluation gap-fill only:** Evaluated entries do not overwrite event-captured entries
10. **Backward compatibility:** Existing `ClCommandParser` usage (by external consumers or the logger) continues to work via obsolete wrapper
11. **Tests:** All new parsers, mappers, and evaluation logic have unit tests
12. **README and CLAUDE.md:** Updated to reflect new capabilities and revised limitations

## Remaining Limitation

Custom MSBuild tasks that invoke compilers via `System.Diagnostics.Process` directly, without logging command lines, without exposing standard task parameters, and without using standard `ClCompile` items in the project file — these remain uncapturable. This would require ETW-based process tracing, which is out of scope.
