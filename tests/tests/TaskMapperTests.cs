using System;
using System.Collections.Generic;
using MsBuildCompileCommands.Core.Extraction;
using MsBuildCompileCommands.Core.Models;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class ClCompileTaskMapperTests
    {
        private readonly ClCompileTaskMapper _mapper = new ClCompileTaskMapper();

        [Theory]
        [InlineData("CL")]
        [InlineData("cl")]
        [InlineData("Cl")]
        public void CanMap_CL_returns_true(string taskName)
        {
            Assert.True(_mapper.CanMap(taskName));
        }

        [Theory]
        [InlineData("CudaCompile")]
        [InlineData("Link")]
        [InlineData("")]
        public void CanMap_non_CL_returns_false(string taskName)
        {
            Assert.False(_mapper.CanMap(taskName));
        }

        [Fact]
        public void Map_with_sources_defines_includes_and_additional_options()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Sources", new List<string> { "main.cpp", "util.cpp" } },
                { "PreprocessorDefinitions", new List<string> { "DEBUG;_WIN32" } },
                { "AdditionalIncludeDirectories", new List<string> { @"C:\inc;C:\inc2" } },
                { "AdditionalOptions", new List<string> { "/W4 /permissive-" } }
            };

            List<CompileCommand> commands = _mapper.Map("CL", parameters, @"C:\project");
            Assert.Equal(2, commands.Count);

            // Check first command has correct flags
            CompileCommand cmd = commands[0];
            Assert.Equal("cl.exe", cmd.Arguments[0]);
            Assert.Contains("/DDEBUG", cmd.Arguments);
            Assert.Contains("/D_WIN32", cmd.Arguments);
            Assert.Contains(@"/IC:\inc", cmd.Arguments);
            Assert.Contains(@"/IC:\inc2", cmd.Arguments);
            Assert.Contains("/W4", cmd.Arguments);
            Assert.Contains("/permissive-", cmd.Arguments);
            Assert.Contains("main.cpp", cmd.File);

            // Second command
            Assert.Contains("util.cpp", commands[1].File);
        }

        [Fact]
        public void Map_with_forced_include_files()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Sources", new List<string> { "main.cpp" } },
                { "ForcedIncludeFiles", new List<string> { "pch.h;stdafx.h" } }
            };

            List<CompileCommand> commands = _mapper.Map("CL", parameters, @"C:\project");
            Assert.Single(commands);
            Assert.Contains("/FIpch.h", commands[0].Arguments);
            Assert.Contains("/FIstdafx.h", commands[0].Arguments);
        }

        [Theory]
        [InlineData("MultiThreaded", "/MT")]
        [InlineData("MultiThreadedDebug", "/MTd")]
        [InlineData("MultiThreadedDLL", "/MD")]
        [InlineData("MultiThreadedDebugDLL", "/MDd")]
        public void Map_runtime_library_mapping(string rtlValue, string expectedFlag)
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Sources", new List<string> { "main.cpp" } },
                { "RuntimeLibrary", new List<string> { rtlValue } }
            };

            List<CompileCommand> commands = _mapper.Map("CL", parameters, @"C:\project");
            Assert.Single(commands);
            Assert.Contains(expectedFlag, commands[0].Arguments);
        }

        [Theory]
        [InlineData("Sync", "/EHsc")]
        [InlineData("Async", "/EHa")]
        [InlineData("SyncCThrow", "/EHs")]
        public void Map_exception_handling_mapping(string ehValue, string expectedFlag)
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Sources", new List<string> { "main.cpp" } },
                { "ExceptionHandling", new List<string> { ehValue } }
            };

            List<CompileCommand> commands = _mapper.Map("CL", parameters, @"C:\project");
            Assert.Single(commands);
            Assert.Contains(expectedFlag, commands[0].Arguments);
        }

        [Fact]
        public void Map_no_sources_returns_empty()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "PreprocessorDefinitions", new List<string> { "DEBUG" } }
            };

            List<CompileCommand> commands = _mapper.Map("CL", parameters, @"C:\project");
            Assert.Empty(commands);
        }

        [Fact]
        public void Map_empty_sources_returns_empty()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Sources", new List<string>() }
            };

            List<CompileCommand> commands = _mapper.Map("CL", parameters, @"C:\project");
            Assert.Empty(commands);
        }
    }

    public class CudaCompileTaskMapperTests
    {
        private readonly CudaCompileTaskMapper _mapper = new CudaCompileTaskMapper();

        [Theory]
        [InlineData("CudaCompile")]
        [InlineData("cudacompile")]
        public void CanMap_CudaCompile_returns_true(string taskName)
        {
            Assert.True(_mapper.CanMap(taskName));
        }

        [Theory]
        [InlineData("CL")]
        [InlineData("Link")]
        public void CanMap_non_CudaCompile_returns_false(string taskName)
        {
            Assert.False(_mapper.CanMap(taskName));
        }

        [Fact]
        public void Map_sources_defines_and_includes()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Sources", new List<string> { "kernel.cu" } },
                { "Defines", new List<string> { "USE_CUDA;ARCH_SM_80" } },
                { "AdditionalIncludeDirectories", new List<string> { @"C:\cuda\include;C:\project\inc" } },
                { "AdditionalOptions", new List<string> { "--gpu-architecture=sm_80" } }
            };

            List<CompileCommand> commands = _mapper.Map("CudaCompile", parameters, @"C:\project");
            Assert.Single(commands);

            CompileCommand cmd = commands[0];
            Assert.Equal("nvcc", cmd.Arguments[0]);
            Assert.Contains("-DUSE_CUDA", cmd.Arguments);
            Assert.Contains("-DARCH_SM_80", cmd.Arguments);
            Assert.Contains(@"-IC:\cuda\include", cmd.Arguments);
            Assert.Contains(@"-IC:\project\inc", cmd.Arguments);
            Assert.Contains("--gpu-architecture=sm_80", cmd.Arguments);
            Assert.Contains("kernel.cu", cmd.File);
        }

        [Fact]
        public void Map_no_sources_returns_empty()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Defines", new List<string> { "FOO" } }
            };

            List<CompileCommand> commands = _mapper.Map("CudaCompile", parameters, @"C:\project");
            Assert.Empty(commands);
        }
    }

    public class GenericTaskMapperTests
    {
        private readonly GenericTaskMapper _mapper = new GenericTaskMapper();

        [Fact]
        public void CanMap_always_returns_true()
        {
            Assert.True(_mapper.CanMap("CL"));
            Assert.True(_mapper.CanMap("Anything"));
            Assert.True(_mapper.CanMap(""));
        }

        [Fact]
        public void Map_with_Sources_parameter()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Sources", new List<string> { "main.cpp" } }
            };

            List<CompileCommand> commands = _mapper.Map("CustomTask", parameters, @"C:\project");
            Assert.Single(commands);
            Assert.Contains("main.cpp", commands[0].File);
        }

        [Fact]
        public void Map_with_SourceFiles_parameter()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "SourceFiles", new List<string> { "util.c" } }
            };

            List<CompileCommand> commands = _mapper.Map("CustomTask", parameters, @"C:\project");
            Assert.Single(commands);
            Assert.Contains("util.c", commands[0].File);
        }

        [Fact]
        public void Map_with_InputFiles_parameter()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "InputFiles", new List<string> { "lib.cpp" } }
            };

            List<CompileCommand> commands = _mapper.Map("CustomTask", parameters, @"C:\project");
            Assert.Single(commands);
            Assert.Contains("lib.cpp", commands[0].File);
        }

        [Fact]
        public void Map_no_source_parameters_returns_empty()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "OutputFile", new List<string> { "out.obj" } }
            };

            List<CompileCommand> commands = _mapper.Map("CustomTask", parameters, @"C:\project");
            Assert.Empty(commands);
        }

        [Fact]
        public void Map_filters_to_recognized_source_extensions()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Sources", new List<string> { "main.cpp", "readme.txt", "data.json", "kernel.cu" } }
            };

            List<CompileCommand> commands = _mapper.Map("CustomTask", parameters, @"C:\project");
            Assert.Equal(2, commands.Count);
            Assert.Contains(commands, c => c.File.Contains("main.cpp"));
            Assert.Contains(commands, c => c.File.Contains("kernel.cu"));
        }

        [Fact]
        public void Map_with_Defines_parameter()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Sources", new List<string> { "main.cpp" } },
                { "Defines", new List<string> { "FOO;BAR" } }
            };

            List<CompileCommand> commands = _mapper.Map("CustomTask", parameters, @"C:\project");
            Assert.Single(commands);
            Assert.Contains("/DFOO", commands[0].Arguments);
            Assert.Contains("/DBAR", commands[0].Arguments);
        }

        [Fact]
        public void Map_uses_cl_exe_as_compiler()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Sources", new List<string> { "main.cpp" } }
            };

            List<CompileCommand> commands = _mapper.Map("CustomTask", parameters, @"C:\project");
            Assert.Single(commands);
            Assert.Equal("cl.exe", commands[0].Arguments[0]);
        }
    }

    public class TaskMapperRegistryTests
    {
        private readonly TaskMapperRegistry _registry = new TaskMapperRegistry();

        [Fact]
        public void CL_uses_ClCompileTaskMapper()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Sources", new List<string> { "main.cpp" } },
                { "RuntimeLibrary", new List<string> { "MultiThreadedDLL" } }
            };

            List<CompileCommand> commands = _registry.TryMap("CL", parameters, @"C:\project");
            Assert.Single(commands);
            Assert.Equal("cl.exe", commands[0].Arguments[0]);
            Assert.Contains("/MD", commands[0].Arguments);
        }

        [Fact]
        public void CudaCompile_uses_CudaCompileTaskMapper()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Sources", new List<string> { "kernel.cu" } },
                { "Defines", new List<string> { "CUDA_ENABLED" } }
            };

            List<CompileCommand> commands = _registry.TryMap("CudaCompile", parameters, @"C:\project");
            Assert.Single(commands);
            Assert.Equal("nvcc", commands[0].Arguments[0]);
            Assert.Contains("-DCUDA_ENABLED", commands[0].Arguments);
        }

        [Fact]
        public void Unknown_task_uses_generic_fallback()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Sources", new List<string> { "main.cpp" } }
            };

            List<CompileCommand> commands = _registry.TryMap("SomeUnknownTask", parameters, @"C:\project");
            Assert.Single(commands);
            // Generic mapper uses cl.exe
            Assert.Equal("cl.exe", commands[0].Arguments[0]);
        }

        [Fact]
        public void Unknown_task_with_no_sources_returns_empty()
        {
            var parameters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "OutputFile", new List<string> { "out.obj" } }
            };

            List<CompileCommand> commands = _registry.TryMap("SomeTask", parameters, @"C:\project");
            Assert.Empty(commands);
        }
    }
}
