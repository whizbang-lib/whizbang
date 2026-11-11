// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// Marks the shared naming code: the one place in the generator code that may read a type's name or resolve a type from a
/// name (#2267). WHIZ606 reports any other place; this attribute is how the analyzer knows where names come from.
/// </summary>
/// <docs>operations/diagnostics/whiz606</docs>
/// <tests>tests/Whizbang.RepoAnalyzers.Tests/TypeNameSourceAnalyzerTests.cs:Runtime_InsideANamingAuthority_IsNotReportedAsync</tests>
[System.AttributeUsage(System.AttributeTargets.Class, Inherited = false)]
internal sealed class TypeNameAuthorityAttribute : System.Attribute;
