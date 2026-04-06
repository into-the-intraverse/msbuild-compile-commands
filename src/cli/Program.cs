using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;
using MsBuildCompileCommands.Cli.Evaluation;
using MsBuildCompileCommands.Core.Extraction;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;

namespace MsBuildCompileCommands.Cli
{
    internal static class Program
    {
        private static readonly string Version =
            typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion?.Split('+')[0]
            ?? "0.0.0";

        private static int Main(string[] args)
        {
            try { Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults(); }
            catch (Exception) { /* MSBuild not found — --evaluate will fail gracefully later */ }

            if (args.Length == 0 || HasFlag(args, "--help") || HasFlag(args, "-h"))
            {
                PrintUsage();
                return 0;
            }

            if (HasFlag(args, "--version"))
            {
                Console.WriteLine($"MsBuildCompileCommands {Version}");
                return 0;
            }

            string? binlogPath = null;
            string outputPath = "compile_commands.json";
            bool overwrite = false;
            string? projectFilter = null;
            string? configFilter = null;
            bool evaluate = false;
            string? flagRulesPath = null;
            bool noTranslate = false;
            bool dumpRules = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                if (string.Equals(arg, "--output", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "-o", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        outputPath = args[++i];
                    else
                    {
                        Console.Error.WriteLine("Error: --output requires a path argument.");
                        return 1;
                    }
                }
                else if (string.Equals(arg, "--overwrite", StringComparison.OrdinalIgnoreCase))
                {
                    overwrite = true;
                }
                else if (string.Equals(arg, "--project", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        projectFilter = args[++i];
                    else
                    {
                        Console.Error.WriteLine("Error: --project requires a value.");
                        return 1;
                    }
                }
                else if (arg.StartsWith("--project=", StringComparison.OrdinalIgnoreCase))
                {
                    projectFilter = arg.Substring("--project=".Length);
                }
                else if (string.Equals(arg, "--configuration", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        configFilter = args[++i];
                    else
                    {
                        Console.Error.WriteLine("Error: --configuration requires a value.");
                        return 1;
                    }
                }
                else if (arg.StartsWith("--configuration=", StringComparison.OrdinalIgnoreCase))
                {
                    configFilter = arg.Substring("--configuration=".Length);
                }
                else if (string.Equals(arg, "--evaluate", StringComparison.OrdinalIgnoreCase))
                {
                    evaluate = true;
                }
                else if (string.Equals(arg, "--flag-rules", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        flagRulesPath = args[++i];
                    else
                    {
                        Console.Error.WriteLine("Error: --flag-rules requires a path argument.");
                        return 1;
                    }
                }
                else if (arg.StartsWith("--flag-rules=", StringComparison.OrdinalIgnoreCase))
                {
                    flagRulesPath = arg.Substring("--flag-rules=".Length);
                }
                else if (string.Equals(arg, "--no-translate", StringComparison.OrdinalIgnoreCase))
                {
                    noTranslate = true;
                }
                else if (string.Equals(arg, "--dump-rules", StringComparison.OrdinalIgnoreCase))
                {
                    dumpRules = true;
                }
                else if (!arg.StartsWith("-", StringComparison.Ordinal))
                {
                    binlogPath = arg;
                }
                else
                {
                    Console.Error.WriteLine($"Error: Unknown option '{arg}'. Use --help for usage.");
                    return 1;
                }
            }

            if (dumpRules)
            {
                Console.WriteLine(TranslationRuleLoader.Serialize(TranslationRule.MsvcBuiltins()));
                return 0;
            }

            if (binlogPath == null)
            {
                Console.Error.WriteLine("Error: No .binlog file specified. Use --help for usage.");
                return 1;
            }

            if (!File.Exists(binlogPath))
            {
                Console.Error.WriteLine($"Error: File not found: {binlogPath}");
                return 1;
            }

            return GenerateFromBinlog(binlogPath, outputPath, overwrite, projectFilter, configFilter, evaluate, flagRulesPath, noTranslate);
        }

        private static int GenerateFromBinlog(string binlogPath, string outputPath, bool overwrite,
            string? projectFilter, string? configFilter, bool evaluate, string? flagRulesPath, bool noTranslate)
        {
            Console.Error.WriteLine($"Reading {binlogPath}...");

            CompileCommandFilter? filter = BuildFilter(projectFilter, configFilter);

            FlagTranslator? translator = null;
            if (!noTranslate)
            {
                IReadOnlyList<TranslationRule> rules;
                if (flagRulesPath != null)
                {
                    if (!File.Exists(flagRulesPath))
                    {
                        Console.Error.WriteLine($"Error: Flag rules file not found: {flagRulesPath}");
                        return 1;
                    }
                    try
                    {
                        rules = TranslationRuleLoader.Load(flagRulesPath);
                        Console.Error.WriteLine($"Loaded {rules.Count} translation rules from {flagRulesPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error loading flag rules: {ex.Message}");
                        return 1;
                    }
                }
                else
                {
                    rules = TranslationRule.MsvcBuiltins();
                }
                translator = new FlagTranslator(rules);
            }
            var collector = new CompileCommandCollector(filter, translator);

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

            if (evaluate)
            {
                var capturedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (CompileCommand cmd in commands)
                    capturedFiles.Add(cmd.DeduplicationKey);

                var evaluator = new ProjectEvaluator();
                List<string> projectPaths = collector.GetProjectPaths();
                int evalCount = 0;

                foreach (string projectPath in projectPaths)
                {
                    if (!File.Exists(projectPath))
                    {
                        Console.Error.WriteLine($"  Warning: project file not found for evaluation: {projectPath}");
                        continue;
                    }

                    // Pass first configuration from filter, if any
                    string? config = null;
                    if (configFilter != null)
                    {
                        string[] parts = configFilter.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0) config = parts[0].Trim();
                    }

                    List<CompileCommand> evalCommands = evaluator.Evaluate(projectPath, config);
                    foreach (CompileCommand cmd in evalCommands)
                    {
                        if (capturedFiles.Add(cmd.DeduplicationKey))
                        {
                            commands.Add(cmd);
                            evalCount++;
                        }
                    }
                }

                if (evalCount > 0)
                    Console.Error.WriteLine($"  Evaluation added {evalCount} entries not captured by build events");

                foreach (string diag in evaluator.Diagnostics)
                    Console.Error.WriteLine($"  {diag}");
            }

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

        private static bool HasFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static CompileCommandFilter? BuildFilter(string? projectFilter, string? configFilter)
        {
            HashSet<string>? projects = ParseCommaSeparated(projectFilter);
            HashSet<string>? configurations = ParseCommaSeparated(configFilter);

            if (projects == null && configurations == null)
                return null;

            return new CompileCommandFilter(projects, configurations);
        }

        private static HashSet<string>? ParseCommaSeparated(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in value.Split(','))
            {
                string trimmed = item.Trim();
                if (trimmed.Length > 0)
                    set.Add(trimmed);
            }

            return set.Count > 0 ? set : null;
        }

        private static void PrintUsage()
        {
            Console.WriteLine($@"MsBuildCompileCommands {Version}
Generate compile_commands.json from MSBuild binary logs.

USAGE:
    MsBuildCompileCommands <binlog-path> [OPTIONS]

ARGUMENTS:
    <binlog-path>           Path to the MSBuild .binlog file

OPTIONS:
    -o, --output <path>     Output file path (default: compile_commands.json)
    --overwrite             Overwrite existing file instead of merging
    --project <names>       Include only these projects (comma-separated, matched by name)
    --configuration <names> Include only these configurations (comma-separated)
    --evaluate              After binlog replay, evaluate .vcxproj files to fill in
                            source files not captured by build events
    --flag-rules <path>     Path to custom flag translation rules JSON file
                            (replaces built-in MSVC→clang rules entirely)
    --no-translate          Disable all flag translation (built-in and custom)
    --dump-rules            Print built-in translation rules as JSON and exit
    -h, --help              Show this help message
    --version               Show version

EXAMPLES:
    # Generate from a binlog
    MsBuildCompileCommands build.binlog

    # Specify output path
    MsBuildCompileCommands build.binlog -o build/compile_commands.json

    # Overwrite instead of merging with existing database
    MsBuildCompileCommands build.binlog --overwrite

    # Typical CMake + Visual Studio workflow
    cmake -B build -G ""Visual Studio 17 2022""
    cmake --build build -- /bl:build.binlog
    MsBuildCompileCommands build.binlog -o compile_commands.json

    # Filter by project
    MsBuildCompileCommands build.binlog --project=MyApp,MyLib

    # Filter by configuration
    MsBuildCompileCommands build.binlog --configuration=Release

    # Live logger (no binlog needed)
    msbuild MyProject.sln /logger:MsBuildCompileCommands.Logger,path\to\MsBuildCompileCommands.dll
");
        }
    }
}
