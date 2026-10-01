using System;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Declares that a receptor runs once for a message across every service that registers it, rather
/// than once in each service.
/// </summary>
/// <remarks>
/// <para>
/// By default a receptor runs once per message in each service: the invocation records a message
/// carries stop a receptor only in the service that wrote them. A receptor class shared by two
/// services, writing to each service's own store, therefore runs in both.
/// </para>
/// <para>
/// A receptor whose effect is outside every service, such as sending an email or calling a webhook,
/// has to happen once. Marking it with this attribute makes a record from any service stop it.
/// </para>
/// <code>
/// [ReceptorOnceAcrossServices]
/// public class OrderConfirmationEmail : IReceptor&lt;OrderPlaced&gt; {
///   public ValueTask HandleAsync(OrderPlaced message, CancellationToken ct) {
///     // Sends one email, whichever services handle OrderPlaced.
///     return ValueTask.CompletedTask;
///   }
/// }
/// </code>
/// </remarks>
/// <docs>fundamentals/receptors/exactly-once-firing#once-per-service</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherLocalDispatchRecordTests.cs:OnceAcrossServicesReceptor_InTwoServices_RunsOnceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvocationTrackingTests.cs:OnceAcrossServices_FiredInAnotherService_IsSkippedAsync</tests>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ReceptorOnceAcrossServicesAttribute : Attribute;
