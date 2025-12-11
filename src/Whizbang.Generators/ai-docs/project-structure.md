# Project Structure

**File organization and .csproj configuration for source generators**

This document explains how to organize generator project files and configure the `.csproj` for proper template handling and Roslyn integration.

---

## Table of Contents

1. [File Organization](#file-organization)
2. [Project Configuration](#project-configuration)
3. [Template Configuration](#template-configuration)
4. [Debug Configuration](#debug-configuration)
5. [Package References](#package-references)

---

## File Organization

### Recommended Structure

```
Whizbang.Generators/
├── CLAUDE.md                           # This file
├── ai-docs/                            # Focused AI documentation
│   ├── README.md
│   ├── architecture.md
│   ├── performance-principles.md
│   └── ...
├── Whizbang.Generators.csproj          # Project configuration
├── ReceptorDiscoveryGenerator.cs       # Generator implementations
├── MessageRegistryGenerator.cs
├── DiagnosticsGenerator.cs
├── ReceptorInfo.cs                     # Value type records
├── MessageTypeInfo.cs
├── DiagnosticDescriptors.cs            # Diagnostic definitions
├── TemplateUtilities.cs                # Shared template utilities
└── Templates/                          # Template files (embedded resources)
    ├── DispatcherTemplate.cs
    ├── DispatcherRegistrationsTemplate.cs
    ├── WhizbangDiagnosticsTemplate.cs
    ├── Placeholders/
    │   └── PlaceholderTypes.cs         # Placeholder types for IDE support
    └── Snippets/
        └── DispatcherSnippets.cs       # Reusable code snippets
```

---

### Key Directories

**Root** (`/`):
- Generator implementations (`*Generator.cs`)
- Value type records (`*Info.cs`)
- Shared utilities (`TemplateUtilities.cs`)
- Diagnostic descriptors (`DiagnosticDescriptors.cs`)

**Templates** (`Templates/`):
- Template files (`*Template.cs`)
- Embedded as resources
- Excluded from compilation
- Full IDE support

**Placeholders** (`Templates/Placeholders/`):
- Placeholder types for template IntelliSense
- Example: `PlaceholderMessage`, `IPlaceholderReceptor`
- Excluded from compilation

**Snippets** (`Templates/Snippets/`):
- Reusable code snippets
- Extracted via `TemplateUtilities.ExtractSnippet`
- Example: `DispatcherSnippets.cs`

**AI Docs** (`ai-docs/`):
- Focused AI documentation
- Topic-specific guidance
- See [README.md](README.md)

---

### Naming Conventions

**Generators**:
- `*Generator.cs` - Generator implementations
- Example: `ReceptorDiscoveryGenerator.cs`, `MessageRegistryGenerator.cs`

**Value Types**:
- `*Info.cs` - Value type records for caching
- Example: `ReceptorInfo.cs`, `MessageTypeInfo.cs`

**Templates**:
- `*Template.cs` - Full file templates
- Example: `DispatcherTemplate.cs`, `RegistrationsTemplate.cs`

**Snippets**:
- `*Snippets.cs` - Snippet collections
- Example: `DispatcherSnippets.cs`, `EntitySnippets.cs`

**Utilities**:
- `*Utilities.cs` - Shared utility classes
- Example: `TemplateUtilities.cs`, `SymbolUtilities.cs`

---

## Project Configuration

### Basic Settings

```xml
<PropertyGroup>
  <!-- Target netstandard2.0 for broad compatibility -->
  <TargetFramework>netstandard2.0</TargetFramework>

  <!-- Mark as Roslyn component -->
  <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
  <IsRoslynComponent>true</IsRoslynComponent>

  <!-- C# 12.0 language features -->
  <LangVersion>12.0</LangVersion>

  <!-- Null reference types -->
  <Nullable>enable</Nullable>
</PropertyGroup>
```

---

### Why These Settings?

**TargetFramework: netstandard2.0**:
- Broad compatibility with .NET Framework, .NET Core, .NET 5+
- Required for Roslyn generators
- DON'T use net8.0 or net9.0

**EnforceExtendedAnalyzerRules**:
- Enables additional analyzer rules for generators
- Catches common generator mistakes at compile-time
- HIGHLY RECOMMENDED

**IsRoslynComponent**:
- Marks project as Roslyn component
- Enables generator-specific tooling
- Required for generators

**LangVersion: 12.0**:
- Modern C# features (record types, pattern matching, etc.)
- Target-typed new expressions
- File-scoped namespaces
- Recommended for generator development

**Nullable: enable**:
- Null reference type checking
- Catches null reference issues at compile-time
- Best practice for all C# projects

---

## Template Configuration

### CRITICAL: Template Exclusion and Embedding

```xml
<ItemGroup>
  <!-- Templates: exclude from compilation, embed as resources -->
  <Compile Remove="Templates\**\*.cs" />
  <None Include="Templates\**\*.cs" />
  <EmbeddedResource Include="Templates\**\*.cs" />
</ItemGroup>
```

---

### Why This Configuration?

**`<Compile Remove="Templates\**\*.cs" />`**:
- Templates EXCLUDED from compilation
- Prevents build errors from placeholder types
- Templates are not part of generator assembly code

**`<None Include="Templates\**\*.cs" />`**:
- Templates VISIBLE in IDE
- Full IntelliSense support
- Syntax highlighting
- Refactoring support
- Can edit like normal C# files

**`<EmbeddedResource Include="Templates\**\*.cs" />`**:
- Templates EMBEDDED in generator assembly
- Available at runtime via `Assembly.GetManifestResourceStream`
- Loaded with `TemplateUtilities.GetEmbeddedTemplate`
- No external file dependencies

---

### Result

Templates are:
- ✅ Real C# files with full IDE support
- ✅ Excluded from compilation (no build errors)
- ✅ Embedded in assembly (available at runtime)

---

## Debug Configuration

### Enable Viewing Generated Files

```xml
<PropertyGroup>
  <!-- Enable viewing generated files (for debugging) -->
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>$(MSBuildProjectDirectory)/.whizbang-generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
```

---

### Why?

**EmitCompilerGeneratedFiles**:
- Writes generated files to disk
- Allows viewing generated code during development
- Essential for debugging generators

**CompilerGeneratedFilesOutputPath**:
- Custom output directory for generated files
- Default: `obj/Debug/netstandard2.0/generated/`
- Custom: `.whizbang-generated/` (easier to find)

**Generated files location**:
```
.whizbang-generated/
├── Whizbang.Generators/
│   ├── ReceptorDiscoveryGenerator/
│   │   ├── Dispatcher.g.cs
│   │   ├── DispatcherRegistrations.g.cs
│   │   └── Diagnostics.g.cs
│   └── MessageRegistryGenerator/
│       └── MessageRegistry.g.cs
```

---

### Performance Reporting

```xml
<PropertyGroup>
  <!-- Enable performance reporting -->
  <ReportAnalyzer>true</ReportAnalyzer>
</PropertyGroup>
```

**Benefits**:
- Reports generator execution time in build output
- Helps identify performance issues
- Useful during development

**Example output**:
```
Generator 'ReceptorDiscoveryGenerator' took 45ms
Generator 'MessageRegistryGenerator' took 32ms
Generator 'DiagnosticsGenerator' took 1ms
```

---

## Package References

### Required Packages

```xml
<ItemGroup>
  <!-- Roslyn API for source generation -->
  <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" PrivateAssets="all" />

  <!-- Analyzer infrastructure -->
  <PackageReference Include="Microsoft.CodeAnalysis.Analyzers" Version="3.3.4" PrivateAssets="all" />

  <!-- Polyfills for modern C# features in netstandard2.0 -->
  <PackageReference Include="PolySharp" Version="1.14.1" PrivateAssets="all" />
</ItemGroup>
```

---

### Why These Packages?

**Microsoft.CodeAnalysis.CSharp**:
- Roslyn API for C# syntax and semantic analysis
- Required for all source generators
- Version should match target Visual Studio/SDK

**Microsoft.CodeAnalysis.Analyzers**:
- Analyzer infrastructure and best practices
- Provides analyzer-specific diagnostics
- Helps catch generator mistakes

**PolySharp**:
- Polyfills for modern C# features in netstandard2.0
- Enables C# 12.0 features in netstandard2.0 projects
- Example: `required` keyword, init-only setters
- Zero runtime dependencies (compile-time only)

---

### PrivateAssets="all"

**CRITICAL**: All package references MUST have `PrivateAssets="all"`:

```xml
<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" PrivateAssets="all" />
```

**Why?**
- Prevents generator dependencies from leaking to consuming projects
- Generator runs in compiler process (isolated)
- Consumer projects don't need Roslyn packages
- Reduces dependency bloat

---

## Complete .csproj Example

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- Target Framework -->
    <TargetFramework>netstandard2.0</TargetFramework>

    <!-- Language -->
    <LangVersion>12.0</LangVersion>
    <Nullable>enable</Nullable>

    <!-- Roslyn Component -->
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <IsRoslynComponent>true</IsRoslynComponent>

    <!-- Debugging -->
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
    <CompilerGeneratedFilesOutputPath>$(MSBuildProjectDirectory)/.whizbang-generated</CompilerGeneratedFilesOutputPath>

    <!-- Performance -->
    <ReportAnalyzer>true</ReportAnalyzer>
  </PropertyGroup>

  <ItemGroup>
    <!-- Required Packages -->
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" PrivateAssets="all" />
    <PackageReference Include="Microsoft.CodeAnalysis.Analyzers" Version="3.3.4" PrivateAssets="all" />
    <PackageReference Include="PolySharp" Version="1.14.1" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <!-- Templates: exclude from compilation, embed as resources -->
    <Compile Remove="Templates\**\*.cs" />
    <None Include="Templates\**\*.cs" />
    <EmbeddedResource Include="Templates\**\*.cs" />
  </ItemGroup>

</Project>
```

---

## Checklist

Before deploying generator project:

- [ ] TargetFramework is `netstandard2.0`
- [ ] `EnforceExtendedAnalyzerRules` enabled
- [ ] `IsRoslynComponent` enabled
- [ ] Templates excluded from compilation (`Compile Remove`)
- [ ] Templates included for IDE (`None Include`)
- [ ] Templates embedded as resources (`EmbeddedResource Include`)
- [ ] All package references have `PrivateAssets="all"`
- [ ] `EmitCompilerGeneratedFiles` enabled (for debugging)
- [ ] `ReportAnalyzer` enabled (for performance monitoring)
- [ ] File organization follows conventions

---

## See Also

- [template-system.md](template-system.md) - Using templates in generators
- [architecture.md](architecture.md) - Why multiple generators
- [quick-reference.md](quick-reference.md) - Complete generator example
