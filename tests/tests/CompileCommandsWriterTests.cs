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
            string tempFile = Path.Combine(Path.GetTempPath(), $"compile_commands_{Guid.NewGuid()}.json");

            try
            {
                var commands = new List<CompileCommand>
                {
                    new CompileCommand("C:/project", "C:/project/main.cpp", new[] { "cl.exe", "/c", "main.cpp" })
                };

                CompileCommandsWriter.Write(tempFile, commands);

                Assert.True(File.Exists(tempFile));

                string content = File.ReadAllText(tempFile);
                using var doc = JsonDocument.Parse(content);
                Assert.Equal(1, doc.RootElement.GetArrayLength());
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        [Fact]
        public void Write_with_merge_combines_entries()
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

                // Merge new entries (c.cpp is new, a.cpp should be updated)
                var updates = new List<CompileCommand>
                {
                    new CompileCommand("C:/project", "C:/project/a.cpp", new[] { "cl.exe", "/c", "/O2", "a.cpp" }),
                    new CompileCommand("C:/project", "C:/project/c.cpp", new[] { "cl.exe", "/c", "c.cpp" })
                };
                CompileCommandsWriter.Write(tempFile, updates, merge: true);

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
    }
}
