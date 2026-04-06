using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class CompileCommandsWriterTests
    {
        [Fact]
        public void Serialize_produces_valid_json_array()
        {
            var commands = new List<CompileCommand>
            {
                new CompileCommand("C:/project", "C:/project/main.cpp", new[] { "cl.exe", "/c", "C:/project/main.cpp" })
            };

            string json = CompileCommandsWriter.Serialize(commands);
            using var doc = JsonDocument.Parse(json);

            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.Equal(1, doc.RootElement.GetArrayLength());
        }

        [Fact]
        public void Entries_contain_required_fields()
        {
            var commands = new List<CompileCommand>
            {
                new CompileCommand("C:/project", "C:/project/main.cpp", new[] { "cl.exe", "/c", "C:/project/main.cpp" })
            };

            string json = CompileCommandsWriter.Serialize(commands);
            using var doc = JsonDocument.Parse(json);

            JsonElement entry = doc.RootElement[0];
            Assert.Equal("C:/project", entry.GetProperty("directory").GetString());
            Assert.Equal("C:/project/main.cpp", entry.GetProperty("file").GetString());
            Assert.True(entry.TryGetProperty("arguments", out JsonElement args));
            Assert.Equal(JsonValueKind.Array, args.ValueKind);
            Assert.Equal(3, args.GetArrayLength());
        }

        [Fact]
        public void Output_is_sorted_deterministically()
        {
            var commands = new List<CompileCommand>
            {
                new CompileCommand("C:/project", "C:/project/z.cpp", new[] { "cl.exe", "/c", "z.cpp" }),
                new CompileCommand("C:/project", "C:/project/a.cpp", new[] { "cl.exe", "/c", "a.cpp" }),
                new CompileCommand("C:/project", "C:/project/m.cpp", new[] { "cl.exe", "/c", "m.cpp" })
            };

            string json = CompileCommandsWriter.Serialize(commands);
            using var doc = JsonDocument.Parse(json);

            string? first = doc.RootElement[0].GetProperty("file").GetString();
            string? last = doc.RootElement[2].GetProperty("file").GetString();

            Assert.Contains("a.cpp", first);
            Assert.Contains("z.cpp", last);
        }

        [Fact]
        public void Empty_list_produces_empty_array()
        {
            string json = CompileCommandsWriter.Serialize(new List<CompileCommand>());
            using var doc = JsonDocument.Parse(json);

            Assert.Equal(0, doc.RootElement.GetArrayLength());
        }

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

        [Fact]
        public void Write_merges_with_existing_file_by_default()
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

                var updates = new List<CompileCommand>
                {
                    new CompileCommand(tempDir, fileA, new[] { "cl.exe", "/c", "/O2", "a.cpp" }),
                    new CompileCommand(tempDir, fileC, new[] { "cl.exe", "/c", "c.cpp" })
                };
                CompileCommandsWriter.Write(output, updates);

                string content = File.ReadAllText(output);
                using var doc = JsonDocument.Parse(content);

                Assert.Equal(3, doc.RootElement.GetArrayLength());

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
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Paths_with_spaces_are_properly_escaped()
        {
            var commands = new List<CompileCommand>
            {
                new CompileCommand(
                    "C:/Program Files/My Project",
                    "C:/Program Files/My Project/main.cpp",
                    new[] { @"C:\Program Files\VC\cl.exe", "/c", "C:/Program Files/My Project/main.cpp" })
            };

            string json = CompileCommandsWriter.Serialize(commands);

            // Should be parseable and contain the space-containing paths
            using var doc = JsonDocument.Parse(json);
            JsonElement entry = doc.RootElement[0];
            Assert.Equal("C:/Program Files/My Project", entry.GetProperty("directory").GetString());
        }

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
    }
}
