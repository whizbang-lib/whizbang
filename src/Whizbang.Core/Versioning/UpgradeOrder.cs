// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

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
/// never makes a newer one stand down. The library decides first and the application version decides between equal (or
/// unreadable) library versions: an older library cannot serve a schema a newer one migrated, while a newer library runs
/// an older application fine. Read separately, crossed versions (a newer library with an older application against the
/// reverse) would stand both down and take the fleet down with them; read in this order, exactly one stands down.
/// </para>
/// </remarks>
/// <docs>operations/startup/rolling-upgrades#cluster-wide-stand-down</docs>
/// <tests>tests/Whizbang.Core.Tests/Versioning/UpgradeOrderTests.cs</tests>
public static class UpgradeOrder {
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
  /// none. Of several, the one reported is a library finding over an application one, then the newest, then one online
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

  /// <summary>The finding when <paramref name="sighting"/> is strictly newer than <paramref name="own"/>, else null.</summary>
  private static NewerVersionFound? _newerThan(VersionSighting own, VersionSighting sighting) {
    var library = CompareLibrary(sighting.LibraryVersion, own.LibraryVersion);
    if (library > 0) {
      return new(VersionKind.Library, sighting.LibraryVersion!, own.LibraryVersion!, sighting.InstanceId);
    }
    if (library < 0) {
      return null;   // the library decides first: an older library is not newer, whatever its application version
    }
    return CompareApplication(sighting.ApplicationVersion, own.ApplicationVersion) > 0
      ? new(VersionKind.Application, sighting.ApplicationVersion!, own.ApplicationVersion!, sighting.InstanceId)
      : null;
  }

  /// <summary>Whether <paramref name="candidate"/> is the finding to report over <paramref name="best"/>.</summary>
  private static bool _outranks(NewerVersionFound candidate, NewerVersionFound best) {
    if (candidate.Kind != best.Kind) {
      return candidate.Kind == VersionKind.Library;
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
