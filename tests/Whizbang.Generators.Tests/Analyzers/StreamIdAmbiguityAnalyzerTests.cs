// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// WHIZ036 (#1908): a concrete type that gets <c>[StreamId]</c> from two or more sources naming different properties (two
/// interfaces, or an interface and a base class) and declares none of its own has no single stream id, and the build
/// says so. Sources naming the same property are one source; a type's own <c>[StreamId]</c> settles the question.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/StreamIdAttributeAnalyzer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Utilities/StreamIdSelection.cs</code-under-test>
public class StreamIdAmbiguityAnalyzerTests {
  private const string WHIZ036 = "WHIZ036";

  private const string INTERFACES = """
    using System;
    using Whizbang.Core;
    namespace App;
    public interface IOrderKeyed { [StreamId] Guid OrderId { get; } }
    public interface ICustomerKeyed { [StreamId] Guid CustomerId { get; } }
    public interface IAlsoOrderKeyed { [StreamId] Guid OrderId { get; } }
    """;

  [Test]
  public async Task TwoInterfacesNamingDifferentProperties_ReportWhiz036Async() {
    var diagnostics = await _diagnosticsAsync(INTERFACES + """
      public class Ambiguous : IEvent, IOrderKeyed, ICustomerKeyed { public Guid OrderId { get; set; } public Guid CustomerId { get; set; } }
      """);

    var whiz036 = diagnostics.Single(d => d.Id == WHIZ036);
    var message = whiz036.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(whiz036.Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(message).Contains("Ambiguous");
    await Assert.That(message).Contains("CustomerId");
    await Assert.That(message).Contains("OrderId");
  }

  [Test]
  public async Task AnInterfaceAndABaseClassNamingDifferentProperties_ReportWhiz036Async() {
    var diagnostics = await _diagnosticsAsync(INTERFACES + """
      public abstract class CustomerEvent : IEvent { [StreamId] public Guid CustomerId { get; set; } }
      public class OrderLinked : CustomerEvent, IOrderKeyed { public Guid OrderId { get; set; } }
      """);

    await Assert.That(diagnostics.Count(d => d.Id == WHIZ036)).IsEqualTo(1);
  }

  [Test]
  public async Task OneInterface_IsNotReportedAsync() {
    var diagnostics = await _diagnosticsAsync(INTERFACES + """
      public class OrderNoted : IEvent, IOrderKeyed { public Guid OrderId { get; set; } }
      """);

    await Assert.That(diagnostics.Any(d => d.Id == WHIZ036)).IsFalse();
  }

  [Test]
  public async Task TwoInterfacesNamingTheSameProperty_AreNotReportedAsync() {
    var diagnostics = await _diagnosticsAsync(INTERFACES + """
      public class OrderNoted : IEvent, IOrderKeyed, IAlsoOrderKeyed { public Guid OrderId { get; set; } }
      """);

    await Assert.That(diagnostics.Any(d => d.Id == WHIZ036)).IsFalse();
  }

  [Test]
  public async Task AnInterfaceAndABaseClassNamingTheSameProperty_AreNotReportedAsync() {
    var diagnostics = await _diagnosticsAsync(INTERFACES + """
      public abstract class OrderEvent : IEvent { [StreamId] public Guid OrderId { get; set; } }
      public class OrderNoted : OrderEvent, IOrderKeyed { }
      """);

    await Assert.That(diagnostics.Any(d => d.Id == WHIZ036)).IsFalse();
  }

  [Test]
  public async Task ATypeThatDeclaresItsOwnStreamIdOverAnAmbiguousPair_IsNotReportedAsync() {
    var diagnostics = await _diagnosticsAsync(INTERFACES + """
      public class Settled : IEvent, IOrderKeyed, ICustomerKeyed {
        public Guid OrderId { get; set; }
        public Guid CustomerId { get; set; }
        [StreamId] public Guid SettledId { get; set; }
      }
      """);

    await Assert.That(diagnostics.Any(d => d.Id == WHIZ036)).IsFalse();
  }

  /// <summary>An abstract type is never a message itself; only the concrete types that would carry the ambiguity are reported.</summary>
  [Test]
  public async Task AnAbstractAmbiguousType_IsNotReportedButItsConcreteSubclassIsAsync() {
    var diagnostics = await _diagnosticsAsync(INTERFACES + """
      public abstract class Both : IEvent, IOrderKeyed, ICustomerKeyed { public Guid OrderId { get; set; } public Guid CustomerId { get; set; } }
      public class Concrete : Both { }
      """);

    var reported = diagnostics.Where(d => d.Id == WHIZ036).ToList();
    await Assert.That(reported.Count).IsEqualTo(1);
    await Assert.That(reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Concrete");
  }

  [Test]
  public async Task TheAnalyzer_DeclaresWhiz036AsAnErrorAsync() {
    var descriptor = new StreamIdAttributeAnalyzer().SupportedDiagnostics.Single(d => d.Id == WHIZ036);

    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(descriptor.IsEnabledByDefault).IsTrue();
  }

  private static async Task<ImmutableArray<Diagnostic>> _diagnosticsAsync(string source) =>
    await AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(source)
      .WithAnalyzers([new StreamIdAttributeAnalyzer()])
      .GetAnalyzerDiagnosticsAsync();
}
