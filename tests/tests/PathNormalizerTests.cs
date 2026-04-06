using MsBuildCompileCommands.Core.Utils;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class PathNormalizerTests
    {
        [Fact]
        public void Backslashes_become_forward_slashes()
        {
            string result = PathNormalizer.Normalize(@"C:\Users\dev\project\main.cpp");
            Assert.Equal("C:/Users/dev/project/main.cpp", result);
        }

        [Fact]
        public void Drive_letter_uppercased()
        {
            string result = PathNormalizer.Normalize(@"c:\project\main.cpp");
            Assert.StartsWith("C:/", result);
        }

        [Fact]
        public void Already_normalized_path_unchanged()
        {
            string result = PathNormalizer.Normalize("C:/project/main.cpp");
            Assert.Equal("C:/project/main.cpp", result);
        }

        [Fact]
        public void Relative_path_resolved_with_base_directory()
        {
            string result = PathNormalizer.Normalize("src/main.cpp", @"C:\project");
            Assert.Equal("C:/project/src/main.cpp", result);
        }

        [Fact]
        public void Dotdot_resolved()
        {
            string result = PathNormalizer.Normalize(@"C:\project\build\..\src\main.cpp");
            Assert.Equal("C:/project/src/main.cpp", result);
        }

        [Fact]
        public void Surrounding_quotes_stripped()
        {
            string result = PathNormalizer.Normalize(@"""C:\project\main.cpp""");
            Assert.Equal("C:/project/main.cpp", result);
        }

        [Fact]
        public void NormalizeDirectory_strips_trailing_slash()
        {
            string result = PathNormalizer.NormalizeDirectory(@"C:\project\build\");
            Assert.False(result.EndsWith("/"));
            Assert.Equal("C:/project/build", result);
        }

        [Fact]
        public void Empty_and_whitespace_passthrough()
        {
            Assert.Equal("", PathNormalizer.Normalize(""));
            Assert.Equal("  ", PathNormalizer.Normalize("  "));
        }
    }
}
