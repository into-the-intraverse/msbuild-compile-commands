# Incremental Build Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `compile_commands.json` stay current across incremental builds by always merging with the existing file and pruning entries for deleted source files.

**Architecture:** Change `CompileCommandsWriter.Write` to always-merge + prune-deleted-files by default. Replace the `merge` parameter with `overwrite` (opt-in clean-slate mode). Update Logger and CLI to always call Write (even on 0 commands) and expose the new `overwrite` flag.

**Tech Stack:** C# / .NET 8 / netstandard2.0, xunit

---

## File Map

| File | Action | Responsibility |
|------|--------|----------------|
| `src/core/IO/CompileCommandsWriter.cs` | Modify | Change `Write` signature, add always-merge + prune logic |
| `src/logger/CompileCommandsLogger.cs` | Modify | Remove early return on 0 commands, replace `merge` with `overwrite` param |
| `src/cli/Program.cs` | Modify | Replace `--merge` with `--overwrite`, always call Write |
| `tests/tests/CompileCommandsWriterTests.cs` | Modify | Add 6 new test cases, update existing merge test |

---

### Task 1: Update CompileCommandsWriter — change signature and always-merge logic

**Files:**
- Modify: `tests/tests/CompileCommandsWriterTests.cs`
- Modify: `src/core/IO/CompileCommandsWriter.cs`

- [ ] **Step 1: Update existing merge test to use new API**

The existing test `Write_with_merge_combines_entries` calls `Write(tempFile, updates, merge: true)`. This needs to become the default behavior. Update the test to call `Write(tempFile, updates)` (no merge flag) and expect the same merged result.

In `tests/tests/CompileCommandsWriterTests.cs`, replace the `Write_with_merge_combines_entries` method:

```csharp
[Fact]
public void Write_merges_with_existing_file_by_default()
{
    string tempFile = Path.Combine(Path.GetTempPath(), $"compile_commands_{Guid.NewGuid()}.json");

    try
    {
        // Write initial entries
        var initial = new List<CompileCommand>
        {
            new CompileCommand("C:/project", "C:/project/a.cpp", new[] { "cl.exe", "/c", "a.cpp" }),
            new CompileCommand("C:/project", "C:/project/b.cpp", new[] { "cl.exe", "/c", "b.cpp" })
        };
        CompileCommandsWriter.Write(tempFile, initial);

        // Write again with overlapping + new entries — should merge by default
        var updates = new List<CompileCommand>
        {
            new CompileCommand("C:/project", "C:/project/a.cpp", new[] { "cl.exe", "/c", "/O2", "a.cpp" }),
            new CompileCommand("C:/project", "C:/project/c.cpp", new[] { "cl.exe", "/c", "c.cpp" })
        };
        CompileCommandsWriter.Write(tempFile, updates);

        string content = File.ReadAllText(tempFile);
        using var doc = JsonDocument.Parse(content);

        Assert.Equal(3, doc.RootElement.GetArrayLength());

        // Verify a.cpp was updated (has /O2)
        foreach (JsonElement entry in doc.RootElement.EnumerateArray())
        {
            string? file = entry.GetProperty("file").GetString();
            if (file != null && file.EndsWith("a.cpp"))
            {
                string argsJson = entry.GetProperty("arguments").ToString();
                Assert.Contains("/O2", argsJson);
            }
        }
    }
    finally
    {
        if (File.Exists(tempFile))
            File.Delete(tempFile);
    }
}
```

- [ ] **Step 2: Run tests to verify the updated test fails**

Run: `dotnet test tests/tests/tests.csproj --filter "Write_merges_with_existing_file_by_default" -v n`

Expected: Compilation error — `Write` still has the old `bool merge` signature and the test name changed.

- [ ] **Step 3: Update CompileCommandsWriter.Write signature and logic**

In `src/core/IO/CompileCommandsWriter.cs`, replace the `Write` method:

```csharp
/// <summary>
/// Write compile commands to a file. By default, merges with any existing file
/// (new entries win on file-path conflict) and prunes entries whose source files
/// no longer exist on disk.
/// When <paramref name="overwrite"/> is true, ignores the existing file and writes
/// only the provided commands (clean-slate mode).
/// </summary>
public static void Write(string outputPath, IReadOnlyList<CompileCommand> commands, bool overwrite = false)
{
    List<CompileCommand> finalCommands;

    if (!overwrite && File.Exists(outputPath))
    {
        var existing = Read(outputPath);
        var merged = new Dictionary<string, CompileCommand>(StringComparer.OrdinalIgnoreCase);

        foreach (CompileCommand cmd in existing)
            merged[cmd.DeduplicationKey] = cmd;

        foreach (CompileCommand cmd in commands)
            merged[cmd.DeduplicationKey] = cmd;

        finalCommands = new List<CompileCommand>(merged.Values);
    }
    else
    {
        finalCommands = new List<CompileCommand>(commands);
    }

    // Prune entries whose source files no longer exist on disk
    if (!overwrite)
    {
        finalCommands.RemoveAll(cmd => !File.Exists(cmd.File));
    }

    // Sort for deterministic output
    finalCommands.Sort((a, b) => string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase));

    string? dir = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
        Directory.CreateDirectory(dir);
    }

    using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
    using (var writer = new Utf8JsonWriter(stream, WriterOptions))
    {
        writer.WriteStartArray();

        foreach (CompileCommand cmd in finalCommands)
        {
            writer.WriteStartObject();
            writer.WriteString("directory", cmd.Directory);
            writer.WriteString("file", cmd.File);

            writer.WritePropertyName("arguments");
            writer.WriteStartArray();
            foreach (string arg in cmd.Arguments)
            {
                writer.WriteStringValue(arg);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
```

- [ ] **Step 4: Run tests to verify the updated test passes**

Run: `dotnet test tests/tests/tests.csproj --filter "Write_merges_with_existing_file_by_default" -v n`

Expected: PASS

- [ ] **Step 5: Run all existing writer tests to check for regressions**

Run: `dotnet test tests/tests/tests.csproj --filter "CompileCommandsWriterTests" -v n`

Expected: All pass. The `Write_creates_file_on_disk` test still works because the first call to `Write` with no existing file just writes the new commands. The pruning will remove entries whose `File` paths don't exist on disk (the test uses `C:/project/main.cpp` which doesn't exist), so that test will now produce an empty array.

If `Write_creates_file_on_disk` fails because of pruning, update it to use real temp files:

```csharp
[Fact]
public void Write_creates_file_on_disk()
{
    string tempDir = Path.Combine(Path.GetTempPath(), $"msbcc_test_{Guid.NewGuid()}");
    Directory.CreateDirectory(tempDir);
    string tempSource = Path.Combine(tempDir, "main.cpp");
    File.WriteAllText(tempSource, "// test");
    string tempOutput = Path.Combine(tempDir, "compile_commands.json");

    try
    {
        var commands = new List<CompileCommand>
        {
            new CompileCommand(tempDir, tempSource, new[] { "cl.exe", "/c", "main.cpp" })
        };

        CompileCommandsWriter.Write(tempOutput, commands);

        Assert.True(File.Exists(tempOutput));

        string content = File.ReadAllText(tempOutput);
        using var doc = JsonDocument.Parse(content);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}
```

- [ ] **Step 6: Commit**

```bash
git add src/core/IO/CompileCommandsWriter.cs tests/tests/CompileCommandsWriterTests.cs
git commit -m "feat: always merge and prune deleted files in CompileCommandsWriter"
```

---

### Task 2: Add test cases for incremental build scenarios

**Files:**
- Modify: `tests/tests/CompileCommandsWriterTests.cs`

All new tests need real temp files on disk because the pruning logic checks `File.Exists`. Each test creates a temp directory, writes dummy `.cpp` files, and cleans up in `finally`.

- [ ] **Step 1: Write test — incremental merge adds new entries**

```csharp
[Fact]
public void Write_incremental_merge_adds_new_entries()
{
    string tempDir = Path.Combine(Path.GetTempPath(), $"msbcc_test_{Guid.NewGuid()}");
    Directory.CreateDirectory(tempDir);
    string fileA = Path.Combine(tempDir, "a.cpp");
    string fileB = Path.Combine(tempDir, "b.cpp");
    string fileC = Path.Combine(tempDir, "c.cpp");
    File.WriteAllText(fileA, "// a");
    File.WriteAllText(fileB, "// b");
    File.WriteAllText(fileC, "// c");
    string output = Path.Combine(tempDir, "compile_commands.json");

    try
    {
        // Clean build produces A, B
        var initial = new List<CompileCommand>
        {
            new CompileCommand(tempDir, fileA, new[] { "cl.exe", "/c", "a.cpp" }),
            new CompileCommand(tempDir, fileB, new[] { "cl.exe", "/c", "b.cpp" })
        };
        CompileCommandsWriter.Write(output, initial);

        // Incremental build only compiles C (new file)
        var incremental = new List<CompileCommand>
        {
            new CompileCommand(tempDir, fileC, new[] { "cl.exe", "/c", "c.cpp" })
        };
        CompileCommandsWriter.Write(output, incremental);

        string content = File.ReadAllText(output);
        using var doc = JsonDocument.Parse(content);
        Assert.Equal(3, doc.RootElement.GetArrayLength());
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}
```

- [ ] **Step 2: Write test — prune deleted files**

```csharp
[Fact]
public void Write_prunes_entries_for_deleted_source_files()
{
    string tempDir = Path.Combine(Path.GetTempPath(), $"msbcc_test_{Guid.NewGuid()}");
    Directory.CreateDirectory(tempDir);
    string fileA = Path.Combine(tempDir, "a.cpp");
    string fileB = Path.Combine(tempDir, "b.cpp");
    File.WriteAllText(fileA, "// a");
    File.WriteAllText(fileB, "// b");
    string output = Path.Combine(tempDir, "compile_commands.json");

    try
    {
        var initial = new List<CompileCommand>
        {
            new CompileCommand(tempDir, fileA, new[] { "cl.exe", "/c", "a.cpp" }),
            new CompileCommand(tempDir, fileB, new[] { "cl.exe", "/c", "b.cpp" })
        };
        CompileCommandsWriter.Write(output, initial);

        // Delete file A from disk
        File.Delete(fileA);

        // Incremental build compiles nothing
        CompileCommandsWriter.Write(output, new List<CompileCommand>());

        string content = File.ReadAllText(output);
        using var doc = JsonDocument.Parse(content);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.EndsWith("b.cpp", doc.RootElement[0].GetProperty("file").GetString());
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}
```

- [ ] **Step 3: Write test — empty build with no existing file writes empty array**

```csharp
[Fact]
public void Write_empty_commands_no_existing_file_writes_empty_array()
{
    string tempDir = Path.Combine(Path.GetTempPath(), $"msbcc_test_{Guid.NewGuid()}");
    Directory.CreateDirectory(tempDir);
    string output = Path.Combine(tempDir, "compile_commands.json");

    try
    {
        CompileCommandsWriter.Write(output, new List<CompileCommand>());

        Assert.True(File.Exists(output));
        string content = File.ReadAllText(output);
        using var doc = JsonDocument.Parse(content);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}
```

- [ ] **Step 4: Write test — overwrite flag ignores existing file**

```csharp
[Fact]
public void Write_with_overwrite_ignores_existing_file()
{
    string tempDir = Path.Combine(Path.GetTempPath(), $"msbcc_test_{Guid.NewGuid()}");
    Directory.CreateDirectory(tempDir);
    string fileA = Path.Combine(tempDir, "a.cpp");
    string fileB = Path.Combine(tempDir, "b.cpp");
    string fileC = Path.Combine(tempDir, "c.cpp");
    File.WriteAllText(fileA, "// a");
    File.WriteAllText(fileB, "// b");
    File.WriteAllText(fileC, "// c");
    string output = Path.Combine(tempDir, "compile_commands.json");

    try
    {
        var initial = new List<CompileCommand>
        {
            new CompileCommand(tempDir, fileA, new[] { "cl.exe", "/c", "a.cpp" }),
            new CompileCommand(tempDir, fileB, new[] { "cl.exe", "/c", "b.cpp" })
        };
        CompileCommandsWriter.Write(output, initial);

        // Overwrite with only C
        var overwriteCommands = new List<CompileCommand>
        {
            new CompileCommand(tempDir, fileC, new[] { "cl.exe", "/c", "c.cpp" })
        };
        CompileCommandsWriter.Write(output, overwriteCommands, overwrite: true);

        string content = File.ReadAllText(output);
        using var doc = JsonDocument.Parse(content);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.EndsWith("c.cpp", doc.RootElement[0].GetProperty("file").GetString());
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}
```

- [ ] **Step 5: Write test — first clean build (no existing file) writes all entries**

```csharp
[Fact]
public void Write_first_clean_build_writes_all_entries()
{
    string tempDir = Path.Combine(Path.GetTempPath(), $"msbcc_test_{Guid.NewGuid()}");
    Directory.CreateDirectory(tempDir);
    string fileA = Path.Combine(tempDir, "a.cpp");
    string fileB = Path.Combine(tempDir, "b.cpp");
    string fileC = Path.Combine(tempDir, "c.cpp");
    File.WriteAllText(fileA, "// a");
    File.WriteAllText(fileB, "// b");
    File.WriteAllText(fileC, "// c");
    string output = Path.Combine(tempDir, "compile_commands.json");

    try
    {
        var commands = new List<CompileCommand>
        {
            new CompileCommand(tempDir, fileA, new[] { "cl.exe", "/c", "a.cpp" }),
            new CompileCommand(tempDir, fileB, new[] { "cl.exe", "/c", "b.cpp" }),
            new CompileCommand(tempDir, fileC, new[] { "cl.exe", "/c", "c.cpp" })
        };
        CompileCommandsWriter.Write(output, commands);

        string content = File.ReadAllText(output);
        using var doc = JsonDocument.Parse(content);
        Assert.Equal(3, doc.RootElement.GetArrayLength());
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}
```

- [ ] **Step 6: Run all writer tests**

Run: `dotnet test tests/tests/tests.csproj --filter "CompileCommandsWriterTests" -v n`

Expected: All pass.

- [ ] **Step 7: Commit**

```bash
git add tests/tests/CompileCommandsWriterTests.cs
git commit -m "test: add incremental merge and prune test cases"
```

---

### Task 3: Update Logger to remove early return and replace merge with overwrite

**Files:**
- Modify: `src/logger/CompileCommandsLogger.cs`

- [ ] **Step 1: Replace merge parameter with overwrite and remove early return**

In `src/logger/CompileCommandsLogger.cs`, make these changes:

1. Change the `_merge` field to `_overwrite`:

```csharp
private bool _overwrite;
```

2. In `OnBuildFinished`, remove the early return when `commands.Count == 0`. Always call Write. Replace:

```csharp
private void OnBuildFinished(object sender, BuildFinishedEventArgs e)
{
    if (_collector == null)
        return;

    var commands = _collector.GetCommands();

    try
    {
        string outputPath = Path.GetFullPath(_outputPath);
        CompileCommandsWriter.Write(outputPath, commands, _overwrite);

        WriteMessage(
            $"MsBuildCompileCommands: Wrote {commands.Count} entries to {outputPath}",
            MessageImportance.High);
    }
    catch (Exception ex)
    {
        WriteMessage(
            $"MsBuildCompileCommands: Error writing compile_commands.json: {ex.Message}",
            MessageImportance.High);
    }

    foreach (string diag in _collector.Diagnostics)
    {
        WriteMessage($"MsBuildCompileCommands: {diag}", MessageImportance.Normal);
    }
}
```

3. In `ParseParameters`, replace `merge` with `overwrite`:

```csharp
else if (string.Equals(key, "overwrite", StringComparison.OrdinalIgnoreCase))
{
    _overwrite = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/logger/logger.csproj -v n`

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/logger/CompileCommandsLogger.cs
git commit -m "feat: logger always merges by default, add overwrite parameter"
```

---

### Task 4: Update CLI to replace --merge with --overwrite

**Files:**
- Modify: `src/cli/Program.cs`

- [ ] **Step 1: Replace --merge with --overwrite and always call Write**

In `src/cli/Program.cs`:

1. In `Main`, rename the variable and change the flag:

Replace:
```csharp
else if (string.Equals(arg, "--merge", StringComparison.OrdinalIgnoreCase))
{
    merge = true;
}
```
With:
```csharp
else if (string.Equals(arg, "--overwrite", StringComparison.OrdinalIgnoreCase))
{
    overwrite = true;
}
```

And rename the local variable from `merge` to `overwrite` (with `= false` default). Update the call to `GenerateFromBinlog(binlogPath, outputPath, overwrite)`.

2. In `GenerateFromBinlog`, rename the parameter from `merge` to `overwrite`. Remove the early return when `commands.Count == 0`. Replace the method body:

```csharp
private static int GenerateFromBinlog(string binlogPath, string outputPath, bool overwrite)
{
    Console.Error.WriteLine($"Reading {binlogPath}...");

    var collector = new CompileCommandCollector();

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

    List<MsBuildCompileCommands.Core.Models.CompileCommand> commands = collector.GetCommands();

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
```

3. In `PrintUsage`, update the help text. Replace `--merge` line and example:

```
    --overwrite             Overwrite existing file instead of merging
```

```
    # Overwrite instead of merging with existing database
    MsBuildCompileCommands build.binlog --overwrite
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/cli/cli.csproj -v n`

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/cli/Program.cs
git commit -m "feat: CLI always merges by default, replace --merge with --overwrite"
```

---

### Task 5: Update README and run full test suite

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update README**

In `README.md`, make these changes:

1. Replace logger parameter docs. Change:
```
- `merge=true` - Merge with existing file instead of overwriting
```
To:
```
- `overwrite=true` - Overwrite existing file instead of merging (default: merge with existing)
```

2. Replace CLI options. Change:
```
- `--merge` - Merge with existing file
```
To:
```
- `--overwrite` - Overwrite existing file instead of merging (default: merge with existing)
```

3. In the workflow examples, replace `--merge` with `--overwrite` wherever it appears.

4. Update the logger usage example with `merge=true` to use `overwrite=true`:
```bash
msbuild MyProject.sln -logger:MsBuildCompileCommands.Logger,path\to\MsBuildCompileCommands.dll;output=build/compile_commands.json;overwrite=true
```

5. Remove or update the incremental build limitation. Replace:
```
- Incremental builds where nothing recompiles produce no cl.exe invocations, so `compile_commands.json` won't be regenerated — run a clean build once to generate it, then it persists for clangd
```
With:
```
- The first build must be a clean build to populate `compile_commands.json`; subsequent incremental builds automatically merge new entries and prune deleted files
```

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test tests/tests/tests.csproj -v n`

Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: update README for always-merge behavior and --overwrite flag"
```
