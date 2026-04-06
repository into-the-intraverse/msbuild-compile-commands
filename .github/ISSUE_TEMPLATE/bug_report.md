---
name: Bug report
about: Report a problem with compile_commands.json generation
title: ''
labels: bug
assignees: ''
---

**Describe the bug**
A clear description of what the bug is.

**To reproduce**
Steps to reproduce the behavior:
1. Build command used: `...`
2. Logger or CLI invocation: `...`
3. Expected compile_commands.json output
4. Actual output or error

**Environment**
- OS: [e.g., Windows 11]
- .NET SDK version: [output of `dotnet --version`]
- MSBuild version: [output of `msbuild -version`]
- Tool version: [e.g., 0.1.0]
- Build system: [e.g., CMake + VS 2022 generator, raw MSBuild]

**Command line (sanitized)**
If the issue is about command line parsing, paste the cl.exe command line that produced unexpected results. Sanitize sensitive paths if needed.

```
cl.exe /c ...
```

**Expected compile_commands.json entry**
```json
{
  "directory": "...",
  "file": "...",
  "arguments": [...]
}
```

**Actual output**
```json

```

**Additional context**
Any other context about the problem.
