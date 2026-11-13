// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Declares that a receptor runs for a repaired event this service can prove it never handled. Without it a receptor
/// never runs for a repaired event: the event is stored and projected, state only.
/// </summary>
/// <remarks>
/// <para>
/// Stream-integrity repair delivers historical events a receiver is missing, with their original ids. A receiver can be
/// missing an event it already reacted to (it handled the event through a trigger receptor but its event store lost
/// the row), so running every receptor again would repeat side effects that already happened. A repaired event
/// therefore runs a receptor only when all three of these hold:
/// </para>
/// <list type="number">
/// <item><description>the receptor carries this attribute;</description></item>
/// <item><description>the event's origin time is inside
/// <see cref="StreamIntegrityOptions.RepairReceptorLookback"/>, clamped to the time this service's handled ledger
/// starts covering;</description></item>
/// <item><description>the handled ledger shows this service never handled the event.</description></item>
/// </list>
/// <para>
/// A receptor that opts in <b>must be idempotent</b>: the ledger proves the event was never handled here, but an effect
/// outside this service (an email, a webhook) may still have happened through another path.
/// </para>
/// <code>
/// [RunOnRepair]
/// public class ProjectInvoiceTotals : IReceptor&lt;InvoiceIssued&gt; {
///   public ValueTask HandleAsync(InvoiceIssued message, CancellationToken ct) {
///     // Idempotent: keyed by the invoice id, so running it twice changes nothing.
///     return ValueTask.CompletedTask;
///   }
/// }
/// </code>
/// </remarks>
/// <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerRepairTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorRunOnRepairTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RunOnRepairAttribute : Attribute;
