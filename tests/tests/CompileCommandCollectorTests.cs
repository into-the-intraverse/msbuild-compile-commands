using System.Collections.Generic;
using Microsoft.Build.Framework;
using MsBuildCompileCommands.Core.Extraction;
using MsBuildCompileCommands.Core.Models;
using Xunit;

namespace MsBuildCompileCommands.Tests
{
    public class CompileCommandCollectorTests
    {
        [Fact]
        public void Collects_commands_from_task_command_line_events()
        {
            var collector = new CompileCommandCollector();

            // Simulate ProjectStarted
            var projectStarted = CreateProjectStartedEvent(@"C:\project\myapp.vcxproj", projectContextId: 1);
            collector.HandleEvent(projectStarted);

            // Simulate TaskCommandLine for cl.exe
            var taskCmd = CreateTaskCommandLineEvent(
                "cl.exe /c /EHsc main.cpp",
                projectContextId: 1);
            collector.HandleEvent(taskCmd);

            List<CompileCommand> commands = collector.GetCommands();
            Assert.Single(commands);
            Assert.Contains("main.cpp", commands[0].File);
        }

        [Fact]
        public void Ignores_non_compiler_task_commands()
        {
            var collector = new CompileCommandCollector();

            var projectStarted = CreateProjectStartedEvent(@"C:\project\myapp.vcxproj", projectContextId: 1);
            collector.HandleEvent(projectStarted);

            var linkCmd = CreateTaskCommandLineEvent(
                "link.exe /OUT:main.exe main.obj",
                projectContextId: 1);
            collector.HandleEvent(linkCmd);

            Assert.Equal(0, collector.Count);
        }

        [Fact]
        public void Deduplicates_by_file_path()
        {
            var collector = new CompileCommandCollector();

            var projectStarted = CreateProjectStartedEvent(@"C:\project\myapp.vcxproj", projectContextId: 1);
            collector.HandleEvent(projectStarted);

            // Same source file compiled twice with different flags - last wins
            var cmd1 = CreateTaskCommandLineEvent("cl.exe /c /Od main.cpp", projectContextId: 1);
            var cmd2 = CreateTaskCommandLineEvent("cl.exe /c /O2 main.cpp", projectContextId: 1);
            collector.HandleEvent(cmd1);
            collector.HandleEvent(cmd2);

            List<CompileCommand> commands = collector.GetCommands();
            Assert.Single(commands);
            Assert.Contains("/O2", commands[0].Arguments);
        }

        [Fact]
        public void Output_is_sorted_by_file_path()
        {
            var collector = new CompileCommandCollector();

            var projectStarted = CreateProjectStartedEvent(@"C:\project\myapp.vcxproj", projectContextId: 1);
            collector.HandleEvent(projectStarted);

            collector.HandleEvent(CreateTaskCommandLineEvent("cl.exe /c zebra.cpp", projectContextId: 1));
            collector.HandleEvent(CreateTaskCommandLineEvent("cl.exe /c alpha.cpp", projectContextId: 1));
            collector.HandleEvent(CreateTaskCommandLineEvent("cl.exe /c middle.cpp", projectContextId: 1));

            List<CompileCommand> commands = collector.GetCommands();
            Assert.Equal(3, commands.Count);
            Assert.Contains("alpha.cpp", commands[0].File);
            Assert.Contains("middle.cpp", commands[1].File);
            Assert.Contains("zebra.cpp", commands[2].File);
        }

        [Fact]
        public void Multiple_projects_tracked_independently()
        {
            var collector = new CompileCommandCollector();

            var proj1 = CreateProjectStartedEvent(@"C:\project1\app.vcxproj", projectContextId: 1);
            var proj2 = CreateProjectStartedEvent(@"C:\project2\lib.vcxproj", projectContextId: 2);
            collector.HandleEvent(proj1);
            collector.HandleEvent(proj2);

            collector.HandleEvent(CreateTaskCommandLineEvent("cl.exe /c app.cpp", projectContextId: 1));
            collector.HandleEvent(CreateTaskCommandLineEvent("cl.exe /c lib.cpp", projectContextId: 2));

            List<CompileCommand> commands = collector.GetCommands();
            Assert.Equal(2, commands.Count);

            var appCmd = commands.Find(c => c.File.Contains("app.cpp"));
            var libCmd = commands.Find(c => c.File.Contains("lib.cpp"));

            Assert.NotNull(appCmd);
            Assert.NotNull(libCmd);
            Assert.Contains("project1", appCmd!.Directory);
            Assert.Contains("project2", libCmd!.Directory);
        }

        [Fact]
        public void Empty_command_line_ignored()
        {
            var collector = new CompileCommandCollector();

            var projectStarted = CreateProjectStartedEvent(@"C:\project\myapp.vcxproj", projectContextId: 1);
            collector.HandleEvent(projectStarted);

            var emptyCmd = CreateTaskCommandLineEvent("", projectContextId: 1);
            collector.HandleEvent(emptyCmd);

            Assert.Equal(0, collector.Count);
        }

        // --- Helper methods to create MSBuild event instances ---

        private static ProjectStartedEventArgs CreateProjectStartedEvent(string projectFile, int projectContextId)
        {
            var e = new ProjectStartedEventArgs(
                message: "Project started",
                helpKeyword: null!,
                projectFile: projectFile,
                targetNames: "",
                properties: null!,
                items: null!);

            SetBuildEventContext(e, projectContextId);
            return e;
        }

        private static TaskCommandLineEventArgs CreateTaskCommandLineEvent(string commandLine, int projectContextId)
        {
            var e = new TaskCommandLineEventArgs(
                commandLine: commandLine,
                taskName: "CL",
                importance: MessageImportance.High);

            SetBuildEventContext(e, projectContextId);
            return e;
        }

        private static void SetBuildEventContext(BuildEventArgs e, int projectContextId)
        {
            e.BuildEventContext = new BuildEventContext(
                nodeId: 1,
                targetId: 1,
                projectContextId: projectContextId,
                taskId: 1);
        }
    }
}
