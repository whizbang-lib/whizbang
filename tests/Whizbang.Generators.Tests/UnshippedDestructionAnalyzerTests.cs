// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ135 (#1735): a <c>Destruction</c> value whose handler has not shipped is a compile error, not a silent delete.
/// <c>OnCompaction</c> has no framework handler, and <c>Archived</c> has none for event bodies, so either is refused
/// wherever it is named; <c>WhenConsumed</c> and <c>AfterTtl</c> are acted on and pass.
/// </summary>
/// <docs>operations/diagnostics/whiz135</docs>
[Category("Analyzers")]
public class UnshippedDestructionAnalyzerTests {
  private const string HEADER = """
    using Whizbang.Core;
    using Whizbang.Core.Attributes;
    namespace TestApp;

    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task OnCompaction_OnAnEphemeralEvent_IsAnErrorAsync() {
    const string source = HEADER + """
      [Ephemeral(Destruction = Destruction.OnCompaction)]
      public record PresencePing : IEvent;
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<UnshippedDestructionAnalyzer>(source);

    var found = diagnostics.Where(d => d.Id == "WHIZ135").ToArray();
    await Assert.That(found.Length).IsEqualTo(1);
    await Assert.That(found[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(found[0].GetMessage(CultureInfo.InvariantCulture)).Contains("OnCompaction");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Archived_NamedAnywhere_IsAnErrorAsync() {
    const string source = HEADER + """
      public static class Settings {
        public static readonly Destruction Chosen = Destruction.Archived;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<UnshippedDestructionAnalyzer>(source);

    var found = diagnostics.Where(d => d.Id == "WHIZ135").ToArray();
    await Assert.That(found.Length).IsEqualTo(1);
    await Assert.That(found[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Archived");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ShippedValues_AndAnotherEnumsMemberOfTheSameName_PassAsync() {
    // The control: the analyzer flags only the two Destruction members, by symbol, never by name.
    const string source = HEADER + """
      public enum Mine { OnCompaction, Archived }
      [Ephemeral(Destruction = Destruction.AfterTtl, TtlSeconds = 60)]
      public record Ttl : IEvent;
      [Ephemeral(Destruction = Destruction.WhenConsumed)]
      public record Consumed : IEvent;
      public static class Settings {
        public static readonly Mine A = Mine.OnCompaction;
        public static readonly Mine B = Mine.Archived;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<UnshippedDestructionAnalyzer>(source);

    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ135")).IsFalse();
  }
}
