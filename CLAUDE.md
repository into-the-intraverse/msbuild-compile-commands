# CLAUDE.md

## Project

MsBuildCompileCommands generates `compile_commands.json` from MSBuild-based C/C++ builds for clangd and clang tooling.

## Build & test

```bash
dotnet build              # Debug build
dotnet test               # Run all tests
dotnet build -c Release   # Release build
rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj bin/ obj/  # Clean all outputs
```

Output goes to `bin/<project>/` (configured in `Directory.Build.props`).

CI runs Release build + test on dotnet 8.0/9.0 matrix (see `.github/workflows/ci.yml`).

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
MSBuild events → CompileCommandCollector → CommandParserFactory → [Msvc|GccClang|Nvcc]Parser → CompileCommand[] → FlagTranslator → CompileCommandsWriter → JSON
                                         → TaskMapperRegistry → [ClCompile|CudaCompile|Generic]Mapper ↗ (fallback when no command-line event)
```

- `CompileCommandCollector` routes MSBuild events, tracks project context, applies optional `CompileCommandFilter`, applies optional `FlagTranslator`
- `ClCommandParser` tokenizes command lines, expands @response files, separates flags from source files
- `FlagTranslator` translates compiler flags using `TranslationRule` list (built-in MSVC→clang rules by default)
- `CompileCommandsWriter` merges with existing JSON (by default), prunes deleted files, writes sorted output

### Key conventions

- `CompileCommand` uses the `arguments` form (string array), not `command` form (single string)
- `CompileCommand.ParserKind` tags each entry with its source compiler (Msvc, GccClang, Nvcc, Unknown) — internal only, not serialized to JSON
- Deduplication is by normalized file path (case-insensitive, last-wins)
- Writer always merges by default; `overwrite=true` for clean-slate mode
- Paths normalized to forward slashes with uppercase drive letters
- `ICommandParser` is the parser interface; `CommandParserFactory` dispatches by compiler detection
- Parser priority: nvcc → MSVC (clang-cl) → GCC/Clang
- Task parameter extraction synthesizes commands when `TaskCommandLineEventArgs` is absent; event-captured commands take precedence
- `--evaluate` mode lives in CLI only (requires `Microsoft.Build`, not available in core/logger)
- `ClCommandParser` is an `[Obsolete]` wrapper around `MsvcCommandParser` for backward compatibility

### Flag translation

- Built-in MSVC→clang translation is **on by default**; disable with `--no-translate` (CLI) or `translate=false` (logger)
- `TranslationRule` has four fields: `When` (ParserKind?), `From` (string), `To` (string?), `Prefix` (bool)
- `TranslationRule.MsvcBuiltins()` returns the 29 built-in rules
- `FlagTranslator` runs rules in the collector after parse/map, before dedup; index 0 (compiler) and last index (source file) are never translated
- Exact match: replaces flag, splits `To` on spaces for multi-arg (e.g., `/W4` → `-Wall -Wextra`)
- Prefix match: replaces prefix, preserves suffix (e.g., `/DFOO` → `-DFOO`); trailing space in `To` splits into separate args (e.g., `/FIfile.h` → `-include`, `file.h`)
- `To == null` drops the flag
- Rules are scoped by `ParserKind`; MSVC rules don't fire on GCC/Clang commands
- Providing a custom rules file (`--flag-rules`) **replaces all built-ins** (no merging)
- `TranslationRuleLoader` handles JSON load/serialize; `--dump-rules` prints built-ins as JSON
- Untranslated flags (optimization, codegen, debug info, runtime library) pass through unchanged — they don't affect clangd

## Packaging

- **NuGet logger**: `dotnet pack src/logger/logger.csproj` — ships DLLs in `build/` folder (not `lib/`)
- **dotnet tool**: `dotnet pack src/cli/cli.csproj` — installs as `msbuild-compile-commands`
- **Conan recipe**: `conan/conanfile.py`
- Version set in `Directory.Build.props`, CLI reads from assembly metadata
- Run `/pack` for the full packaging workflow (tests, NuGet, Conan, artifact summary)

## Style

- netstandard2.0 code avoids C# features unavailable there (no `IReadOnlySet<T>`, no `Split(char)` overload, etc.)
- `TreatWarningsAsErrors` is enabled globally
- Tests use `[Fact]` and `[Theory]` with temp directories for I/O tests
