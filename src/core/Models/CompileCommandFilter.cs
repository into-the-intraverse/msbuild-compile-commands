using System;
using System.Collections.Generic;

namespace MsBuildCompileCommands.Core.Models
{
    public sealed class CompileCommandFilter
    {
        public HashSet<string>? Projects { get; }
        public HashSet<string>? Configurations { get; }

        public CompileCommandFilter(HashSet<string>? projects, HashSet<string>? configurations)
        {
            Projects = projects;
            Configurations = configurations;
        }
    }
}
