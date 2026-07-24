using System;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Specifies the lifecycle stage(s) at which a receptor should be invoked.
/// Can be applied multiple times to fire a receptor at different lifecycle stages.
/// If not applied, receptor defaults to <see cref="LifecycleStage.ImmediateDetached"/>.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle receptors allow fine-grained control over when receptors execute during
/// message processing. This enables scenarios like:
/// </para>
/// <list type="bullet">
/// <item><description>Test synchronization (wait for perspective processing to complete)</description></item>
/// <item><description>Metrics collection at specific pipeline stages</description></item>
/// <item><description>Custom logging or observability hooks</description></item>
/// <item><description>Side effects triggered at precise moments in the pipeline</description></item>
/// </list>
/// <para>
/// <strong>Example:</strong> Fire receptor after perspective processing completes:
/// </para>
/// <code>
/// [FireAt(LifecycleStage.PostPerspectiveInline)]
/// public class NotificationReceptor : IReceptor&lt;OrderCreatedEvent&gt; {
///   public async ValueTask HandleAsync(OrderCreatedEvent evt, CancellationToken ct) {
///     // This fires AFTER perspectives have been updated
///     await _notificationService.NotifyAsync(evt.CustomerId, ct);
///   }
/// }
/// </code>
/// <para>
/// <strong>Multiple Stages:</strong> Fire receptor at multiple lifecycle stages:
/// </para>
/// <code>
/// [FireAt(LifecycleStage.ImmediateDetached)]
/// [FireAt(LifecycleStage.PostPerspectiveDetached)]
/// public class AuditReceptor : IReceptor&lt;OrderCreatedEvent&gt; {
///   public async ValueTask HandleAsync(OrderCreatedEvent evt, CancellationToken ct) {
///     // Fires both immediately AND after perspective processing
///     await _auditLog.WriteAsync($"OrderCreated: {evt.OrderId}", ct);
///   }
/// }
/// </code>
/// </remarks>
/// <docs>fundamentals/receptors/lifecycle-receptors</docs>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class FireAtAttribute(LifecycleStage stage) : Attribute {
  /// <summary>
  /// Gets the lifecycle stage at which the receptor should be invoked.
  /// </summary>
  public LifecycleStage Stage { get; } = stage;
}
