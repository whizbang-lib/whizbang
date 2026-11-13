// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>A posting to a ledger: the ledger's balance grows by <see cref="Amount"/>.</summary>
public sealed record LedgerPosted : IEvent {
  [StreamId]
  public Guid LedgerId { get; init; }
  public decimal Amount { get; init; }
  public string Owner { get; init; } = "";
}

/// <summary>Closes a ledger: its row is purged, and stays purged.</summary>
public sealed record LedgerClosed : IEvent {
  [StreamId]
  public Guid LedgerId { get; init; }
}

/// <summary>An event the ledger's Apply refuses by throwing: a failing apply.</summary>
public sealed record LedgerRejected : IEvent {
  [StreamId]
  public Guid LedgerId { get; init; }
}

/// <summary>
/// The write-mode conformance suite's ledger (#2246). Its fold is additive, so an event applied twice or lost shows in
/// the balance and the posting count; its owner is a physical field stored Extracted (in its column and the document).
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveWriteModeConformanceTests.cs</tests>
public class ConformanceLedgerPerspective :
    IPerspectiveFor<ConformanceLedger, LedgerPosted>,
    IPerspectiveWithActionsFor<ConformanceLedger, LedgerClosed>,
    IPerspectiveFor<ConformanceLedger, LedgerRejected> {
  public ConformanceLedger Apply(ConformanceLedger currentData, LedgerPosted @event) =>
    currentData with { Id = @event.LedgerId, Balance = currentData.Balance + @event.Amount, Postings = currentData.Postings + 1, Owner = @event.Owner };

  public ApplyResult<ConformanceLedger> Apply(ConformanceLedger currentData, LedgerClosed @event) =>
    ApplyResult<ConformanceLedger>.Purge();

  public ConformanceLedger Apply(ConformanceLedger currentData, LedgerRejected @event) =>
    throw new InvalidOperationException($"Ledger {@event.LedgerId} rejected the event.");
}

/// <summary>The ledger's model: its owner promoted to a column and kept in the document (Extracted).</summary>
[PerspectiveStorage(FieldStorageMode.Extracted)]
[SuppressIndexAdvisory("test fixture; the promoted field is read back, never filtered on")]
public record ConformanceLedger {
  [StreamId]
  public Guid Id { get; init; }

  public decimal Balance { get; init; }

  public int Postings { get; init; }

  [PhysicalField]
  public string? Owner { get; init; }
}

/// <summary>The same postings folded into a model whose owner lives only in its column (Split).</summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveWriteModeConformanceTests.cs</tests>
public class ConformanceSplitLedgerPerspective : IPerspectiveFor<ConformanceSplitLedger, LedgerPosted> {
  public ConformanceSplitLedger Apply(ConformanceSplitLedger currentData, LedgerPosted @event) =>
    currentData with { Id = @event.LedgerId, Balance = currentData.Balance + @event.Amount, Owner = @event.Owner };
}

/// <summary>The split ledger's model: its owner only in its column.</summary>
[PerspectiveStorage(FieldStorageMode.Split)]
[SuppressIndexAdvisory("test fixture; the promoted field is read back, never filtered on")]
public record ConformanceSplitLedger {
  [StreamId]
  public Guid Id { get; init; }

  public decimal Balance { get; init; }

  [PhysicalField]
  public string? Owner { get; init; }
}
