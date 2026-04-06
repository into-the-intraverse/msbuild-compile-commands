using System;
using System.IO;
using Microsoft.Build.Framework;
using MsBuildCompileCommands.Core.Extraction;
using MsBuildCompileCommands.Core.IO;

namespace MsBuildCompileCommands
{
    /// <summary>
    /// MSBuild logger that generates compile_commands.json during a build.
    ///
    /// Usage:
    ///   msbuild MyProject.sln -logger:MsBuildCompileCommands.Logger,MsBuildCompileCommands.dll
    ///   msbuild MyProject.sln -logger:MsBuildCompileCommands.Logger,MsBuildCompileCommands.dll;output=build/compile_commands.json;merge=true
    ///
    /// Parameters (semicolon-separated after the DLL path):
    ///   output=&lt;path&gt;   Output file path (default: compile_commands.json in the working directory)
    ///   merge=true|false  Merge with existing file instead of overwriting (default: false)
    /// </summary>
    public sealed class Logger : ILogger
    {
        private CompileCommandCollector? _collector;
        private string _outputPath = "compile_commands.json";
        private bool _merge;

        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Normal;

        public string? Parameters { get; set; }

        public void Initialize(IEventSource eventSource)
        {
            ParseParameters();

            _collector = new CompileCommandCollector();

            eventSource.MessageRaised += OnMessageRaised;
            eventSource.ProjectStarted += OnProjectStarted;
            eventSource.BuildFinished += OnBuildFinished;

            // TaskCommandLineEventArgs is delivered via MessageRaised (it inherits BuildMessageEventArgs)
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
            if (e is TaskCommandLineEventArgs taskCmd)
            {
                _collector?.HandleEvent(taskCmd);
            }
        }

        private void OnBuildFinished(object sender, BuildFinishedEventArgs e)
        {
            if (_collector == null)
                return;

            var commands = _collector.GetCommands();

            if (commands.Count == 0)
            {
                WriteMessage("MsBuildCompileCommands: No C/C++ compilation steps detected. " +
                    "Ensure the build includes cl.exe or clang-cl invocations.", MessageImportance.High);

                foreach (string diag in _collector.Diagnostics)
                {
                    WriteMessage($"MsBuildCompileCommands: {diag}", MessageImportance.Normal);
                }
                return;
            }

            try
            {
                string outputPath = Path.GetFullPath(_outputPath);
                CompileCommandsWriter.Write(outputPath, commands, _merge);

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
                else if (string.Equals(key, "merge", StringComparison.OrdinalIgnoreCase))
                {
                    _merge = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }
}
