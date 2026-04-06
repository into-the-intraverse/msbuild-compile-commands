using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using MsBuildCompileCommands.Core.Models;

namespace MsBuildCompileCommands.Core.Extraction
{
    /// <summary>
    /// Processes MSBuild events and collects <see cref="CompileCommand"/> entries.
    /// Shared between the live logger and the binlog replay CLI.
    /// </summary>
    public sealed class CompileCommandCollector
    {
        private readonly Dictionary<int, string> _projectDirectories = new Dictionary<int, string>();
        private readonly Dictionary<string, CompileCommand> _commands = new Dictionary<string, CompileCommand>(StringComparer.OrdinalIgnoreCase);
        private readonly ClCommandParser _parser;

        private readonly List<string> _diagnostics = new List<string>();

        public CompileCommandCollector() : this(new ClCommandParser()) { }

        public CompileCommandCollector(ClCommandParser parser)
        {
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        }

        /// <summary>Diagnostic messages emitted during processing.</summary>
        public IReadOnlyList<string> Diagnostics => _diagnostics;

        /// <summary>Number of compile commands collected so far.</summary>
        public int Count => _commands.Count;

        /// <summary>
        /// Route any MSBuild event to the appropriate handler.
        /// </summary>
        public void HandleEvent(BuildEventArgs e)
        {
            switch (e)
            {
                case ProjectStartedEventArgs projectStarted:
                    HandleProjectStarted(projectStarted);
                    break;
                case TaskCommandLineEventArgs taskCommandLine:
                    HandleTaskCommandLine(taskCommandLine);
                    break;
            }
        }

        /// <summary>
        /// Returns the deduplicated compile commands, sorted by file path for deterministic output.
        /// </summary>
        public List<CompileCommand> GetCommands()
        {
            var result = new List<CompileCommand>(_commands.Values);
            result.Sort((a, b) => string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private void HandleProjectStarted(ProjectStartedEventArgs e)
        {
            if (e.ProjectFile == null)
                return;

            string? dir = Path.GetDirectoryName(e.ProjectFile);
            if (dir == null)
                return;

            int contextId = e.BuildEventContext?.ProjectContextId ?? -1;
            if (contextId >= 0)
            {
                _projectDirectories[contextId] = dir;
            }
        }

        private void HandleTaskCommandLine(TaskCommandLineEventArgs e)
        {
            string? commandLine = e.CommandLine;
            if (string.IsNullOrWhiteSpace(commandLine))
                return;

            if (!ClCommandParser.IsCompilerInvocation(commandLine))
                return;

            string directory = ResolveDirectory(e.BuildEventContext);

            try
            {
                List<CompileCommand> commands = _parser.Parse(commandLine, directory);

                foreach (CompileCommand cmd in commands)
                {
                    // Last-wins deduplication by file path
                    _commands[cmd.DeduplicationKey] = cmd;
                }

                if (commands.Count == 0)
                {
                    _diagnostics.Add($"Warning: compiler invocation produced no entries: {Truncate(commandLine, 200)}");
                }
            }
            catch (Exception ex)
            {
                _diagnostics.Add($"Error parsing command line: {ex.Message} | {Truncate(commandLine, 200)}");
            }
        }

        private string ResolveDirectory(BuildEventContext? context)
        {
            if (context != null && _projectDirectories.TryGetValue(context.ProjectContextId, out string? dir))
            {
                return dir;
            }

            return System.IO.Directory.GetCurrentDirectory();
        }

        private static string Truncate(string value, int maxLength)
        {
            if (value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength) + "...";
        }
    }
}
