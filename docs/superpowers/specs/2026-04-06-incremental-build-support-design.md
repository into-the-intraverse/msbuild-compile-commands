# Incremental Build Support

## Problem

Incremental builds where not all files recompile produce only a partial set of cl.exe invocations. The current behavior:

- When `merge=false` (default): only the recompiled files appear in `compile_commands.json`, losing entries for untouched files.
- When `commands.Count == 0` (fully up-to-date): no file is written at all.

This means new source files don't appear until a clean build, and there's no mechanism to remove entries for deleted files.

## Solution

Always merge new compile commands into the existing `compile_commands.json`, then prune entries whose source files no longer exist on disk.

## Design

### Core behavior change

- The writer **always reads the existing file** (if present) and merges new commands into it. New entries win on file-path conflict (same as the current `merge=true` behavior).
- After merging, **prune any entry where the source file doesn't exist on disk** (`File.Exists` check on `cmd.File`).
- When `commands.Count == 0` (fully up-to-date build), **still run the prune pass** on the existing file. This handles the "deleted a file, then incremental build" case.
- If the final list is empty (everything pruned, nothing new), write an empty JSON array `[]` to keep the file valid for clangd.

### API / flag changes

- **Remove** the `merge` parameter from `CompileCommandsWriter.Write`. Merging is now always-on.
- **Remove** the `--merge` CLI flag and the `merge=true` logger parameter. This is a hard removal (unknown-option error), acceptable at v0.1.
- **Add** `--overwrite` CLI flag and `overwrite=true` logger parameter for the rare case where a user wants a clean slate (current default behavior). When overwrite is set, skip reading the existing file — just write new commands and prune.

### Changes per component

**CompileCommandsWriter.Write(string outputPath, IReadOnlyList<CompileCommand> commands, bool overwrite = false):**
- When `overwrite=false` (default): read existing file, merge new commands (new wins on conflict), prune entries where `!File.Exists(cmd.File)`, write result.
- When `overwrite=true`: skip reading existing file, just write `commands` as-is (current non-merge behavior, no pruning needed since these are fresh from the build).

**CompileCommandCollector, ClCommandParser:**
- No changes.

**Logger (OnBuildFinished):**
- Remove early return when `commands.Count == 0`. Always call `CompileCommandsWriter.Write` so the prune pass runs.
- Replace `merge` parameter parsing with `overwrite` parameter parsing.
- Adjust log messages: include count of new, retained, and pruned entries.

**CLI (Program.cs):**
- Replace `--merge` with `--overwrite`.
- Always call `Write` even when collector found 0 commands.

### Test cases

1. **Incremental merge** - existing file has entries A, B; new build produces C -> output has A, B, C.
2. **Prune deleted files** - existing file has entries A, B; file A no longer exists on disk -> output has B.
3. **Empty build + prune** - existing file has entries A, B; build produces 0 commands; A exists, B doesn't -> output has A.
4. **Empty build, no existing file** - build produces 0 commands, no existing file -> write empty `[]`.
5. **Overwrite flag** - existing file has entries A, B; new build produces C; `--overwrite` -> output has only C.
6. **First clean build** - no existing file, build produces A, B, C -> output has A, B, C (same as today).

### Edge cases

- **File.Exists on network/UNC paths:** Works on Windows, no special handling needed.
- **Concurrent builds:** Not addressed; same limitation as before.
- **Large compile_commands.json:** The read-merge-prune-write cycle is in-memory. For typical projects (thousands of entries), this is fine.
