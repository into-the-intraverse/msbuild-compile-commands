using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using MsBuildCompileCommands.Core.Models;

namespace MsBuildCompileCommands.Core.IO
{
    /// <summary>
    /// Writes a list of <see cref="CompileCommand"/> entries to a compile_commands.json file.
    /// Uses the "arguments" form (string array) which is more robust than the "command" form
    /// for Windows paths with spaces and special characters.
    /// </summary>
    public static class CompileCommandsWriter
    {
        private static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

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

        /// <summary>
        /// Serialize compile commands to a JSON string (for testing / inspection).
        /// </summary>
        public static string Serialize(IReadOnlyList<CompileCommand> commands)
        {
            var sorted = new List<CompileCommand>(commands);
            sorted.Sort((a, b) => string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase));

            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream, WriterOptions))
                {
                    writer.WriteStartArray();

                    foreach (CompileCommand cmd in sorted)
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

                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>
        /// Read existing compile_commands.json entries for merging.
        /// </summary>
        private static List<CompileCommand> Read(string path)
        {
            var commands = new List<CompileCommand>();

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (var doc = JsonDocument.Parse(bytes))
                {
                    foreach (JsonElement entry in doc.RootElement.EnumerateArray())
                    {
                        string directory = entry.GetProperty("directory").GetString() ?? "";
                        string file = entry.GetProperty("file").GetString() ?? "";

                        var args = new List<string>();
                        if (entry.TryGetProperty("arguments", out JsonElement argsElement))
                        {
                            foreach (JsonElement arg in argsElement.EnumerateArray())
                            {
                                string? val = arg.GetString();
                                if (val != null) args.Add(val);
                            }
                        }

                        commands.Add(new CompileCommand(directory, file, args));
                    }
                }
            }
            catch (Exception)
            {
                // If we can't parse the existing file, start fresh
            }

            return commands;
        }
    }
}
