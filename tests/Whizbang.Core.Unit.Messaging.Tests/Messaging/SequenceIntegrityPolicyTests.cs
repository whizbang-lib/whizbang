// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Which sequence integrity an outgoing event is held under: the dispatch's own setting, else the type's
/// <c>[SequenceIntegrity]</c>, else the configured default, else <see cref="SequenceIntegrity.Bounded"/> for one second.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/SequenceIntegrityPolicy.cs</code-under-test>
[Category("Unit")]
public class SequenceIntegrityPolicyTests {
  private sealed class PlainEvent;
  private sealed class StampedEvent;
  private sealed class RacingEvent;
  private sealed class PatientEvent;
  private sealed class BoundedWithNoTimeEvent;

  private sealed class Catalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(PlainEvent), TypeNameFormatter.FormatClrTypeName(typeof(PlainEvent)), "event", null),
      new(typeof(StampedEvent), TypeNameFormatter.FormatClrTypeName(typeof(StampedEvent)), "event", null) {
        SequenceIntegrity = SequenceIntegrity.UntilStamped
      },
      new(typeof(RacingEvent), TypeNameFormatter.FormatClrTypeName(typeof(RacingEvent)), "event", null) {
        SequenceIntegrity = SequenceIntegrity.None
      },
      new(typeof(PatientEvent), TypeNameFormatter.FormatClrTypeName(typeof(PatientEvent)), "event", null) {
        SequenceIntegrity = SequenceIntegrity.Bounded, SequenceHoldMilliseconds = 5000
      },
      new(typeof(BoundedWithNoTimeEvent), TypeNameFormatter.FormatClrTypeName(typeof(BoundedWithNoTimeEvent)), "event", null) {
        SequenceIntegrity = SequenceIntegrity.Bounded
      },
    ];
  }

  private static SequenceIntegrityPolicy _policy(WhizbangCoreOptions? options = null) =>
    new(options ?? new WhizbangCoreOptions(), new Catalog());

  [Test]
  public async Task OutOfTheBox_AnEventIsHeldBoundedForOneSecondAsync() {
    var options = new WhizbangCoreOptions();

    var hold = _policy(options).For(typeof(PlainEvent));

    await Assert.That(options.SequenceIntegrity).IsEqualTo(SequenceIntegrity.Bounded);
    await Assert.That(options.SequenceHoldTime).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(hold).IsEqualTo(new SequenceHold(SequenceIntegrity.Bounded, TimeSpan.FromSeconds(1)));
    await Assert.That(hold.HoldMilliseconds).IsEqualTo(1000);
  }

  [Test]
  public async Task TheConfiguredDefault_AppliesToATypeThatDeclaresNoneAsync() {
    var options = new WhizbangCoreOptions {
      SequenceIntegrity = SequenceIntegrity.Bounded,
      SequenceHoldTime = TimeSpan.FromMilliseconds(2500),
    };

    var hold = _policy(options).For(typeof(PlainEvent));

    await Assert.That(hold).IsEqualTo(new SequenceHold(SequenceIntegrity.Bounded, TimeSpan.FromMilliseconds(2500)));
    await Assert.That(_policy(new WhizbangCoreOptions { SequenceIntegrity = SequenceIntegrity.None }).For(typeof(PlainEvent)).Mode)
      .IsEqualTo(SequenceIntegrity.None);
  }

  [Test]
  public async Task TheTypesAttribute_BeatsTheConfiguredDefaultAsync() {
    var policy = _policy(new WhizbangCoreOptions { SequenceIntegrity = SequenceIntegrity.None });

    await Assert.That(policy.For(typeof(StampedEvent)).Mode).IsEqualTo(SequenceIntegrity.UntilStamped);
    await Assert.That(_policy().For(typeof(RacingEvent)).Mode).IsEqualTo(SequenceIntegrity.None)
      .Because("a type that races the stamp on purpose keeps doing so under a holding default");
  }

  [Test]
  public async Task TheTypesHoldTime_BeatsTheConfiguredHoldTime_AndAbsentOneTakesItAsync() {
    var policy = _policy(new WhizbangCoreOptions { SequenceHoldTime = TimeSpan.FromSeconds(2) });

    await Assert.That(policy.For(typeof(PatientEvent))).IsEqualTo(new SequenceHold(SequenceIntegrity.Bounded, TimeSpan.FromSeconds(5)));
    await Assert.That(policy.For(typeof(BoundedWithNoTimeEvent))).IsEqualTo(new SequenceHold(SequenceIntegrity.Bounded, TimeSpan.FromSeconds(2)));
  }

  [Test]
  public async Task TheDispatchsSetting_BeatsTheTypeAndTheDefaultAsync() {
    var policy = _policy();

    await Assert.That(policy.For(typeof(StampedEvent), SequenceIntegrity.None).Mode).IsEqualTo(SequenceIntegrity.None);
    await Assert.That(policy.For(typeof(RacingEvent), SequenceIntegrity.UntilStamped).Mode).IsEqualTo(SequenceIntegrity.UntilStamped);
    await Assert.That(policy.For(typeof(PlainEvent), SequenceIntegrity.None).Mode).IsEqualTo(SequenceIntegrity.None);
  }

  [Test]
  public async Task TheDispatchsHoldTime_BeatsTheTypes_AndAnAbsentOneFallsToTheTypeThenTheDefaultAsync() {
    var policy = _policy(new WhizbangCoreOptions { SequenceHoldTime = TimeSpan.FromSeconds(2) });

    await Assert.That(policy.For(typeof(PatientEvent), SequenceIntegrity.Bounded, TimeSpan.FromMilliseconds(300)).HoldTime)
      .IsEqualTo(TimeSpan.FromMilliseconds(300));
    await Assert.That(policy.For(typeof(PatientEvent), SequenceIntegrity.Bounded).HoldTime).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(policy.For(typeof(PlainEvent), SequenceIntegrity.Bounded).HoldTime).IsEqualTo(TimeSpan.FromSeconds(2));
    await Assert.That(policy.For(typeof(PlainEvent), null, TimeSpan.FromMilliseconds(700)))
      .IsEqualTo(new SequenceHold(SequenceIntegrity.Bounded, TimeSpan.FromMilliseconds(700)))
      .Because("a hold time on the dispatch alone still shortens the hold the type or the default chose");
  }

  [Test]
  public async Task HoldMilliseconds_IsTheStoredFormOfEachModeAsync() {
    await Assert.That(new SequenceHold(SequenceIntegrity.None, TimeSpan.FromSeconds(5)).HoldMilliseconds).IsEqualTo(0);
    await Assert.That(new SequenceHold(SequenceIntegrity.UntilStamped, TimeSpan.Zero).HoldMilliseconds)
      .IsEqualTo(SequenceHold.UNTIL_STAMPED_MILLISECONDS);
    await Assert.That(new SequenceHold(SequenceIntegrity.Bounded, TimeSpan.FromMilliseconds(1500)).HoldMilliseconds).IsEqualTo(1500);
    await Assert.That(new SequenceHold(SequenceIntegrity.Bounded, TimeSpan.FromTicks(1)).HoldMilliseconds).IsEqualTo(1)
      .Because("a bounded hold shorter than a millisecond is still a hold, rounded up");
    await Assert.That(new SequenceHold(SequenceIntegrity.Bounded, TimeSpan.Zero).HoldMilliseconds).IsEqualTo(0)
      .Because("a bounded hold of no time is no hold");
    await Assert.That(new SequenceHold(SequenceIntegrity.Bounded, TimeSpan.FromDays(100)).HoldMilliseconds).IsEqualTo(int.MaxValue)
      .Because("a hold too long for the stored form is capped, never wrapped negative into a hold until stamped");
  }

  [Test]
  public async Task Resolve_PrefersTheRegisteredInstance_AndBuildsOneFromTheContainerOtherwiseAsync() {
    var registered = _policy();
    var services = new ServiceCollection()
      .AddSingleton(registered)
      .BuildServiceProvider();
    var bare = new ServiceCollection()
      .AddSingleton(new WhizbangCoreOptions { SequenceIntegrity = SequenceIntegrity.UntilStamped })
      .AddSingleton<IMessageTypeCatalog>(new Catalog())
      .BuildServiceProvider();
    var empty = new ServiceCollection().BuildServiceProvider();

    await Assert.That(SequenceIntegrityPolicy.Resolve(services)).IsSameReferenceAs(registered);
    await Assert.That(SequenceIntegrityPolicy.Resolve(bare).For(typeof(PlainEvent)).Mode).IsEqualTo(SequenceIntegrity.UntilStamped);
    await Assert.That(SequenceIntegrityPolicy.Resolve(bare).For(typeof(RacingEvent)).Mode).IsEqualTo(SequenceIntegrity.None);
    await Assert.That(SequenceIntegrityPolicy.Resolve(empty).For(typeof(PlainEvent)))
      .IsEqualTo(new SequenceHold(SequenceIntegrity.Bounded, TimeSpan.FromSeconds(1)));
  }

  [Test]
  public async Task NullArguments_AreRefusedAsync() {
    await Assert.That(() => new SequenceIntegrityPolicy(null!, new Catalog())).ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => new SequenceIntegrityPolicy(new WhizbangCoreOptions(), null!)).ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => SequenceIntegrityPolicy.Resolve(null!)).ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => SequenceIntegrityPolicy.Create(null!)).ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => _policy().For(null!)).ThrowsExactly<ArgumentNullException>();
  }
}
