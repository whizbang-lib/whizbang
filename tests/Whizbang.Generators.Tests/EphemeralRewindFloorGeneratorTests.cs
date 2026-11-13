// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #2295: whether a perspective keeps a rewind floor is decided by what it consumes. A perspective that applies only
/// ephemeral events takes no snapshots and its ephemeral bodies are reaped without one, unless it opts in with
/// <c>[KeepEphemeralRewindFloor]</c>; a perspective that applies any durable event keeps its floor. Both generators read
/// the one classification: the runner generator turns snapshots off, and the EF Core association generator registers a
/// marker the reaper reads.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#snapshots-follow-what-a-read-model-consumes</docs>
public partial class EphemeralRewindFloorGeneratorTests {
  private const string EPHEMERAL_ONLY = """
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  [Ephemeral]
  public record UserIsTyping : IEvent {
    public string ConversationId { get; init; } = "";
  }

  [Ephemeral]
  public record UserStoppedTyping : IEvent {
    public string ConversationId { get; init; } = "";
  }

  public record PresenceModel {
    [StreamId]
    public string ConversationId { get; init; } = "";
  }

  public class PresencePerspective : IPerspectiveFor<PresenceModel, UserIsTyping, UserStoppedTyping> {
    public PresenceModel Apply(PresenceModel currentData, UserIsTyping @event) => currentData;
    public PresenceModel Apply(PresenceModel currentData, UserStoppedTyping @event) => currentData;
  }
}
""";

  private const string OPTED_IN = """
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  [Ephemeral]
  public record UserIsTyping : IEvent {
    public string ConversationId { get; init; } = "";
  }

  public record PresenceModel {
    [StreamId]
    public string ConversationId { get; init; } = "";
  }

  [KeepEphemeralRewindFloor]
  public class PresencePerspective : IPerspectiveFor<PresenceModel, UserIsTyping> {
    public PresenceModel Apply(PresenceModel currentData, UserIsTyping @event) => currentData;
  }
}
""";

  private const string MIXED = """
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  [Ephemeral]
  public record UserIsTyping : IEvent {
    public string ConversationId { get; init; } = "";
  }

  public record ConversationOpened : IEvent {
    public string ConversationId { get; init; } = "";
  }

  public record PresenceModel {
    [StreamId]
    public string ConversationId { get; init; } = "";
  }

  public class PresencePerspective : IPerspectiveFor<PresenceModel, UserIsTyping, ConversationOpened> {
    public PresenceModel Apply(PresenceModel currentData, UserIsTyping @event) => currentData;
    public PresenceModel Apply(PresenceModel currentData, ConversationOpened @event) => currentData;
  }
}
""";

  private const string DURABLE = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record ConversationOpened : IEvent {
    public string ConversationId { get; init; } = "";
  }

  public record PresenceModel {
    [StreamId]
    public string ConversationId { get; init; } = "";
  }

  public class PresencePerspective : IPerspectiveFor<PresenceModel, ConversationOpened> {
    public PresenceModel Apply(PresenceModel currentData, ConversationOpened @event) => currentData;
  }
}
""";

  // The only durable event is applied through the global interface: it still counts, so the perspective keeps its
  // floor. Reading only the single-stream interfaces would class it as ephemeral-only and reap its bodies unprotected.
  private const string DURABLE_THROUGH_GLOBAL = """
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  [Ephemeral]
  public record UserIsTyping : IEvent {
    public string ConversationId { get; init; } = "";
  }

  public record ConversationOpened : IEvent {
    public string ConversationId { get; init; } = "";
  }

  public record PresenceModel {
    [StreamId]
    public string ConversationId { get; init; } = "";
  }

  public class PresencePerspective
      : IPerspectiveFor<PresenceModel, UserIsTyping>, IGlobalPerspectiveFor<PresenceModel, string, ConversationOpened> {
    public PresenceModel Apply(PresenceModel currentData, UserIsTyping @event) => currentData;
    public string GetPartitionKey(ConversationOpened @event) => @event.ConversationId;
    public PresenceModel Apply(PresenceModel currentData, ConversationOpened @event) => currentData;
  }
}
""";

  private const string KEEPS_NO_FLOOR = "private const bool _keepsRewindFloor = false;";
  private const string KEEPS_A_FLOOR = "private const bool _keepsRewindFloor = true;";
  private const string MARKER_CALL = "EphemeralRewindFloor.NoFloorAssociationEntryJson(\"TestNamespace.PresencePerspective\", serviceName)";

  private static string _runner(string source) =>
    GeneratorTestHelper.GetGeneratedSource(
      GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source), "PresencePerspectiveRunner.g.cs")!;

  private static string _associations(string source) =>
    GeneratorTestHelper.GetGeneratedSource(
      GeneratorTestHelper.RunGenerator<EFCorePerspectiveAssociationGenerator>(source), "EFCorePerspectiveAssociations.g.cs")!;

  [Test]
  [RequiresAssemblyFiles]
  public async Task Runner_OfAnEphemeralOnlyPerspective_KeepsNoRewindFloorAsync() {
    var runner = _runner(EPHEMERAL_ONLY);

    await Assert.That(runner).Contains(KEEPS_NO_FLOOR)
      .Because("a perspective that applies only ephemeral events takes no snapshots by default");
    await Assert.That(runner).Contains("private const bool _isEphemeralPerspective = true;")
      .Because("it stays an ephemeral perspective: a late event with no floor is dropped, never replayed from zero");
  }

  [Test]
  [RequiresAssemblyFiles]
  [Arguments(OPTED_IN, "opted in with [KeepEphemeralRewindFloor]")]
  [Arguments(MIXED, "applies a durable event beside the ephemeral one")]
  [Arguments(DURABLE, "applies only durable events")]
  public async Task Runner_OfAPerspectiveThatNeedsAFloor_KeepsItAsync(string source, string why) {
    var runner = _runner(source);

    await Assert.That(runner).Contains(KEEPS_A_FLOOR).Because($"the perspective {why}");
    await Assert.That(runner).DoesNotContain(KEEPS_NO_FLOOR);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Associations_OfAnEphemeralOnlyPerspective_RegisterTheNoFloorMarkerAsync() {
    var associations = _associations(EPHEMERAL_ONLY);

    await Assert.That(associations).Contains(MARKER_CALL)
      .Because("the reaper reads the marker to reap the perspective's bodies without waiting for a snapshot");
  }

  [Test]
  [RequiresAssemblyFiles]
  [Arguments(OPTED_IN)]
  [Arguments(MIXED)]
  [Arguments(DURABLE)]
  [Arguments(DURABLE_THROUGH_GLOBAL)]
  public async Task Associations_OfAPerspectiveThatKeepsAFloor_RegisterNoMarkerAsync(string source) {
    var associations = _associations(source);

    await Assert.That(associations).DoesNotContain("NoFloorAssociationEntryJson")
      .Because("without the marker the coverage gate holds the perspective's bodies until a snapshot covers them");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Associations_TheMarker_ChangesTheAssociationsHash_SoAStartupWithItReRegistersAsync() {
    var noFloor = _hash(_associations(EPHEMERAL_ONLY));
    var optedIn = _hash(_associations(EPHEMERAL_ONLY.Replace("public class PresencePerspective", "[KeepEphemeralRewindFloor] public class PresencePerspective", StringComparison.Ordinal)));

    await Assert.That(noFloor).IsNotEqualTo(optedIn)
      .Because("the same associations with and without the marker are different registrations; an unchanged hash skips the step");
  }

  private static string _hash(string source) => _associationsHash().Match(source).Groups[1].Value;

  [System.Text.RegularExpressions.GeneratedRegex(@"AssociationsHash\s*=\s*""([0-9a-f]{64})""")]
  private static partial System.Text.RegularExpressions.Regex _associationsHash();
}
