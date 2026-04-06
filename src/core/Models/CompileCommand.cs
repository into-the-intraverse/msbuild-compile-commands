using System;
using System.Collections.Generic;

namespace MsBuildCompileCommands.Core.Models
{
    /// <summary>
    /// A single entry in compile_commands.json.
    /// Uses the "arguments" form for maximum robustness with clangd.
    /// </summary>
    public sealed class CompileCommand : IEquatable<CompileCommand>
    {
        /// <summary>Working directory of the compilation.</summary>
        public string Directory { get; }

        /// <summary>Absolute path to the source file.</summary>
        public string File { get; }

        /// <summary>Argument list (argv) for the compilation, including compiler path at index 0.</summary>
        public IReadOnlyList<string> Arguments { get; }

        public CompileCommand(string directory, string file, IReadOnlyList<string> arguments)
        {
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            File = file ?? throw new ArgumentNullException(nameof(file));
            Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        }

        /// <summary>
        /// Deduplication key: normalized file path.
        /// Last-seen entry wins when duplicates exist.
        /// </summary>
        public string DeduplicationKey => File.ToUpperInvariant();

        public bool Equals(CompileCommand? other)
        {
            if (other is null) return false;
            return string.Equals(File, other.File, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj) => Equals(obj as CompileCommand);

        public override int GetHashCode() =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(File);
    }
}
