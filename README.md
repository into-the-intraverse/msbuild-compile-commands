# MsBuildCompileCommands

Generate `compile_commands.json` from MSBuild-based C/C++ builds for use with [clangd](https://clangd.llvm.org/) and other clang tooling.

## Problem

Windows C++ projects using CMake with Visual Studio generators cannot use `CMAKE_EXPORT_COMPILE_COMMANDS` (it only works with Makefile and Ninja generators) ot generate a compilation database used for clangd, clang-tidy, and similar tools.

**MsBuildCompileCommands** fills this gap by extracting compile commands directly from MSBuild logs, either live during a build or offline from a binary log.

## What it allows you to do

- Get clangd IntelliSense in any MSBuild-based C++ project
- Use CMake + Visual Studio generator with full clangd support
- Integrate Conan 2 + CMake + MSBuild workflows with clang tooling
- Generate compilation databases in CI pipelines
- Run clang-tidy, include-what-you-use, and other clang tools on MSBuild projects

## Two modes of operation

| Mode | How it works | When to use |
|------|-------------|-------------|
| **Live logger** | Attaches to MSBuild as a logger; generates `compile_commands.json` at the end of the build | Normal development workflow |
| **Binlog replay** | Reads an existing `.binlog` file and extracts compile commands offline | CI, post-hoc analysis, when you don't want to modify the build invocation |

Both modes use the same extraction core and produce identical output.

## Installation

### NuGet package (logger)

```bash
dotnet add package MsBuildCompileCommands
```

The package installs the logger DLL into your NuGet cache. Use it with MSBuild:

```bash
# Find the logger DLL path (look under the global-packages path)
dotnet nuget locals global-packages --list

# Use the logger — replace <cache> and <version> with your actual paths
msbuild MyProject.sln -logger:MsBuildCompileCommands.Logger,"<cache>/msbuildcompilecommands/<version>/build/MsBuildCompileCommands.dll"
```

The package also exports `$(MsBuildCompileCommandsLoggerPath)` for use in MSBuild targets and props files.

### dotnet tool (CLI)

```bash
dotnet tool install -g MsBuildCompileCommands.Cli
msbuild-compile-commands build.binlog
```

### Conan 2

Add as a tool requirement in your `conanfile.py`:

```python
def build_requirements(self):
    self.tool_requires("msbuild-compile-commands/0.1.1")
```

Or in a Conan profile:

```ini
[tool_requires]
msbuild-compile-commands/0.1.1
```

This puts `MsBuildCompileCommands.exe` on PATH and sets `MSBUILDCOMPILECOMMANDS_LOGGER_DLL` to the logger DLL path.

### Build from source

Requires .NET 8 SDK or later.

```bash
git clone https://github.com/into-the-intraverse/msbuild-compile-commands.git
cd msbuild-compile-commands
dotnet build -c Release
```

Outputs:
- **Logger DLL**: `bin/logger/MsBuildCompileCommands.dll`
- **CLI tool**: `bin/cli/MsBuildCompileCommands.exe`

## Usage

### Live logger (recommended for development)

Attach the logger to any MSBuild invocation:

```bash
msbuild MyProject.sln -logger:MsBuildCompileCommands.Logger,path\to\MsBuildCompileCommands.dll
```

With options:

```bash
msbuild MyProject.sln -logger:MsBuildCompileCommands.Logger,path\to\MsBuildCompileCommands.dll;output=build/compile_commands.json;overwrite=true
```

Logger parameters (semicolon-separated):
- `output=<path>` - Output file path (default: `compile_commands.json`)
- `overwrite=true` - Overwrite existing file instead of merging (default: merge with existing)
- `project=Name1,Name2` - Include only these projects (comma-separated)
- `configuration=Cfg` - Include only these configurations (comma-separated)
- `flagrules=<path>` - Path to custom flag translation rules JSON (replaces built-in rules)
- `translate=false` - Disable all flag translation

### Binlog replay (recommended for CI)

First, build with binary logging enabled:

```bash
msbuild MyProject.sln /bl:build.binlog
```

Then extract compile commands:

```bash
MsBuildCompileCommands build.binlog
MsBuildCompileCommands build.binlog -o compile_commands.json
MsBuildCompileCommands build.binlog --overwrite
```

CLI options:
- `-o, --output <path>` - Output file path (default: `compile_commands.json`)
- `--overwrite` - Overwrite existing file instead of merging (default: merge with existing)
- `--project <names>` - Include only these projects (comma-separated, matched by project name)
- `--configuration <names>` - Include only these configurations (comma-separated)
- `--evaluate` - After binlog replay, evaluate `.vcxproj` files to fill in source files not captured by build events (requires MSBuild installation)
- `--flag-rules <path>` - Path to custom flag translation rules JSON (replaces built-in rules entirely)
- `--no-translate` - Disable all flag translation (built-in and custom)
- `--dump-rules` - Print built-in translation rules as JSON and exit
- `--version` - Show version
- `-h, --help` - Show help

## Workflow examples

### CMake + Visual Studio generator

```bash
# Configure with Visual Studio generator
cmake -B build -G "Visual Studio 17 2022"

# Build with binary logging
cmake --build build --config Release -- /bl:build.binlog

# Generate compile_commands.json
MsBuildCompileCommands build.binlog -o compile_commands.json
```

Or with the live logger:

```bash
cmake -B build -G "Visual Studio 17 2022"
cmake --build build --config Release -- -logger:path\to\MsBuildCompileCommands.dll
```

### Conan 2 + CMake + MSBuild

```bash
# Install dependencies (with msbuild-compile-commands as tool_requires)
conan install . --output-folder=build --build=missing

# Configure
cmake --preset conan-default

# Option A: binlog replay
cmake --build --preset conan-release -- /bl:build.binlog
MsBuildCompileCommands build.binlog -o compile_commands.json

# Option B: live logger (using env var set by the Conan package)
cmake --build --preset conan-release -- -logger:MsBuildCompileCommands.Logger,%MSBUILDCOMPILECOMMANDS_LOGGER_DLL%
```

### MSBuild only (no CMake)

```bash
# Live logger
msbuild MyProject.vcxproj -logger:path\to\MsBuildCompileCommands.dll

# Or via binlog
msbuild MyProject.vcxproj /bl
MsBuildCompileCommands msbuild.binlog
```

### clangd configuration

After generating `compile_commands.json`, create or update `.clangd` in your project root:

```yaml
CompileFlags:
  CompilationDatabase: .
```

If `compile_commands.json` is in a subdirectory:

```yaml
CompileFlags:
  CompilationDatabase: build
```

## Output format

Uses the `arguments` form (array of strings) rather than the `command` form (single string). This is more robust for Windows paths with spaces and avoids shell escaping issues.

```json
[
  {
    "directory": "C:/project/build",
    "file": "C:/project/src/main.cpp",
    "arguments": [
      "C:/Program Files/VS/VC/Tools/MSVC/14.38/bin/Hostx64/x64/cl.exe",
      "-fexceptions",
      "-std=c++17",
      "-IC:/project/include",
      "-DWIN32",
      "-c",
      "C:/project/src/main.cpp"
    ]
  }
]
```

With `--no-translate`, original MSVC flags are preserved:

```json
{
  "arguments": ["cl.exe", "/EHsc", "/std:c++17", "/IC:/project/include", "/DWIN32", "/c", "..."]
}
```

Paths are normalized to forward slashes with uppercase drive letters for consistency.

## Flag translation

By default, MSVC flags are translated to clang equivalents so clangd understands the output without extra configuration. This is on by default and can be disabled with `--no-translate` (CLI) or `translate=false` (logger).

### What gets translated

| MSVC | clang | Category |
|------|-------|----------|
| `/std:c++20` | `-std=c++20` | Language standard |
| `/EHsc`, `/EHa`, `/EHs` | `-fexceptions` | Exceptions |
| `/GR`, `/GR-` | `-frtti`, `-fno-rtti` | RTTI |
| `/W0`–`/W4`, `/Wall` | `-w`, `-Wall`, `-Wextra`, `-Weverything` | Warning level |
| `/WX`, `/WX-` | `-Werror`, `-Wno-error` | Warnings as errors |
| `/D`, `/U`, `/I` | `-D`, `-U`, `-I` | Defines, undefines, includes |
| `/FI` | `-include` | Forced includes |
| `/external:I` | `-isystem` | System includes |
| `/J` | `-funsigned-char` | Char signedness |
| `/permissive-` | `-fno-ms-extensions` | Conformance |
| `/c` | `-c` | Compile only |

### What is NOT translated

Optimization levels (`/O1`, `/O2`), code generation (`/arch:*`), debug info (`/Zi`), runtime library (`/MT`, `/MD`), and output paths (`/Fo`, `/Fd`) are not translated — they don't affect clangd's understanding of source code. Output paths are already stripped by the parsers.

### Custom rules

Export the built-in rules as a starting point, edit, and pass back:

```bash
# Export built-in rules
msbuild-compile-commands --dump-rules > my-rules.json

# Edit my-rules.json to add/remove/modify rules...

# Use custom rules (replaces all built-ins)
msbuild-compile-commands build.binlog --flag-rules my-rules.json
```

Rule format:

```json
[
  { "when": "msvc", "from": "/EHsc", "to": "-fexceptions" },
  { "when": "msvc", "from": "/D", "to": "-D", "prefix": true },
  { "when": "nvcc", "from": "--expt-relaxed-constexpr", "to": null }
]
```

- `when` — `"msvc"`, `"gcc-clang"`, `"nvcc"`, or omitted for all compilers
- `from` — flag to match (exact or prefix)
- `to` — replacement (space-separated for multi-arg), `null` to drop the flag
- `prefix` — `true` to match start of argument and preserve the suffix (e.g., `/DFOO` → `-DFOO`)

## What gets captured

- Include directories (`/I`, `/external:I`, `-I`, `-isystem`, `-imsvc`)
- Preprocessor defines (`/D`, `-D`)
- Undefines (`/U`, `-U`)
- Forced includes (`/FI`, `-include`)
- Language standard (`/std:c++17`, `-std=c++17`, etc.)
- Exception handling (`/EHsc`, `/EHa`)
- RTTI (`/GR`, `/GR-`)
- Warning flags (`/W0-4`, `/WX`, `/Wall`, `/wdNNNN`)
- Conformance flags (`/Zc:*`, `/permissive-`)
- Character set flags (`/utf-8`, `/source-charset:*`)
- Treat-as-language flags (`/TP`, `/TC`, `/Tp`, `/Tc`)
- Other compilation flags

## What gets excluded

- Output path flags (`/Fo`, `/Fe`, `/Fd`, `/Fa`, `/Fp`, etc.)
- Cosmetic flags (`/nologo`, `/showIncludes`)
- Linker invocations (automatically filtered)

## Supported compilers

- **cl.exe** (MSVC) - primary target
- **clang-cl** - MSVC-compatible Clang driver
- **gcc / g++** - GNU Compiler Collection (including cross-compiler and versioned variants like `x86_64-linux-gnu-gcc`, `gcc-12`)
- **clang / clang++** - LLVM Clang (native mode, not clang-cl)
- **nvcc** - NVIDIA CUDA compiler (extracts host compiler flags via `-Xcompiler`/`--compiler-options`)

## Limitations

- The first build must be a clean build to populate `compile_commands.json`; subsequent incremental builds automatically merge new entries and prune deleted files
- Response file expansion requires the response files to exist on disk at parse time; a warning is emitted when a response file cannot be read (common when replaying `.binlog` files after the build's temporary files have been cleaned up)
- Custom MSBuild tasks that invoke compilers via `System.Diagnostics.Process` without logging, without standard task parameters, and without standard `ClCompile` items in the project file are not captured by any method
- Generated source files are captured if they appear in the compiler command line, but the files must exist for clangd to use them

### Precompiled headers

`/Yc` (create) is stripped and `/Yu` (use) is converted to `/FI` (forced include) so clangd sees the implicit PCH header. The `.pch` file itself is not used.

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
  Extraction/FlagTranslator        Translates compiler flags using rules
  Extraction/CompileCommandCollector  MSBuild event processor
  Extraction/ITaskMapper          Task parameter → CompileCommand mapper interface
  Extraction/TaskMapperRegistry   Dispatches to known + fallback mappers
  Extraction/ClCompileTaskMapper  Maps CL task parameters
  Extraction/CudaCompileTaskMapper Maps CudaCompile task parameters
  Extraction/GenericTaskMapper    Best-effort fallback for unknown tasks
  IO/ResponseFileParser           @response-file expansion
  IO/TranslationRuleLoader        JSON rule loading/serialization
  IO/CompileCommandsWriter        JSON output
  Utils/PathNormalizer            Path normalization

MsBuildCompileCommands (netstandard2.0, logger)
  Logger                          ILogger implementation for live builds

MsBuildCompileCommands (net8.0, CLI)
  Program                         Binlog replay CLI
  Evaluation/ProjectEvaluator     .vcxproj evaluation via MSBuild API
  Evaluation/ClCompileItemMapper  ClCompile metadata → flags mapping
```

The core library targets `netstandard2.0` for compatibility with both .NET Framework MSBuild (Visual Studio) and .NET SDK MSBuild.

## Building

```bash
dotnet build
dotnet test
dotnet build -c Release
```

## Roadmap

- [ ] ETW-based process tracing for compilers invoked without MSBuild events
- [ ] Linux/macOS support for offline binlog parsing

## License

[MIT](LICENSE)

