namespace MsBuildCompileCommands.Core.Extraction
{
    public static class CommandParserFactory
    {
        // Order: nvcc (most specific), Msvc (clang-cl before bare clang), GccClang (last)
        private static readonly ICommandParser[] Parsers =
        {
            new NvccCommandParser(),
            new MsvcCommandParser(),
            new GccClangCommandParser()
        };

        public static ICommandParser? FindParser(string commandLine)
        {
            for (int i = 0; i < Parsers.Length; i++)
            {
                if (Parsers[i].IsCompilerInvocation(commandLine))
                    return Parsers[i];
            }
            return null;
        }
    }
}
