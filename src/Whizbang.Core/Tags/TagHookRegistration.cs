using System.Diagnostics.CodeAnalysis;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tags;

/// <summary>
/// Registration record for a message tag hook.
/// Associates an attribute type with a hook type and execution priority.
/// </summary>
/// <remarks>
/// <para>
/// Hooks are executed in ascending priority order: -100 → 500.
/// The default priority of -100 ensures hooks fire first unless explicitly ordered.
/// </para>
/// <para>
/// Example execution order for a message with multiple tag types:
/// <list type="number">
/// <item><description>UniversalTagLoggerHook (-100, default)</description></item>
/// <item><description>SignalRNotificationHook (-100, default)</description></item>
/// <item><description>AuditLogHook (-10)</description></item>
/// <item><description>MetricsPublishHook (30)</description></item>
/// <item><description>AnalyticsHook (500)</description></item>
/// </list>
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/message-tags#hook-registration</docs>
/// <tests>Whizbang.Core.Tests/Tags/TagHookRegistrationTests.cs</tests>
/// <param name="AttributeType">The tag attribute type this hook handles (e.g., typeof(SignalTagAttribute)).</param>
/// <param name="HookType">The hook implementation type (e.g., typeof(SignalRNotificationHook)).</param>
/// <param name="Priority">Execution priority. Lower values execute first. Default is -100.</param>
/// <param name="FireAt">Lifecycle stage when this hook fires. Null means fire at all stages (default).</param>
public sealed record TagHookRegistration(
  Type AttributeType,
  [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
  Type HookType,
  int Priority = -100,
  LifecycleStage? FireAt = null
) {
  /// <summary>
  /// Gets the default priority for hooks. Lower values execute first.
  /// </summary>
  public static int DefaultPriority => -100;

  /// <summary>
  /// Gets the default lifecycle stage for hooks. Null means fire at all stages.
  /// </summary>
  public static LifecycleStage? DefaultFireAt => null;
}
