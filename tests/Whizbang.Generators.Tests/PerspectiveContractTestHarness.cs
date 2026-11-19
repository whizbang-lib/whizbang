// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Runs every analyzer the generator assembly ships over a perspective source and returns what it reports, so a test
/// of the perspective contract (#1936) asks about a diagnostic id rather than about whichever class reports it. The
/// source is first proven to compile: a contract test over code the compiler rejects would measure the compiler.
/// </summary>
internal static class PerspectiveContractTestHarness {
  /// <summary>Usings and types every contract test shares: two models and the events they handle.</summary>
  internal const string PREAMBLE = """
      #nullable enable
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record Order {
        [StreamId] public Guid Id { get; init; }
        public string Status { get; init; } = "";
      }

      public record Invoice {
        [StreamId] public Guid Id { get; init; }
      }

      public record OrderPlaced : IEvent {
        [StreamId] public Guid OrderId { get; init; }
      }

      public record OrderUpdated : IEvent {
        [StreamId] public Guid OrderId { get; init; }
      }

      public record OrderStatusChanged : IEvent {
        [StreamId] public Guid OrderId { get; init; }
        public string Status { get; init; } = "";
      }

      public record InvoiceIssued : IEvent {
        [StreamId] public Guid InvoiceId { get; init; }
      }

      public record OrderLine(string Sku);

      """;

  /// <summary>
  /// The diagnostics with <paramref name="id"/> that the generator assembly's analyzers report over
  /// <paramref name="source"/>, after proving the source compiles.
  /// </summary>
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  internal static async Task<Diagnostic[]> ReportedAsync(string source, string id) {
    var compilation = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(source);
    var errors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();
    if (errors.Length > 0) {
      throw new InvalidOperationException(
        "The test source does not compile: " + string.Join(Environment.NewLine, errors.Select(static e => e.ToString())));
    }

    var diagnostics = await compilation.WithAnalyzers(_analyzers()).GetAnalyzerDiagnosticsAsync();
    return [.. diagnostics.Where(d => d.Id == id)];
  }

  /// <summary>The source text a diagnostic points at.</summary>
  internal static string LocatedText(Diagnostic diagnostic) =>
    diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan);

  [RequiresUnreferencedCode("Finds the analyzers by reflection.")]
  private static ImmutableArray<DiagnosticAnalyzer> _analyzers() =>
    [.. typeof(DiagnosticDescriptors).Assembly.GetTypes()
      .Where(static t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t)
        && t.GetCustomAttribute<DiagnosticAnalyzerAttribute>() is not null)
      .Select(static t => (DiagnosticAnalyzer)Activator.CreateInstance(t)!)];
}
