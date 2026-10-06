// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Branch coverage for <see cref="TagOptions"/>: per-tag payload-size thresholds (null disables,
/// negative refuses, non-negative is kept), and the stage-filtered hook lookup's every combination
/// of attribute match and stage match.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Tags/TagOptions.cs</code-under-test>
[Category("Core")]
[Category("Tags")]
public class TagOptionsBranchCoverageTests {

  [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
  private sealed class TargetTagAttribute : MessageTagAttribute;

  [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
  private sealed class OtherTagAttribute : MessageTagAttribute;

  private sealed class TargetAllStagesHook : IMessageTagHook<TargetTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(TagContext<TargetTagAttribute> context, CancellationToken ct) => new((JsonElement?)null);
  }

  private sealed class TargetThisStageHook : IMessageTagHook<TargetTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(TagContext<TargetTagAttribute> context, CancellationToken ct) => new((JsonElement?)null);
  }

  private sealed class TargetOtherStageHook : IMessageTagHook<TargetTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(TagContext<TargetTagAttribute> context, CancellationToken ct) => new((JsonElement?)null);
  }

  private sealed class UniversalThisStageHook : IMessageTagHook<MessageTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(TagContext<MessageTagAttribute> context, CancellationToken ct) => new((JsonElement?)null);
  }

  private sealed class OtherAttributeHook : IMessageTagHook<OtherTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(TagContext<OtherTagAttribute> context, CancellationToken ct) => new((JsonElement?)null);
  }

  #region Per-tag payload-size thresholds

  [Test]
  public async Task SetPayloadSizeWarningThreshold_Negative_ThrowsNamingTheParameterAsync() {
    var options = new TagOptions();

    var ex = await Assert.That(() => options.SetPayloadSizeWarningThreshold("orders", -1))
      .Throws<ArgumentOutOfRangeException>();

    await Assert.That(ex!.ParamName).IsEqualTo("warningBytes");
    await Assert.That(options.PayloadSizeWarningThresholdBytesByTag.ContainsKey("orders")).IsFalse()
      .Because("a refused threshold must not be recorded");
  }

  [Test]
  public async Task SetPayloadSizeErrorThreshold_Negative_ThrowsNamingTheParameterAsync() {
    var options = new TagOptions();

    var ex = await Assert.That(() => options.SetPayloadSizeErrorThreshold("orders", -5))
      .Throws<ArgumentOutOfRangeException>();

    await Assert.That(ex!.ParamName).IsEqualTo("errorBytes");
  }

  [Test]
  public async Task SetPayloadSizeWarningThreshold_Null_DisablesTheCheckForThatTagOnlyAsync() {
    var options = new TagOptions { PayloadSizeWarningThresholdBytes = 1024 };

    options.SetPayloadSizeWarningThreshold("wide-by-design", null);

    await Assert.That(options.ResolvePayloadSizeWarningThreshold("wide-by-design")).IsNull()
      .Because("null declared for a tag disables the warning for that tag, overriding the global value");
    await Assert.That(options.ResolvePayloadSizeWarningThreshold("other")).IsEqualTo(1024)
      .Because("every other tag still falls back to the global threshold");
  }

  [Test]
  public async Task SetPayloadSizeErrorThreshold_NonNegative_IsKeptForThatTagAsync() {
    var options = new TagOptions();

    options.SetPayloadSizeErrorThreshold("orders", 0);
    options.SetPayloadSizeErrorThreshold("audit", 4096);

    await Assert.That(options.ResolvePayloadSizeErrorThreshold("orders")).IsEqualTo(0)
      .Because("zero is a valid threshold; only negative values are refused");
    await Assert.That(options.ResolvePayloadSizeErrorThreshold("audit")).IsEqualTo(4096);
  }

  #endregion

  #region Stage-filtered hook lookup

  /// <summary>
  /// The stage-filtered lookup returns hooks registered for the attribute or universally, that
  /// fire at every stage or at this one, in priority order. A hook for another attribute or for
  /// another stage firing here would run a hook its author scoped away.
  /// </summary>
  [Test]
  public async Task GetHooksFor_WithStage_ReturnsMatchingAttributeOrUniversalAtThisStageInPriorityOrderAsync() {
    var options = new TagOptions();
    options.UseHook<TargetTagAttribute, TargetAllStagesHook>(priority: 30);
    options.UseHook<TargetTagAttribute, TargetThisStageHook>(priority: 10, fireAt: LifecycleStage.PostPerspectiveInline);
    options.UseHook<TargetTagAttribute, TargetOtherStageHook>(priority: 5, fireAt: LifecycleStage.AfterReceptorCompletion);
    options.UseHook<MessageTagAttribute, UniversalThisStageHook>(priority: 20, fireAt: LifecycleStage.PostPerspectiveInline);
    options.UseHook<OtherTagAttribute, OtherAttributeHook>(priority: 1);

    var hooks = options.GetHooksFor<TargetTagAttribute>(LifecycleStage.PostPerspectiveInline)
      .Select(r => r.HookType.FullName)
      .ToList();

    await Assert.That(hooks).IsEquivalentTo(
      [typeof(TargetThisStageHook).FullName, typeof(UniversalThisStageHook).FullName, typeof(TargetAllStagesHook).FullName],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("attribute-or-universal AND every-stage-or-this-stage, ordered by priority");
  }

  #endregion
}
