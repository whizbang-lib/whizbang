// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The lock order across a call, not just inside one function: the inbox emit chain and everything it calls.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NoFunctionLocksTheWorkTablesOutOfCanonicalOrderAsync"/> reads each function alone, so a function
/// that calls another inherits nothing from it: a <c>PERFORM</c> of a function that deletes from <c>wh_inbox</c> is
/// invisible to it. That is how the first version of the mixed-stream refusal (#1546) slipped past it: the inbox
/// emit chain dead-lettered a refused row through <c>move_to_dead_letters</c>, which deletes the <c>wh_inbox</c>
/// row.
/// </para>
/// <para>
/// The chain runs inside <c>claim_work</c>, after the claim has leased its <c>wh_inbox_state</c> rows and written
/// the stream ledger (<c>wh_active_streams</c>). Anything it locks that comes earlier in the canonical order
/// (<c>wh_outbox</c>, <c>wh_inbox</c>) is therefore locked out of order, against every transaction that takes the
/// tables the right way round, such as <c>store_inbox_messages</c> (<c>wh_inbox</c>, then
/// <c>wh_active_streams</c>). The chain is followed through every W! function it calls, transitively.
/// </para>
/// </remarks>
public partial class WorkTablesAreLockedInOneOrderTests {
  private const string INBOX_EMIT_CHAIN = "_emit_event_store_chain_for_inbox";

  /// <summary>
  /// Neither the inbox emit chain nor any function it reaches locks a work table that comes before
  /// <c>wh_inbox_state</c> in the canonical order.
  /// </summary>
  [Test]
  public async Task TheInboxEmitChain_AndWhatItCalls_NeverLocksATableBeforeTheClaimsOwnAsync() {
    var bodies = _latestFunctionBodies();
    var reached = _reachedFrom(INBOX_EMIT_CHAIN, bodies);

    var locks = reached
      .SelectMany(function => _lockingStatements(bodies[function])
        .SelectMany(statement => statement.Tables.Select(table => (Function: function, Table: table))))
      .ToList();
    string[] lockedTooEarly = ["wh_outbox", "wh_inbox"];
    var offenses = locks
      .Where(l => lockedTooEarly.Contains(l.Table, StringComparer.Ordinal))
      .Select(l => $"{l.Function} locks {l.Table}")
      .Distinct(StringComparer.Ordinal)
      .ToList();

    // Not vacuous: the walk found the chain and the lock it is known to take.
    await Assert.That(locks).Contains((INBOX_EMIT_CHAIN, "wh_inbox_state"));
    await Assert.That(offenses).IsEmpty()
      .Because("the chain runs inside claim_work after the claim has locked wh_inbox_state and wh_active_streams, so "
        + "locking wh_outbox or wh_inbox there inverts the canonical order " + string.Join(" -> ", CANONICAL)
        + ". Offenses: " + string.Join("; ", offenses));
  }

  /// <summary>
  /// The premise of the rule above: the claim calls the inbox emit chain after it has written the stream ledger.
  /// </summary>
  [Test]
  public async Task TheClaim_CallsTheInboxEmitChainAfterWritingTheStreamLedgerAsync() {
    var claim = _latestFunctionBodies()["claim_work"];

    var ledger = claim.IndexOf("wh_lease_claimed_streams(", StringComparison.Ordinal);
    var chain = claim.IndexOf(INBOX_EMIT_CHAIN + "(", StringComparison.Ordinal);

    await Assert.That(ledger).IsGreaterThan(0);
    await Assert.That(chain).IsGreaterThan(ledger);
  }

  /// <summary>Every function's last definition, as replay leaves it on the server, with comments and literals blanked.</summary>
  private static Dictionary<string, string> _latestFunctionBodies() {
    var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var script in new PostgresMigrationProvider(
        typeof(PostgresMigrationProvider).Assembly, SCHEMA).GetMigrations()) {
      foreach (var (name, body) in _functionBodies(script.Sql)) {
        bodies[name] = body;
      }
    }
    return bodies;
  }

  /// <summary>
  /// The function and every W! function it calls, transitively, in the order each is first reached. A call is any
  /// name of a defined function followed by an opening parenthesis, schema-qualified or not.
  /// </summary>
  private static List<string> _reachedFrom(string root, Dictionary<string, string> bodies) {
    var reached = new List<string>();
    var pending = new Queue<string>([root]);
    while (pending.Count > 0) {
      var function = pending.Dequeue();
      if (reached.Contains(function, StringComparer.Ordinal) || !bodies.TryGetValue(function, out var body)) {
        continue;
      }
      reached.Add(function);
      // The body's own CREATE line names the function itself; skip past it so a function is not its own callee.
      var afterHeader = body.IndexOf('(', StringComparison.Ordinal) + 1;
      foreach (var call in CallSite().Matches(body[afterHeader..]).Select(m => m.Groups[1].Value).Where(bodies.ContainsKey)) {
        pending.Enqueue(call);
      }
    }
    return reached;
  }

  [GeneratedRegex(@"\b(\w+)\s*\(")]
  private static partial Regex CallSite();
}
