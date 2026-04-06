from conan import ConanFile


class TestPackageConan(ConanFile):
    settings = "os", "arch"
    test_type = "explicit"

    def build_requirements(self):
        self.tool_requires(self.tested_reference_str)

    def test(self):
        self.run("MsBuildCompileCommands --version")
        self.run(
            'cmd /c "if not exist "%MSBUILDCOMPILECOMMANDS_LOGGER_DLL%" exit 1"'
        )
