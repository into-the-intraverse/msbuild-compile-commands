using System.Collections.Generic;
using System.Linq;
using MsBuildCompileCommands.Core.Extraction;
using MsBuildCompileCommands.Core.IO;
using MsBuildCompileCommands.Core.Models;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class ClCommandParserTests
    {
        private readonly ClCommandParser _parser;

        public ClCommandParserTests()
        {
            // Use a ResponseFileParser with a no-op reader (tests don't need file I/O)
            var rsp = new ResponseFileParser(_ => null);
            _parser = new ClCommandParser(rsp);
        }

        [Theory]
        [InlineData("cl.exe /c main.cpp", true)]
        [InlineData("cl /c main.cpp", true)]
        [InlineData("clang-cl.exe /c main.cpp", true)]
        [InlineData("clang-cl /c main.cpp", true)]
        [InlineData(@"C:\VS\VC\bin\cl.exe /c main.cpp", true)]
        [InlineData("link.exe /OUT:main.exe main.obj", false)]
        [InlineData("lib.exe /OUT:static.lib obj1.obj", false)]
        [InlineData("", false)]
        public void IsCompilerInvocation_detects_cl_and_clang_cl(string commandLine, bool expected)
        {
            Assert.Equal(expected, ClCommandParser.IsCompilerInvocation(commandLine));
        }

        [Fact]
        public void Parse_simple_cl_command()
        {
            string cmd = "cl.exe /c /EHsc /std:c++17 main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");

            Assert.Single(commands);
            CompileCommand entry = commands[0];
            Assert.Contains("main.cpp", entry.File);
            Assert.Contains("/EHsc", entry.Arguments);
            Assert.Contains("/std:c++17", entry.Arguments);
            Assert.Contains("/c", entry.Arguments);
        }

        [Fact]
        public void Parse_multiple_source_files()
        {
            string cmd = "cl.exe /c /EHsc foo.cpp bar.cpp baz.c";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");

            Assert.Equal(3, commands.Count);
            Assert.Contains(commands, c => c.File.Contains("foo.cpp"));
            Assert.Contains(commands, c => c.File.Contains("bar.cpp"));
            Assert.Contains(commands, c => c.File.Contains("baz.c"));
        }

        [Fact]
        public void Parse_include_directories_joined_form()
        {
            string cmd = @"cl.exe /c /IC:\sdk\include /IC:\project\src main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");

            Assert.Single(commands);
            Assert.Contains(@"/IC:\sdk\include", commands[0].Arguments);
            Assert.Contains(@"/IC:\project\src", commands[0].Arguments);
        }

        [Fact]
        public void Parse_include_directories_separate_form()
        {
            string cmd = @"cl.exe /c /I C:\sdk\include main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");

            Assert.Single(commands);
            var args = new List<string>(commands[0].Arguments);
            // /I should be followed by the directory as separate argument
            int idx = args.IndexOf("/I");
            Assert.True(idx >= 0, "Expected /I flag in arguments");
            Assert.Equal(@"C:\sdk\include", args[idx + 1]);
        }

        [Fact]
        public void Parse_defines()
        {
            string cmd = "cl.exe /c /DWIN32 /D_DEBUG /DVERSION=42 main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");

            Assert.Single(commands);
            Assert.Contains("/DWIN32", commands[0].Arguments);
            Assert.Contains("/D_DEBUG", commands[0].Arguments);
            Assert.Contains("/DVERSION=42", commands[0].Arguments);
        }

        [Fact]
        public void Parse_forced_include()
        {
            string cmd = @"cl.exe /c /FIpch.h /FI""C:\project\config.h"" main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");

            Assert.Single(commands);
            Assert.Contains("/FIpch.h", commands[0].Arguments);
            Assert.Contains(@"/FIC:\project\config.h", commands[0].Arguments);
        }

        [Fact]
        public void Output_flags_are_excluded()
        {
            string cmd = @"cl.exe /c /Fo""C:\build\main.obj"" /Fd""C:\build\vc.pdb"" main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");

            Assert.Single(commands);
            var args = commands[0].Arguments;
            Assert.DoesNotContain(args, a => a.Contains("/Fo"));
            Assert.DoesNotContain(args, a => a.Contains("/Fd"));
        }

        [Fact]
        public void Nologo_is_excluded()
        {
            string cmd = "cl.exe /nologo /c /EHsc main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");

            Assert.Single(commands);
            Assert.DoesNotContain(commands[0].Arguments, a => a == "/nologo");
        }

        [Fact]
        public void Compiler_path_is_first_argument()
        {
            string cmd = @"C:\VS\VC\bin\cl.exe /c main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");

            Assert.Single(commands);
            Assert.Equal(@"C:\VS\VC\bin\cl.exe", commands[0].Arguments[0]);
        }

        [Fact]
        public void Directory_is_normalized()
        {
            string cmd = "cl.exe /c main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project\build\..\src");

            Assert.Single(commands);
            // Should have forward slashes
            Assert.DoesNotContain("\\", commands[0].Directory);
        }

        [Fact]
        public void Clang_cl_is_supported()
        {
            string cmd = "clang-cl.exe /c /EHsc -std=c++17 -Wno-unused main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");

            Assert.Single(commands);
            Assert.Equal("clang-cl.exe", commands[0].Arguments[0]);
            Assert.Contains("-std=c++17", commands[0].Arguments);
            Assert.Contains("-Wno-unused", commands[0].Arguments);
        }

        [Fact]
        public void Non_compiler_command_returns_empty()
        {
            string cmd = "link.exe /OUT:main.exe main.obj";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");
            Assert.Empty(commands);
        }

        [Fact]
        public void Ixx_module_files_are_recognized()
        {
            string cmd = "cl.exe /c /std:c++latest module.ixx";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");
            Assert.Single(commands);
            Assert.Contains("module.ixx", commands[0].File);
        }

        [Fact]
        public void Warning_flags_are_preserved()
        {
            string cmd = "cl.exe /c /W4 /WX /wd4100 main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");

            Assert.Single(commands);
            Assert.Contains("/W4", commands[0].Arguments);
            Assert.Contains("/WX", commands[0].Arguments);
            Assert.Contains("/wd4100", commands[0].Arguments);
        }

        [Fact]
        public void Conformance_flags_are_preserved()
        {
            string cmd = "cl.exe /c /Zc:__cplusplus /permissive- main.cpp";
            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\project");

            Assert.Single(commands);
            Assert.Contains("/Zc:__cplusplus", commands[0].Arguments);
            Assert.Contains("/permissive-", commands[0].Arguments);
        }

        [Fact]
        public void Realistic_cmake_command_line()
        {
            string cmd = @"C:\PROGRA~1\MICROS~2\2022\Professional\VC\Tools\MSVC\14.38.33130\bin\Hostx64\x64\cl.exe " +
                @"/nologo /TP /DWIN32 /D_WINDOWS /W3 /GR /EHsc /MDd /Zi /Ob0 /Od /RTC1 " +
                @"/std:c++17 /Zc:__cplusplus " +
                @"/IC:\Users\dev\project\include " +
                @"/IC:\Users\dev\.conan2\p\b\fmt\include " +
                @"/FI""C:\Users\dev\project\build\pch.h"" " +
                @"/Fo""C:\Users\dev\project\build\CMakeFiles\myapp.dir\src\main.cpp.obj"" " +
                @"/Fd""C:\Users\dev\project\build\CMakeFiles\myapp.dir\myapp.pdb"" " +
                @"/FS /c C:\Users\dev\project\src\main.cpp";

            List<CompileCommand> commands = _parser.Parse(cmd, @"C:\Users\dev\project\build");

            Assert.Single(commands);
            CompileCommand entry = commands[0];

            // Source file captured
            Assert.Contains("main.cpp", entry.File);

            // Important flags preserved
            Assert.Contains("/EHsc", entry.Arguments);
            Assert.Contains("/std:c++17", entry.Arguments);
            Assert.Contains("/Zc:__cplusplus", entry.Arguments);
            Assert.Contains("/W3", entry.Arguments);

            // Include dirs preserved
            Assert.Contains(entry.Arguments, a => a.Contains("project") && a.Contains("/I"));

            // Output flags removed
            Assert.DoesNotContain(entry.Arguments, a => a.Contains("/Fo"));
            Assert.DoesNotContain(entry.Arguments, a => a.Contains("/Fd"));
            Assert.DoesNotContain(entry.Arguments, a => a == "/nologo");
        }
    }
}
