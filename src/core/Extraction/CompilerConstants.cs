using System;
using System.Collections.Generic;

namespace MsBuildCompileCommands.Core.Extraction
{
    internal static class CompilerConstants
    {
        public static readonly HashSet<string> SourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".c", ".cc", ".cpp", ".cxx", ".c++", ".cp", ".ixx", ".cppm",
            ".cu"
        };
    }
}
