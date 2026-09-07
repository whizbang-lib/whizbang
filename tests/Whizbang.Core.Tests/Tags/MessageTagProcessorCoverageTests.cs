using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Coverage-round-23 targets for <see cref="MessageTagProcessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only the base-context fallback in <c>_createHookContextForAttribute</c> (source lines
/// 326-333) is exercised here. The other five target lines for this class — 86, 87 (the
/// Debug-log inside the "neither resolver nor scope factory" early return) and 113, 114 (the
/// Debug-log in the direct-hookResolver branch), plus 137 (the <c>continue;</c> when
/// <c>_enforcePayloadSize</c> returns <c>false</c>) — are unreachable given the current
/// implementation, not merely untested:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>Logger</c> resolves to <c>NullLogger.Instance</c> whenever <c>_scopeFactory</c> is
/// <c>null</c> (see the <c>Logger</c> property's <c>??</c> fallback). Reaching the "neither
/// resolver nor scope factory" branch (line 84) and the direct-hookResolver "else" branch (line
/// 111) both REQUIRE <c>_scopeFactory is null</c> — which pins <c>Logger</c> to
/// <c>NullLogger</c>, whose <c>IsEnabled</c> always returns <c>false</c>. So the
/// <c>Logger.IsEnabled(LogLevel.Debug)</c> guard at lines 85 and 112 can never be true in that
/// branch, and the guarded lines 86/87/113/114 can never execute. (Contrast with the existing
/// <c>WithDebugLoggingOn_TheProcessorNarratesWhatItDecidedAsync</c> test, which gets real Debug
/// logging only by using the scope-factory constructor — the one path that structurally excludes
/// lines 86/87/113/114.)
/// </description></item>
/// <item><description>
/// <c>_enforcePayloadSize</c> has exactly two <c>return</c> statements and both return
/// <c>true</c>; the only other exit is a <c>throw</c> on the error-threshold path. It can never
/// return <c>false</c>, so the <c>continue;</c> at line 137 (guarded by
/// <c>!_enforcePayloadSize(...)</c>) is dead code under the current method body.
/// </description></item>
/// </list>
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
  private sealed class FallbackOnlyTagAttribute : MessageTagAttribute {
  }

  private sealed record FallbackTaggedMessage(string Value);

  private sealed class FallbackOnlyTrackingHook : IMessageTagHook<FallbackOnlyTagAttribute> {
    public int InvokedCount { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<FallbackOnlyTagAttribute> context, CancellationToken _) {
      InvokedCount++;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class SignalTrackingHook : IMessageTagHook<SignalTagAttribute> {
    public int InvokedCount { get; private set; }

    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> context, CancellationToken _) {
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
}
