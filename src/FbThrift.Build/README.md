# FbThrift.Build

Shared MSBuild targets, properties, and build infrastructure for FbThrift projects.

## Features

- **Centralized Versioning**: Single source of truth for version numbers via `Directory.Build.props`
- **Automated Versioning**: Git tag-based versioning via `GitVersion.targets`
- **SourceLink**: Automatic SourceLink integration for debugging
- **Deterministic Builds**: Reproducible builds with `Deterministic=true`
- **SourceLink**: Embedded source code for debugging NuGet packages
- **Code Signing**: Automated assembly signing
- **SBOM Generation**: Software Bill of Materials generation
- **Code Analysis**: Built-in analyzers (Roslyn, StyleCop, Sonar)
- **Documentation**: Auto-generated XML documentation

## Installation

```xml
<PackageReference Include="FbThrift.Build" Version="1.0.0" PrivateAssets="all" />
```

## Usage

The package automatically imports `Directory.Build.props` and `Directory.Build.targets` which are applied to all projects in the solution.

### Versioning

Versions are automatically derived from Git tags:
```bash
git tag v1.2.3
dotnet pack
# Produces 1.2.3
```

For pre-releases:
```bash
git tag v1.2.3-beta.1
dotnet pack
# Produces 1.2.3-beta.1
```

### Overriding Version

```xml
<PropertyGroup>
  <VersionPrefix>2.0.0</VersionPrefix>
  <VersionSuffix>preview.1</VersionSuffix>
</PropertyGroup>
```

## Included Analyzers

- **Roslyn Analyzers** (Microsoft.CodeAnalysis.NetAnalyzers)
- **StyleCop** (StyleCop.Analyzers)
- **SonarAnalyzer** (SonarAnalyzer.CSharp)

## Build Features

- **Deterministic Builds**: `Deterministic=true`
- **SourceLink**: Embedded source for NuGet packages
- **Source Control**: Git-based versioning
- **Signing**: Strong name signing support
- **SBOM**: Software Bill of Materials generation
- **Documentation**: Auto-generated XML docs

## Requirements

- .NET SDK 8.0+
- Git (for versioning)
- SNK key for signing (optional)

## License

MIT License - see LICENSE file for details.