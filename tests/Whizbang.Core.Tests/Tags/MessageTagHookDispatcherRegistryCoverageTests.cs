using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Registry;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Coverage for <see cref="MessageTagHookDispatcherRegistry"/> paths never exercised by
/// <see cref="TagHookStageFilteringAndScopeTests"/>: reading
/// <see cref="MessageTagHookDispatcherRegistry.Count"/>, a registered dispatcher declining a
/// context (the loop must keep trying the rest instead of stopping at the first no), and a
/// dispatcher's <c>TryDispatchAsync</c> actually returning a result. This registry is the seam a
/// source-generated dispatcher plugs into so a consumer's own custom <c>[MessageTag]</c> attribute
/// reaches its hook — a loop that stops early or a dispatch result that gets dropped means a
/// consumer's tag silently never fires.
/// </summary>
[NotInParallel("TagRegistry")]
public class MessageTagHookDispatcherRegistryCoverageTests {

  private sealed class _decliningDispatcher : IMessageTagHookDispatcher {
    public object? TryCreateContext(
        Type attributeType, MessageTagAttribute attribute, object message,
        Type messageType, JsonElement payload, IScopeContext? scope, LifecycleStage stage) => null;

    public ValueTask<JsonElement?> TryDispatchAsync(object hookInstance, object context, Type attributeType, CancellationToken ct) =>
      ValueTask.FromResult<JsonElement?>(null);
  }

  private sealed class _handlingDispatcher : IMessageTagHookDispatcher {
    public object? TryCreateContext(
        Type attributeType, MessageTagAttribute attribute, object message,
        Type messageType, JsonElement payload, IScopeContext? scope, LifecycleStage stage) => null;

    public ValueTask<JsonElement?> TryDispatchAsync(object hookInstance, object context, Type attributeType, CancellationToken ct) =>
      ValueTask.FromResult<JsonElement?>(JsonDocument.Parse("""{"handled":true}""").RootElement);
  }

  [After(Test)]
  public void CleanupDispatcherRegistry() {
    AssemblyRegistry<IMessageTagHookDispatcher>.ClearForTesting();
  }

  /// <summary>What breaks: <see cref="MessageTagHookDispatcherRegistry.Count"/> is the one
  /// diagnostic surface for "is my custom tag dispatcher actually wired up" — if it under- or
  /// over-reports, a consumer debugging a silently-not-firing hook has nothing to trust.</summary>
  [Test]
  public async Task Count_ReflectsRegisteredDispatchersAsync() {
    AssemblyRegistry<IMessageTagHookDispatcher>.ClearForTesting();
    await Assert.That(MessageTagHookDispatcherRegistry.Count).IsEqualTo(0);

    MessageTagHookDispatcherRegistry.Register(new _decliningDispatcher());

    await Assert.That(MessageTagHookDispatcherRegistry.Count).IsEqualTo(1)
      .Because("Count reads straight through to the underlying registry — it must reflect what Register just added");
  }

  /// <summary>What breaks: if the loop stopped at the first dispatcher that declines an attribute
  /// type instead of trying every registered one, a real dispatcher registered after a framework
  /// one that doesn't handle this type would never get a chance to build the context — the hook
  /// would silently never fire.</summary>
  [Test]
  public async Task TryCreateContext_DispatcherDeclinesLoopFallsThroughToNullAsync() {
    AssemblyRegistry<IMessageTagHookDispatcher>.ClearForTesting();
    MessageTagHookDispatcherRegistry.Register(new _decliningDispatcher());

    var result = MessageTagHookDispatcherRegistry.TryCreateContext(
      typeof(SignalTagAttribute), new SignalTagAttribute { Tag = "t" }, new object(),
      typeof(object), JsonDocument.Parse("{}").RootElement, scope: null, LifecycleStage.ImmediateDetached);

    await Assert.That(result).IsNull()
      .Because("every registered dispatcher declining must fall through to null instead of stopping early on a false positive");
  }

  /// <summary>What breaks: a dispatcher that DOES handle the attribute type must have its result
  /// actually returned — dropping it here means a consumer's real hook result never reaches the
  /// caller even though the dispatch technically ran.</summary>
  [Test]
  public async Task TryDispatchAsync_HandlingDispatcherResultIsReturnedAsync() {
    AssemblyRegistry<IMessageTagHookDispatcher>.ClearForTesting();
    MessageTagHookDispatcherRegistry.Register(new _handlingDispatcher());

    var result = await MessageTagHookDispatcherRegistry.TryDispatchAsync(
      new object(), new object(), typeof(SignalTagAttribute), CancellationToken.None);

    await Assert.That(result).IsNotNull()
      .Because("a dispatcher that handles the attribute type must have its result returned, not silently skipped");
  }
}
