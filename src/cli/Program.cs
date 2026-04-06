using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;
using MsBuildCompileCommands.Core.Extraction;
using MsBuildCompileCommands.Core.IO;

namespace MsBuildCompileCommands.Cli
{
    internal static class Program
    {
        private const string Version = "0.1.1";

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
            bool merge = false;

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
                else if (string.Equals(arg, "--merge", StringComparison.OrdinalIgnoreCase))
                {
                    merge = true;
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

            return GenerateFromBinlog(binlogPath, outputPath, merge);
        }

        private static int GenerateFromBinlog(string binlogPath, string outputPath, bool merge)
        {
            Console.Error.WriteLine($"Reading {binlogPath}...");

            var collector = new CompileCommandCollector();

            try
            {
                var reader = new BinLogReader();

                // BinLogReader raises events as it replays the binlog
                reader.AnyEventRaised += (sender, e) => collector.HandleEvent(e);

                reader.Replay(binlogPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error reading binlog: {ex.Message}");
                return 1;
            }

            List<MsBuildCompileCommands.Core.Models.CompileCommand> commands = collector.GetCommands();

            if (commands.Count == 0)
            {
                Console.Error.WriteLine("Warning: No C/C++ compilation steps found in the binlog.");
                Console.Error.WriteLine("Ensure the build included cl.exe or clang-cl invocations.");

                foreach (string diag in collector.Diagnostics)
                {
                    Console.Error.WriteLine($"  {diag}");
                }
                return 0;
            }

            try
            {
                string fullOutputPath = Path.GetFullPath(outputPath);
                CompileCommandsWriter.Write(fullOutputPath, commands, merge);
                Console.Error.WriteLine($"Wrote {commands.Count} entries to {fullOutputPath}");
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
    --merge                 Merge with existing file instead of overwriting
    -h, --help              Show this help message
    --version               Show version

EXAMPLES:
    # Generate from a binlog
    MsBuildCompileCommands build.binlog

    # Specify output path
    MsBuildCompileCommands build.binlog -o build/compile_commands.json

    # Merge with existing database
    MsBuildCompileCommands build.binlog --merge

    # Typical CMake + Visual Studio workflow
    cmake -B build -G ""Visual Studio 17 2022""
    cmake --build build -- /bl:build.binlog
    MsBuildCompileCommands build.binlog -o compile_commands.json

    # Live logger (no binlog needed)
    msbuild MyProject.sln /logger:MsBuildCompileCommands.Logger,path\to\MsBuildCompileCommands.dll
");
        }
    }
}
