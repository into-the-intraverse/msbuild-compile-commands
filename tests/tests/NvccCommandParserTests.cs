using System.Collections.Generic;
using System.Linq;
using MsBuildCompileCommands.Core.Extraction;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class NvccCommandParserTests
    {
        private readonly NvccCommandParser _parser;

        public NvccCommandParserTests()
        {
            var rsp = new ResponseFileParser(_ => null);
            _parser = new NvccCommandParser(rsp);
        }

        // ------------------------------------------------------------------ Detection

        [Theory]
        [InlineData("nvcc -c kernel.cu", true)]
        [InlineData("nvcc.exe -c kernel.cu", true)]
        [InlineData(@"C:\CUDA\bin\nvcc.exe -c kernel.cu", true)]
        [InlineData("/usr/local/cuda/bin/nvcc -c kernel.cu", true)]
        [InlineData("gcc -c main.c", false)]
        [InlineData("cl.exe /c main.cpp", false)]
        [InlineData("", false)]
        public void IsCompilerInvocation_detects_nvcc(string commandLine, bool expected)
        {
            Assert.Equal(expected, _parser.IsCompilerInvocation(commandLine));
        }

        // ------------------------------------------------------------------ Parsing

        [Fact]
        public void Parse_simple_nvcc_command()
        {
            string cmd = "nvcc -c kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            CompileCommand entry = commands[0];
            Assert.Contains("kernel.cu", entry.File);
            Assert.Contains("-c", entry.Arguments);
            Assert.Equal("nvcc", entry.Arguments[0]);
        }

        [Fact]
        public void Parse_include_and_define_joined_forms_preserved()
        {
            string cmd = "nvcc -c -I/usr/include -DDEBUG=1 -DFOO kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = commands[0].Arguments;
            Assert.Contains("-I/usr/include", args);
            Assert.Contains("-DDEBUG=1", args);
            Assert.Contains("-DFOO", args);
        }

        [Fact]
        public void Parse_include_and_define_separate_forms_preserved()
        {
            string cmd = "nvcc -c -I /usr/include -D DEBUG kernel.cu";
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
        public void Xcompiler_flag_expanded_from_comma_separated_value()
        {
            string cmd = @"nvcc -c -Xcompiler ""-O2,-Wall,-fPIC"" kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = commands[0].Arguments;
            Assert.Contains("-O2", args);
            Assert.Contains("-Wall", args);
            Assert.Contains("-fPIC", args);
            Assert.DoesNotContain("-Xcompiler", args);
        }

        [Fact]
        public void Compiler_options_long_form_expanded_from_comma_separated_value()
        {
            string cmd = @"nvcc -c --compiler-options ""-O2,-Wall"" kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = commands[0].Arguments;
            Assert.Contains("-O2", args);
            Assert.Contains("-Wall", args);
            Assert.DoesNotContain("--compiler-options", args);
        }

        [Fact]
        public void Gpu_architecture_flag_excluded()
        {
            string cmd = "nvcc -c --gpu-architecture=sm_75 kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = commands[0].Arguments;
            Assert.DoesNotContain("--gpu-architecture=sm_75", args);
        }

        [Fact]
        public void Gencode_flag_excluded()
        {
            string cmd = "nvcc -c -gencode arch=compute_75,code=sm_75 kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = commands[0].Arguments;
            Assert.DoesNotContain("-gencode", args);
            Assert.DoesNotContain("arch=compute_75,code=sm_75", args);
        }

        [Fact]
        public void Output_flag_excluded_with_value()
        {
            string cmd = "nvcc -c -o kernel.o kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = commands[0].Arguments;
            Assert.DoesNotContain("-o", args);
            Assert.DoesNotContain("kernel.o", args);
        }

        [Fact]
        public void Std_flag_preserved()
        {
            string cmd = "nvcc -c -std=c++17 kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            Assert.Contains("-std=c++17", commands[0].Arguments);
        }

        [Fact]
        public void Dependency_flags_excluded()
        {
            string cmd = "nvcc -c -MD -MF kernel.d kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = commands[0].Arguments;
            Assert.DoesNotContain("-MD", args);
            Assert.DoesNotContain("-MF", args);
            Assert.DoesNotContain("kernel.d", args);
        }

        [Fact]
        public void Multiple_source_files_produce_multiple_entries()
        {
            string cmd = "nvcc -c kernel1.cu kernel2.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Equal(2, commands.Count);
            Assert.Contains(commands, c => c.File.Contains("kernel1.cu"));
            Assert.Contains(commands, c => c.File.Contains("kernel2.cu"));
        }

        [Fact]
        public void Cpp_source_file_recognized()
        {
            string cmd = "nvcc -c wrapper.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            Assert.Contains("wrapper.cpp", commands[0].File);
        }

        [Fact]
        public void Dash_c_not_duplicated_when_already_present()
        {
            string cmd = "nvcc -c kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = new List<string>(commands[0].Arguments);
            int count = args.Count(a => a == "-c");
            Assert.Equal(1, count);
        }

        [Fact]
        public void Dash_c_added_when_not_present()
        {
            string cmd = "nvcc kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            Assert.Contains("-c", commands[0].Arguments);
        }

        [Fact]
        public void File_is_last_argument()
        {
            string cmd = "nvcc -c -Wall kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            var args = new List<string>(commands[0].Arguments);
            Assert.Contains("kernel.cu", args[args.Count - 1]);
        }

        [Fact]
        public void Non_nvcc_command_returns_empty()
        {
            string cmd = "gcc -c main.c";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");
            Assert.Empty(commands);
        }

        [Fact]
        public void Gpu_code_flag_with_equals_excluded()
        {
            string cmd = "nvcc -c --gpu-code=sm_75 kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            Assert.DoesNotContain("--gpu-code=sm_75", commands[0].Arguments);
        }

        [Fact]
        public void Relocatable_device_code_with_equals_excluded()
        {
            string cmd = "nvcc -c --relocatable-device-code=true kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            Assert.DoesNotContain("--relocatable-device-code=true", commands[0].Arguments);
        }

        [Fact]
        public void Arch_short_with_equals_excluded()
        {
            string cmd = "nvcc -c -arch=sm_75 kernel.cu";
            List<CompileCommand> commands = _parser.Parse(cmd, "/project");

            Assert.Single(commands);
            Assert.DoesNotContain("-arch=sm_75", commands[0].Arguments);
        }
    }
}
