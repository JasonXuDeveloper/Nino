# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Nino is a high-performance binary serialization library for C# that targets Unity and .NET applications. The project uses source generators to create optimized serialization code at compile time.

## Architecture

The solution consists of several key components:

- **Nino.Core**: Core serialization library with readers, writers, and attributes
- **Nino.Generator**: Roslyn source generator that creates serialization code at compile time
- **Nino.UnitTests**: Comprehensive test suite including cross-reference tests
- **Nino.Benchmark**: Performance benchmarking against other serialization libraries
- **Nino.Unity**: Unity-specific implementation and tests

## Development Commands

### Building the Solution
```bash
dotnet build Nino.sln
```

### Running Unit Tests
```bash
dotnet test Nino.UnitTests/Nino.UnitTests.csproj
dotnet test Nino.UnitTests.Subset/Nino.UnitTests.Subset.csproj
```

### Running Benchmarks
```bash
dotnet run --project Nino.Benchmark/Nino.Benchmark.csproj -c Release
```

### Building Individual Projects
```bash
dotnet build Nino.Core/Nino.Core.csproj
dotnet build Nino.Generator/Nino.Generator.csproj
```

## Key Technical Details

### Target Frameworks
- Nino.Core: net6.0, netstandard2.1, net8.0
- Nino.Generator: netstandard2.0 (Roslyn analyzer)
- Tests: net6.0, net8.0
- Benchmarks: net8.0

### Source Generation
The Nino.Generator project is a Roslyn source generator that analyzes types marked with `[NinoType]` and generates optimized serialization code. It must be referenced as an analyzer in consuming projects.

### Unity Integration
The Nino.Unity folder contains Unity-specific projects and comparisons with other serialization libraries (MessagePack, protobuf-net, MongoDB.Bson).

### Version Management
Version is centrally managed in Version.cs and referenced across all projects. Current version: 3.9.5

### Special Build Configuration
- Unsafe code blocks are enabled in core projects
- Source generators require specific project reference configuration with `OutputItemType="Analyzer"`
- Tests use `WEAK_VERSION_TOLERANCE` for compatibility testing

## Testing Strategy

The test suite includes:
- Basic serialization/deserialization tests
- Complex nested type tests
- Cross-reference type tests
- Multi-threading tests
- Analyzer tests for source generation
- Issue reproduction tests

When making changes, ensure all test projects pass and run the full benchmark suite to verify performance is maintained.