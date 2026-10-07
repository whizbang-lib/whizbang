// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ130's opt-in is matched by the attribute's simple name, so it holds when the attribute does
/// not bind (written without its namespace in scope, the class is an error type carrying the name as
/// written), and an unrelated attribute on the perspective is not mistaken for it.
/// </summary>
/// <tests>src/Whizbang.Generators/EphemeralAnalyzer.cs</tests>
[Category("Analyzers")]
public class EphemeralAnalyzerOptInNameTests {
  private static string _source(string attribute) => $$"""
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    namespace TestApp;
    public class Model { public System.Guid Id { get; set; } }
    [Whizbang.Core.Attributes.Ephemeral]
    public record PresencePing : IEvent;
    public record OrderPlaced : IEvent;

    {{attribute}}
    public class MixedProjection
      : IPerspectiveFor<Model, PresencePing>, IPerspectiveFor<Model, OrderPlaced> {
      public Model Apply(Model current, PresencePing e) => current;
      public Model Apply(Model current, OrderPlaced e) => current;
    }
    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task UnboundOptIn_StillSuppressesWHIZ130Async() {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(
      _source("[DangerouslyAllowMixedEphemeralAndSourcedEvents]"));

    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ130")).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task UnrelatedAttribute_DoesNotSuppressWHIZ130Async() {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(
      _source("[System.Obsolete]"));

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ130")).IsEqualTo(1);
  }
}
