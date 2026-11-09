// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Routing.ExcludedEventFixtures;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// The startup checks and the per-topic plan of the opt-out list (#2175): an excluded type this service consumes fails
/// startup, an unknown or unsubscribed one is a warning, and each topic excludes only the types published to it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Routing/ExcludedEventTypePlanner.cs</code-under-test>
public class ExcludedEventTypePlannerTests {
  private const string FIXTURE_TOPIC = "whizbang.core.tests.routing.excludedeventfixtures";
  private const string OTHER_TOPIC = "myapp.other.events";

  private static readonly string _noisySubject = EventSubject.For(typeof(NoisyEvent));
  private static readonly string _nestedSubject = EventSubject.For(typeof(Container.NestedNoisyEvent));

  private static IReadOnlyDictionary<string, IReadOnlyList<string>> _plan(
      RoutingOptions options,
      RecordingRoutingLogger logger,
      IConsumerRegistry? consumers = null,
      IEnumerable<Type>? runtime = null,
      IMessageTypeCatalog? catalog = null,
      string[]? topics = null) =>
    ExcludedEventTypePlanner.Plan(
      options,
      new HashSet<string>(topics ?? [FIXTURE_TOPIC, OTHER_TOPIC], StringComparer.OrdinalIgnoreCase),
      consumers ?? new FixedConsumerRegistry(),
      runtime ?? [],
      catalog ?? new FixedMessageCatalog(true, typeof(NoisyEvent), typeof(ChattyEvent), typeof(Container.NestedNoisyEvent)),
      logger);

  [Test]
  public async Task Plan_NothingExcluded_EveryTopicExcludesNothingAndNothingIsLoggedAsync() {
    var logger = new RecordingRoutingLogger();

    var plan = _plan(new RoutingOptions(), logger);

    await Assert.That(plan.Keys).IsEquivalentTo([FIXTURE_TOPIC, OTHER_TOPIC]);
    await Assert.That(plan[FIXTURE_TOPIC]).IsEmpty();
    await Assert.That(plan[OTHER_TOPIC]).IsEmpty();
    await Assert.That(logger.Entries).IsEmpty();
  }

  [Test]
  public async Task Plan_ExcludedTypes_LandOnlyOnTheTopicTheyArePublishedTo_OrderedAndDistinctAsync() {
    var logger = new RecordingRoutingLogger();
    var options = new RoutingOptions()
      .ExcludeEventType<Container.NestedNoisyEvent>()
      .ExcludeEventType<NoisyEvent>()
      // The same type by its CLR name: one subject, not two.
      .ExcludeEventTypes("Whizbang.Core.Tests.Routing.ExcludedEventFixtures.NoisyEvent");

    var plan = _plan(options, logger);

    string[] expected = [_nestedSubject, _noisySubject];
    await Assert.That(plan[FIXTURE_TOPIC]).IsEquivalentTo(expected);
    await Assert.That(plan[FIXTURE_TOPIC][0]).IsEqualTo(_nestedSubject)
      .Because("subjects are in ordinal order, so the rule is the same on every instance");
    await Assert.That(plan[OTHER_TOPIC]).IsEmpty();
    await Assert.That(logger.Has(LogLevel.Information, FIXTURE_TOPIC)).IsTrue()
      .Because("the effective exclusions are logged at startup");
    await Assert.That(logger.Has(LogLevel.Information, _noisySubject)).IsTrue();
  }

  [Test]
  public async Task Plan_ExcludedTypeConsumedByACompileTimeConsumer_FailsStartupAsync() {
    var consumers = new FixedConsumerRegistry(consumed: [
      new ConsumedMessageInfo(TypeNameFormatter.Format(typeof(Container.NestedNoisyEvent)), IsEvent: true)]);
    var options = new RoutingOptions().ExcludeEventTypes(
      "Whizbang.Core.Tests.Routing.ExcludedEventFixtures.Container+NestedNoisyEvent");

    var thrown = await Assert.That(() => _plan(options, new RecordingRoutingLogger(), consumers))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains("Container+NestedNoisyEvent");
    await Assert.That(thrown.Message).Contains("consumes");
  }

  [Test]
  public async Task Plan_ExcludedTypeConsumedByARuntimeSubscription_FailsStartupAsync() {
    var options = new RoutingOptions().ExcludeEventType<NoisyEvent>();

    var thrown = await Assert.That(() => _plan(options, new RecordingRoutingLogger(), runtime: [typeof(NoisyEvent)]))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains("NoisyEvent");
  }

  [Test]
  public async Task Plan_ExcludedTypeTheReceiveBoundaryKeeps_FailsStartupAsync() {
    var wire = TypeNameFormatter.Format(typeof(NoisyEvent));
    var consumers = new FixedConsumerRegistry(predicate: name => string.Equals(name, wire, StringComparison.Ordinal));
    var options = new RoutingOptions().ExcludeEventType<NoisyEvent>();

    await Assert.That(() => _plan(options, new RecordingRoutingLogger(), consumers))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Plan_ExcludedTypeOnAnAbsorbedNamespace_IsAllowedWithAWarningAndExcludedAsync() {
    // Absorbing keeps unconsumed events of a namespace; excluding one noisy type of it is a deliberate refinement, so
    // it is allowed, and the warning says that type will not be stored.
    var logger = new RecordingRoutingLogger();
    var options = new RoutingOptions().AbsorbNamespaces(FIXTURE_TOPIC).ExcludeEventType<NoisyEvent>();

    var plan = _plan(options, logger);

    await Assert.That(plan[FIXTURE_TOPIC]).IsEquivalentTo([_noisySubject]);
    await Assert.That(logger.Has(LogLevel.Warning,
      $"absorbed namespace {FIXTURE_TOPIC}: event type {TypeNameFormatter.Format(typeof(NoisyEvent))} is excluded at the broker and will not be delivered"))
      .IsTrue();
  }

  [Test]
  public async Task Plan_SeveralViolations_AreAllNamedInOneFailureAsync() {
    var consumers = new FixedConsumerRegistry(consumed: [
      new ConsumedMessageInfo(TypeNameFormatter.Format(typeof(NoisyEvent)), IsEvent: true),
      new ConsumedMessageInfo(TypeNameFormatter.Format(typeof(ChattyEvent)), IsEvent: true)]);
    var options = new RoutingOptions().ExcludeEventType<NoisyEvent>().ExcludeEventType<ChattyEvent>();

    var thrown = await Assert.That(() => _plan(options, new RecordingRoutingLogger(), consumers))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains("NoisyEvent");
    await Assert.That(thrown.Message).Contains("ChattyEvent");
  }

  [Test]
  public async Task Plan_ExcludedTypeUnknownToTheApplication_IsAWarningAndStillExcludedAsync() {
    var logger = new RecordingRoutingLogger();
    var options = new RoutingOptions().ExcludeEventTypes(
      "Whizbang.Core.Tests.Routing.ExcludedEventFixtures.RetiredEvent, OldContracts");

    var plan = _plan(options, logger);

    await Assert.That(logger.Has(LogLevel.Warning, "RetiredEvent")).IsTrue();
    await Assert.That(plan[FIXTURE_TOPIC])
      .IsEquivalentTo(["whizbang.core.tests.routing.excludedeventfixtures.retiredevent"])
      .Because("a type from a contract this build no longer references can still be published by an older peer");
  }

  [Test]
  public async Task Plan_NoCatalogAvailable_DoesNotWarnUnknownAsync() {
    var logger = new RecordingRoutingLogger();
    var options = new RoutingOptions().ExcludeEventType<NoisyEvent>();

    _ = _plan(options, logger, catalog: new FixedMessageCatalog(false));

    await Assert.That(logger.Entries.Exists(e => e.Level == LogLevel.Warning)).IsFalse();
  }

  [Test]
  public async Task Plan_NullCatalog_DoesNotWarnUnknownAsync() {
    var logger = new RecordingRoutingLogger();
    var options = new RoutingOptions().ExcludeEventType<NoisyEvent>();

    _ = ExcludedEventTypePlanner.Plan(options, new HashSet<string>([FIXTURE_TOPIC]), new FixedConsumerRegistry(), [],
      null, logger);

    await Assert.That(logger.Entries.Exists(e => e.Level == LogLevel.Warning)).IsFalse();
  }

  [Test]
  public async Task Plan_ExcludedTypeOnATopicThisServiceDoesNotSubscribeTo_IsAWarningAndExcludedNowhereAsync() {
    var logger = new RecordingRoutingLogger();
    var options = new RoutingOptions().ExcludeEventType<NoisyEvent>();

    var plan = _plan(options, logger, topics: [OTHER_TOPIC]);

    await Assert.That(plan[OTHER_TOPIC]).IsEmpty();
    await Assert.That(logger.Has(LogLevel.Warning, "does not subscribe")).IsTrue();
  }

  [Test]
  public async Task Plan_NameWithoutNamespace_IsAWarningAndExcludedNowhereAsync() {
    var logger = new RecordingRoutingLogger();
    var options = new RoutingOptions().ExcludeEventTypes("Unqualified");

    var plan = _plan(options, logger);

    await Assert.That(plan.Values.All(subjects => subjects.Count == 0)).IsTrue();
    await Assert.That(logger.Has(LogLevel.Warning, "does not subscribe")).IsTrue();
  }

  [Test]
  public async Task Plan_NullArguments_ThrowAsync() {
    var logger = new RecordingRoutingLogger();
    var topics = new HashSet<string>();
    var consumers = new FixedConsumerRegistry();

    await Assert.That(() => ExcludedEventTypePlanner.Plan(null!, topics, consumers, [], null, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(() => ExcludedEventTypePlanner.Plan(new RoutingOptions(), null!, consumers, [], null, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(() => ExcludedEventTypePlanner.Plan(new RoutingOptions(), topics, null!, [], null, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(() => ExcludedEventTypePlanner.Plan(new RoutingOptions(), topics, consumers, null!, null, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(() => ExcludedEventTypePlanner.Plan(new RoutingOptions(), topics, consumers, [], null, null!))
      .Throws<ArgumentNullException>();
  }
}
