// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Validation;

/// <summary>
/// <see cref="SuppressGuidOrderingValidationAttribute"/> opts one message type out of the event and message id
/// time-ordering check (#2528): a non-v7 id of that type is accepted at <see cref="GuidOrderingSeverity.Error"/>, logged
/// and counted as <see cref="GuidOrderingSeverity.Warning"/> would, never silently; the empty id is refused all the same,
/// and a type without the attribute is still refused. The attribute is read by the generator, never by reflection: the
/// generated module initializer registers each attributed type's name, and both the name and the type resolve.
/// </summary>
[Category("Validation")]
[Category("GuidOrdering")]
public class GuidOrderingSuppressionTests {
  private const string ENTRY_POINT = "Test.EntryPoint";
  private static readonly Guid _v4 = NonTimeOrderedGuidShapes.V4;

  private static (GuidOrderingValidator Validator, CollectingLogger Logger, System.Diagnostics.Metrics.Meter Meter, TestMeterFactory Factory)
      _validator(GuidOrderingSeverity severity) {
    var factory = new TestMeterFactory();
    var metrics = new GuidOrderingMetrics(new WhizbangMetrics(factory));
    var logger = new CollectingLogger();
    var options = new WhizbangOptions { GuidOrderingViolationSeverity = severity };
    return (new GuidOrderingValidator(options, logger, metrics), logger, factory.CreatedMeters[0], factory);
  }

  // ---- The generated registration -------------------------------------------------------------------------------

  [Test]
  public async Task AnAttributedType_IsRegisteredByTheGeneratedModuleInitializerAsync() {
    await Assert.That(GuidOrderingSuppressions.IsSuppressed(typeof(SuppressedOrderingProbe))).IsTrue()
      .Because("the generator registers every type that carries the attribute, so nothing calls Register by hand");
    await Assert.That(GuidOrderingSuppressions.IsSuppressed(TypeNameFormatter.Format(typeof(SuppressedOrderingProbe)))).IsTrue()
      .Because("the outbox and inbox writes know the type only by its stored name");
  }

  [Test]
  public async Task AnUnattributedType_IsNotSuppressedAsync() {
    await Assert.That(GuidOrderingSuppressions.IsSuppressed(typeof(UnsuppressedOrderingProbe))).IsFalse();
    await Assert.That(GuidOrderingSuppressions.IsSuppressed(TypeNameFormatter.Format(typeof(UnsuppressedOrderingProbe)))).IsFalse();
  }

  [Test]
  public async Task ADerivedTypeOfAnAttributedType_IsNotSuppressedAsync() {
    await Assert.That(GuidOrderingSuppressions.IsSuppressed(typeof(DerivedFromSuppressedOrderingProbe))).IsFalse()
      .Because("the attribute is not inherited: each type opts out on its own");
  }

  [Test]
  public async Task APrivateNestedAttributedType_IsRegisteredByNameAsync() {
    await Assert.That(GuidOrderingSuppressions.IsSuppressed(typeof(HiddenSuppressedProbe))).IsTrue()
      .Because("registration is by name, so a type generated code cannot name is still honored");
  }

  [Test]
  public async Task AnAttributedRecordStruct_IsRegisteredAsync() {
    await Assert.That(GuidOrderingSuppressions.IsSuppressed(typeof(SuppressedOrderingStructProbe))).IsTrue();
  }

  [Test]
  public async Task AnAttributedGenericType_IsSuppressedForEveryConstructionAsync() {
    await Assert.That(GuidOrderingSuppressions.IsSuppressed(typeof(SuppressedGenericProbe<int>))).IsTrue();
    await Assert.That(GuidOrderingSuppressions.IsSuppressed(typeof(SuppressedGenericProbe<string>))).IsTrue();
    await Assert.That(GuidOrderingSuppressions.IsSuppressed(TypeNameFormatter.Format(typeof(SuppressedGenericProbe<Guid>)))).IsTrue()
      .Because("a closed generic's stored name carries its type arguments, which the lookup strips");
  }

  // ---- The registry ----------------------------------------------------------------------------------------------

  [Test]
  public async Task IsSuppressed_AVersionedAssemblyQualifiedName_IsNormalizedFirstAsync() {
    GuidOrderingSuppressions.Register("Probe.Registry.Versioned, Probe.Assembly");

    await Assert.That(GuidOrderingSuppressions.IsSuppressed(
      "Probe.Registry.Versioned, Probe.Assembly, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null")).IsTrue();
  }

  [Test]
  public async Task Register_AVersionedName_IsStoredNormalizedAsync() {
    GuidOrderingSuppressions.Register("Probe.Registry.RegisteredVersioned, Probe.Assembly, Version=9.9.9.9, Culture=neutral, PublicKeyToken=null");

    await Assert.That(GuidOrderingSuppressions.IsSuppressed("Probe.Registry.RegisteredVersioned, Probe.Assembly")).IsTrue();
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("Probe.Registry.NeverRegistered, Probe.Assembly")]
  [Arguments("Probe.Registry.NotGeneric`1, Probe.Assembly")]
  [Arguments("Probe.Registry.Unbalanced`1[[System.Int32, System.Private.CoreLib], Probe.Assembly")]
  public async Task IsSuppressed_ANameNotRegistered_IsFalseAsync(string? name) {
    GuidOrderingSuppressions.Register("Probe.Registry.Unbalanced`1, Probe.Assembly");

    await Assert.That(GuidOrderingSuppressions.IsSuppressed(name)).IsFalse();
  }

  [Test]
  public async Task IsSuppressed_AClosedGenericName_MatchesItsRegisteredDefinitionAsync() {
    GuidOrderingSuppressions.Register("Probe.Registry.Wrapper`2, Probe.Assembly");

    await Assert.That(GuidOrderingSuppressions.IsSuppressed(
      "Probe.Registry.Wrapper`2[[System.Int32, System.Private.CoreLib],[Probe.Registry.Inner`1[[System.String, System.Private.CoreLib]], Probe.Assembly]], Probe.Assembly"))
      .IsTrue();
  }

  [Test]
  public async Task IsSuppressed_ANullType_IsFalseAsync() {
    await Assert.That(GuidOrderingSuppressions.IsSuppressed((Type?)null)).IsFalse();
  }

  [Test]
  public async Task IsSuppressed_ATypeAnsweredBeforeItsRegistration_IsAnsweredAgainAfterItAsync() {
    // An assembly loaded later registers its types later: a cached "no" must not outlive the registration
    await Assert.That(GuidOrderingSuppressions.IsSuppressed(typeof(LateRegisteredProbe))).IsFalse();

    GuidOrderingSuppressions.Register(TypeNameFormatter.Format(typeof(LateRegisteredProbe)));

    await Assert.That(GuidOrderingSuppressions.IsSuppressed(typeof(LateRegisteredProbe))).IsTrue();
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("  ")]
  public async Task Register_ABlankName_ThrowsAsync(string? name) {
    await Assert.That(() => GuidOrderingSuppressions.Register(name!)).Throws<ArgumentException>();
  }

  // ---- The validator ---------------------------------------------------------------------------------------------

  [Test]
  public async Task ValidateMessageId_ASuppressedTypeAtError_IsAcceptedLoggedAsAWarningAndCountedOnceAsync() {
    var (validator, logger, meter, factory) = _validator(GuidOrderingSeverity.Error);
    using (factory) {
      validator.ValidateMessageId(_v4, typeof(SuppressedOrderingProbe), ENTRY_POINT);
      validator.ValidateMessageId(_v4, typeof(SuppressedOrderingProbe), ENTRY_POINT);

      await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(1)
        .Because("an opted-out id is logged once, as a warning, so it stays visible");
      await Assert.That(logger.Entries[0].Message).Contains("[SuppressGuidOrderingValidation]")
        .And.Contains(_v4.ToString()).And.Contains(nameof(SuppressedOrderingProbe)).And.Contains(ENTRY_POINT);
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_MESSAGE_IDS)).IsEqualTo(1);
    }
  }

  [Test]
  public async Task ValidateMessageId_ASuppressedTypeByNameAtError_IsAcceptedAndLoggedAsync() {
    var (validator, logger, meter, factory) = _validator(GuidOrderingSeverity.Error);
    using (factory) {
      validator.ValidateMessageId(_v4, TypeNameFormatter.Format(typeof(SuppressedOrderingProbe)), ENTRY_POINT);

      await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(1);
      await Assert.That(logger.Entries[0].Message).Contains("[SuppressGuidOrderingValidation]");
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_MESSAGE_IDS)).IsEqualTo(1);
    }
  }

  [Test]
  public async Task ValidateMessageId_ASuppressedTypeAtInfo_StaysAtInformationAsync() {
    var (validator, logger, meter, factory) = _validator(GuidOrderingSeverity.Info);
    using (factory) {
      validator.ValidateMessageId(_v4, typeof(SuppressedOrderingProbe), ENTRY_POINT);

      await Assert.That(logger.Entries.Single().Level).IsEqualTo(LogLevel.Information)
        .Because("the attribute lowers Error to Warning; it never raises a lower severity");
      await Assert.That(logger.Entries[0].Message).Contains("[SuppressGuidOrderingValidation]");
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_MESSAGE_IDS)).IsEqualTo(1);
    }
  }

  [Test]
  public async Task ValidateMessageId_ASuppressedTypeAtNone_IsSilentAsync() {
    var (validator, logger, meter, factory) = _validator(GuidOrderingSeverity.None);
    using (factory) {
      validator.ValidateMessageId(_v4, typeof(SuppressedOrderingProbe), ENTRY_POINT);

      await Assert.That(logger.Entries).IsEmpty();
      await Assert.That(ProbeMeterReader.ReadTotal(meter, GuidOrderingMetrics.NON_V7_MESSAGE_IDS)).IsEqualTo(0);
    }
  }

  [Test]
  [Arguments(GuidOrderingSeverity.Error)]
  [Arguments(GuidOrderingSeverity.Warning)]
  [Arguments(GuidOrderingSeverity.Info)]
  [Arguments(GuidOrderingSeverity.None)]
  public async Task ValidateMessageId_TheEmptyIdOfASuppressedType_IsRefusedAtEverySeverityAsync(GuidOrderingSeverity severity) {
    var (validator, _, _, factory) = _validator(severity);
    using (factory) {
      await Assert.That(() => validator.ValidateMessageId(Guid.Empty, typeof(SuppressedOrderingProbe), ENTRY_POINT))
        .Throws<GuidOrderingException>();
      await Assert.That(() => validator.ValidateMessageId(Guid.Empty, TypeNameFormatter.Format(typeof(SuppressedOrderingProbe)), ENTRY_POINT))
        .Throws<GuidOrderingException>();
    }
  }

  [Test]
  public async Task ValidateMessageId_AnUnattributedTypeAtError_IsStillRefusedAsync() {
    var (validator, _, _, factory) = _validator(GuidOrderingSeverity.Error);
    using (factory) {
      var byType = await Assert.That(() => validator.ValidateMessageId(_v4, typeof(UnsuppressedOrderingProbe), ENTRY_POINT))
        .Throws<GuidOrderingException>();
      await Assert.That(byType!.MessageType).IsEqualTo(nameof(UnsuppressedOrderingProbe))
        .Because("the type overload names the type as the string overload's callers always have");
      await Assert.That(() => validator.ValidateMessageId(_v4, TypeNameFormatter.Format(typeof(UnsuppressedOrderingProbe)), ENTRY_POINT))
        .Throws<GuidOrderingException>();
    }
  }

  [Test]
  public async Task ValidateMessageId_AV7OfAnyType_IsAcceptedSilentlyAsync() {
    var (validator, logger, _, factory) = _validator(GuidOrderingSeverity.Error);
    using (factory) {
      validator.ValidateMessageId(TrackedGuid.New(), typeof(SuppressedOrderingProbe), ENTRY_POINT);
      validator.ValidateMessageId(TrackedGuid.New(), typeof(UnsuppressedOrderingProbe), ENTRY_POINT);

      await Assert.That(logger.Entries).IsEmpty();
    }
  }

  [Test]
  public async Task ValidateMessageId_ANullType_ThrowsAsync() {
    await Assert.That(() => GuidOrderingValidator.Default.ValidateMessageId(_v4, (Type)null!, ENTRY_POINT))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ValidateAppend_ASuppressedPayload_IsAcceptedAsync() {
    var (validator, logger, _, factory) = _validator(GuidOrderingSeverity.Error);
    using (factory) {
      validator.ValidateAppend(TrackedGuid.New(), _envelope(_v4, new SuppressedOrderingProbe(1)), ENTRY_POINT);

      await Assert.That(logger.Entries.Single().Message).Contains("[SuppressGuidOrderingValidation]");
    }
  }

  [Test]
  public async Task InMemoryAppend_ASuppressedPayloadWithAV4_IsStoredAsync() {
    var store = new InMemoryEventStore();
    var stream = (Guid)TrackedGuid.New();

    await store.AppendAsync(stream, _envelope(_v4, new SuppressedOrderingProbe(1)));

    await Assert.That(await store.GetLastSequenceAsync(stream)).IsGreaterThanOrEqualTo(0L);
  }

  [Test]
  public async Task InMemoryAppend_AnUnattributedPayloadWithAV4_IsRefusedAsync() {
    var store = new InMemoryEventStore();
    var stream = (Guid)TrackedGuid.New();

    await Assert.That(async () => await store.AppendAsync(stream, _envelope(_v4, new UnsuppressedOrderingProbe(1))))
      .Throws<GuidOrderingException>();
    await Assert.That(await store.GetLastSequenceAsync(stream)).IsEqualTo(-1L);
  }

  [Test]
  public async Task InMemoryAppend_ASuppressedPayloadWithTheEmptyId_IsRefusedAsync() {
    var store = new InMemoryEventStore();
    var stream = (Guid)TrackedGuid.New();

    await Assert.That(async () => await store.AppendAsync(stream, _envelope(Guid.Empty, new SuppressedOrderingProbe(1))))
      .Throws<GuidOrderingException>();
    await Assert.That(await store.GetLastSequenceAsync(stream)).IsEqualTo(-1L);
  }

  private static MessageEnvelope<T> _envelope<T>(Guid id, T payload) => new() {
    MessageId = new MessageId(id),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = Dispatch.DispatchModes.Local, Source = MessageSource.Local },
  };

  [SuppressGuidOrderingValidation]
  private sealed record HiddenSuppressedProbe(int Value) : IEvent;

  private sealed record LateRegisteredProbe(int Value) : IEvent;

  private sealed class CollectingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
  }
}

/// <summary>A message type that opts out of the time-ordering check (#2528).</summary>
[SuppressGuidOrderingValidation]
public record SuppressedOrderingProbe(int Value) : IEvent;

/// <summary>Derives from an opted-out type without opting out itself.</summary>
public sealed record DerivedFromSuppressedOrderingProbe(int Value) : SuppressedOrderingProbe(Value);

/// <summary>A message type that does not opt out.</summary>
public sealed record UnsuppressedOrderingProbe(int Value) : IEvent;

/// <summary>An opted-out record struct.</summary>
[SuppressGuidOrderingValidation]
public readonly record struct SuppressedOrderingStructProbe(int Value);

/// <summary>An opted-out generic type: every construction of it is opted out.</summary>
[SuppressGuidOrderingValidation]
public sealed record SuppressedGenericProbe<T>(T Value);
