using Whizbang.Core.Lenses;
using Whizbang.Core.Tags;
using Whizbang.Core.Tracing;

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
///   options.Tags.UseHook&lt;SignalTagAttribute, SignalRNotificationHook&gt;();
///   options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
/// });
/// </code>
/// </example>
/// </remarks>
/// <docs>operations/configuration/whizbang-options</docs>
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
  /// Gets the tracing system configuration.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Use this property to configure handler and message tracing.
  /// Tracing supports both OpenTelemetry spans and structured logging.
  /// </para>
  /// <example>
  /// <code>
  /// services.AddWhizbang(options => {
  ///   options.Tracing.Verbosity = TraceVerbosity.Verbose;
  ///   options.Tracing.Components = TraceComponents.Handlers | TraceComponents.Lifecycle;
  ///   options.Tracing.TracedHandlers["ReseedSystemEventHandler"] = TraceVerbosity.Debug;
  /// });
  /// </code>
  /// </example>
  /// </remarks>
  /// <docs>operations/observability/tracing#configuration</docs>
  /// <tests>tests/Whizbang.Core.Tests/Configuration/WhizbangCoreOptionsTests.cs:Constructor_InitializesTracingOptions_NotNullAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Configuration/WhizbangCoreOptionsTests.cs:Tracing_VerbosityDefaultsToOff_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/Configuration/WhizbangCoreOptionsTests.cs:Tracing_CanBeConfigured_Async</tests>
  public TracingOptions Tracing { get; } = new();

  /// <summary>
  /// Gets the service registration configuration for auto-discovered services.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Use this property to control how lenses and perspectives are registered.
  /// These options are passed to the source-generated registration callbacks.
  /// </para>
  /// <example>
  /// <code>
  /// services.AddWhizbang(options => {
  ///   options.Services.IncludeSelfRegistration = false;  // Only register interfaces
  /// });
  /// </code>
  /// </example>
  /// </remarks>
  /// <docs>operations/configuration/service-registration-options</docs>
  public ServiceRegistrationOptions Services { get; } = new();

  /// <summary>
  /// When <see langword="true"/> (the default — turnkey), <see cref="ServiceCollectionExtensions.AddWhizbang"/>
  /// automatically folds in <c>AddWhizbangAspNet()</c> if the Whizbang.Hosting.AspNet assembly is loaded
  /// (health checks + the schema-availability gate wired via startup filters). Set to
  /// <see langword="false"/> to opt out and call <c>AddWhizbangAspNet()</c> yourself — e.g. if you need
  /// to control its position relative to other registrations.
  /// </summary>
  public bool AutoRegisterAspNetHosting { get; set; } = true;

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

  /// <summary>
  /// Gets or sets the default query scope used by <see cref="ILensQuery{TModel}.DefaultScope"/>.
  /// Controls the default level of scope filtering applied to lens queries.
  /// Default: <see cref="QueryScope.Tenant"/>.
  /// </summary>
  /// <remarks>
  /// This setting determines how lens queries filter data when the caller uses
  /// <c>.DefaultScope.Query</c> or <c>.DefaultScope.GetByIdAsync()</c> without
  /// explicitly choosing a scope level.
  /// </remarks>
  /// <docs>fundamentals/lenses/scoped-queries#default-scope</docs>
  /// <tests>tests/Whizbang.Core.Tests/Configuration/DefaultQueryScopeOptionsTests.cs:DefaultQueryScope_DefaultsToTenantAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Configuration/DefaultQueryScopeOptionsTests.cs:DefaultQueryScope_CanBeSetToGlobalAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Configuration/DefaultQueryScopeOptionsTests.cs:DefaultQueryScope_CanBeSetToAllValuesAsync</tests>
  public QueryScope DefaultQueryScope { get; set; } = QueryScope.Tenant;

  /// <summary>
  /// Gets or sets whether the Whizbang ASCII art banner is printed to console on startup.
  /// The version log line ("Whizbang v{Version} initialized") always prints regardless.
  /// Default: true.
  /// </summary>
  /// <docs>operations/configuration/whizbang-options#banner</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/WhizbangStartupLoggerTests.cs:StartAsync_ConfigShowBannerOverridesCodeOption_TrueAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Observability/WhizbangStartupLoggerTests.cs:StartAsync_ConfigShowBannerOverridesCodeOption_FalseAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Observability/WhizbangStartupLoggerTests.cs:StartAsync_NullConfiguration_UsesCodeOptionAsync</tests>
  public bool ShowBanner { get; set; } = true;

  /// <summary>
  /// Warning threshold for ImmediateDetached chain depth. Logs a warning when exceeded.
  /// No hard limit — chains run until the queue is empty.
  /// Default: 10.
  /// </summary>
  /// <remarks>
  /// ImmediateDetached receptors may dispatch further events that themselves have ImmediateDetached
  /// receptors, creating chains. This threshold triggers a warning log when chain depth
  /// reaches a multiple of this value, helping identify potentially unbounded chains.
  /// </remarks>
  /// <docs>fundamentals/lifecycle/lifecycle-stages#immediate-async</docs>
  public int ImmediateDetachedChainWarningThreshold { get; set; } = 10;

  /// <summary>
  /// Governs how Whizbang handles rows whose <c>stream_id</c> is
  /// <see cref="System.Guid.Empty"/> (the all-zeros UUID, distinct from NULL).
  /// Default: <see cref="EmptyStreamIdPolicy.Reject"/> — producers see a typed
  /// exception at INSERT time. Operators with legacy producers can flip to
  /// <see cref="EmptyStreamIdPolicy.FallbackToMessageId"/> until the source is
  /// fixed.
  /// </summary>
  /// <remarks>
  /// A production forensic investigation found a single producer wrote hundreds of
  /// silent-stuck rows over 24 h because Empty <c>stream_id</c> bypassed the
  /// <c>r.StreamId ?? r.WorkId</c> NULL-only fallback. The
  /// <see cref="EmptyStreamIdPolicy.Reject"/> default closes that surface
  /// at the producer; the coordinator-side Empty→WorkId recovery is
  /// unconditional so already-stored bad rows can still drain.
  /// </remarks>
  /// <docs>operations/configuration/empty-stream-id-policy</docs>
  public EmptyStreamIdPolicy EmptyStreamIdPolicy { get; set; } = EmptyStreamIdPolicy.Reject;
}

/// <summary>
/// Defines when tag processing occurs in the message dispatch pipeline.
/// </summary>
/// <docs>operations/configuration/whizbang-options#tag-processing-mode</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/WhizbangCoreOptionsTests.cs:TagProcessingMode_DefaultsToAfterReceptorCompletion_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Configuration/WhizbangCoreOptionsTests.cs:TagProcessingMode_CanBeSetToAsLifecycleStage_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/DispatcherTagProcessingTests.cs:LocalInvokeAsync_WithLifecycleStageMode_SkipsImmediateProcessingAsync</tests>
public enum TagProcessingMode {
  /// <summary>
  /// Process tags immediately after receptor completes (default).
  /// Tags fire before lifecycle stages like LocalImmediateDetached.
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
