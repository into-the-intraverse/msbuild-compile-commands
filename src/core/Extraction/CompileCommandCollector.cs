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
        private readonly CompileCommandFilter? _filter;
        private readonly Dictionary<int, string> _projectNames = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _projectConfigurations = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _projectFiles = new Dictionary<int, string>();
        private readonly TaskMapperRegistry _taskMapperRegistry = new TaskMapperRegistry();
        private readonly Dictionary<int, TaskState> _activeTasks = new Dictionary<int, TaskState>();

        private readonly FlagTranslator? _translator;
        private readonly List<string> _diagnostics = new List<string>();

        private sealed class TaskState
        {
            public string TaskName;
            public bool HadCommandLine;
            public Dictionary<string, List<string>> Parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public TaskState(string taskName) { TaskName = taskName; }
        }

        public CompileCommandCollector() : this(null, null) { }

        public CompileCommandCollector(CompileCommandFilter? filter) : this(filter, null) { }

        public CompileCommandCollector(CompileCommandFilter? filter, FlagTranslator? translator)
        {
            _filter = filter;
            _translator = translator;
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
                case TaskStartedEventArgs taskStarted:
                    HandleTaskStarted(taskStarted);
                    break;
                case TaskCommandLineEventArgs taskCommandLine:
                    HandleTaskCommandLine(taskCommandLine);
                    break;
                case TaskFinishedEventArgs taskFinished:
                    HandleTaskFinished(taskFinished);
                    break;
                default:
                    HandlePossibleTaskParameter(e);
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

        private void HandleTaskStarted(TaskStartedEventArgs e)
        {
            if (e.TaskName == null || e.BuildEventContext == null)
                return;
            int nodeId = e.BuildEventContext.NodeId;
            _activeTasks[nodeId] = new TaskState(e.TaskName);
        }

        private void HandleTaskFinished(TaskFinishedEventArgs e)
        {
            if (e.BuildEventContext == null)
                return;
            int nodeId = e.BuildEventContext.NodeId;
            if (!_activeTasks.TryGetValue(nodeId, out TaskState? state))
                return;
            _activeTasks.Remove(nodeId);

            if (state.HadCommandLine || state.Parameters.Count == 0)
                return;
            if (!PassesFilter(e.BuildEventContext))
                return;

            string directory = ResolveDirectory(e.BuildEventContext);
            try
            {
                List<CompileCommand> commands = _taskMapperRegistry.TryMap(state.TaskName, state.Parameters, directory);
                foreach (CompileCommand cmd in commands)
                {
                    CompileCommand final = _translator != null ? _translator.Translate(cmd) : cmd;
                    if (!_commands.ContainsKey(final.DeduplicationKey))
                        _commands[final.DeduplicationKey] = final;
                }
            }
            catch (Exception ex)
            {
                _diagnostics.Add($"Error mapping task parameters for '{state.TaskName}': {ex.Message}");
            }
        }

        private void HandlePossibleTaskParameter(BuildEventArgs e)
        {
            if (!(e is TaskParameterEventArgs taskParam))
                return;
            if (e.BuildEventContext == null)
                return;
            if (taskParam.Kind != TaskParameterMessageKind.TaskInput)
                return;

            int nodeId = e.BuildEventContext.NodeId;
            if (!_activeTasks.TryGetValue(nodeId, out TaskState? state))
                return;

            string paramName = taskParam.ItemType;
            if (string.IsNullOrEmpty(paramName))
                return;

            var values = new List<string>();
            if (taskParam.Items != null)
            {
                foreach (object? item in taskParam.Items)
                {
                    if (item is ITaskItem taskItem)
                    {
                        values.Add(taskItem.ItemSpec);
                    }
                    else if (item != null)
                    {
                        string? str = item.ToString();
                        if (str != null)
                            values.Add(str);
                    }
                }
            }
            if (values.Count > 0)
                state.Parameters[paramName] = values;
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
                _projectFiles[contextId] = e.ProjectFile;

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
            if (e.BuildEventContext != null)
            {
                int nodeId = e.BuildEventContext.NodeId;
                if (_activeTasks.TryGetValue(nodeId, out TaskState? activeState))
                    activeState.HadCommandLine = true;
            }

            string? commandLine = e.CommandLine;
            if (string.IsNullOrWhiteSpace(commandLine))
                return;

            ICommandParser? parser = CommandParserFactory.FindParser(commandLine);
            if (parser == null)
                return;

            if (!PassesFilter(e.BuildEventContext))
                return;

            string directory = ResolveDirectory(e.BuildEventContext);

            try
            {
                List<CompileCommand> commands = parser.Parse(commandLine, directory, _diagnostics);
                foreach (CompileCommand cmd in commands)
                {
                    CompileCommand final = _translator != null ? _translator.Translate(cmd) : cmd;
                    _commands[final.DeduplicationKey] = final;
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

        public List<string> GetProjectPaths()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();
            foreach (string projectFile in _projectFiles.Values)
            {
                if (projectFile.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase) && seen.Add(projectFile))
                    paths.Add(projectFile);
            }
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths;
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
