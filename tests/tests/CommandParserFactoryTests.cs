using MsBuildCompileCommands.Core.Extraction;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class CommandParserFactoryTests
    {
        [Theory]
        [InlineData("cl.exe /c main.cpp", typeof(MsvcCommandParser))]
        [InlineData("clang-cl.exe /c main.cpp", typeof(MsvcCommandParser))]
        [InlineData("gcc -c main.c", typeof(GccClangCommandParser))]
        [InlineData("g++ -c main.cpp", typeof(GccClangCommandParser))]
        [InlineData("clang++ -c main.cpp", typeof(GccClangCommandParser))]
        [InlineData("clang -c main.c", typeof(GccClangCommandParser))]
        [InlineData("nvcc -c kernel.cu", typeof(NvccCommandParser))]
        public void FindParser_returns_correct_parser_type(string commandLine, System.Type expectedType)
        {
            ICommandParser? parser = CommandParserFactory.FindParser(commandLine);
            Assert.NotNull(parser);
            Assert.IsType(expectedType, parser);
        }

        [Theory]
        [InlineData("link.exe /OUT:main.exe main.obj")]
        [InlineData("lib.exe /OUT:static.lib obj1.obj")]
        [InlineData("")]
        public void FindParser_returns_null_for_non_compilers(string commandLine)
        {
            Assert.Null(CommandParserFactory.FindParser(commandLine));
        }

        [Fact]
        public void Clang_cl_matched_as_msvc_not_gcc()
        {
            ICommandParser? parser = CommandParserFactory.FindParser("clang-cl /c main.cpp");
            Assert.IsType<MsvcCommandParser>(parser);
        }
    }
}
