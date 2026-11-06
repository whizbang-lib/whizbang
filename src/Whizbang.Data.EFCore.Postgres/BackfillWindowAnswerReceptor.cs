// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The requester's side of a paced backfill window (#2092): applies a received <see cref="BackfillWindowAnswer"/> to the
/// backfill's durable progress (<see cref="IWorkCoordinator.RecordSubscriptionBackfillAnswerAsync"/>), so the next window
/// starts exactly where this one stopped (#2098), counts the window's events per type, and logs the type's finish when
/// the owner had nothing more. A late or duplicate answer applies to nothing and counts nothing. Report-only takes no
/// part in backfill, so the answer is not applied there. Lives in the driver assembly for the same reason as
/// <see cref="RedeliveryRequestReceptor"/>, and is registered beside it.
/// </summary>
/// <docs>resilience/stream-integrity#paced-backfill</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BackfillWindowAnswerReceptorTests.cs</tests>
#pragma warning disable CA1822, S2325, RCS1163, S1144, IDE0052, IDE0060, RCS1213, CS9113 // RED stub (#2092)
public sealed partial class BackfillWindowAnswerReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<BackfillWindowAnswerReceptor> logger) : IReceptor<BackfillWindowAnswer> {
  /// <inheritdoc />
  public ValueTask HandleAsync(BackfillWindowAnswer message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
#pragma warning restore CA1822, S2325, RCS1163, S1144, IDE0052, IDE0060, RCS1213, CS9113
