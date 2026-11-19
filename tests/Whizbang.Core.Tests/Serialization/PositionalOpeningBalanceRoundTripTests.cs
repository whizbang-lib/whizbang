// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>An opening event declared in positional form over the framework's opening base (#2016).</summary>
public sealed record PositionalAccountOpened(decimal Balance) : OpeningBalance;

/// <summary>
/// Issue #2016: a positional record deriving from <see cref="OpeningBalance"/> made the generated JSON context fail to
/// compile (CS7036), because the context constructed it without its positional argument. The context now passes the
/// positional member to the record's constructor and sets the base's members after it, so the event round-trips with
/// every member: the one it declares and the link the framework fills.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
[Category("Serialization")]
public class PositionalOpeningBalanceRoundTripTests {
  private static JsonSerializerOptions _options() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    options.MakeReadOnly();
    return options;
  }

  [Test]
  public async Task GeneratedContext_ResolvesThePositionalOpeningEvent_SoTheRoundTripExercisesItAsync() {
    IJsonTypeInfoResolver generated = MessageJsonContext.Default;
    var info = generated.GetTypeInfo(typeof(PositionalAccountOpened), _options());

    await Assert.That(info).IsNotNull()
      .Because("the generated context, not a reflection fallback, must be the one serializing this event");
    await Assert.That(info!.Properties.Select(p => p.Name))
      .IsEquivalentTo(["Balance", "SuccessorStreamId", "PredecessorStreamId", "PredecessorFinalVersion"]);
  }

  [Test]
  public async Task GeneratedContext_RoundTripsThePositionalMemberAndTheBaseMembersAsync() {
    var options = _options();
    Guid successor = TrackedGuid.New();
    Guid predecessor = TrackedGuid.New();
    var original = new PositionalAccountOpened(125.50m) {
      SuccessorStreamId = successor,
      PredecessorStreamId = predecessor,
      PredecessorFinalVersion = 42,
    };

    var json = JsonSerializer.Serialize(original, options);
    var read = JsonSerializer.Deserialize<PositionalAccountOpened>(json, options)!;

    await Assert.That(read).IsEqualTo(original)
      .Because("the positional member reaches the constructor and the base's members are set after it");
    await Assert.That(read.Balance).IsEqualTo(125.50m);
    await Assert.That(read.SuccessorStreamId).IsEqualTo(successor);
    await Assert.That(read.PredecessorStreamId).IsEqualTo(predecessor);
    await Assert.That(read.PredecessorFinalVersion).IsEqualTo(42L);
  }
}
