using System.Collections.Generic;
using System.Linq;
using MsBuildCompileCommands.Core.Extraction;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class GccClangCommandParserTests
    {
        private readonly GccClangCommandParser _parser;

        public GccClangCommandParserTests()
        {
            var rsp = new ResponseFileParser(_ => null);
            _parser = new GccClangCommandParser(rsp);
        }

        // ------------------------------------------------------------------ Detection

        [Theory]
        [InlineData("gcc -c main.c", true)]
        [InlineData("g++ -c main.cpp", true)]
        [InlineData("cc -c main.c", true)]
        [InlineData("c++ -c main.cpp", true)]
        [InlineData("clang -c main.c", true)]
        [InlineData("clang++ -c main.cpp", true)]
        [InlineData("gcc.exe -c main.c", true)]
        [InlineData("gcc-12 -c main.c", true)]
        [InlineData("clang++-17 -c main.cpp", true)]
        [InlineData(@"C:\msys2\mingw64\bin\g++.exe -c main.cpp", true)]
        [InlineData("/usr/bin/gcc -c main.c", true)]
        [InlineData("x86_64-linux-gnu-gcc -c main.c", true)]
        [InlineData("cl.exe /c main.cpp", false)]
        [InlineData("clang-cl.exe /c main.cpp", false)]
        [InlineData("link.exe /OUT:main.exe main.obj", false)]
        [InlineData("nvcc -c main.cu", false)]
        [InlineData("", false)]
        public void IsCompilerInvocation_detects_gcc_clang_family(string commandLine, bool expected)
        {
            Assert.Equal(expected, _parser.IsCompilerInvocation(commandLine));
        }

        // ------------------------------------------------------------------ Parsing

        [Fact]
        public void Parse_simple_gcc_command()
        {
            string cmd = "gcc -c -Wall -O2 main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            CompileCommand entry = commands[0];
            Assert.Contains("main.c", entry.File);
            Assert.Contains("-Wall", entry.Arguments);
            Assert.Contains("-O2", entry.Arguments);
            Assert.Contains("-c", entry.Arguments);
        }

        [Fact]
        public void Parse_clang_plus_plus_with_includes_and_defines_joined()
        {
            string cmd = "clang++ -c -I/usr/include -DDEBUG=1 -DFOO main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            Assert.Contains("-I/usr/include", commands[0].Arguments);
            Assert.Contains("-DDEBUG=1", commands[0].Arguments);
            Assert.Contains("-DFOO", commands[0].Arguments);
        }

        [Fact]
        public void Parse_clang_plus_plus_with_includes_and_defines_separate()
        {
            string cmd = "clang++ -c -I /usr/include -D DEBUG main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = new List<string>(commands[0].Arguments);
            int iIdx = args.IndexOf("-I");
            Assert.True(iIdx >= 0, "Expected -I flag");
            Assert.Equal("/usr/include", args[iIdx + 1]);
            int dIdx = args.IndexOf("-D");
            Assert.True(dIdx >= 0, "Expected -D flag");
            Assert.Equal("DEBUG", args[dIdx + 1]);
        }

        [Fact]
        public void Output_flag_and_value_are_excluded()
        {
            string cmd = "gcc -c -o main.o main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            Assert.DoesNotContain("-o", commands[0].Arguments);
            Assert.DoesNotContain("main.o", commands[0].Arguments);
        }

        [Fact]
        public void Dependency_flags_are_excluded()
        {
            string cmd = "gcc -c -MMD -MP -MF main.d -MT main.o main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = commands[0].Arguments;
            Assert.DoesNotContain("-MMD", args);
            Assert.DoesNotContain("-MP", args);
            Assert.DoesNotContain("-MF", args);
            Assert.DoesNotContain("main.d", args);
            Assert.DoesNotContain("-MT", args);
            Assert.DoesNotContain("main.o", args);
        }

        [Fact]
        public void Warning_optimization_and_fpic_flags_are_preserved()
        {
            string cmd = "gcc -c -Wall -Wextra -Werror -O2 -fPIC main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = commands[0].Arguments;
            Assert.Contains("-Wall", args);
            Assert.Contains("-Wextra", args);
            Assert.Contains("-Werror", args);
            Assert.Contains("-O2", args);
            Assert.Contains("-fPIC", args);
        }

        [Fact]
        public void Isystem_and_iquote_are_preserved_with_values()
        {
            string cmd = "gcc -c -isystem /usr/include/boost -iquote ./src main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = new List<string>(commands[0].Arguments);
            int sysIdx = args.IndexOf("-isystem");
            Assert.True(sysIdx >= 0, "Expected -isystem");
            Assert.Equal("/usr/include/boost", args[sysIdx + 1]);
            int quoteIdx = args.IndexOf("-iquote");
            Assert.True(quoteIdx >= 0, "Expected -iquote");
            Assert.Equal("./src", args[quoteIdx + 1]);
        }

        [Fact]
        public void Include_forced_include_is_preserved_with_value()
        {
            string cmd = "gcc -c -include pch.h main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = new List<string>(commands[0].Arguments);
            int idx = args.IndexOf("-include");
            Assert.True(idx >= 0, "Expected -include");
            Assert.Equal("pch.h", args[idx + 1]);
        }

        [Fact]
        public void Multiple_source_files_produce_multiple_entries()
        {
            string cmd = "gcc -c -Wall foo.c bar.c baz.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Equal(3, commands.Count);
            Assert.Contains(commands, c => c.File.Contains("foo.c"));
            Assert.Contains(commands, c => c.File.Contains("bar.c"));
            Assert.Contains(commands, c => c.File.Contains("baz.cpp"));
        }

        [Fact]
        public void Versioned_compiler_path_preserved_in_arguments()
        {
            string cmd = "gcc-12 -c main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            Assert.Equal("gcc-12", commands[0].Arguments[0]);
        }

        [Fact]
        public void Cross_compiler_path_preserved()
        {
            string cmd = "x86_64-linux-gnu-g++ -c main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            Assert.Equal("x86_64-linux-gnu-g++", commands[0].Arguments[0]);
        }

        [Fact]
        public void Quoted_windows_path_with_spaces_is_parsed()
        {
            string cmd = @"""C:\Program Files\LLVM\bin\clang++.exe"" -c main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");

            Assert.Single(commands);
            // Compiler token should be the unquoted path
            Assert.Contains("clang++", commands[0].Arguments[0]);
            Assert.Contains("main.cpp", commands[0].File);
        }

        [Fact]
        public void Linker_flags_are_excluded()
        {
            string cmd = "gcc -c -lfoo -L/usr/lib main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = commands[0].Arguments;
            Assert.DoesNotContain("-lfoo", args);
            Assert.DoesNotContain("-L/usr/lib", args);
        }

        [Fact]
        public void Cu_source_files_are_recognized()
        {
            string cmd = "gcc -c kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            Assert.Contains("kernel.cu", commands[0].File);
        }

        [Fact]
        public void Dash_c_not_duplicated_when_already_present()
        {
            string cmd = "gcc -c -Wall main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = new List<string>(commands[0].Arguments);
            int count = args.Count(a => a == "-c");
            Assert.Equal(1, count);
        }

        [Fact]
        public void Dash_c_added_when_not_present()
        {
            // Some build systems emit gcc without -c when the parser still classifies it
            // Here we parse a line with a source file but no -c
            string cmd = "gcc -Wall main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            Assert.Contains("-c", commands[0].Arguments);
        }

        [Fact]
        public void File_is_last_argument()
        {
            string cmd = "gcc -c -Wall main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = new List<string>(commands[0].Arguments);
            Assert.Contains("main.c", args[args.Count - 1]);
        }

        [Fact]
        public void Non_compiler_returns_empty()
        {
            string cmd = "link.exe /OUT:foo.exe foo.obj";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");
            Assert.Empty(commands);
        }

        [Fact]
        public void Md_standalone_flag_excluded()
        {
            string cmd = "gcc -c -MD main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            Assert.DoesNotContain("-MD", commands[0].Arguments);
        }

        [Fact]
        public void Mq_flag_with_value_excluded()
        {
            string cmd = "gcc -c -MQ main.o main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = commands[0].Arguments;
            Assert.DoesNotContain("-MQ", args);
            Assert.DoesNotContain("main.o", args);
        }

        [Fact]
        public void Idirafter_preserved_with_value()
        {
            string cmd = "gcc -c -idirafter /opt/include main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = new List<string>(commands[0].Arguments);
            int idx = args.IndexOf("-idirafter");
            Assert.True(idx >= 0, "Expected -idirafter");
            Assert.Equal("/opt/include", args[idx + 1]);
        }

        [Fact]
        public void Unexpanded_response_file_not_treated_as_source()
        {
            string cmd = "gcc -c @flags.rsp main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            // main.c should be recognized; @flags.rsp should not become a source file
            Assert.Single(commands);
            Assert.Contains("main.c", commands[0].File);
            Assert.DoesNotContain(commands[0].Arguments, a => a.Contains(".rsp"));
        }

        [Fact]
        public void Undefine_flag_is_preserved()
        {
            string cmd = "gcc -c -UFOO main.c";
            var commands = _parser.Parse(cmd, "/home/user/project");
            Assert.Single(commands);
            Assert.Contains("-UFOO", commands[0].Arguments);
        }

        [Fact]
        public void Std_flag_is_preserved()
        {
            string cmd = "g++ -c -std=c++17 main.cpp";
            var commands = _parser.Parse(cmd, "/home/user/project");
            Assert.Single(commands);
            Assert.Contains("-std=c++17", commands[0].Arguments);
        }

        [Fact]
        public void Standalone_M_and_MM_flags_are_excluded()
        {
            string cmd = "gcc -c -M main.c";
            var commands = _parser.Parse(cmd, "/home/user/project");
            Assert.Single(commands);
            Assert.DoesNotContain("-M", commands[0].Arguments);

            string cmd2 = "gcc -c -MM main.c";
            var commands2 = _parser.Parse(cmd2, "/home/user/project");
            Assert.Single(commands2);
            Assert.DoesNotContain("-MM", commands2[0].Arguments);
        }
    }
}
