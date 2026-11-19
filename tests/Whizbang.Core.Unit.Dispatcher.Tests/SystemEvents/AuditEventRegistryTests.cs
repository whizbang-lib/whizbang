// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;
using Whizbang.Core.Audit;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// #1580: the audit settings each event type declares, registered at module load by the generator, so audit decisions
/// read them without reflection.
/// </summary>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditEventRegistry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditEligibility.cs</code-under-test>
public class AuditEventRegistryTests {

  /// <summary>Declared public so the generator in this test assembly registers it.</summary>
  [AuditEvent(Reason = "registered by the generator", Level = AuditLevel.Critical)]
  public sealed record GeneratedAuditProbe([property: StreamId] Guid Id) : IEvent;

  /// <summary>Inherits its base's [AuditEvent], as the attribute declares Inherited = true.</summary>
  public sealed record InheritedAuditProbe(Guid Id) : AuditedProbeBase(Id);

  /// <summary>The base that carries the attribute.</summary>
  [AuditEvent(Exclude = true)]
  public abstract record AuditedProbeBase([property: StreamId] Guid Id) : IEvent;

  /// <summary>A type with no audit attribute.</summary>
  public sealed record UnattributedProbe([property: StreamId] Guid Id) : IEvent;

  [Test]
  public async Task AttributeOf_ATypeTheGeneratorRegistered_ReturnsItsDeclaredValuesAsync() {
    var attribute = AuditEligibility.AttributeOf(typeof(GeneratedAuditProbe));

    await Assert.That(attribute).IsNotNull()
      .Because("the generator registers every event type that declares [AuditEvent], so no reflection is needed");
    await Assert.That(attribute!.Reason).IsEqualTo("registered by the generator");
    await Assert.That(attribute.Level).IsEqualTo(AuditLevel.Critical);
    await Assert.That(attribute.Exclude).IsFalse();
  }

  [Test]
  public async Task AttributeOf_ATypeInheritingTheAttribute_ReturnsTheBasesValuesAsync() {
    var attribute = AuditEligibility.AttributeOf(typeof(InheritedAuditProbe));

    await Assert.That(attribute?.Exclude).IsTrue()
      .Because("[AuditEvent] is Inherited = true, so a derived event carries its base's exclusion");
  }

  [Test]
  public async Task AttributeOf_AnUnattributedType_IsNullAsync() {
    await Assert.That(AuditEligibility.AttributeOf(typeof(UnattributedProbe))).IsNull();
  }

  [Test]
  public async Task Register_ThenFind_ReturnsTheRegisteredAttribute_AndRejectsNullsAsync() {
    var attribute = new AuditEventAttribute { Reason = "explicit" };

    AuditEventRegistry.Register(typeof(ExplicitlyRegistered), attribute);

    await Assert.That(AuditEventRegistry.Find(typeof(ExplicitlyRegistered))).IsSameReferenceAs(attribute);
    await Assert.That(() => AuditEventRegistry.Register(null!, attribute)).Throws<ArgumentNullException>();
    await Assert.That(() => AuditEventRegistry.Register(typeof(ExplicitlyRegistered), null!)).Throws<ArgumentNullException>();
  }

  private sealed record ExplicitlyRegistered : IEvent;
}
