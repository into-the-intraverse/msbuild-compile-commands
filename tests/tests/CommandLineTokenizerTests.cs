using MsBuildCompileCommands.Core.Extraction;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class CommandLineTokenizerTests
    {
        [Fact]
        public void Empty_input_returns_empty_list()
        {
            Assert.Empty(CommandLineTokenizer.Tokenize(""));
            Assert.Empty(CommandLineTokenizer.Tokenize("   "));
#pragma warning disable CS8625
            Assert.Empty(CommandLineTokenizer.Tokenize(null));
#pragma warning restore CS8625
        }

        [Fact]
        public void Simple_tokens_split_on_spaces()
        {
            var tokens = CommandLineTokenizer.Tokenize("cl.exe /c main.cpp");
            Assert.Equal(new[] { "cl.exe", "/c", "main.cpp" }, tokens);
        }

        [Fact]
        public void Quoted_paths_are_unquoted()
        {
            var tokens = CommandLineTokenizer.Tokenize(@"cl.exe ""/IC:\Program Files\SDK\include"" main.cpp");
            Assert.Equal(3, tokens.Count);
            Assert.Equal("cl.exe", tokens[0]);
            Assert.Equal(@"/IC:\Program Files\SDK\include", tokens[1]);
            Assert.Equal("main.cpp", tokens[2]);
        }

        [Fact]
        public void Tabs_are_delimiters()
        {
            var tokens = CommandLineTokenizer.Tokenize("cl.exe\t/c\tmain.cpp");
            Assert.Equal(new[] { "cl.exe", "/c", "main.cpp" }, tokens);
        }

        [Fact]
        public void Adjacent_quotes_produce_single_token()
        {
            // "foo""bar" => foobar (double-quote inside quotes toggles quoting)
            var tokens = CommandLineTokenizer.Tokenize("cl.exe /DFOO=\"bar\" main.cpp");
            Assert.Equal(3, tokens.Count);
            Assert.Equal("cl.exe", tokens[0]);
            Assert.Equal("/DFOO=bar", tokens[1]);
            Assert.Equal("main.cpp", tokens[2]);
        }

        [Fact]
        public void Multiple_spaces_between_tokens()
        {
            var tokens = CommandLineTokenizer.Tokenize("cl.exe   /c   main.cpp");
            Assert.Equal(new[] { "cl.exe", "/c", "main.cpp" }, tokens);
        }

        [Fact]
        public void Backslashes_not_before_quote_are_literal()
        {
            var tokens = CommandLineTokenizer.Tokenize(@"cl.exe /IC:\Users\dev\include main.cpp");
            Assert.Equal(3, tokens.Count);
            Assert.Equal(@"/IC:\Users\dev\include", tokens[1]);
        }

        [Fact]
        public void Mixed_flags_and_paths()
        {
            string cmd = @"C:\PROGRA~1\MICROS~2\2022\Professional\VC\Tools\MSVC\14.38.33130\bin\Hostx64\x64\cl.exe " +
                @"/nologo /TP /DWIN32 /D_WINDOWS /EHsc /Ob0 /Od /RTC1 /MDd /std:c++17 /Zc:__cplusplus " +
                @"/IC:\Users\dev\project\include /FI""pch.h"" /c main.cpp";

            var tokens = CommandLineTokenizer.Tokenize(cmd);
            Assert.True(tokens.Count > 10);
            Assert.EndsWith("cl.exe", tokens[0]);
            Assert.Equal("main.cpp", tokens[tokens.Count - 1]);
        }
    }
}
