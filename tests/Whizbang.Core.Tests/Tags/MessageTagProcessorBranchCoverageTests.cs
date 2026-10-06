// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.Tests.Helpers;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Branch coverage for <see cref="MessageTagProcessor"/>'s Debug diagnostics around hook
/// invocation: a hook that returns a modified payload (which the next hook must then see), and the
/// dispatcher registry both handling a custom attribute and finding no dispatcher for it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tags/MessageTagProcessor.cs</code-under-test>
[NotInParallel("TagRegistry")]
public class MessageTagProcessorBranchCoverageTests {

  private const string TAG = "branch-custom-tag";
  private static readonly JsonElement _modifiedPayload = JsonDocument.Parse("""{"modified":true}""").RootElement.Clone();

  [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = true)]
  private sealed class BranchCustomTagAttribute : MessageTagAttribute;

  private sealed record BranchTaggedMessage(string Value);

  private sealed class BranchRegistry : IMessageTagRegistry {
    public IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType) {
      if (messageType == typeof(BranchTaggedMessage)) {
        yield return new MessageTagRegistration {
          MessageType = typeof(BranchTaggedMessage),
          AttributeType = typeof(BranchCustomTagAttribute),
          Tag = TAG,
          PayloadBuilder = _ => JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
          AttributeFactory = () => new BranchCustomTagAttribute { Tag = TAG }
        };
      }
    }
  }

  /// <summary>Returns a modified payload.</summary>
  private sealed class ModifyingHook : IMessageTagHook<BranchCustomTagAttribute> {
    public int InvokedCount { get; private set; }
    public ValueTask<JsonElement?> OnTaggedMessageAsync(TagContext<BranchCustomTagAttribute> context, CancellationToken ct) {
      InvokedCount++;
      return ValueTask.FromResult<JsonElement?>(_modifiedPayload);
    }
  }

  /// <summary>Records the payload it was handed and leaves it unmodified.</summary>
  private sealed class ObservingHook : IMessageTagHook<BranchCustomTagAttribute> {
    public JsonElement? SeenPayload { get; private set; }
    public ValueTask<JsonElement?> OnTaggedMessageAsync(TagContext<BranchCustomTagAttribute> context, CancellationToken ct) {
      SeenPayload = context.Payload;
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  /// <summary>What a generated dispatcher does for one custom attribute type.</summary>
  private sealed class BranchDispatcher : IMessageTagHookDispatcher {
    public object? TryCreateContext(
        Type attributeType, MessageTagAttribute attribute, object message,
        Type messageType, JsonElement payload, IScopeContext? scope, LifecycleStage stage) =>
      attributeType == typeof(BranchCustomTagAttribute)
        ? new TagContext<BranchCustomTagAttribute> {
          Attribute = (BranchCustomTagAttribute)attribute,
          Message = message,
          MessageType = messageType,
          Payload = payload,
          Scope = scope,
          Stage = stage
        }
        : null;

    public async ValueTask<JsonElement?> TryDispatchAsync(object hookInstance, object context, Type attributeType, CancellationToken ct) {
      if (attributeType == typeof(BranchCustomTagAttribute)
          && hookInstance is IMessageTagHook<BranchCustomTagAttribute> hook
          && context is TagContext<BranchCustomTagAttribute> typed) {
        return await hook.OnTaggedMessageAsync(typed, ct);
      }
      return null;
    }
  }

  private static void _cleanup() {
    Whizbang.Core.Registry.AssemblyRegistry<IMessageTagRegistry>.ClearForTesting();
    Whizbang.Core.Registry.AssemblyRegistry<IMessageTagHookDispatcher>.ClearForTesting();
  }

  /// <summary>
  /// A custom attribute's hook reached through a registered dispatcher returns a modified payload:
  /// the diagnostics must say the dispatch succeeded and the payload was modified, and the next
  /// hook in priority order must be handed that modified payload, not the original.
  /// </summary>
  [Test]
  public async Task ProcessTagsAsync_DispatchedHookReturnsModifiedPayload_LogsItAndTheNextHookSeesItAsync() {
    _cleanup();
    try {
      MessageTagRegistry.Register(new BranchRegistry(), priority: 100);
      MessageTagHookDispatcherRegistry.Register(new BranchDispatcher(), priority: 100);

      var modifying = new ModifyingHook();
      var observing = new ObservingHook();
      var options = new TagOptions();
      options.UseHook<BranchCustomTagAttribute, ModifyingHook>(priority: 1);
      options.UseHook<BranchCustomTagAttribute, ObservingHook>(priority: 2);
      var logger = new CapturingLogger<MessageTagProcessor>();
      object? resolve(Type type) {
        if (type == typeof(ModifyingHook)) {
          return modifying;
        }
        if (type == typeof(ObservingHook)) {
          return observing;
        }
        return null;
      }
      var processor = new MessageTagProcessor(options, logger, hookResolver: resolve, scopeFactory: null);

      await processor.ProcessTagsAsync(
        new BranchTaggedMessage("value"), typeof(BranchTaggedMessage), LifecycleStage.AfterReceptorCompletion);

      var messages = logger.Snapshot().ConvertAll(e => e.Message);
      await Assert.That(modifying.InvokedCount).IsEqualTo(1);
      await Assert.That(messages.Any(m => m.Contains("Dispatcher registry result: success", StringComparison.Ordinal))).IsTrue()
        .Because("the registered dispatcher handled the custom attribute and returned a payload");
      await Assert.That(messages.Any(m => m.Contains("Hook invocation complete, result: modified payload", StringComparison.Ordinal))).IsTrue()
        .Because("the first hook returned a payload, and the diagnosis must say it modified it");
      await Assert.That(observing.SeenPayload).IsNotNull();
      await Assert.That(observing.SeenPayload!.Value.GetRawText()).IsEqualTo(_modifiedPayload.GetRawText())
        .Because("a hook's modified payload replaces the current payload for every later hook");
    } finally {
      _cleanup();
    }
  }

  /// <summary>
  /// With no dispatcher registered for a custom attribute, the registry finds nothing: the
  /// diagnostics must say the registry result was null and the hook left the payload alone,
  /// which is the only explanation an operator gets for a custom hook that never ran.
  /// </summary>
  [Test]
  public async Task ProcessTagsAsync_CustomAttributeWithNoDispatcher_LogsANullRegistryResultAsync() {
    _cleanup();
    try {
      MessageTagRegistry.Register(new BranchRegistry(), priority: 100);

      var modifying = new ModifyingHook();
      var options = new TagOptions();
      options.UseHook<BranchCustomTagAttribute, ModifyingHook>(priority: 1);
      var logger = new CapturingLogger<MessageTagProcessor>();
      var processor = new MessageTagProcessor(
        options,
        logger,
        hookResolver: type => type == typeof(ModifyingHook) ? modifying : null,
        scopeFactory: null);

      await processor.ProcessTagsAsync(
        new BranchTaggedMessage("value"), typeof(BranchTaggedMessage), LifecycleStage.AfterReceptorCompletion);

      var messages = logger.Snapshot().ConvertAll(e => e.Message);
      await Assert.That(modifying.InvokedCount).IsEqualTo(0)
        .Because("nothing can dispatch to a custom attribute's typed hook without a dispatcher");
      await Assert.That(messages.Any(m => m.Contains("Dispatcher registry result: null", StringComparison.Ordinal))).IsTrue();
      await Assert.That(messages.Any(m => m.Contains("Dispatcher registry result: success", StringComparison.Ordinal))).IsFalse();
      await Assert.That(messages.Any(m => m.Contains("Hook invocation complete, result: null", StringComparison.Ordinal))).IsTrue();
    } finally {
      _cleanup();
    }
  }
}
