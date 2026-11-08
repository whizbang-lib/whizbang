// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Versioning;

/// <summary>Which of an instance's two versions a finding is about.</summary>
/// <docs>operations/startup/rolling-upgrades#cluster-wide-stand-down</docs>
public enum VersionKind {
  /// <summary>The library (Whizbang) version, ordered by SemVer precedence.</summary>
  Library = 0,

  /// <summary>The application version, ordered by SemVer or by a host-supplied deploy number.</summary>
  Application = 1,
}

/// <summary>
/// Both versions of something that was seen (#2022): a live, serving instance (with its id), or a row of the migration
/// ledger (with no id).
/// </summary>
/// <param name="LibraryVersion">The library version, as recorded; null when none was.</param>
/// <param name="ApplicationVersion">The application version, as recorded (<see cref="Versioning.ApplicationVersion.Recorded"/>);
/// null or <c>unknown</c> when nobody knows it.</param>
/// <param name="InstanceId">The instance seen, or null for the migration ledger.</param>
/// <docs>operations/startup/rolling-upgrades#cluster-wide-stand-down</docs>
public sealed record VersionSighting(string? LibraryVersion, string? ApplicationVersion, Guid? InstanceId = null);

/// <summary>A newer version the rule found, and where it was seen (#2022).</summary>
/// <param name="Kind">Which version is newer.</param>
/// <param name="NewerVersion">The newer version, as recorded.</param>
/// <param name="OwnVersion">This instance's version of the same kind.</param>
/// <param name="InstanceId">The instance it is online on, or null when the migration ledger records it.</param>
/// <docs>operations/startup/rolling-upgrades#cluster-wide-stand-down</docs>
public sealed record NewerVersionFound(VersionKind Kind, string NewerVersion, string OwnVersion, Guid? InstanceId) {
  /// <summary>Says what was found, for the stand-down reason recorded on the instance's row and logged.</summary>
  /// <returns>The reason.</returns>
  public string Describe() {
    var (kind, own) = Kind == VersionKind.Library ? ("library", "this binary's") : ("application", "this instance's");
    var where = InstanceId is { } id ? $"instance {id} is online with" : "the ledger records";
    return $"{where} {kind} version {NewerVersion}, newer than {own} {OwnVersion}";
  }
}

/// <summary>Who owns a schema change the migration ledger records (#2022).</summary>
/// <docs>operations/startup/rolling-upgrades#the-rollback-gate</docs>
public enum SchemaChangeKind {
  /// <summary>A W! SQL migration: the library's, judged by the library version alone.</summary>
  Platform = 0,

  /// <summary>An application schema change (a perspective table, an association, an application object): the library
  /// generates it from the application's model, so it is judged by both versions.</summary>
  Application = 1,
}

/// <summary>A schema change the migration ledger records, and the versions that applied it (#2022).</summary>
/// <param name="Name">The ledger's name for it (a migration file, a perspective table, an object).</param>
/// <param name="Kind">Whose change it is.</param>
/// <param name="LibraryVersion">The library version that applied it; null when unknown.</param>
/// <param name="ApplicationVersion">The application version that applied it, as recorded; null or <c>unknown</c> when unknown.</param>
/// <docs>operations/startup/rolling-upgrades#the-rollback-gate</docs>
public sealed record AppliedSchemaChange(string Name, SchemaChangeKind Kind, string? LibraryVersion, string? ApplicationVersion);

/// <summary>
/// The one rule that orders versions (#2022). It decides whether a newer version is recorded (in the migration ledger) or
/// online (on a live, serving instance), and it is the only place a version is compared: startup's Assess and the
/// running instance's re-assessment (the same verdict), the newer instance's announcement, the standby handshake and the
/// migration guard all ask it. The coordination source guard fails a comparison anywhere else.
/// </summary>
/// <remarks>
/// <para>
/// Both versions drive it (owner decisions on #2022). The library version orders by SemVer precedence. The application
/// version orders by SemVer, or by a host-supplied monotonic deploy number, and only like with like: an unknown
/// application version never orders against anything, and a SemVer version and a deploy number say nothing about each
/// other (a mixed scheme is treated as unknown for that comparison, since no principled order between them exists).
/// </para>
/// <para>
/// Only a strictly newer sighting is found, so ordering is one-way: an equal version does nothing and an older instance
/// never makes a newer one stand down. The application version decides first (owner decision, #2022: the newest
/// application has priority), and the library decides when the application versions are equal, unknown or of different
/// schemes. Read separately, crossed versions (a newer application on an older library against the reverse) would
/// stand both down and take the fleet with them; read in this order, the older application stands down. Whether the
/// newer application may serve a schema its library did not create is the rollback gate's question
/// (<see cref="AppliedSince"/>), not this order's.
/// </para>
/// <para>
/// The rollback gate (#2022) asks of each schema change the ledger records whether it was applied after this instance's
/// version: a W! migration by a newer library, or an application schema change (a perspective table, an association,
/// an application object, which the library generates from the application's model) by a newer application or a newer
/// library. A migration cannot be reversed yet (#2122), so an instance with anything applied since stands down.
/// </para>
/// </remarks>
/// <docs>operations/startup/rolling-upgrades#cluster-wide-stand-down</docs>
/// <tests>tests/Whizbang.Core.Tests/Versioning/UpgradeOrderTests.cs</tests>
public static class UpgradeOrder {
  /// <summary>
  /// The configuration key of the incompatibility override (owner decision, #2022): <c>true</c> lets an instance start
  /// when the verdict would stand it down for an incompatibility (the schema newer than this build, or a change it
  /// cannot reverse applied since its version). Off by default. It never lifts the stand-down for a newer version
  /// online, and it never runs a migration: the instance serves the schema as it is.
  /// </summary>
  public const string OVERRIDE_INCOMPATIBILITY_KEY = "Whizbang:Startup:OverrideIncompatibility";

  /// <summary>Whether the host turned the incompatibility override on (#2022).</summary>
  /// <param name="configuration">The host's configuration; null reads none.</param>
  /// <returns><see langword="true"/> only when the key says <c>true</c>.</returns>
  /// <exception cref="InvalidOperationException">The key holds something that is not a boolean: a host that says so
  /// explicitly is told at once, never read as off.</exception>
  public static bool IsIncompatibilityOverridden(IConfiguration? configuration) {
    var value = configuration?[OVERRIDE_INCOMPATIBILITY_KEY];
    if (string.IsNullOrWhiteSpace(value)) {
      return false;
    }
    return bool.TryParse(value, out var on)
      ? on
      : throw new InvalidOperationException($"{OVERRIDE_INCOMPATIBILITY_KEY} is '{value}', which is not true or false.");
  }

  /// <summary>Whether a library version reads as SemVer.</summary>
  /// <param name="libraryVersion">The version.</param>
  /// <returns>True when it can be ordered.</returns>
  public static bool IsReadable(string? libraryVersion) => SemanticVersion.TryParse(libraryVersion, out _);

  /// <summary>Orders two library versions by SemVer precedence: negative, zero or positive; null when either is unreadable.</summary>
  /// <param name="left">The first version.</param>
  /// <param name="right">The second version.</param>
  /// <returns>The order, or null.</returns>
  public static int? CompareLibrary(string? left, string? right) =>
    SemanticVersion.TryParse(left, out var l) && SemanticVersion.TryParse(right, out var r) ? Math.Sign(l.CompareTo(r)) : null;

  /// <summary>Orders two recorded application versions, like with like; null when either is unknown or the schemes differ.</summary>
  /// <param name="left">The first version, as recorded.</param>
  /// <param name="right">The second version, as recorded.</param>
  /// <returns>The order, or null.</returns>
  public static int? CompareApplication(string? left, string? right) =>
    ApplicationVersion.FromRecorded(left).CompareTo(ApplicationVersion.FromRecorded(right));

  /// <summary>The newest readable library version among <paramref name="versions"/>, or null when none reads.</summary>
  /// <param name="versions">The versions.</param>
  /// <returns>The newest, as given.</returns>
  public static string? NewestLibrary(IEnumerable<string?> versions) {
    ArgumentNullException.ThrowIfNull(versions);
    return versions.Where(IsReadable)
      .Aggregate((string?)null, (newest, version) => newest is null || CompareLibrary(version, newest) > 0 ? version : newest);
  }

  /// <summary>
  /// Finds a version newer than this instance's among what was seen: the reason to stand down, or null when there is
  /// none. Of several, the one reported is an application finding over a library one, then the newest, then one online
  /// over the ledger, then the lowest instance id, so the reason does not depend on the order things were read in.
  /// </summary>
  /// <param name="own">This instance's versions and id; a sighting with the same id is its own row and is skipped.</param>
  /// <param name="seen">The ledger's rows and the live, serving instances.</param>
  /// <returns>The finding, or null.</returns>
  public static NewerVersionFound? FindNewer(VersionSighting own, IEnumerable<VersionSighting> seen) {
    ArgumentNullException.ThrowIfNull(own);
    ArgumentNullException.ThrowIfNull(seen);
    // An instance's own row is never a sighting: a null id (the ledger) is never equal to it.
    return seen
      .Where(s => own.InstanceId is null || s.InstanceId != own.InstanceId)
      .Select(s => _newerThan(own, s))
      .OfType<NewerVersionFound>()
      .Aggregate((NewerVersionFound?)null, (best, found) => best is null || _outranks(found, best) ? found : best);
  }

  /// <summary>
  /// The same rule asked from the newer side: every sighting that would find <paramref name="own"/> newer than itself.
  /// A newer instance announces itself only when one of these is online.
  /// </summary>
  /// <param name="own">This instance's versions and id.</param>
  /// <param name="seen">The live, serving instances.</param>
  /// <returns>The older ones.</returns>
  public static IReadOnlyList<VersionSighting> OlderThan(VersionSighting own, IEnumerable<VersionSighting> seen) {
    ArgumentNullException.ThrowIfNull(own);
    ArgumentNullException.ThrowIfNull(seen);
    return [.. seen.Where(peer => FindNewer(peer, [own]) is not null)];
  }

  /// <summary>
  /// The rollback gate (#2022): every schema change applied after this instance's version. A W! migration counts when
  /// a newer library applied it; an application schema change counts when a newer application or a newer library did.
  /// A change whose applier is unknown never counts.
  /// </summary>
  /// <param name="own">This instance's versions.</param>
  /// <param name="changes">The changes the ledger records, with who applied each.</param>
  /// <returns>The changes applied since, in the order given.</returns>
  public static IReadOnlyList<AppliedSchemaChange> AppliedSince(VersionSighting own, IEnumerable<AppliedSchemaChange> changes) {
    ArgumentNullException.ThrowIfNull(own);
    ArgumentNullException.ThrowIfNull(changes);
    return [.. changes.Where(change => CompareLibrary(change.LibraryVersion, own.LibraryVersion) > 0
      || (change.Kind == SchemaChangeKind.Application
          && CompareApplication(change.ApplicationVersion, own.ApplicationVersion) > 0))];
  }

  /// <summary>
  /// The gate's error (#2022): this instance's versions, how many changes were applied since, the first five by name with
  /// who applied each, and that a migration cannot be reversed yet (#2122).
  /// </summary>
  /// <param name="own">This instance's versions.</param>
  /// <param name="since">The changes applied since (<see cref="AppliedSince"/>).</param>
  /// <returns>The error text.</returns>
  public static string DescribeAppliedSince(VersionSighting own, IReadOnlyList<AppliedSchemaChange> since) {
    ArgumentNullException.ThrowIfNull(own);
    ArgumentNullException.ThrowIfNull(since);
    const int NAMED = 5;
    var named = string.Join(", ", since.Take(NAMED).Select(c =>
      $"{c.Name} (applied by library {_text(c.LibraryVersion)}, application {_text(c.ApplicationVersion)})"));
    var more = since.Count > NAMED ? $", and {since.Count - NAMED} more" : string.Empty;
    return $"since this instance's version (library {_text(own.LibraryVersion)}, application {_text(own.ApplicationVersion)}), "
      + $"{since.Count} schema change(s) were applied by a newer version that this version did not make, and a migration "
      + $"cannot be reversed yet (down migrations: whizbang-lib/whizbang#2122): {named}{more}";
  }

  private static string _text(string? version) => string.IsNullOrWhiteSpace(version) ? "unknown" : version;

  /// <summary>The finding when <paramref name="sighting"/> is strictly newer than <paramref name="own"/>, else null.</summary>
  private static NewerVersionFound? _newerThan(VersionSighting own, VersionSighting sighting) {
    var application = CompareApplication(sighting.ApplicationVersion, own.ApplicationVersion);
    if (application > 0) {
      return new(VersionKind.Application, sighting.ApplicationVersion!, own.ApplicationVersion!, sighting.InstanceId);
    }
    if (application < 0) {
      return null;   // the application decides first: an older application is not newer, whatever its library version
    }
    return CompareLibrary(sighting.LibraryVersion, own.LibraryVersion) > 0
      ? new(VersionKind.Library, sighting.LibraryVersion!, own.LibraryVersion!, sighting.InstanceId)
      : null;
  }

  /// <summary>Whether <paramref name="candidate"/> is the finding to report over <paramref name="best"/>.</summary>
  private static bool _outranks(NewerVersionFound candidate, NewerVersionFound best) {
    if (candidate.Kind != best.Kind) {
      return candidate.Kind == VersionKind.Application;
    }
    // Two findings of one kind are both newer than this instance's version, so they share its scheme and always order.
    var newer = (candidate.Kind == VersionKind.Library
      ? CompareLibrary(candidate.NewerVersion, best.NewerVersion)
      : CompareApplication(candidate.NewerVersion, best.NewerVersion)).GetValueOrDefault();
    if (newer != 0) {
      return newer > 0;
    }
    if (candidate.InstanceId is not { } id) {
      return false;   // the ledger never outranks an equal finding
    }
    return best.InstanceId is not { } bestId || id.CompareTo(bestId) < 0;
  }
}
