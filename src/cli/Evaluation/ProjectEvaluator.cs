using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Evaluation;
using MsBuildCompileCommands.Core.Models;
using MsBuildCompileCommands.Core.Utils;

namespace MsBuildCompileCommands.Cli.Evaluation
{
    /// <summary>
    /// Evaluates .vcxproj files to extract ClCompile items and produce
    /// <see cref="CompileCommand"/> entries for source files not captured by build events.
    /// </summary>
    public sealed class ProjectEvaluator
    {
        private readonly List<string> _diagnostics = new List<string>();
        public IReadOnlyList<string> Diagnostics => _diagnostics;

        public List<CompileCommand> Evaluate(string projectPath, string? configuration = null, string? platform = null)
        {
            var commands = new List<CompileCommand>();

            try
            {
                var globalProps = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(configuration))
                    globalProps["Configuration"] = configuration!;
                if (!string.IsNullOrEmpty(platform))
                    globalProps["Platform"] = platform!;

                using (var collection = new ProjectCollection(globalProps))
                {
                    var project = new Project(projectPath, globalProps, null, collection);
                    string projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".";

                    string compiler = project.GetPropertyValue("CLToolExe");
                    if (string.IsNullOrEmpty(compiler))
                        compiler = "cl.exe";

                    string normalizedDir = PathNormalizer.NormalizeDirectory(projectDir);

                    foreach (ProjectItem item in project.GetItems("ClCompile"))
                    {
                        try
                        {
                            string sourceFile = item.EvaluatedInclude;
                            string normalizedFile = PathNormalizer.Normalize(sourceFile, projectDir);

                            var metadataDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (ProjectMetadata meta in item.Metadata)
                            {
                                metadataDict[meta.Name] = meta.EvaluatedValue;
                            }

                            List<string> flags = ClCompileItemMapper.MapMetadataToFlags(metadataDict);

                            var arguments = new List<string> { compiler };
                            arguments.AddRange(flags);
                            arguments.Add("/c");
                            arguments.Add(normalizedFile);

                            commands.Add(new CompileCommand(normalizedDir, normalizedFile, arguments));
                        }
                        catch (Exception ex)
                        {
                            _diagnostics.Add($"Warning: failed to process item '{item.EvaluatedInclude}' in {projectPath}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _diagnostics.Add($"Warning: failed to evaluate project {projectPath}: {ex.Message}");
            }

            return commands;
        }
    }
}
