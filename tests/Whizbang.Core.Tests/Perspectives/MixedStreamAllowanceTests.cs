// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// #2255: which (ephemeral type, Sourced type) pairs may share a stream. A pair is allowed only when a read model with
/// <c>Streams = MixedStreams.Allow</c> applies it and no read model without that opt-in does, so one read model's opt-in
/// can never break another's rebuild.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
/// <code-under-test>src/Whizbang.Core/Perspectives/MixedStreamAllowance.cs</code-under-test>
public class MixedStreamAllowanceTests {
  // Real types, by the name the append path stores them under.
  private static readonly string PING = TypeNameFormatter.Format(typeof(AllowancePinged));
  private static readonly string OPENED = TypeNameFormatter.Format(typeof(AllowanceOpened));
  private static readonly string CLOSED = TypeNameFormatter.Format(typeof(AllowanceClosed));

  [Test]
  public async Task Allowed_TheDefaultRefuse_AllowsNothingAsync() {
    var mixed = _perspective("App.Mixed", MixedStreams.Refuse, [PING, OPENED], new MixedStreamPair(PING, OPENED));

    var allowed = MixedStreamAllowance.Allowed([mixed]);

    await Assert.That(allowed).IsEmpty()
      .Because("the attribute without Streams = Allow changes nothing at run time: a stream still never mixes");
  }

  [Test]
  public async Task Allowed_AnAllowingReadModel_AllowsItsPairsAsync() {
    var mixed = _perspective("App.Mixed", MixedStreams.Allow, [PING, OPENED, CLOSED],
      new MixedStreamPair(PING, OPENED), new MixedStreamPair(PING, CLOSED));
    var plain = _perspective("App.Plain", MixedStreams.Refuse, [OPENED]);

    var allowed = MixedStreamAllowance.Allowed([mixed, plain]);

    await Assert.That(allowed).IsEquivalentTo([new MixedStreamPair(PING, CLOSED), new MixedStreamPair(PING, OPENED)]);
    await Assert.That(allowed[0]).IsEqualTo(new MixedStreamPair(PING, CLOSED)).Because("ordered, so the answer is stable");
  }

  [Test]
  public async Task Allowed_ASecondReadModelWithoutTheOptIn_KeepsThePairRefusedAsync() {
    var mixed = _perspective("App.Mixed", MixedStreams.Allow, [PING, OPENED, CLOSED],
      new MixedStreamPair(PING, OPENED), new MixedStreamPair(PING, CLOSED));
    var other = _perspective("App.Other", MixedStreams.Refuse, [PING, OPENED], new MixedStreamPair(PING, OPENED));

    var allowed = MixedStreamAllowance.Allowed([mixed, other]);

    await Assert.That(allowed).IsEquivalentTo([new MixedStreamPair(PING, CLOSED)])
      .Because("App.Other applies both types without the opt-in, so their streams must stay homogeneous for its rebuild");
  }

  [Test]
  public async Task Allowed_NamesWithAVersion_AreNormalizedAsync() {
    var versioned = OPENED + ", Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    var mixed = _perspective("App.Mixed", MixedStreams.Allow, [PING, versioned], new MixedStreamPair(PING, versioned));

    var allowed = MixedStreamAllowance.Allowed([mixed]);

    await Assert.That(allowed).IsEquivalentTo([new MixedStreamPair(PING, OPENED)]);
  }

  [Test]
  public async Task Allowed_WithoutARegistry_IsEmptyAsync() {
    await Assert.That(MixedStreamAllowance.Allowed((IPerspectiveRunnerRegistry?)null)).IsEmpty();
    var registry = new Registry(_perspective("App.Mixed", MixedStreams.Allow, [PING, OPENED], new MixedStreamPair(PING, OPENED)));
    await Assert.That(MixedStreamAllowance.Allowed(registry)).IsEquivalentTo([new MixedStreamPair(PING, OPENED)]);
    await Assert.That(() => MixedStreamAllowance.Allowed((IEnumerable<PerspectiveRegistrationInfo>)null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Stored_AddsEachTypesFormerNames_FromTheCatalogAsync() {
    var catalog = new Catalog(
      new MessageTypeCatalogEntry(typeof(AllowancePinged), TypeNameFormatter.FormatClrTypeName(typeof(AllowancePinged)), "event", null) {
        FormerNames = ["Old.Pinged", " "],
      },
      new MessageTypeCatalogEntry(typeof(AllowanceOpened), TypeNameFormatter.FormatClrTypeName(typeof(AllowanceOpened)), "event", null));
    var assembly = typeof(AllowancePinged).Assembly.GetName().Name;

    var stored = MixedStreamAllowance.Stored([new MixedStreamPair(PING, OPENED)], catalog);

    await Assert.That(stored).IsEquivalentTo([new MixedStreamPair($"Old.Pinged, {assembly}", OPENED), new MixedStreamPair(PING, OPENED)])
      .Because("events stored under a former name hold the stream as the type does");
    await Assert.That(MixedStreamAllowance.StoredNames(OPENED, null)).IsEquivalentTo([OPENED]);
    await Assert.That(MixedStreamAllowance.StoredNames(OPENED, new Unavailable())).IsEquivalentTo([OPENED]);
    await Assert.That(() => MixedStreamAllowance.Stored(null!, catalog)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task IgnoredByRebuild_IsTheEphemeralSide_OfPairsTheReadModelDoesNotApplyAsync() {
    IReadOnlyList<MixedStreamPair> allowed = [new MixedStreamPair(PING, OPENED)];
    var durableOnly = _perspective("App.Durable", MixedStreams.Refuse, [OPENED]);
    var mixed = _perspective("App.Mixed", MixedStreams.Allow, [PING, OPENED], new MixedStreamPair(PING, OPENED));

    await Assert.That(MixedStreamAllowance.IgnoredByRebuild(durableOnly, allowed)).IsEquivalentTo([PING])
      .Because("a read model applying only the Sourced type rebuilds a mixed stream from its Sourced events");
    await Assert.That(MixedStreamAllowance.IgnoredByRebuild(mixed, allowed)).IsEmpty()
      .Because("the opted-in read model applies the ephemeral type, so its full rebuild of the stream stays refused");
    await Assert.That(MixedStreamAllowance.IgnoredByRebuild(null, allowed)).IsEmpty();
    await Assert.That(MixedStreamAllowance.IgnoredByRebuild(durableOnly, [])).IsEmpty();
    await Assert.That(() => MixedStreamAllowance.IgnoredByRebuild(durableOnly, null!)).Throws<ArgumentNullException>();
  }

  private static PerspectiveRegistrationInfo _perspective(string name, MixedStreams streams, string[] events, params MixedStreamPair[] pairs) =>
    new(name, "global::" + name, "global::App.Model", events) { MixedStreams = streams, MixedStreamPairs = pairs };

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  private sealed class Unavailable : IMessageTypeCatalog {
    public bool IsAvailable => false;
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => throw new InvalidOperationException("an unavailable catalog is never read");
  }

  private sealed class Registry(params PerspectiveRegistrationInfo[] perspectives) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => perspectives;
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
    public IReadOnlyList<Type> GetEventTypes() => [];
  }
}

/// <summary>A real ephemeral event type for the mixed-stream allowance tests.</summary>
internal sealed record AllowancePinged;

/// <summary>A real Sourced event type for the mixed-stream allowance tests.</summary>
internal sealed record AllowanceOpened;

/// <summary>A second real Sourced event type for the mixed-stream allowance tests.</summary>
internal sealed record AllowanceClosed;
