import os

from conan import ConanFile
from conan.errors import ConanInvalidConfiguration
from conan.tools.files import copy


class MsBuildCompileCommandsConan(ConanFile):
    name = "msbuild-compile-commands"
    description = (
        "Generate compile_commands.json from MSBuild C/C++ builds "
        "for clangd and other clang tooling."
    )
    license = "MIT"
    url = "https://github.com/conan-io/conan-center-index"
    homepage = "https://github.com/into-the-intraverse/msbuild-compile-commands"
    topics = ("clangd", "msbuild", "compile-commands", "compilation-database")

    package_type = "application"
    settings = "os", "arch"

    def export_sources(self):
        root = os.path.join(self.recipe_folder, "..")
        copy(self, "src/*", src=root, dst=self.export_sources_folder)
        copy(self, "Directory.Build.props", src=root, dst=self.export_sources_folder)
        copy(self, "global.json", src=root, dst=self.export_sources_folder)
        copy(self, "MsBuildCompileCommands.slnx", src=root, dst=self.export_sources_folder)
        copy(self, "LICENSE", src=root, dst=self.export_sources_folder)

    def set_version(self):
        if not self.version:
            import re
            props = os.path.join(self.recipe_folder, "..", "Directory.Build.props")
            content = open(props).read()
            match = re.search(r"<Version>(.*?)</Version>", content)
            if match:
                self.version = match.group(1)

    def validate(self):
        if self.settings.os != "Windows":
            raise ConanInvalidConfiguration(
                f"{self.ref} only supports Windows."
            )
        if self.settings.arch != "x86_64":
            raise ConanInvalidConfiguration(
                f"{self.ref} only provides x86_64 binaries."
            )

    def build(self):
        out = os.path.join(self.build_folder, "publish")
        self.run(
            "dotnet publish src/cli/cli.csproj"
            " --configuration Release"
            " --runtime win-x64"
            " --no-self-contained"
            " /p:PublishSingleFile=true"
            f" --output {out}"
        )
        self.run(
            "dotnet build src/logger/logger.csproj"
            " --configuration Release"
            f" --output {out}"
        )

    def package(self):
        publish = os.path.join(self.build_folder, "publish")
        copy(self, "LICENSE",
             src=self.source_folder,
             dst=os.path.join(self.package_folder, "licenses"))
        copy(self, "*",
             src=publish,
             dst=os.path.join(self.package_folder, "bin"),
             excludes=["*.pdb"])

    def package_info(self):
        self.cpp_info.bindirs = ["bin"]
        self.cpp_info.libdirs = []
        self.cpp_info.includedirs = []

        logger_dll = os.path.join(
            self.package_folder, "bin", "MsBuildCompileCommands.dll"
        )
        self.buildenv_info.define(
            "MSBUILDCOMPILECOMMANDS_LOGGER_DLL", logger_dll
        )
        self.runenv_info.define(
            "MSBUILDCOMPILECOMMANDS_LOGGER_DLL", logger_dll
        )
