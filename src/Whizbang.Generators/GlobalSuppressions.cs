// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

// RS1035: File I/O is intentionally used for VSCode tooling integration
// MessageRegistryGenerator needs to load code-docs-map.json and code-tests-map.json
// from the sibling documentation repository for enhanced IDE features.
// This is acceptable because:
// 1. This generator is ONLY for VSCode tooling (not runtime code generation)
// 2. File I/O happens during build-time, not at runtime
// 3. Graceful fallback if files are not found (returns empty collections)
[assembly: SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1035:Do not use APIs banned for analyzers", Justification = "VSCode tooling integration requires file I/O to load documentation and test mappings", Scope = "member", Target = "~M:Whizbang.Generators.PathResolver.FindDocsRepositoryPath~System.String")]
[assembly: SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1035:Do not use APIs banned for analyzers", Justification = "VSCode tooling integration requires file I/O to load documentation and test mappings", Scope = "member", Target = "~M:Whizbang.Generators.PathResolver.FindGitRoot(System.String)~System.String")]
[assembly: SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1035:Do not use APIs banned for analyzers", Justification = "VSCode tooling integration requires file I/O to load documentation and test mappings", Scope = "member", Target = "~M:Whizbang.Generators.MessageRegistryGenerator.LoadCodeDocsMap(Microsoft.CodeAnalysis.SourceProductionContext)~System.Collections.Generic.Dictionary{System.String,System.String}")]
[assembly: SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1035:Do not use APIs banned for analyzers", Justification = "VSCode tooling integration requires file I/O to load documentation and test mappings", Scope = "member", Target = "~M:Whizbang.Generators.MessageRegistryGenerator.LoadCodeTestsMap(Microsoft.CodeAnalysis.SourceProductionContext)~System.Collections.Generic.Dictionary{System.String,Whizbang.Generators.TestInfo[]}")]
