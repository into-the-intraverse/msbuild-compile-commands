using System.Collections.Generic;
using MsBuildCompileCommands.Core.IO;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class ResponseFileParserTests
    {
        [Fact]
        public void Non_response_tokens_pass_through()
        {
            var parser = new ResponseFileParser(_ => null);
            var tokens = new List<string> { "cl.exe", "/c", "main.cpp" };

            List<string> result = parser.Expand(tokens);

            Assert.Equal(tokens, result);
        }

        [Fact]
        public void Response_file_contents_are_inlined()
        {
            var files = new Dictionary<string, string>
            {
                { @"C:\project\flags.rsp", "/EHsc /std:c++17 /W4" }
            };

            var parser = new ResponseFileParser(path =>
                files.TryGetValue(path, out string? content) ? content : null);

            var tokens = new List<string> { "cl.exe", @"@C:\project\flags.rsp", "main.cpp" };

            List<string> result = parser.Expand(tokens, @"C:\project");

            Assert.Equal(new[] { "cl.exe", "/EHsc", "/std:c++17", "/W4", "main.cpp" }, result);
        }

        [Fact]
        public void Relative_response_file_resolved_against_base_dir()
        {
            var files = new Dictionary<string, string>
            {
                { @"C:\project\build\flags.rsp", "/DRELEASE" }
            };

            var parser = new ResponseFileParser(path =>
                files.TryGetValue(path.Replace("/", @"\"), out string? content) ? content : null);

            var tokens = new List<string> { "cl.exe", "@flags.rsp", "main.cpp" };

            List<string> result = parser.Expand(tokens, @"C:\project\build");

            Assert.Contains("/DRELEASE", result);
        }

        [Fact]
        public void Nested_response_files_are_expanded()
        {
            var files = new Dictionary<string, string>
            {
                { @"C:\project\outer.rsp", "/W4 @inner.rsp" },
                { @"C:\project\inner.rsp", "/EHsc /std:c++17" }
            };

            var parser = new ResponseFileParser(path =>
                files.TryGetValue(path, out string? content) ? content : null);

            var tokens = new List<string> { "cl.exe", @"@C:\project\outer.rsp", "main.cpp" };

            List<string> result = parser.Expand(tokens, @"C:\project");

            Assert.Equal(new[] { "cl.exe", "/W4", "/EHsc", "/std:c++17", "main.cpp" }, result);
        }

        [Fact]
        public void Missing_response_file_kept_as_token()
        {
            var parser = new ResponseFileParser(_ => null);

            var tokens = new List<string> { "cl.exe", "@missing.rsp", "main.cpp" };

            List<string> result = parser.Expand(tokens);

            Assert.Contains("@missing.rsp", result);
        }

        [Fact]
        public void Deep_nesting_stops_at_max_depth()
        {
            // Create a self-referencing response file (infinite recursion)
            var parser = new ResponseFileParser(path =>
                path.EndsWith("loop.rsp") ? "@loop.rsp /flag" : null);

            var tokens = new List<string> { "cl.exe", "@loop.rsp", "main.cpp" };

            // Should not throw; depth limit prevents infinite recursion
            List<string> result = parser.Expand(tokens, @"C:\project");

            Assert.Contains("cl.exe", result);
            Assert.Contains("main.cpp", result);
        }

        [Fact]
        public void Response_file_with_quoted_paths()
        {
            var files = new Dictionary<string, string>
            {
                { @"C:\project\paths.rsp", @"""/IC:\Program Files\SDK\include"" /DWIN32" }
            };

            var parser = new ResponseFileParser(path =>
                files.TryGetValue(path, out string? content) ? content : null);

            var tokens = new List<string> { "cl.exe", @"@C:\project\paths.rsp", "main.cpp" };

            List<string> result = parser.Expand(tokens, @"C:\project");

            Assert.Contains(@"/IC:\Program Files\SDK\include", result);
            Assert.Contains("/DWIN32", result);
        }

        [Fact]
        public void At_sign_alone_is_not_a_response_file()
        {
            var parser = new ResponseFileParser(_ => "should not be read");

            var tokens = new List<string> { "cl.exe", "@", "main.cpp" };

            // "@" alone (length 1) should not be treated as a response file
            // Actually our implementation checks length > 1, so "@" passes through
            List<string> result = parser.Expand(tokens);

            // The bare "@" should pass through unchanged
            Assert.Equal(tokens, result);
        }
    }
}
