// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Startup;

#pragma warning disable S2325, CA1822, IDE0060 // RED stub, replaced by the implementation

/// <summary>
/// Where a unit of heavy startup work has its effect, and so how often the fleet runs it (#2596).
/// </summary>
/// <remarks>
/// There is no default value on purpose: a declaration states its scope, and <see cref="StartupWorkDeclaration"/>
/// refuses a value that is neither, so work cannot be declared without one.
/// </remarks>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Startup/StartupWorkCatalogTests.cs</tests>
public enum StartupWorkScope {
  /// <summary>
  /// The work changes this instance, or records something about this instance (the managed-object reconcile reports
  /// what this instance declares). Every instance runs it at every start, within the fleet's heavy-start cap.
  /// </summary>
  PerInstance = 1,

  /// <summary>
  /// The work changes state the whole schema shares, the same way from any instance of one build. The first instance of
  /// a build to start runs it, once per schema; the others skip it while that instance runs it and after it finished.
  /// A run that fails releases its claim, so the next start runs it again.
  /// </summary>
  PerSchemaPerBuild = 2,
}

/// <summary>One declared unit of heavy startup work: its stable name and its scope (#2596).</summary>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Startup/StartupWorkCatalogTests.cs</tests>
public sealed record StartupWorkDeclaration {
  /// <summary>Declares <paramref name="name"/> with <paramref name="scope"/>.</summary>
  /// <param name="name">The stable name; it keys the once-per-build record and appears in logs.</param>
  /// <param name="scope">Where the work has its effect.</param>
  /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="scope"/> is not a defined scope.</exception>
  public StartupWorkDeclaration(string name, StartupWorkScope scope) {
    throw new NotImplementedException();
  }

  /// <summary>The stable name.</summary>
  public string Name { get; }

  /// <summary>Where the work has its effect.</summary>
  public StartupWorkScope Scope { get; }
}

/// <summary>
/// The heavy work every start runs in the Converge stage, declared once (#2596). Both drivers run each of these only
/// through a <see cref="StartupWorkSession"/>, which refuses a name declared nowhere, so the fleet-wide cap and the
/// once-per-build rule cannot be bypassed by a call that forgot them.
/// </summary>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Startup/StartupWorkCatalogTests.cs</tests>
public static class FrameworkStartupWork {
#pragma warning disable CA1707 // SCREAMING_CASE constants are the established convention here
  /// <summary>Statistics for each table the schema pass indexed in this start. Per instance: only its own pass queues tables.</summary>
  public const string ANALYZE_NEW_INDEXES = "analyze-new-indexes";

  /// <summary>
  /// The managed-object reconcile. Per instance: each run records what this instance declares, which the drop gate
  /// reads to keep an object while any live instance still declares it.
  /// </summary>
  public const string MANAGED_OBJECT_RECONCILE = "managed-object-reconcile";

  /// <summary>The perspective registry checked against this build and reconciled when it differs. Per schema per build.</summary>
  public const string PERSPECTIVE_REGISTRY = "perspective-registry";

  /// <summary>Each perspective table's fillfactor and autovacuum settings brought in line with its model. Per schema per build.</summary>
  public const string PERSPECTIVE_STORAGE_SETTINGS = "perspective-storage-settings";
#pragma warning restore CA1707

  /// <summary>Every framework declaration.</summary>
  public static IReadOnlyList<StartupWorkDeclaration> Declarations { get; } = [];

  /// <summary>The declaration named <paramref name="name"/>, or null when nothing declares it.</summary>
  /// <param name="name">The name.</param>
  /// <returns>The declaration, or null.</returns>
  public static StartupWorkDeclaration? Find(string name) => throw new NotImplementedException();
}

/// <summary>What the database answered to a claim on per-schema-per-build work (#2596).</summary>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
public enum StartupWorkClaim {
  /// <summary>This instance holds the claim and runs the work.</summary>
  Claimed,

  /// <summary>The work last ran to completion for this build; nothing is owed.</summary>
  Done,

  /// <summary>A live instance of this build holds the claim and is running it.</summary>
  RunningElsewhere,
}

/// <summary>What the database answered to a request for a heavy-start permit (#2596).</summary>
/// <param name="Granted">Whether this instance now holds a permit.</param>
/// <param name="Held">How many permits are held, this instance's included when it was granted one.</param>
/// <docs>operations/startup/startup-pipeline#the-heavy-start-cap</docs>
public readonly record struct StartupPermitAnswer(bool Granted, int Held);

/// <summary>
/// The database's side of heavy startup work (#2596): the once-per-build record and the fleet's heavy-start permits.
/// Every driver uses the same functions, so a mixed-version fleet never runs two rules.
/// </summary>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
public interface IStartupWorkLedger {
  /// <summary>Whether the schema has the functions; a schema migrated by an older release does not.</summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>True when the ledger can be used.</returns>
  Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

  /// <summary>Claims <paramref name="work"/> for <paramref name="fingerprint"/> on behalf of <paramref name="instanceId"/>.</summary>
  /// <param name="work">The declared name.</param>
  /// <param name="fingerprint">This build's fingerprint of the work.</param>
  /// <param name="instanceId">This instance.</param>
  /// <param name="force">Claim even when the work is done for this build: this start's own schema pass applied something.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The answer.</returns>
  Task<StartupWorkClaim> ClaimAsync(string work, string fingerprint, Guid instanceId, bool force, CancellationToken cancellationToken);

  /// <summary>Ends this instance's claim: done when it succeeded, released for the next start otherwise.</summary>
  /// <param name="work">The declared name.</param>
  /// <param name="fingerprint">The fingerprint it was claimed for.</param>
  /// <param name="instanceId">This instance.</param>
  /// <param name="succeeded">Whether the work completed.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes once the claim is ended.</returns>
  Task CompleteAsync(string work, string fingerprint, Guid instanceId, bool succeeded, CancellationToken cancellationToken);

  /// <summary>Asks for one of <paramref name="cap"/> heavy-start permits for <paramref name="instanceId"/>.</summary>
  /// <param name="instanceId">This instance.</param>
  /// <param name="cap">How many instances may hold a permit at once.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The answer.</returns>
  Task<StartupPermitAnswer> TryAcquirePermitAsync(Guid instanceId, int cap, CancellationToken cancellationToken);

  /// <summary>Gives this instance's permit back.</summary>
  /// <param name="instanceId">This instance.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes once the permit is released.</returns>
  Task ReleasePermitAsync(Guid instanceId, CancellationToken cancellationToken);
}

/// <summary>How one declared unit of startup work ended in a <see cref="StartupWorkSession"/> (#2596).</summary>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
public enum StartupWorkOutcome {
  /// <summary>This instance ran it.</summary>
  Ran,

  /// <summary>It already ran to completion for this build; this instance skipped it.</summary>
  SkippedDone,

  /// <summary>A live instance of this build is running it; this instance skipped it.</summary>
  SkippedRunningElsewhere,
}
