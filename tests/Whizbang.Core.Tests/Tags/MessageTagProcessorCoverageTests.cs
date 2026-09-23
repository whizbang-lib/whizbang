using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tags;
using Whizbang.Core.Tests.Helpers;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Coverage-round-23 targets for <see cref="MessageTagProcessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two of the diagnostics here were once recorded as unreachable, and the reasoning still holds
/// for the public constructors: <c>Logger</c> resolves to a no-op whenever <c>_scopeFactory</c> is
/// null, and both the "neither resolver nor scope factory" branch and the direct-hook-resolver
/// branch require exactly that. They are asserted through the internal constructor that takes the
/// logger directly, because those two lines are the only explanation an operator gets for tag
/// hooks that silently never ran.
/// </para>
/// <para>
/// One target is still unreachable: <c>_enforcePayloadSize</c> has exactly two <c>return</c>
/// statements and both return <c>true</c>; the only other exit is a <c>throw</c> on the
/// error-threshold path. It can never return <c>false</c>, so the <c>continue;</c> guarded by
/// <c>!_enforcePayloadSize(...)</c> is dead under the current method body.
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/message-tags#processing</docs>
public class MessageTagProcessorCoverageTests {

  // Tag processing decides how a message is routed and grouped. If the base-context fallback in
  // _createHookContextForAttribute regressed — e.g. by returning null or throwing instead of a
  // usable TagContext<MessageTagAttribute> — a message carrying a custom tag attribute with no
  // generated dispatcher would blow up (or silently drop) tag processing for the ENTIRE message,
  // taking down every other tag on it too, instead of just leaving that one custom hook
  // un-invoked.
  [Test]
  [NotInParallel("TagRegistry")]
  public async Task ProcessTagsAsync_CustomAttributeWithNoDispatcher_FallsBackAndOtherTagsStillProcessAsync() {
    _cleanupRegistry();
    _cleanupDispatcherRegistry();

    MessageTagRegistry.Register(new FallbackAndSignalRegistry(), priority: 100);

    var fallbackHook = new FallbackOnlyTrackingHook();
    var signalHook = new SignalTrackingHook();
    var options = new TagOptions();
    options.UseHook<FallbackOnlyTagAttribute, FallbackOnlyTrackingHook>();
    options.UseHook<SignalTagAttribute, SignalTrackingHook>();

    object? hookResolver(Type type) {
      if (type == typeof(FallbackOnlyTrackingHook)) {
        return fallbackHook;
      }
      if (type == typeof(SignalTrackingHook)) {
        return signalHook;
      }
      return null;
    }

    var processor = new MessageTagProcessor(options, hookResolver);

    var message = new FallbackTaggedMessage("value");

    await processor.ProcessTagsAsync(
      message, typeof(FallbackTaggedMessage), LifecycleStage.AfterReceptorCompletion);

    await Assert.That(fallbackHook.InvokedCount).IsEqualTo(0)
      .Because("a custom attribute with no generated dispatcher gets a base context built for "
        + "it, but nothing can dispatch to its typed hook — that gap is exactly what the "
        + "fallback exists to survive without throwing");
    await Assert.That(signalHook.InvokedCount).IsEqualTo(1)
      .Because("the loop must keep processing later tag registrations after the fallback tag; "
        + "a signal tag on the same message is the control proving the pipeline wasn't aborted");
  }

  private static void _cleanupRegistry() {
    Whizbang.Core.Registry.AssemblyRegistry<IMessageTagRegistry>.ClearForTesting();
  }

  private static void _cleanupDispatcherRegistry() {
    Whizbang.Core.Registry.AssemblyRegistry<IMessageTagHookDispatcher>.ClearForTesting();
  }

  // A custom attribute type that is neither a built-in (Signal/Telemetry/Metric) nor registered
  // with MessageTagHookDispatcherRegistry — the exact shape that forces
  // _createHookContextForAttribute past both fast paths into the base-context fallback.
  [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = true)]
  private sealed class FallbackOnlyTagAttribute : MessageTagAttribute;

  private sealed record FallbackTaggedMessage(string Value);

  private sealed class FallbackOnlyTrackingHook : IMessageTagHook<FallbackOnlyTagAttribute> {
    public int InvokedCount { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<FallbackOnlyTagAttribute> context, CancellationToken ct) {
      InvokedCount++;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class SignalTrackingHook : IMessageTagHook<SignalTagAttribute> {
    public int InvokedCount { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> context, CancellationToken ct) {
      InvokedCount++;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class FallbackAndSignalRegistry : IMessageTagRegistry {
    public IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType) {
      if (messageType == typeof(FallbackTaggedMessage)) {
        yield return new MessageTagRegistration {
          MessageType = typeof(FallbackTaggedMessage),
          AttributeType = typeof(FallbackOnlyTagAttribute),
          Tag = "fallback-only-tag",
          PayloadBuilder = _ => JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
          AttributeFactory = () => new FallbackOnlyTagAttribute { Tag = "fallback-only-tag" }
        };
        yield return new MessageTagRegistration {
          MessageType = typeof(FallbackTaggedMessage),
          AttributeType = typeof(SignalTagAttribute),
          Tag = "signal-tag",
          PayloadBuilder = _ => JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
          AttributeFactory = () => new SignalTagAttribute { Tag = "signal-tag" }
        };
      }
    }
  }

  // Tag hooks that never run are invisible: nothing fails, the message just goes through without
  // its routing or telemetry side effects. These two Debug lines are the whole diagnosis, and
  // they say which of the two reasons applies — no resolver was wired at all, versus a resolver
  // was wired and used. Getting them the wrong way round sends an operator looking in the wrong
  // place.

  [Test]
  public async Task ProcessTagsAsync_NoResolverAndNoScopeFactory_SaysSoAndProcessesNothingAsync() {
    var logger = new CapturingLogger<MessageTagProcessor>();
    var processor = new MessageTagProcessor(new TagOptions(), logger);

    await processor.ProcessTagsAsync(
      new FallbackTaggedMessage("value"),
      typeof(FallbackTaggedMessage),
      LifecycleStage.AfterReceptorCompletion);

    var messages = logger.Snapshot().Select(e => e.Message).ToList();
    await Assert.That(messages.Any(m => m.Contains("No hook resolver or scope factory", StringComparison.Ordinal))).IsTrue()
      .Because("a processor with nothing to resolve hooks through runs no hook at all, and this "
        + "line is the only thing that tells an operator why");
    await Assert.That(messages.Any(m => m.Contains("tag registrations", StringComparison.Ordinal))).IsFalse()
      .Because("the early return happens before the registry is consulted; looking up tags for a "
        + "processor that could not invoke them is work with no possible effect");
  }

  [Test]
  [NotInParallel("TagRegistry")]
  public async Task ProcessTagsAsync_DirectHookResolver_SaysSoAndInvokesTheHookAsync() {
    _cleanupRegistry();
    _cleanupDispatcherRegistry();
    try {
      MessageTagRegistry.Register(new FallbackAndSignalRegistry(), priority: 100);

      var signalHook = new SignalTrackingHook();
      var options = new TagOptions();
      options.UseHook<SignalTagAttribute, SignalTrackingHook>();
      var logger = new CapturingLogger<MessageTagProcessor>();
      var processor = new MessageTagProcessor(
        options,
        logger,
        hookResolver: type => type == typeof(SignalTrackingHook) ? signalHook : null);

      await processor.ProcessTagsAsync(
        new FallbackTaggedMessage("value"),
        typeof(FallbackTaggedMessage),
        LifecycleStage.AfterReceptorCompletion);

      var messages = logger.Snapshot().Select(e => e.Message).ToList();
      await Assert.That(messages.Any(m => m.Contains("Using direct hook resolver", StringComparison.Ordinal))).IsTrue()
        .Because("the resolver a processor used decides which scope the hooks saw; saying 'scope "
          + "factory' here would send an operator hunting a scope that was never created");
      await Assert.That(messages.Any(m => m.Contains("Using scope factory", StringComparison.Ordinal))).IsFalse()
        .Because("the two branches are mutually exclusive and the diagnosis has to name the right one");
      await Assert.That(signalHook.InvokedCount).IsEqualTo(1)
        .Because("the branch really did dispatch through the resolver it announced");
    } finally {
      _cleanupRegistry();
      _cleanupDispatcherRegistry();
    }
  }
}
