using System.Collections.Generic;
using MsBuildCompileCommands.Cli.Evaluation;

namespace MsBuildCompileCommands.Tests
{
    public class ClCompileItemMapperTests
    {
        [Fact]
        public void PreprocessorDefinitions_MapsToDefineFlags()
        {
            var metadata = new Dictionary<string, string>
            {
                ["PreprocessorDefinitions"] = "WIN32;_DEBUG;VERSION=42"
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Equal(new[] { "/DWIN32", "/D_DEBUG", "/DVERSION=42" }, flags);
        }

        [Fact]
        public void AdditionalIncludeDirectories_MapsToIncludeFlags()
        {
            var metadata = new Dictionary<string, string>
            {
                ["AdditionalIncludeDirectories"] = @"C:\src\include;C:\sdk\headers"
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Equal(new[] { @"/IC:\src\include", @"/IC:\sdk\headers" }, flags);
        }

        [Fact]
        public void ForcedIncludeFiles_MapsToFIFlags()
        {
            var metadata = new Dictionary<string, string>
            {
                ["ForcedIncludeFiles"] = "pch.h;stdafx.h"
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Equal(new[] { "/FIpch.h", "/FIstdafx.h" }, flags);
        }

        [Theory]
        [InlineData("MultiThreaded", "/MT")]
        [InlineData("MultiThreadedDebug", "/MTd")]
        [InlineData("MultiThreadedDLL", "/MD")]
        [InlineData("MultiThreadedDebugDLL", "/MDd")]
        public void RuntimeLibrary_MapsToCorrectFlag(string value, string expected)
        {
            var metadata = new Dictionary<string, string>
            {
                ["RuntimeLibrary"] = value
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Single(flags);
            Assert.Equal(expected, flags[0]);
        }

        [Theory]
        [InlineData("Sync", "/EHsc")]
        [InlineData("Async", "/EHa")]
        [InlineData("SyncCThrow", "/EHs")]
        public void ExceptionHandling_MapsToCorrectFlag(string value, string expected)
        {
            var metadata = new Dictionary<string, string>
            {
                ["ExceptionHandling"] = value
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Single(flags);
            Assert.Equal(expected, flags[0]);
        }

        [Theory]
        [InlineData("stdcpp17", "/std:c++17")]
        [InlineData("stdcpp20", "/std:c++20")]
        [InlineData("stdcpp23", "/std:c++23")]
        [InlineData("stdcpp14", "/std:c++14")]
        [InlineData("stdcpplatest", "/std:c++latest")]
        public void LanguageStandard_MapsToCorrectFlag(string value, string expected)
        {
            var metadata = new Dictionary<string, string>
            {
                ["LanguageStandard"] = value
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Single(flags);
            Assert.Equal(expected, flags[0]);
        }

        [Theory]
        [InlineData("stdc11", "/std:c11")]
        [InlineData("stdc17", "/std:c17")]
        public void LanguageStandard_C_MapsToCorrectFlag(string value, string expected)
        {
            var metadata = new Dictionary<string, string>
            {
                ["LanguageStandard_C"] = value
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Single(flags);
            Assert.Equal(expected, flags[0]);
        }

        [Fact]
        public void ConformanceMode_True_MapsToPermissiveMinus()
        {
            var metadata = new Dictionary<string, string>
            {
                ["ConformanceMode"] = "true"
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Single(flags);
            Assert.Equal("/permissive-", flags[0]);
        }

        [Fact]
        public void ConformanceMode_False_ProducesNoFlag()
        {
            var metadata = new Dictionary<string, string>
            {
                ["ConformanceMode"] = "false"
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Empty(flags);
        }

        [Theory]
        [InlineData("CompileAsC", "/TC")]
        [InlineData("CompileAsCpp", "/TP")]
        public void CompileAs_MapsToCorrectFlag(string value, string expected)
        {
            var metadata = new Dictionary<string, string>
            {
                ["CompileAs"] = value
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Single(flags);
            Assert.Equal(expected, flags[0]);
        }

        [Fact]
        public void TreatWChar_tAsBuiltInType_False_MapsToFlag()
        {
            var metadata = new Dictionary<string, string>
            {
                ["TreatWChar_tAsBuiltInType"] = "false"
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Single(flags);
            Assert.Equal("/Zc:wchar_t-", flags[0]);
        }

        [Fact]
        public void AdditionalOptions_TokenizedAndAdded()
        {
            var metadata = new Dictionary<string, string>
            {
                ["AdditionalOptions"] = "/W4 /Zc:__cplusplus"
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Equal(new[] { "/W4", "/Zc:__cplusplus" }, flags);
        }

        [Fact]
        public void EmptyMetadata_ReturnsEmptyFlags()
        {
            var metadata = new Dictionary<string, string>();

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Empty(flags);
        }

        [Fact]
        public void InheritedValueMarkers_AreSkipped()
        {
            var metadata = new Dictionary<string, string>
            {
                ["PreprocessorDefinitions"] = "FOO;%(PreprocessorDefinitions)"
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Single(flags);
            Assert.Equal("/DFOO", flags[0]);
        }

        [Fact]
        public void InheritedValueMarkers_InIncludeDirectories_AreSkipped()
        {
            var metadata = new Dictionary<string, string>
            {
                ["AdditionalIncludeDirectories"] = @"C:\mylib;%(AdditionalIncludeDirectories)"
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Single(flags);
            Assert.Equal(@"/IC:\mylib", flags[0]);
        }

        [Fact]
        public void MultipleMetadataKeys_ProduceCorrectOrderedFlags()
        {
            var metadata = new Dictionary<string, string>
            {
                ["PreprocessorDefinitions"] = "WIN32",
                ["RuntimeLibrary"] = "MultiThreadedDLL",
                ["LanguageStandard"] = "stdcpp17",
                ["ConformanceMode"] = "true"
            };

            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadata);

            Assert.Equal(new[] { "/DWIN32", "/MD", "/std:c++17", "/permissive-" }, flags);
        }
    }
}
