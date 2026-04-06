using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;
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

            return GenerateFromBinlog(binlogPath, outputPath, overwrite, projectFilter, configFilter);
        }

        private static int GenerateFromBinlog(string binlogPath, string outputPath, bool overwrite,
            string? projectFilter, string? configFilter)
        {
            Console.Error.WriteLine($"Reading {binlogPath}...");

            CompileCommandFilter? filter = BuildFilter(projectFilter, configFilter);
            var collector = new CompileCommandCollector(filter);

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
