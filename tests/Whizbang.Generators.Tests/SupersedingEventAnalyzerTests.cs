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
/// Tests for <see cref="SupersedingEventAnalyzer"/> (#1729): WHIZ135, an event that is both ephemeral and superseding,
/// and WHIZ136, a superseding event that names more than one key.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/SupersedingEventAnalyzer.cs</code-under-test>
[Category("Analyzers")]
public class SupersedingEventAnalyzerTests {
  private const string HEADER = """
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Attributes;
    namespace TestApp;

    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task EphemeralAndSuperseding_ReportsWHIZ135ErrorAsync() {
    const string source = HEADER + """
      [Ephemeral, Superseding]
      public record PresenceReplaced : IEvent;
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SupersedingEventAnalyzer>(source);
    var found = diagnostics.Where(d => d.Id == "WHIZ135").ToArray();

    await Assert.That(found.Length).IsEqualTo(1);
    await Assert.That(found[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(found[0].GetMessage(CultureInfo.InvariantCulture)).Contains("PresenceReplaced");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task EphemeralByAProfile_AndSupersedingByABase_ReportsWHIZ135Async() {
    const string source = HEADER + """
      [Ephemeral]
      public interface IPresenceProfile : IEvent;
      [Superseding]
      public record SnapshotBase : IEvent;
      public record PresenceSnapshot : SnapshotBase, IPresenceProfile;
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SupersedingEventAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ135")).IsEqualTo(1)
      .Because("both modes resolve the way the generator resolves them: ephemeral through an interface, superseding through a base");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SupersedingAlone_OrEphemeralAlone_NoWHIZ135Async() {
    const string source = HEADER + """
      [Superseding]
      public record EmbeddingUpdated([property: SupersedingKey] string Model) : IEvent;
      [Ephemeral]
      public record Typing : IEvent;
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SupersedingEventAnalyzer>(source);

    await Assert.That(diagnostics.Any(d => d.Id is "WHIZ135" or "WHIZ136")).IsFalse()
      .Because("the control: each mode on its own is valid");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TwoKeys_ReportsWHIZ136Error_NamingBothAsync() {
    const string source = HEADER + """
      [Superseding]
      public record EmbeddingUpdated([property: SupersedingKey] string Model, [property: SupersedingKey] Guid Owner) : IEvent;
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SupersedingEventAnalyzer>(source);
    var found = diagnostics.Where(d => d.Id == "WHIZ136").ToArray();

    await Assert.That(found.Length).IsEqualTo(1);
    await Assert.That(found[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    var message = found[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("EmbeddingUpdated");
    await Assert.That(message).Contains("Model");
    await Assert.That(message).Contains("Owner");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AKeyOnTheBase_AndAnotherOnTheDerivedType_ReportsWHIZ136Async() {
    const string source = HEADER + """
      [Superseding]
      public record SnapshotBase : IEvent { [SupersedingKey] public Guid Owner { get; init; } }
      public record RegionSnapshot : SnapshotBase { [SupersedingKey] public string Region { get; init; } = ""; }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SupersedingEventAnalyzer>(source);
    var found = diagnostics.Where(d => d.Id == "WHIZ136").ToArray();

    await Assert.That(found.Length).IsEqualTo(1).Because("the derived type has two keys; its base, with one, is valid");
    await Assert.That(found[0].GetMessage(CultureInfo.InvariantCulture)).Contains("RegionSnapshot");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AParameterKeyAndItsOwnProperty_CountOnce_NoWHIZ136Async() {
    const string source = HEADER + """
      [Superseding]
      public record ParameterKeyed([SupersedingKey] int Slot) : IEvent;
      public record NotSuperseding([property: SupersedingKey] int A, [property: SupersedingKey] int B) : IEvent;
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SupersedingEventAnalyzer>(source);

    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ136")).IsFalse()
      .Because("a record parameter and the property it declares are one key, and keys on a type that is not superseding are never read");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AGroupWhoseTypesDisagreeOnTheKey_ReportsWHIZ138OnEachAsync() {
    // #2124: the types of a group supersede each other by key, so they must agree on it: all keyless, or all keyed by
    // one CLR type. A Guid? key agrees with a Guid key: both are stored as the same text.
    const string source = HEADER + """
      [Superseding(Group = "region")]
      public record RegionOpened([property: SupersedingKey] Guid Region) : IEvent;
      [Superseding(Group = "region")]
      public record RegionRevised([property: SupersedingKey] Guid? Region) : IEvent;
      [Superseding(Group = "region")]
      public record RegionReplaced : IEvent;
      [Superseding(Group = "slot")]
      public record SlotOpened([property: SupersedingKey] int Slot) : IEvent;
      [Superseding(Group = "slot")]
      public record SlotRenamed([property: SupersedingKey] string Slot) : IEvent;
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SupersedingEventAnalyzer>(source);
    var found = diagnostics.Where(d => d.Id == "WHIZ138").ToArray();
    var messages = found.Select(d => d.GetMessage(CultureInfo.InvariantCulture)).ToArray();

    await Assert.That(found.Length).IsEqualTo(5).Because("every type of a group that disagrees is named, so each can be fixed");
    await Assert.That(found.All(d => d.Severity == DiagnosticSeverity.Error)).IsTrue();
    await Assert.That(messages.Single(m => m.Contains("'RegionReplaced'", StringComparison.Ordinal))).Contains("no key")
      .Because("a keyless type in a keyed group is named with what it has");
    await Assert.That(messages.Single(m => m.Contains("'RegionOpened'", StringComparison.Ordinal))).Contains("System.Guid");
    await Assert.That(messages.Single(m => m.Contains("'SlotRenamed'", StringComparison.Ordinal))).Contains("'slot'");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AGroupWhoseTypesAgree_OrTypesOutsideAnyGroup_NoWHIZ138Async() {
    const string source = HEADER + """
      [Superseding(Group = "region")]
      public record RegionOpened([property: SupersedingKey] Guid Region) : IEvent;
      [Superseding(Group = "region")]
      public record RegionRevised([property: SupersedingKey] Guid? Region) : IEvent;
      [Superseding(Group = "profile")]
      public record ProfileOpened : IEvent;
      [Superseding(Group = "profile")]
      public record ProfileReplaced : IEvent;
      [Superseding]
      public record Keyed([property: SupersedingKey] string Model) : IEvent;
      [Superseding]
      public record Keyless : IEvent;
      [Superseding(Group = "lonely")]
      public record Alone([property: SupersedingKey] string Model) : IEvent;
      public record NotSuperseding : IEvent;
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SupersedingEventAnalyzer>(source);

    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ138")).IsFalse()
      .Because("the control: agreeing groups and ungrouped types, keyed or not, are valid");
  }
}
