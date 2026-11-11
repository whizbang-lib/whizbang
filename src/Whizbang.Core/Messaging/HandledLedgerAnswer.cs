// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace Whizbang.Core.Messaging;

/// <summary>
/// What the handled ledger says about a set of repaired events (#2273): the ones it records as handled by this service,
/// and when its coverage starts. Only an event whose origin time is at or after <see cref="CoversFrom"/> can be proven
/// never handled by its absence from <see cref="Handled"/>.
/// </summary>
/// <param name="CoversFrom">The moment from which every event handled here is recorded; null when there is no ledger.</param>
/// <param name="Handled">The asked-about events the ledger records as handled.</param>
/// <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/RepairReceptorGateTests.cs</tests>
public sealed record HandledLedgerAnswer(DateTimeOffset? CoversFrom, IReadOnlySet<Guid> Handled) {
  /// <summary>The answer of a store that keeps no ledger: no coverage, so no repaired event runs a receptor.</summary>
  public static HandledLedgerAnswer NoLedger { get; } = new(null, new HashSet<Guid>());
}
