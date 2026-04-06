using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using MsBuildCompileCommands.Core.Extraction;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;

namespace MsBuildCompileCommands
{
    /// <summary>
    /// MSBuild logger that generates compile_commands.json during a build.
    ///
    /// Usage:
    ///   msbuild MyProject.sln -logger:MsBuildCompileCommands.Logger,MsBuildCompileCommands.dll
    ///   msbuild MyProject.sln -logger:MsBuildCompileCommands.Logger,MsBuildCompileCommands.dll;output=build/compile_commands.json;overwrite=true
    ///
    /// Parameters (semicolon-separated after the DLL path):
    ///   output=&lt;path&gt;       Output file path (default: compile_commands.json in the working directory)
    ///   overwrite=true|false  Overwrite existing file instead of merging (default: false)
    ///   project=Name1,Name2   Include only these projects (comma-separated)
    ///   configuration=Cfg     Include only these configurations (comma-separated)
    ///   flagrules=&lt;path&gt;       Path to custom flag translation rules JSON
    ///   translate=false         Disable all flag translation
    ///</summary>
    public sealed class Logger : ILogger
    {
        private CompileCommandCollector? _collector;
        private string _outputPath = "compile_commands.json";
        private bool _overwrite;
        private string? _projectFilter;
        private string? _configFilter;
        private string? _flagRulesPath;
        private bool _noTranslate;

        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Normal;

        public string? Parameters { get; set; }

        public void Initialize(IEventSource eventSource)
        {
            ParseParameters();

            FlagTranslator? translator = null;
            if (!_noTranslate)
            {
                IReadOnlyList<TranslationRule> rules;
                if (_flagRulesPath != null && System.IO.File.Exists(_flagRulesPath))
                {
                    try
                    {
                        rules = TranslationRuleLoader.Load(_flagRulesPath);
                    }
                    catch (Exception)
                    {
                        rules = TranslationRule.MsvcBuiltins();
                    }
                }
                else
                {
                    rules = TranslationRule.MsvcBuiltins();
                }
                translator = new FlagTranslator(rules);
            }

            _collector = new CompileCommandCollector(BuildFilter(), translator);

            eventSource.MessageRaised += OnMessageRaised;
            eventSource.ProjectStarted += OnProjectStarted;
            eventSource.BuildFinished += OnBuildFinished;
            eventSource.TaskStarted += OnTaskStarted;
            eventSource.TaskFinished += OnTaskFinished;
        }

        public void Shutdown()
        {
            // Nothing to clean up
        }

        private void OnProjectStarted(object sender, ProjectStartedEventArgs e)
        {
            _collector?.HandleEvent(e);
        }

        private void OnMessageRaised(object sender, BuildMessageEventArgs e)
        {
            _collector?.HandleEvent(e);
        }

        private void OnTaskStarted(object sender, TaskStartedEventArgs e)
        {
            _collector?.HandleEvent(e);
        }

        private void OnTaskFinished(object sender, TaskFinishedEventArgs e)
        {
            _collector?.HandleEvent(e);
        }

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

        private void WriteMessage(string message, MessageImportance importance)
        {
            // Use Console.Error since we don't have access to the build event context here
            // for creating proper build events
            Console.Error.WriteLine(message);
        }

        private void ParseParameters()
        {
            if (string.IsNullOrWhiteSpace(Parameters))
                return;

            string[] parts = Parameters!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                int eq = trimmed.IndexOf('=');
                if (eq < 0)
                    continue;

                string key = trimmed.Substring(0, eq).Trim();
                string value = trimmed.Substring(eq + 1).Trim();

                if (string.Equals(key, "output", StringComparison.OrdinalIgnoreCase))
                {
                    _outputPath = value;
                }
                else if (string.Equals(key, "overwrite", StringComparison.OrdinalIgnoreCase))
                {
                    _overwrite = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                }
                else if (string.Equals(key, "project", StringComparison.OrdinalIgnoreCase))
                {
                    _projectFilter = value;
                }
                else if (string.Equals(key, "configuration", StringComparison.OrdinalIgnoreCase))
                {
                    _configFilter = value;
                }
                else if (string.Equals(key, "flagrules", StringComparison.OrdinalIgnoreCase))
                {
                    _flagRulesPath = value;
                }
                else if (string.Equals(key, "translate", StringComparison.OrdinalIgnoreCase))
                {
                    _noTranslate = string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private CompileCommandFilter? BuildFilter()
        {
            HashSet<string>? projects = ParseCommaSeparated(_projectFilter);
            HashSet<string>? configurations = ParseCommaSeparated(_configFilter);

            if (projects == null && configurations == null)
                return null;

            return new CompileCommandFilter(projects, configurations);
        }

        private static HashSet<string>? ParseCommaSeparated(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in value!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = item.Trim();
                if (trimmed.Length > 0)
                    set.Add(trimmed);
            }

            return set.Count > 0 ? set : null;
        }
    }
}
