@echo off
chcp 65001 > nul
@echo off


setlocal
echo @echo off > "%~dp0/deactivate_conanbuildenv-x86_64.bat"
echo echo Restoring environment for conanbuildenv-x86_64.bat >> "%~dp0/deactivate_conanbuildenv-x86_64.bat"
for %%v in (MSBUILDCOMPILECOMMANDS_LOGGER_DLL PATH) do (
    set foundenvvar=
    for /f "delims== tokens=1,2" %%a in ('set') do (
        if /I "%%a" == "%%v" (
            echo set "%%a=%%b">> "%~dp0/deactivate_conanbuildenv-x86_64.bat"
            set foundenvvar=1
        )
    )
    if not defined foundenvvar (
        echo set %%v=>> "%~dp0/deactivate_conanbuildenv-x86_64.bat"
    )
)
endlocal




set "MSBUILDCOMPILECOMMANDS_LOGGER_DLL=C:\Users\intruder\.conan2\p\b\msbuiedef63acd3035\p\logger\MsBuildCompileCommands.dll"
if defined PATH (
    set "PATH=C:\Users\intruder\.conan2\p\b\msbuiedef63acd3035\p\bin;%PATH%"
) else (
    set "PATH=C:\Users\intruder\.conan2\p\b\msbuiedef63acd3035\p\bin"
)