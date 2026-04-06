# Custom Flag Translation Rules — Design Spec

## Problem

`compile_commands.json` generated from MSVC-based builds contains MSVC-native flags (`/EHsc`, `/std:c++20`, `/DFOO`) that clangd doesn't understand. Users must manually translate flags or live with degraded diagnostics. NVCC output has similar issues with GPU-specific flags.

## Goals

- Built-in MSVC→clang flag translation that makes clangd work out of the box
- User-defined rules for arbitrary flag replacement/removal
- Rules scoped by source compiler (MSVC, GCC/Clang, NVCC)
- Config via file, CLI argument, and MSBuild property
- Ship built-in rules as extractable templates for user customization

## Non-Goals

- Regex or glob pattern matching in rules
- Translating optimization, code generation, debug info, or linker flags
- Automatic clangd detection

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Pipeline location | Post-parse in collector | Centralized; works for both command-line and task-mapper paths |
| Rule matching | Exact match + prefix match | Simple, predictable, covers value-carrying flags like `/DFOO` |
| Override semantics | Config file replaces all built-ins | Clean slate; users copy template and edit, no merge confusion |
| Default behavior | Built-in rules ON | Core value prop is clangd compatibility; opt-out via `--no-translate` |
| Parser scoping | Rules declare which parser they target | Prevents accidental cross-compiler flag mangling |

## Data Model

### ParserKind Enum

Added to `CompileCommand` to identify the source compiler:

```csharp
public enum ParserKind { Unknown, Msvc, GccClang, Nvcc }
```

Set by each `ICommandParser` implementation and each task mapper when constructing `CompileCommand`.

### TranslationRule

```csharp
public sealed class TranslationRule
{
    public ParserKind? When { get; }   // null = applies to all parsers
    public string From { get; }        // flag to match
    public string? To { get; }         // replacement (null = drop flag)
    public bool Prefix { get; }        // true = match start of argument, preserve suffix
}
```

### JSON Schema

```json
[
  { "when": "msvc", "from": "/EHsc", "to": "-fexceptions" },
  { "when": "msvc", "from": "/D", "to": "-D", "prefix": true },
  { "when": "nvcc", "from": "--expt-relaxed-constexpr", "to": null }
]
```

Fields:
- `when` — `"msvc"`, `"gcc-clang"`, `"nvcc"`, or omitted for all parsers
- `from` — exact flag or flag prefix to match
- `to` — replacement flag(s), space-separated for multi-arg expansion; `null` to drop
- `prefix` — optional, default `false`; when `true`, matches start of argument and preserves the suffix (e.g., `/DFOO` with `from: "/D", to: "-D", prefix: true` produces `-DFOO`)

## FlagTranslator

```csharp
public sealed class FlagTranslator
{
    public FlagTranslator(IReadOnlyList<TranslationRule> rules);
    public CompileCommand Translate(CompileCommand command);
}
```

### Translation Algorithm

For each argument in `command.Arguments` (skipping index 0 — compiler executable — and the final element — source file):

1. Find the first rule where:
   - `rule.When` is null OR matches `command.ParserKind`
   - `rule.Prefix == false` AND argument equals `rule.From` exactly, OR
   - `rule.Prefix == true` AND argument starts with `rule.From`
2. If matched:
   - `rule.To == null` → drop the argument
   - `rule.Prefix == false` → replace with `rule.To` (split on spaces for multi-arg expansion)
   - `rule.Prefix == true` AND `rule.To` does NOT end with a space → concatenate `rule.To` + suffix → single argument (e.g., `/DFOO` with `from: "/D", to: "-D"` → `-DFOO`)
   - `rule.Prefix == true` AND `rule.To` ends with a space → `rule.To` (trimmed) and suffix become separate arguments (e.g., `/FIfile.h` with `from: "/FI", to: "-include "` → two args: `-include`, `file.h`)
3. No match → pass through unchanged

**Rule ordering:** Rules are matched in declaration order — first match wins. Exact-match rules are unambiguous (distinct strings), but prefix rules can overlap. If a user defines both a prefix rule for `/D` and an exact rule for `/DSPECIAL`, the exact rule must come first to avoid the prefix rule consuming it. The built-in rules have no ordering conflicts; user-defined rules inherit this responsibility.

Returns a new `CompileCommand` with translated arguments, preserving `Directory`, `File`, and `ParserKind`.

## Built-in Rules

Static method `TranslationRule.MsvcBuiltins()` returns the default MSVC→clang rule set.

### Covered Flags (Semantic — Affect clangd Analysis)

| Category | From | To | Mode | Notes |
|----------|------|----|------|-------|
| Language standard | `/std:c++14` | `-std=c++14` | exact | |
| Language standard | `/std:c++17` | `-std=c++17` | exact | |
| Language standard | `/std:c++20` | `-std=c++20` | exact | |
| Language standard | `/std:c++latest` | `-std=c++2c` | exact | Maps to latest clang knows |
| Language standard | `/std:c11` | `-std=c11` | exact | |
| Language standard | `/std:c17` | `-std=c17` | exact | |
| Exceptions | `/EHsc` | `-fexceptions` | exact | |
| Exceptions | `/EHa` | `-fexceptions` | exact | |
| Exceptions | `/EHs` | `-fexceptions` | exact | |
| RTTI | `/GR-` | `-fno-rtti` | exact | |
| RTTI | `/GR` | `-frtti` | exact | |
| Warning level | `/W0` | `-w` | exact | |
| Warning level | `/W1` | `-Wall` | exact | |
| Warning level | `/W2` | `-Wall` | exact | |
| Warning level | `/W3` | `-Wall` | exact | |
| Warning level | `/W4` | `-Wall -Wextra` | exact | Approximate mapping |
| Warning level | `/Wall` | `-Weverything` | exact | |
| Warnings as errors | `/WX` | `-Werror` | exact | |
| Warnings as errors | `/WX-` | `-Wno-error` | exact | |
| Char signedness | `/J` | `-funsigned-char` | exact | |
| Conformance | `/permissive-` | `-fno-ms-extensions` | exact | Approximate |
| Defines | `/D` | `-D` | prefix | Preserves value: `/DFOO` → `-DFOO` |
| Undefines | `/U` | `-U` | prefix | Preserves value |
| Includes | `/I` | `-I` | prefix | Preserves path |
| Forced include | `/FI` | `-include ` | prefix | Preserves path |
| System includes | `/external:I` | `-isystem ` | prefix | Preserves path |
| Compile only | `/c` | `-c` | exact | |

### Documented Limitation

Built-in rules translate flags that affect clangd's **semantic analysis**: language standard, warning model, preprocessor state, include paths, exception/RTTI model. The following categories are **NOT translated** because they don't affect clangd's understanding of source code and some have no meaningful clang equivalent:

- Optimization levels (`/O1`, `/O2`, `/Ox`, `/Os`)
- Code generation (`/arch:AVX2`, `/favor:AMD64`)
- Debug info format (`/Zi`, `/ZI`, `/Z7`)
- Runtime library selection (`/MT`, `/MD`, `/MTd`, `/MDd`)
- Link-time code generation (`/GL`, `/LTCG`)
- Output paths (`/Fo`, `/Fd`, `/Fe`) — already stripped by parsers
- PDB-related flags (`/FS`)

Users needing translation for these flags can provide a custom config file that includes them.

## Config Loading & Override Semantics

### Resolution

1. If a config file is provided → load it, **replace all built-ins entirely**
2. If `--no-translate` / `/p:CompileCommandsTranslate=false` → no translation at all
3. Otherwise → use built-in rules

There is no merging. Providing a config file is a full override. Users start from the template (via `--dump-rules`) and add/remove/modify rules as needed.

### Configuration Sources

| Source | Syntax | Available in |
|--------|--------|-------------|
| CLI argument | `--flag-rules <path>` | CLI (binlog replay) |
| MSBuild property | `/p:CompileCommandsFlagRules=<path>` | Logger (live builds) |
| CLI disable | `--no-translate` | CLI |
| MSBuild disable | `/p:CompileCommandsTranslate=false` | Logger |

### Template Extraction

```
msbuild-compile-commands --dump-rules > my-rules.json
```

Writes the built-in rules as formatted JSON to stdout. Users copy, edit, and pass back via `--flag-rules`.

## Integration Points

### CompileCommand (Model Change)

Add `ParserKind` property:

```csharp
public sealed class CompileCommand : IEquatable<CompileCommand>
{
    public string Directory { get; }
    public string File { get; }
    public IReadOnlyList<string> Arguments { get; }
    public ParserKind ParserKind { get; }  // NEW
}
```

`ParserKind` does not participate in equality/deduplication (keyed on `File` only). It is not serialized to JSON output — it's internal pipeline metadata.

### Parsers

Each `ICommandParser.Parse()` already creates `CompileCommand` instances. Each implementation sets `ParserKind`:
- `MsvcCommandParser` → `ParserKind.Msvc`
- `GccClangCommandParser` → `ParserKind.GccClang`
- `NvccCommandParser` → `ParserKind.Nvcc`

### Task Mappers

- `ClCompileTaskMapper` → `ParserKind.Msvc`
- `CudaCompileTaskMapper` → `ParserKind.Nvcc`
- `GenericTaskMapper` → `ParserKind.Unknown`

### CompileCommandCollector

Accepts optional `FlagTranslator`. After parsing/mapping a command, before storing in the deduplication dictionary:

```
command = parser.Parse(...)
if (translator != null)
    command = translator.Translate(command)  // NEW
store(command)
```

### Logger

`MsBuildCompileCommandsLogger` reads MSBuild properties during `Initialize()`:
- `CompileCommandsFlagRules` → config file path
- `CompileCommandsTranslate` → `"false"` disables translation

Constructs `FlagTranslator` and passes to `CompileCommandCollector`.

### CLI

New options on the binlog replay command:
- `--flag-rules <path>` — path to rules JSON file
- `--no-translate` — disable all translation
- `--dump-rules` — print built-in rules as JSON and exit

## Pipeline (Updated)

```
MSBuild events
  → ICommandParser.Parse() / TaskMapper.Map()    [sets ParserKind]
  → FlagTranslator.Translate()                    [NEW — applies rules]
  → CompileCommandCollector dedup/filter/store
  → CompileCommandsWriter → JSON
```

## Testing Strategy

### Unit Tests — FlagTranslatorTests

- Exact match replacement
- Exact match drop (to = null)
- Prefix match with suffix preservation
- Multi-arg expansion (space-separated `to`)
- Parser scoping: rule with `when: msvc` skips GccClang commands
- Global rule (when = null) applies to all parsers
- No match → passthrough
- Compiler executable (index 0) is never translated
- Source file (last argument) is never translated
- Empty rules list → passthrough (no-op)

### Unit Tests — Built-in Rules

- Each built-in rule tested against a representative MSVC command line
- Verify `/std:c++20` → `-std=c++20`, `/EHsc` → `-fexceptions`, etc.
- Verify prefix rules: `/DFOO=bar` → `-DFOO=bar`, `/I"C:/inc"` → `-I"C:/inc"`

### Unit Tests — Config Loading

- Valid JSON parses correctly
- Invalid JSON produces clear error
- Unknown `when` value produces clear error
- Missing `from` field produces clear error

### Integration Tests

- End-to-end: collector with translator enabled, verify output arguments are clang-style
- Override: provide config file, verify built-ins are replaced
- Disable: `--no-translate`, verify MSVC flags pass through unchanged
- Dump: `--dump-rules` produces valid JSON that round-trips through config loading

## File Inventory

| File | Action |
|------|--------|
| `src/core/Models/ParserKind.cs` | New |
| `src/core/Models/CompileCommand.cs` | Modify — add ParserKind property |
| `src/core/Models/TranslationRule.cs` | New |
| `src/core/Extraction/FlagTranslator.cs` | New |
| `src/core/Extraction/MsvcCommandParser.cs` | Modify — set ParserKind.Msvc |
| `src/core/Extraction/GccClangCommandParser.cs` | Modify — set ParserKind.GccClang |
| `src/core/Extraction/NvccCommandParser.cs` | Modify — set ParserKind.Nvcc |
| `src/core/Extraction/ClCompileTaskMapper.cs` | Modify — set ParserKind.Msvc |
| `src/core/Extraction/CudaCompileTaskMapper.cs` | Modify — set ParserKind.Nvcc |
| `src/core/Extraction/GenericTaskMapper.cs` | Modify — set ParserKind.Unknown |
| `src/core/Extraction/CompileCommandCollector.cs` | Modify — accept and call FlagTranslator |
| `src/core/IO/TranslationRuleLoader.cs` | New — JSON deserialization |
| `src/logger/MsBuildCompileCommandsLogger.cs` | Modify — read properties, construct translator |
| `src/cli/Program.cs` (or equivalent) | Modify — add CLI options |
| `tests/tests/FlagTranslatorTests.cs` | New |
| `tests/tests/TranslationRuleLoaderTests.cs` | New |
| `tests/tests/BuiltinRulesTests.cs` | New |
