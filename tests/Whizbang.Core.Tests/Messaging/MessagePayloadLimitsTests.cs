using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The message payload limit: which limit applies, when it rejects, what the rejection carries, and how
/// hooks change the outcome.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/MessagePayloadLimits.cs</code-under-test>
public class MessagePayloadLimitsTests {
  private sealed class PlainMessage;
  private sealed class BigMessage;
  private sealed class SmallMessage;
  private sealed class UnlimitedMessage;

  private sealed class Catalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(PlainMessage), TypeNameFormatter.FormatClrTypeName(typeof(PlainMessage)), "event", null),
      new(typeof(BigMessage), TypeNameFormatter.FormatClrTypeName(typeof(BigMessage)), "event", null) { MaxPayloadBytes = 1000 },
      new(typeof(SmallMessage), TypeNameFormatter.FormatClrTypeName(typeof(SmallMessage)), "event", null) { MaxPayloadBytes = 10 },
      new(typeof(UnlimitedMessage), TypeNameFormatter.FormatClrTypeName(typeof(UnlimitedMessage)), "event", null) { MaxPayloadBytes = 0 },
    ];
  }

  private sealed class Hook(Func<MessagePayloadSizeContext, MessagePayloadSizeDecision> decide) : IMessagePayloadSizeHook {
    public List<MessagePayloadSizeContext> Seen { get; } = [];
    public MessagePayloadSizeDecision Evaluate(MessagePayloadSizeContext context) {
      Seen.Add(context);
      return decide(context);
    }
  }

  private static readonly Guid _messageId = Guid.CreateVersion7();
  private static readonly Guid _streamId = Guid.CreateVersion7();

  private static MessagePayloadLimits _limits(long? max = 100, FakeLogger<MessagePayloadLimits>? logger = null, params IMessagePayloadSizeHook[] hooks) =>
    new(new WhizbangCoreOptions { MaxMessagePayloadBytes = max }, new Catalog(), hooks, logger ?? new FakeLogger<MessagePayloadLimits>());

  private static JsonElement _payload(int bytes) {
    // {"v":"xxx"} is 8 bytes of framing around the string.
    using var doc = JsonDocument.Parse($"{{\"v\":\"{new string('x', bytes - 8)}\"}}");
    return doc.RootElement.Clone();
  }

  [Test]
  public async Task Default_IsFiveMebibytes_WithWarningAtEightyPercentAsync() {
    var options = new WhizbangCoreOptions();

    await Assert.That(options.MaxMessagePayloadBytes).IsEqualTo(5L * 1024 * 1024);
    await Assert.That(options.MessagePayloadWarningRatio).IsEqualTo(0.8);
  }

  [Test]
  public async Task Measure_IsTheSerializedUtf8LengthAsync() {
    var payload = _payload(50);
    using var doc = JsonDocument.Parse("{\"v\":\"é\"}");

    await Assert.That(MessagePayloadLimits.Measure(payload)).IsEqualTo(50);
    await Assert.That(MessagePayloadLimits.Measure(doc.RootElement)).IsEqualTo(Encoding.UTF8.GetByteCount("{\"v\":\"é\"}"))
      .Because("the limit is on stored and transmitted bytes, not characters");
  }

  [Test]
  public async Task Enforce_UnderTheWarningThreshold_PassesWithoutConsultingHooksAsync() {
    var hook = new Hook(_ => MessagePayloadSizeDecision.Reject("no"));
    var logger = new FakeLogger<MessagePayloadLimits>();

    _limits(100, logger, hook).Enforce(_payload(79), typeof(PlainMessage), _messageId, _streamId);

    await Assert.That(hook.Seen).IsEmpty();
    await Assert.That(logger.Collector.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Enforce_OverTheDefault_ThrowsWithEverythingTheCallerNeedsAsync() {
    var ex = Assert.Throws<MessagePayloadTooLargeException>(
      () => _limits(100).Enforce(_payload(101), typeof(PlainMessage), _messageId, _streamId));

    await Assert.That(ex.ErrorCode).IsEqualTo("WHIZ-PAYLOAD-TOO-LARGE");
    await Assert.That(ex.ErrorCode).IsEqualTo(MessagePayloadTooLargeException.DEFAULT_ERROR_CODE);
    await Assert.That(ex.MessageType).IsEqualTo(TypeNameFormatter.FormatClrTypeName(typeof(PlainMessage)));
    await Assert.That(ex.MessageId).IsEqualTo(_messageId);
    await Assert.That(ex.StreamId).IsEqualTo(_streamId);
    await Assert.That(ex.PayloadBytes).IsEqualTo(101);
    await Assert.That(ex.LimitBytes).IsEqualTo(100);
    await Assert.That(ex.LimitSource).IsEqualTo(PayloadLimitSource.Default);
    await Assert.That(ex.UserMessage).IsNull();
    await Assert.That(ex.Message).Contains("WHIZ-PAYLOAD-TOO-LARGE");
    await Assert.That(ex.Message).Contains("one message per item")
      .Because("the exception must say how to fix the producer, not just that it failed");
  }

  [Test]
  public async Task Enforce_ExactlyAtTheLimit_PassesAsync() {
    await Assert.That(() => _limits(100).Enforce(_payload(100), typeof(PlainMessage), _messageId, null)).ThrowsNothing();
  }

  [Test]
  public async Task Enforce_TypeLimitAboveTheDefault_AllowsTheLargerTypeAsync() {
    _limits(100).Enforce(_payload(500), typeof(BigMessage), _messageId, null);

    var ex = Assert.Throws<MessagePayloadTooLargeException>(
      () => _limits(100).Enforce(_payload(1001), typeof(BigMessage), _messageId, null));
    await Assert.That(ex.LimitBytes).IsEqualTo(1000);
    await Assert.That(ex.LimitSource).IsEqualTo(PayloadLimitSource.MessageType);
  }

  [Test]
  public async Task Enforce_TypeLimitBelowTheDefault_RejectsTheSmallerTypeAsync() {
    var ex = Assert.Throws<MessagePayloadTooLargeException>(
      () => _limits(100).Enforce(_payload(11), typeof(SmallMessage), _messageId, null));

    await Assert.That(ex.LimitBytes).IsEqualTo(10);
    await Assert.That(ex.StreamId).IsNull();
  }

  [Test]
  public async Task Enforce_CallLimit_OverridesTheTypeAndTheDefaultAsync() {
    _limits(100).Enforce(_payload(900), typeof(SmallMessage), _messageId, null, callLimit: 1000);

    var ex = Assert.Throws<MessagePayloadTooLargeException>(
      () => _limits(100).Enforce(_payload(51), typeof(BigMessage), _messageId, null, callLimit: 50));
    await Assert.That(ex.LimitBytes).IsEqualTo(50);
    await Assert.That(ex.LimitSource).IsEqualTo(PayloadLimitSource.Call);
  }

  [Test]
  [Arguments(null)]
  [Arguments(0L)]
  [Arguments(-1L)]
  public async Task Enforce_DefaultOff_AcceptsAnySizeAsync(long? max) {
    var hook = new Hook(_ => MessagePayloadSizeDecision.Reject("no"));

    _limits(max, null, hook).Enforce(_payload(10_000), typeof(PlainMessage), _messageId, null);

    await Assert.That(hook.Seen).IsEmpty();
  }

  [Test]
  public async Task Enforce_TypeLimitZero_TurnsTheLimitOffForThatTypeAsync() {
    await Assert.That(() => _limits(100).Enforce(_payload(10_000), typeof(UnlimitedMessage), _messageId, null)).ThrowsNothing();
  }

  [Test]
  public async Task Enforce_CallLimitZero_TurnsTheLimitOffForThatCallAsync() {
    await Assert.That(() => _limits(100).Enforce(_payload(10_000), typeof(SmallMessage), _messageId, null, callLimit: 0)).ThrowsNothing();
  }

  [Test]
  public async Task Enforce_TypeNotInCatalog_UsesTheDefaultAsync() {
    var ex = Assert.Throws<MessagePayloadTooLargeException>(
      () => _limits(100).Enforce(_payload(101), typeof(string), _messageId, null));

    await Assert.That(ex.LimitSource).IsEqualTo(PayloadLimitSource.Default);
    await Assert.That(ex.MessageType).IsEqualTo(TypeNameFormatter.FormatClrTypeName(typeof(string)));
  }

  [Test]
  public async Task Enforce_InTheWarningZone_PassesLogsAndTellsHooksItIsNotOverAsync() {
    var hook = new Hook(_ => MessagePayloadSizeDecision.NoOpinion);
    var logger = new FakeLogger<MessagePayloadLimits>();

    _limits(100, logger, hook).Enforce(_payload(80), typeof(PlainMessage), _messageId, _streamId);

    var seen = hook.Seen.Single();
    await Assert.That(seen.OverLimit).IsFalse();
    await Assert.That(seen.PayloadBytes).IsEqualTo(80);
    await Assert.That(seen.LimitBytes).IsEqualTo(100);
    await Assert.That(seen.MessageId).IsEqualTo(_messageId);
    await Assert.That(seen.StreamId).IsEqualTo(_streamId);
    var record = logger.Collector.LatestRecord;
    await Assert.That(record.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(record.Message).Contains("80");
  }

  [Test]
  public async Task Enforce_OverTheLimit_TellsHooksItIsOverAsync() {
    var hook = new Hook(_ => MessagePayloadSizeDecision.NoOpinion);

    Assert.Throws<MessagePayloadTooLargeException>(
      () => _limits(100, null, hook).Enforce(_payload(150), typeof(PlainMessage), _messageId, null));

    await Assert.That(hook.Seen.Single().OverLimit).IsTrue();
    await Assert.That(hook.Seen.Single().LimitSource).IsEqualTo(PayloadLimitSource.Default);
  }

  [Test]
  public async Task Enforce_HookAllows_AcceptsAMessageOverTheLimitAndLogsItAsync() {
    var logger = new FakeLogger<MessagePayloadLimits>();

    _limits(100, logger, new Hook(_ => MessagePayloadSizeDecision.Allow()))
      .Enforce(_payload(150), typeof(PlainMessage), _messageId, null);

    await Assert.That(logger.Collector.LatestRecord.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(logger.Collector.LatestRecord.Message).Contains("allowed");
  }

  [Test]
  public async Task Enforce_HookRejects_InTheWarningZone_ThrowsWithTheHooksMessageAndCodeAsync() {
    var ex = Assert.Throws<MessagePayloadTooLargeException>(
      () => _limits(100, null, new Hook(_ => MessagePayloadSizeDecision.Reject("Import fewer rows at once.", "APP-TOO-MANY-ROWS")))
        .Enforce(_payload(90), typeof(PlainMessage), _messageId, null));

    await Assert.That(ex.UserMessage).IsEqualTo("Import fewer rows at once.");
    await Assert.That(ex.ErrorCode).IsEqualTo("APP-TOO-MANY-ROWS");
    await Assert.That(ex.Message).Contains("Import fewer rows at once.");
  }

  [Test]
  public async Task Enforce_HookRejectsWithoutACode_KeepsTheFrameworkCodeAsync() {
    var ex = Assert.Throws<MessagePayloadTooLargeException>(
      () => _limits(100, null, new Hook(_ => MessagePayloadSizeDecision.Reject("Too big.")))
        .Enforce(_payload(150), typeof(PlainMessage), _messageId, null));

    await Assert.That(ex.ErrorCode).IsEqualTo(MessagePayloadTooLargeException.DEFAULT_ERROR_CODE);
    await Assert.That(ex.UserMessage).IsEqualTo("Too big.");
  }

  [Test]
  public async Task Enforce_ARejectionBeatsAnAllowance_WhateverTheOrderAsync() {
    var allow = new Hook(_ => MessagePayloadSizeDecision.Allow());
    var reject = new Hook(_ => MessagePayloadSizeDecision.Reject("no"));

    Assert.Throws<MessagePayloadTooLargeException>(
      () => _limits(100, null, allow, reject).Enforce(_payload(150), typeof(PlainMessage), _messageId, null));
    Assert.Throws<MessagePayloadTooLargeException>(
      () => _limits(100, null, reject, allow).Enforce(_payload(150), typeof(PlainMessage), _messageId, null));

    await Assert.That(allow.Seen.Count).IsEqualTo(2).Because("every hook sees the message, so each can record it");
  }

  [Test]
  public async Task Evaluate_ByWireName_ResolvesTheTypeLimitWithoutThrowingAsync() {
    var limits = _limits(100);
    var name = TypeNameFormatter.FormatClrTypeName(typeof(BigMessage));

    await Assert.That(limits.Evaluate(500, name, _messageId, null)).IsNull();
    var rejection = limits.Evaluate(1001, name, _messageId, _streamId);
    await Assert.That(rejection).IsNotNull();
    await Assert.That(rejection!.LimitSource).IsEqualTo(PayloadLimitSource.MessageType);
    await Assert.That(rejection.MessageType).IsEqualTo(name);
    await Assert.That(limits.Evaluate(101, "Unknown.Type, Unknown", _messageId, null)!.LimitSource)
      .IsEqualTo(PayloadLimitSource.Default);
  }

  [Test]
  public async Task Decision_Default_IsNoOpinionAsync() {
    var none = MessagePayloadSizeDecision.NoOpinion;

    await Assert.That(none.Verdict).IsNull();
    await Assert.That(none.UserMessage).IsNull();
    await Assert.That(none.ErrorCode).IsNull();
    await Assert.That(MessagePayloadSizeDecision.Allow().Verdict).IsTrue();
    await Assert.That(MessagePayloadSizeDecision.Reject("m").Verdict).IsFalse();
  }

  [Test]
  public async Task Exception_WithoutContext_ThrowsArgumentNullAsync() {
    await Assert.That(() => _ = new MessagePayloadTooLargeException(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullArguments_ThrowAsync() {
    var options = new WhizbangCoreOptions();
    var logger = new FakeLogger<MessagePayloadLimits>();
    await Assert.That(() => _ = new MessagePayloadLimits(null!, new Catalog(), [], logger)).Throws<ArgumentNullException>();
    await Assert.That(() => _ = new MessagePayloadLimits(options, null!, [], logger)).Throws<ArgumentNullException>();
    await Assert.That(() => _ = new MessagePayloadLimits(options, new Catalog(), null!, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => _ = new MessagePayloadLimits(options, new Catalog(), [], null!)).Throws<ArgumentNullException>();
  }

  public sealed class RejectEverythingNearTheLimit : IMessagePayloadSizeHook {
    public MessagePayloadSizeDecision Evaluate(MessagePayloadSizeContext context) => MessagePayloadSizeDecision.Reject("registered");
  }

  [Test]
  public async Task Registration_Defaults_ResolveOneInstanceThatSeesRegisteredHooksAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(new WhizbangCoreOptions { MaxMessagePayloadBytes = 100 });
    services.TryAddWhizbangDefaults();
    services.AddMessagePayloadSizeHook<RejectEverythingNearTheLimit>();
    services.AddMessagePayloadSizeHook<RejectEverythingNearTheLimit>();
    var provider = services.BuildServiceProvider();

    var limits = MessagePayloadLimits.Resolve(provider);

    await Assert.That(limits).IsSameReferenceAs(provider.GetRequiredService<MessagePayloadLimits>());
    await Assert.That(provider.GetServices<IMessagePayloadSizeHook>().Count()).IsEqualTo(1);
    var ex = Assert.Throws<MessagePayloadTooLargeException>(() => limits.Enforce(_payload(90), typeof(PlainMessage), _messageId, null));
    await Assert.That(ex.UserMessage).IsEqualTo("registered");
  }

  [Test]
  public async Task Resolve_WithoutRegistration_BuildsFromTheFrameworkDefaultsAsync() {
    var limits = MessagePayloadLimits.Resolve(new ServiceCollection().BuildServiceProvider());

    await Assert.That(() => limits.Enforce(_payload(4 * 1024 * 1024), typeof(PlainMessage), _messageId, null)).ThrowsNothing();
    await Assert.That(() => limits.Enforce(_payload((5 * 1024 * 1024) + 1), typeof(PlainMessage), _messageId, null))
      .Throws<MessagePayloadTooLargeException>()
      .Because("a host that never registered the limit still gets the 5 MiB default");
  }

  [Test]
  public async Task Resolve_AndCreate_NullProvider_ThrowAsync() {
    await Assert.That(() => MessagePayloadLimits.Resolve(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => MessagePayloadLimits.Create(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => MessagePayloadSizeServiceCollectionExtensions.AddMessagePayloadSizeHook<RejectEverythingNearTheLimit>(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Enforce_NullType_ThrowsAsync() {
    await Assert.That(() => _limits().Enforce(_payload(10), null!, _messageId, null)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task MaxPayloadSizeAttribute_CarriesItsLimitAsync() {
    // The generator reads the constructor argument at compile time; the property is what a reader of
    // the attribute at run time sees, so both must agree.
    var attribute = new Whizbang.Core.Attributes.MaxPayloadSizeAttribute(20_000_000);

    await Assert.That(attribute.Bytes).IsEqualTo(20_000_000L);
  }

  /// <summary>A provider that knows no services at all, as a hand-built one in a host or test may be.</summary>
  private sealed class EmptyProvider : IServiceProvider {
    public object? GetService(Type serviceType) => null;
  }

  [Test]
  public async Task Resolve_FromAProviderThatKnowsNothing_UsesTheDefaultsAsync() {
    // Asking such a provider for "every hook" answers null rather than an empty list; the dispatcher
    // resolves the limits at construction, so a throw here broke every dispatcher built that way.
    var limits = MessagePayloadLimits.Resolve(new EmptyProvider());

    await Assert.That(() => limits.Enforce(_payload((5 * 1024 * 1024) + 1), typeof(PlainMessage), _messageId, null))
      .Throws<MessagePayloadTooLargeException>();
  }
}
