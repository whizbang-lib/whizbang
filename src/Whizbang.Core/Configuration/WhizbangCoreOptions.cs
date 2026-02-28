using Whizbang.Core.Tags;

namespace Whizbang.Core.Configuration;

/// <summary>
/// Configuration options for AddWhizbang() setup.
/// Provides access to subsystem configuration like Tags.
/// </summary>
/// <remarks>
/// <para>
/// Use this class to configure Whizbang behavior at startup through the AddWhizbang() method.
/// Tag processing can be configured via the <see cref="Tags"/> property.
/// </para>
/// <example>
/// <code>
/// services.AddWhizbang(options => {
///   options.Tags.UseHook&lt;NotificationTagAttribute, SignalRNotificationHook&gt;();
///   options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
/// });
/// </code>
/// </example>
/// </remarks>
/// <docs>configuration/whizbang-options</docs>
/// <tests>Whizbang.Core.Tests/Configuration/WhizbangCoreOptionsTests.cs</tests>
public sealed class WhizbangCoreOptions {
  /// <summary>
  /// Gets the tag system configuration.
  /// </summary>
  /// <remarks>
  /// Use this property to register tag hooks that process messages after successful handling.
  /// </remarks>
  public TagOptions Tags { get; } = new();

  /// <summary>
  /// Gets or sets whether tag processing is enabled.
  /// Default: true (process tags after receptor completion).
  /// </summary>
  /// <remarks>
  /// When disabled, no tag hooks will be invoked regardless of registered hooks.
  /// </remarks>
  public bool EnableTagProcessing { get; set; } = true;

  /// <summary>
  /// Gets or sets the tag processing mode.
  /// Default: <see cref="TagProcessingMode.AfterReceptorCompletion"/>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <see cref="TagProcessingMode.AfterReceptorCompletion"/> processes tags immediately
  /// after the receptor completes, before lifecycle stages.
  /// </para>
  /// <para>
  /// <see cref="TagProcessingMode.AsLifecycleStage"/> processes tags during lifecycle
  /// invocation, after other lifecycle receptors have completed.
  /// </para>
  /// </remarks>
  public TagProcessingMode TagProcessingMode { get; set; } = TagProcessingMode.AfterReceptorCompletion;
}

/// <summary>
/// Defines when tag processing occurs in the message dispatch pipeline.
/// </summary>
/// <docs>configuration/whizbang-options#tag-processing-mode</docs>
public enum TagProcessingMode {
  /// <summary>
  /// Process tags immediately after receptor completes (default).
  /// Tags fire before lifecycle stages like LocalImmediateAsync.
  /// </summary>
  /// <remarks>
  /// Use this mode when tag hooks need to execute as early as possible
  /// after message handling, or when hooks don't depend on lifecycle receptors.
  /// </remarks>
  AfterReceptorCompletion,

  /// <summary>
  /// Process tags as a lifecycle stage (PostLocalImmediateInline).
  /// Use when tag hooks need to run after other lifecycle receptors.
  /// </summary>
  /// <remarks>
  /// Use this mode when tag hooks depend on state changes made by
  /// lifecycle receptors, or when you need hooks to run after all
  /// local lifecycle processing is complete.
  /// </remarks>
  AsLifecycleStage
}
