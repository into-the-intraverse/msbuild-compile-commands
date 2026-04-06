using System;
using System.Collections;
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
        private readonly CompileCommandFilter? _filter;
        private readonly Dictionary<int, string> _projectNames = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _projectConfigurations = new Dictionary<int, string>();

        private readonly List<string> _diagnostics = new List<string>();

        public CompileCommandCollector() : this(new ClCommandParser(), null) { }

        public CompileCommandCollector(CompileCommandFilter? filter) : this(new ClCommandParser(), filter) { }

        public CompileCommandCollector(ClCommandParser parser) : this(parser, null) { }

        public CompileCommandCollector(ClCommandParser parser, CompileCommandFilter? filter)
        {
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
            _filter = filter;
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
                _projectNames[contextId] = Path.GetFileNameWithoutExtension(e.ProjectFile);

                if (e.Properties != null)
                {
                    foreach (DictionaryEntry entry in e.Properties)
                    {
                        if (entry.Key is string key &&
                            string.Equals(key, "Configuration", StringComparison.OrdinalIgnoreCase))
                        {
                            _projectConfigurations[contextId] = entry.Value as string ?? "";
                            break;
                        }
                    }
                }
            }
        }

        private void HandleTaskCommandLine(TaskCommandLineEventArgs e)
        {
            string? commandLine = e.CommandLine;
            if (string.IsNullOrWhiteSpace(commandLine))
                return;

            if (!ClCommandParser.IsCompilerInvocation(commandLine))
                return;

            if (!PassesFilter(e.BuildEventContext))
                return;

            string directory = ResolveDirectory(e.BuildEventContext);

            try
            {
                List<CompileCommand> commands = _parser.Parse(commandLine, directory, _diagnostics);

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

        private bool PassesFilter(BuildEventContext? context)
        {
            if (_filter == null)
                return true;

            int contextId = context?.ProjectContextId ?? -1;

            if (_filter.Projects != null)
            {
                if (contextId < 0 || !_projectNames.TryGetValue(contextId, out string? name) ||
                    !_filter.Projects.Contains(name))
                {
                    return false;
                }
            }

            if (_filter.Configurations != null)
            {
                if (contextId < 0 || !_projectConfigurations.TryGetValue(contextId, out string? config) ||
                    !_filter.Configurations.Contains(config))
                {
                    return false;
                }
            }

            return true;
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
