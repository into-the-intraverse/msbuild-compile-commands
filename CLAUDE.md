# CLAUDE.md

## Project

MsBuildCompileCommands generates `compile_commands.json` from MSBuild-based C/C++ builds for clangd and clang tooling.

## Build & test

```bash
dotnet build              # Debug build
dotnet test               # Run all tests
dotnet build -c Release   # Release build
```

Output goes to `bin/<project>/` (configured in `Directory.Build.props`).

## Architecture

Three projects, one solution (`MsBuildCompileCommands.slnx`):

| Project | Target | Purpose |
|---------|--------|---------|
| `src/core/` | netstandard2.0 | Extraction logic: tokenizer, parser, collector, writer |
| `src/logger/` | netstandard2.0 | MSBuild `ILogger` — attaches to live builds |
| `src/cli/` | net8.0 | Binlog replay CLI |

Tests in `tests/tests/` (xunit).

### Data flow

```
MSBuild events → CompileCommandCollector → ClCommandParser → CompileCommand[] → CompileCommandsWriter → JSON
```

- `CompileCommandCollector` routes MSBuild events, tracks project context, applies optional `CompileCommandFilter`
- `ClCommandParser` tokenizes command lines, expands @response files, separates flags from source files
- `CompileCommandsWriter` merges with existing JSON (by default), prunes deleted files, writes sorted output

### Key conventions

- `CompileCommand` uses the `arguments` form (string array), not `command` form (single string)
- Deduplication is by normalized file path (case-insensitive, last-wins)
- Writer always merges by default; `overwrite=true` for clean-slate mode
- Paths normalized to forward slashes with uppercase drive letters

## Packaging

- **NuGet logger**: `dotnet pack src/logger/logger.csproj` — ships DLLs in `build/` folder (not `lib/`)
- **dotnet tool**: `dotnet pack src/cli/cli.csproj` — installs as `msbuild-compile-commands`
- **Conan recipe**: `conan/conanfile.py`
- Version set in `Directory.Build.props`, CLI reads from assembly metadata

## Style

- netstandard2.0 code avoids C# features unavailable there (no `IReadOnlySet<T>`, no `Split(char)` overload, etc.)
- `TreatWarningsAsErrors` is enabled globally
- Tests use `[Fact]` and `[Theory]` with temp directories for I/O tests
