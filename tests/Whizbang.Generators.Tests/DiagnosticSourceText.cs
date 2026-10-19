// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The source text a diagnostic points at. A diagnostic can be suppressed where it appears only when it has a location
/// in a syntax tree (#1429): <see cref="Location.None"/> and an external-file location both read as having none here.
/// </summary>
public static class DiagnosticSourceText {
  /// <summary>The located text, or "(no source location)".</summary>
  public static string Of(Diagnostic diagnostic) =>
    diagnostic.Location is { IsInSource: true, SourceTree: { } tree } location
      ? tree.ToString().Substring(location.SourceSpan.Start, location.SourceSpan.Length)
      : "(no source location)";
}
