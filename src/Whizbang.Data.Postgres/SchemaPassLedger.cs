// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Whizbang.Data.Postgres;

/// <summary>
/// What a schema pass records in <c>wh_schema_migrations</c> so that a pass applied one step at a time can be resumed
/// at the step that failed, by a retry or by another instance, and never applies a committed step again.
/// </summary>
/// <remarks>
/// <para>
/// Each step of the pass is a transaction of its own. A migration file and a perspective table already have a ledger
/// row whose hash says whether they are current. The steps that have none (the core tables, the constraints, the
/// associations and the registries) get one here, owned by <see cref="STEP_OWNER"/>: marked pending when a pass that
/// needs them begins, and applied in the same transaction as their work. A step is current exactly when its row says
/// applied.
/// </para>
/// <para>
/// A pass is recognized by its <em>fingerprint</em>, a hash of everything the build would apply. While any step row
/// carrying this build's fingerprint is still outstanding, a pass of this build is part way through, and whoever
/// takes the lock next (a retry, or a second instance) continues it rather than marking its steps pending again,
/// which would apply what already committed a second time. A pass that finished, or one left behind by a different
/// build, starts afresh: the steps this pass needs are marked, and so is every step still outstanding, so nothing a
/// crashed pass owed is lost.
/// </para>
/// <para>
/// A migration file that has to run again although its hash is unchanged (pulled in by the redefinition closure or
/// one of the sweeps) is recorded with <see cref="PENDING_HASH"/> in place of its hash before any file runs. Each
/// file then needs only its own hash re-checked under the lock: the plan survives a failure part way through, so a
/// later definition the closure owes is not forgotten because the earlier one already committed.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaPassLedgerTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigrationStepTransactionTests.cs</tests>
public static class SchemaPassLedger {
  /// <summary>The <c>owner</c> of the ledger rows that record the steps with no row of their own.</summary>
  public const string STEP_OWNER = "step";

  /// <summary>
  /// The <c>content_hash</c> of a file that has to run again: never a SHA-256, so it matches no file and every reader
  /// of the ledger treats the file as changed until it has run.
  /// </summary>
  public const string PENDING_HASH = "pending-rerun";

  /// <summary>A step row's <c>status</c> while the step is owed.</summary>
  public const short PENDING = 0;

  /// <summary>A step row's <c>status</c> once the step has committed.</summary>
  public const short APPLIED = 1;

  /// <summary>A row's <c>status</c> after its step failed; still owed.</summary>
  public const short FAILED = -1;

  /// <summary>The ledger key of <paramref name="step"/> for the context named <paramref name="context"/>.</summary>
  /// <param name="context">The context whose pass the step belongs to.</param>
  /// <param name="step">The step's name.</param>
  /// <returns><c>step:&lt;context&gt;:&lt;step&gt;</c>.</returns>
  public static string StepKey(string context, string step) => $"step:{context}:{step}";

  /// <summary>Whether a step row with <paramref name="status"/> is still owed.</summary>
  /// <param name="status">The row's status.</param>
  /// <returns>True for pending and failed.</returns>
  public static bool IsOutstanding(short status) => status <= PENDING;

  /// <summary>A hash over everything a build applies, in order.</summary>
  /// <param name="parts">The build's version and the hash or text of each thing it applies.</param>
  /// <returns>The lower-case hexadecimal SHA-256 of the parts.</returns>
  public static string Fingerprint(IEnumerable<string> parts) {
    ArgumentNullException.ThrowIfNull(parts);
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var part in parts) {
      // Length-prefixed, so two lists that concatenate to the same text still differ.
      hash.AppendData(Encoding.UTF8.GetBytes($"{part.Length}:{part}\n"));
    }
    return Convert.ToHexStringLower(hash.GetHashAndReset());
  }

  /// <summary>
  /// Which step rows a pass beginning now marks pending, and which steps it runs.
  /// </summary>
  /// <param name="rows">This context's step rows, by step name: the fingerprint each carries and its status.</param>
  /// <param name="needed">The steps the ledger's hashes say this pass needs.</param>
  /// <param name="fingerprint">This build's fingerprint.</param>
  /// <returns>The rows to mark pending, and the steps to run once they are.</returns>
  public static SchemaPassSteps Plan(
      IReadOnlyDictionary<string, (string Fingerprint, short Status)> rows,
      IEnumerable<string> needed,
      string fingerprint) {
    ArgumentNullException.ThrowIfNull(rows);
    ArgumentNullException.ThrowIfNull(needed);

    var outstanding = rows.Where(r => IsOutstanding(r.Value.Status)).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
    if (rows.Any(r => IsOutstanding(r.Value.Status) && r.Value.Fingerprint == fingerprint)) {
      // A pass of this build is part way through; its rows already say what is owed.
      return new SchemaPassSteps([], outstanding);
    }

    var toMark = needed.Union(outstanding, StringComparer.Ordinal).ToList();
    return new SchemaPassSteps(toMark, toMark.ToHashSet(StringComparer.Ordinal));
  }
}

/// <summary>What <see cref="SchemaPassLedger.Plan"/> decided.</summary>
/// <docs>operations/infrastructure/migrations</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaPassLedgerTests.cs</tests>
public sealed class SchemaPassSteps {
  /// <summary>Creates the decision.</summary>
  /// <param name="toMark">The step rows to mark pending with this build's fingerprint.</param>
  /// <param name="toRun">The steps the pass runs.</param>
  public SchemaPassSteps(IReadOnlyList<string> toMark, IReadOnlySet<string> toRun) {
    ToMark = toMark;
    ToRun = toRun;
  }

  /// <summary>The step rows to mark pending with this build's fingerprint.</summary>
  public IReadOnlyList<string> ToMark { get; }

  /// <summary>The steps the pass runs.</summary>
  public IReadOnlySet<string> ToRun { get; }
}
