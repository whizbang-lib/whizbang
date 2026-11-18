// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Minting;

namespace Whizbang.Core.Messaging;

/// <summary>
/// One answer to a redelivery request, published over several selections such as the pages of the origin's selection
/// (#2311). Each <see cref="PublishAsync"/> publishes its bundles saying more of the answer follows, except the last one,
/// which it holds back: the next selection's publish sends it on as it is, and <see cref="CompleteAsync"/> sends it as
/// the answer's last bundle. The receiver then judges a whole-type answer as a whole: a bundle that changed nothing and
/// says the answer continues leaves the type lane waiting, so only the last bundle can mark it, and only when no bundle
/// before it brought anything.
/// </summary>
/// <remarks>
/// The held bundle is bounded like any bundle (<see cref="RedeliveryPumpOptions.MaxInnerEventsPerComposite"/> and
/// <see cref="RedeliveryPumpOptions.MaxBytesPerComposite"/>), so the origin still holds about one page of bodies at a
/// time. Bundles leave in the order they were selected. Created by <see cref="RedeliveryPump.BeginAnswer"/>.
/// </remarks>
/// <docs>resilience/stream-integrity#a-whole-type-answer-is-judged-as-a-whole</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RedeliveryPumpWindowTests.WholeAnswer.cs</tests>
public sealed class RedeliveryAnswerPublication {
  private readonly RedeliveryPump _pump;
  private readonly string _topic;
  private readonly string? _target;
  private readonly Guid _originServiceId;
  private readonly bool _stateOnly;
  private (CompositeEnvelopePlan<RedeliveryEvent> Plan, RedeliveryAnswerWindow Window)? _held;

  internal RedeliveryAnswerPublication(RedeliveryPump pump, string topic, string? target, Guid originServiceId, bool stateOnly) {
    _pump = pump;
    _topic = topic;
    _target = target;
    _originServiceId = originServiceId;
    _stateOnly = stateOnly;
  }

  /// <summary>
  /// Publishes one selection of the answer: the bundle held back from the previous selection first, then every bundle
  /// of this one but its last, which is held back until the answer goes on or ends.
  /// </summary>
  /// <param name="events">The selection, ordered (stream, version).</param>
  /// <param name="answeredWindow">The window this selection answers (#2143), with the streams it carries only in part
  /// (#2149).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The number of bundles this call published.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RedeliveryPumpWindowTests.WholeAnswer.cs</tests>
  public async Task<int> PublishAsync(
      IReadOnlyList<RedeliveryEvent> events, RedeliveryAnswerWindow answeredWindow, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(events);
    if (events.Count == 0) {
      return 0;
    }
    var plans = _pump.PlanBundles(events, _topic, _target, _originServiceId, answeredWindow);
    var published = 0;
    if (_held is { } held) {
      await _publishAsync(held.Plan, (RedeliveryComposite)held.Plan.Composite, cancellationToken).ConfigureAwait(false);
      published++;
    }
    for (var i = 0; i < plans.Count - 1; i++) {
      await _publishAsync(plans[i], (RedeliveryComposite)plans[i].Composite, cancellationToken).ConfigureAwait(false);
      published++;
    }
    _held = (plans[plans.Count - 1], answeredWindow);
    return published;
  }

  /// <summary>
  /// Ends the answer: publishes the held-back bundle saying the answer ends with it. Publishes nothing when no
  /// selection published anything.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The number of bundles this call published: 1, or 0 when the answer carried nothing.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RedeliveryPumpWindowTests.WholeAnswer.cs</tests>
  public async Task<int> CompleteAsync(CancellationToken cancellationToken = default) {
    if (_held is not { } held) {
      return 0;
    }
    _held = null;
    await _publishAsync(held.Plan, _pump.LastOfAnswer(held.Plan, _originServiceId, held.Window), cancellationToken)
      .ConfigureAwait(false);
    return 1;
  }

  private Task _publishAsync(
      CompositeEnvelopePlan<RedeliveryEvent> plan, RedeliveryComposite composite, CancellationToken cancellationToken) =>
    _pump.PublishBundleAsync(plan, composite, _topic, _target, _stateOnly, cancellationToken);
}
