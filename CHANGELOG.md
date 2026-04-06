# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-04-06

### Added

- Core compile command extraction from cl.exe and clang-cl command lines
- MSBuild live logger (`CompileCommandsLogger`) for generating compile_commands.json during builds
- CLI tool for generating compile_commands.json from MSBuild binary logs (.binlog)
- Response file (@file) expansion with nested file support
- Path normalization (forward slashes, uppercase drive letters, absolute paths)
- Deduplication of compile entries by source file path (last-wins)
- Deterministic sorted JSON output
- Merge mode for combining with existing compile_commands.json
- Support for include directories, preprocessor defines, forced includes, language standard flags, warning flags, conformance flags
- 61 unit tests covering tokenization, parsing, normalization, collection, and JSON output
