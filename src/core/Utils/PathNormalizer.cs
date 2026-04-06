using System;
using System.IO;

namespace MsBuildCompileCommands.Core.Utils
{
    /// <summary>
    /// Normalizes file paths for consistent compile_commands.json output.
    /// Produces forward-slash absolute paths with uppercase drive letters.
    /// </summary>
    public static class PathNormalizer
    {
        /// <summary>
        /// Normalize a path to absolute form with forward slashes and uppercase drive letter.
        /// </summary>
        public static string Normalize(string path, string? baseDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            // Trim surrounding quotes
            path = path.Trim('"', '\'');

            // Resolve to absolute
            if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(baseDirectory))
            {
                path = Path.Combine(baseDirectory, path);
            }

            // Get full path (resolves .., ., etc.)
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                // If GetFullPath fails (e.g., invalid chars), return as-is with slash normalization
            }

            // Normalize separators to forward slashes
            path = path.Replace('\\', '/');

            // Uppercase drive letter for consistency
            if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
            {
                path = char.ToUpperInvariant(path[0]) + path.Substring(1);
            }

            return path;
        }

        /// <summary>
        /// Normalize a directory path, ensuring it does not end with a trailing slash.
        /// </summary>
        public static string NormalizeDirectory(string path, string? baseDirectory = null)
        {
            string result = Normalize(path, baseDirectory);
            return result.TrimEnd('/');
        }
    }
}
