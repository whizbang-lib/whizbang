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

/// <summary>How a schema change is reversed (#2122, owner decision 2), as the version that applied it recorded.</summary>
/// <docs>operations/infrastructure/migrations#how-a-migration-is-reversed</docs>
public enum SchemaChangeReversal {
  /// <summary>It cannot be reversed (a data rewrite, for example), or nothing recorded how: every change before #2122,
  /// and every stored-form conversion. A rollback across it always stands down.</summary>
  Irreversible = 0,

  /// <summary>Reversed without losing anything (an added index, an added table with no data yet).</summary>
  Reversible = 1,

  /// <summary>Reversed by discarding data the newer version wrote (dropping a column or a table it added); a rollback
  /// across it runs only when the operator allows it (<see cref="UpgradeOrder.ALLOW_LOSSY_ROLLBACK_KEY"/>).</summary>
  Lossy = 2,
}

/// <summary>A schema change the migration ledger records, and the versions that applied it (#2022).</summary>
/// <param name="Name">The ledger's name for it (a migration file, a perspective table, an object).</param>
/// <param name="Kind">Whose change it is.</param>
/// <param name="LibraryVersion">The library version that applied it; null when unknown.</param>
/// <param name="ApplicationVersion">The application version that applied it, as recorded; null or <c>unknown</c> when unknown.</param>
/// <docs>operations/startup/rolling-upgrades#the-rollback-gate</docs>
public sealed record AppliedSchemaChange(string Name, SchemaChangeKind Kind, string? LibraryVersion, string? ApplicationVersion) {
  /// <summary>How the change is reversed, as recorded when it was applied (#2122); irreversible when nothing says.</summary>
  /// <docs>operations/infrastructure/migrations#how-a-migration-is-reversed</docs>
  public SchemaChangeReversal Reversal { get; init; }

  /// <summary>
  /// The content hash the ledger recorded for the change (#2553): what was applied, which the rollback gate compares
  /// with what this build defines under the same name. Null when the ledger recorded none (a stored-form or temporal-form
  /// conversion, which its journal records without one), and the change is then judged by who applied it.
  /// </summary>
  /// <docs>operations/startup/rolling-upgrades#the-rollback-gate</docs>
  public string? ContentHash { get; init; }

  /// <summary>
  /// Whether the change's name alone identifies what was applied when no content hash was recorded (#2553): a
  /// stored-form or temporal-form conversion recorded before its journal held hashes (568), whose name is the conversion
  /// form. Such a row counts as current when this build declares a conversion under the same name. Never for a row of the
  /// migration ledger, which always records its hash.
  /// </summary>
  /// <docs>operations/startup/rolling-upgrades#the-rollback-gate</docs>
  public bool NameIdentifiesContent { get; init; }
}

/// <summary>
/// The operator's overrides of the rollback gate for each side (#2553, owner decision 2): the W! migrations and the
/// application's schema are judged apart, so an operator can serve one side as it is, or let one side's rollback discard
/// data, without doing the same to the other. Read by <see cref="UpgradeOrder.ReadOverrides"/>, where the shared keys
/// still mean both sides.
/// </summary>
/// <param name="OverridePlatform">Serve the W! migrations as they are when they would stand down.</param>
/// <param name="OverrideApplication">Serve the application's schema as it is when it would stand down.</param>
/// <param name="AllowLossyPlatform">Let a rollback of the W! migrations discard data a newer version wrote.</param>
/// <param name="AllowLossyApplication">Let a rollback of the application's schema discard data a newer version wrote.</param>
/// <docs>operations/startup/rolling-upgrades#the-incompatibility-override</docs>
/// <tests>tests/Whizbang.Core.Tests/Versioning/UpgradeOrderTests.cs:TheOverrides_AreScopedPerSide_AndTheSharedKeysStillMeanBothAsync</tests>
public sealed record SchemaGateOverrides(bool OverridePlatform, bool OverrideApplication, bool AllowLossyPlatform, bool AllowLossyApplication) {
  /// <summary>No override on either side: the default.</summary>
  public static SchemaGateOverrides None { get; } = new(false, false, false, false);

  /// <summary>The same overrides on both sides, as the shared keys set them.</summary>
  /// <param name="overrideIncompatibility">Serve each side as it is when it would stand down.</param>
  /// <param name="allowLossyRollback">Let each side's rollback discard data.</param>
  /// <returns>The overrides.</returns>
  public static SchemaGateOverrides Both(bool overrideIncompatibility, bool allowLossyRollback) =>
    new(overrideIncompatibility, overrideIncompatibility, allowLossyRollback, allowLossyRollback);

  /// <summary>Whether <paramref name="side"/> is served as it is when it would stand down.</summary>
  /// <param name="side">The side.</param>
  /// <returns>The side's override.</returns>
  public bool Overrides(SchemaChangeKind side) => side == SchemaChangeKind.Platform ? OverridePlatform : OverrideApplication;

  /// <summary>Whether a rollback of <paramref name="side"/> may discard data.</summary>
  /// <param name="side">The side.</param>
  /// <returns>The side's setting.</returns>
  public bool AllowsLossy(SchemaChangeKind side) => side == SchemaChangeKind.Platform ? AllowLossyPlatform : AllowLossyApplication;
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
/// library. Anything applied since is rolled back when every change can be reversed (#2122): the changes and how each
/// is reversed are what the ledger recorded when each was applied (<see cref="WhyNotReversible"/>).
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

  /// <summary>
  /// The configuration key that lets a rollback discard data (#2122, owner decision 3): <c>true</c> lets an older version
  /// roll back changes whose reversal discards data a newer version wrote. Off by default, so such a rollback stands
  /// down with an error naming each change; it never lets an irreversible change be rolled back.
  /// </summary>
  public const string ALLOW_LOSSY_ROLLBACK_KEY = "Whizbang:Startup:AllowLossyRollback";

  /// <summary>
  /// The incompatibility override for the W! migrations alone (#2553): <c>true</c> serves the schema as it is when the
  /// W! migrations would stand down, <c>false</c> refuses even when <see cref="OVERRIDE_INCOMPATIBILITY_KEY"/> is on.
  /// Unset, the shared key decides for both sides.
  /// </summary>
  /// <docs>operations/startup/rolling-upgrades#the-incompatibility-override</docs>
  public const string PLATFORM_OVERRIDE_INCOMPATIBILITY_KEY = "Whizbang:Startup:Platform:OverrideIncompatibility";

  /// <summary>
  /// The incompatibility override for the application's schema alone (#2553): <c>true</c> serves the application's schema
  /// as it is when it would stand down, while the W! migrations are still judged and run on their own. Unset, the
  /// shared key (<see cref="OVERRIDE_INCOMPATIBILITY_KEY"/>) decides.
  /// </summary>
  /// <docs>operations/startup/rolling-upgrades#the-incompatibility-override</docs>
  public const string APPLICATION_OVERRIDE_INCOMPATIBILITY_KEY = "Whizbang:Startup:Application:OverrideIncompatibility";

  /// <summary>
  /// Whether a rollback of the W! migrations may discard data (#2553). Unset, the shared key
  /// (<see cref="ALLOW_LOSSY_ROLLBACK_KEY"/>) decides for both sides.
  /// </summary>
  /// <docs>operations/startup/rolling-upgrades#rolling-back</docs>
  public const string PLATFORM_ALLOW_LOSSY_ROLLBACK_KEY = "Whizbang:Startup:Platform:AllowLossyRollback";

  /// <summary>
  /// Whether a rollback of the application's schema may discard data (#2553). Unset, the shared key
  /// (<see cref="ALLOW_LOSSY_ROLLBACK_KEY"/>) decides for both sides.
  /// </summary>
  /// <docs>operations/startup/rolling-upgrades#rolling-back</docs>
  public const string APPLICATION_ALLOW_LOSSY_ROLLBACK_KEY = "Whizbang:Startup:Application:AllowLossyRollback";

  /// <summary>
  /// The operator's overrides of the gate, for each side (#2553): a side's own key when it is set, else the shared key,
  /// so the shared keys keep meaning both sides.
  /// </summary>
  /// <param name="configuration">The host's configuration; null reads none.</param>
  /// <returns>The overrides.</returns>
  /// <exception cref="InvalidOperationException">A key holds something that is not a boolean.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Versioning/UpgradeOrderTests.cs:TheOverrides_AreScopedPerSide_AndTheSharedKeysStillMeanBothAsync</tests>
  public static SchemaGateOverrides ReadOverrides(IConfiguration? configuration) {
    var overrideBoth = IsIncompatibilityOverridden(configuration);
    var lossyBoth = IsLossyRollbackAllowed(configuration);
    return new SchemaGateOverrides(
      _sideOr(configuration, PLATFORM_OVERRIDE_INCOMPATIBILITY_KEY, overrideBoth),
      _sideOr(configuration, APPLICATION_OVERRIDE_INCOMPATIBILITY_KEY, overrideBoth),
      _sideOr(configuration, PLATFORM_ALLOW_LOSSY_ROLLBACK_KEY, lossyBoth),
      _sideOr(configuration, APPLICATION_ALLOW_LOSSY_ROLLBACK_KEY, lossyBoth));
  }

  /// <summary>A side's own key when it is set, else the shared key's value.</summary>
  private static bool _sideOr(IConfiguration? configuration, string key, bool shared) =>
    string.IsNullOrWhiteSpace(configuration?[key]) ? shared : _isOn(configuration, key);

  /// <summary>The incompatibility override key that serves <paramref name="side"/> as it is.</summary>
  /// <param name="side">The side.</param>
  /// <returns>The side's own key.</returns>
  public static string OverrideKeyFor(SchemaChangeKind side) =>
    side == SchemaChangeKind.Platform ? PLATFORM_OVERRIDE_INCOMPATIBILITY_KEY : APPLICATION_OVERRIDE_INCOMPATIBILITY_KEY;

  /// <summary>Whether the host turned the incompatibility override on (#2022).</summary>
  /// <param name="configuration">The host's configuration; null reads none.</param>
  /// <returns><see langword="true"/> only when the key says <c>true</c>.</returns>
  /// <exception cref="InvalidOperationException">The key holds something that is not a boolean: a host that says so
  /// explicitly is told at once, never read as off.</exception>
  public static bool IsIncompatibilityOverridden(IConfiguration? configuration) => _isOn(configuration, OVERRIDE_INCOMPATIBILITY_KEY);

  /// <summary>Whether the host lets a rollback discard data (#2122, <see cref="ALLOW_LOSSY_ROLLBACK_KEY"/>).</summary>
  /// <param name="configuration">The host's configuration; null reads none.</param>
  /// <returns><see langword="true"/> only when the key says <c>true</c>.</returns>
  /// <exception cref="InvalidOperationException">The key holds something that is not a boolean.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Versioning/UpgradeOrderTests.cs:TheLossyRollbackKey_IsOffUnlessItSaysTrue_AndRefusedLoudlyWhenItIsNotABooleanAsync</tests>
  public static bool IsLossyRollbackAllowed(IConfiguration? configuration) => _isOn(configuration, ALLOW_LOSSY_ROLLBACK_KEY);

  private static bool _isOn(IConfiguration? configuration, string key) {
    var value = configuration?[key];
    if (string.IsNullOrWhiteSpace(value)) {
      return false;
    }
    return bool.TryParse(value, out var on)
      ? on
      : throw new InvalidOperationException($"{key} is '{value}', which is not true or false.");
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
    // An instance's own row is never a sighting: a null id (the ledger) is never equal to it. Spelled out rather than
    // as a lifted comparison of two nullable ids, whose both-null outcome cannot happen once this instance has an id.
    return seen
      .Where(s => own.InstanceId is not { } ownId || s.InstanceId is not { } theirs || theirs != ownId)
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
  /// The rollback gate (#2022): every schema change applied after this instance's version, judged by what was applied
  /// (#2553, owner decision 1). A change this build defines identically (the same name, recorded with the same content
  /// hash) is current, whatever version applied it: a database restored from another environment records that
  /// environment's versions, which say nothing about whether this build makes the same change. Only a change this build
  /// does not define that way (a different hash, a name it does not produce, or no recorded hash) is judged by who
  /// applied it: a W! migration counts when a newer library applied it; an application schema change counts when a newer
  /// application or a newer library did. A change whose applier is unknown never counts.
  /// </summary>
  /// <param name="own">This instance's versions.</param>
  /// <param name="changes">The changes the ledger records, with who applied each.</param>
  /// <param name="defined">What this build defines (#2553): each change it produces, by the ledger's name for it, with
  /// its content hash; null when nothing is known.</param>
  /// <returns>The changes applied since, in the order given.</returns>
  public static IReadOnlyList<AppliedSchemaChange> AppliedSince(
      VersionSighting own, IEnumerable<AppliedSchemaChange> changes, IReadOnlyDictionary<string, string>? defined) {
    ArgumentNullException.ThrowIfNull(own);
    ArgumentNullException.ThrowIfNull(changes);
    return [.. changes.Where(change => !IsDefinedIdentically(change, defined)
      && (CompareLibrary(change.LibraryVersion, own.LibraryVersion) > 0
      || (change.Kind == SchemaChangeKind.Application
          && CompareApplication(change.ApplicationVersion, own.ApplicationVersion) > 0)))];
  }

  /// <summary>
  /// Whether this build makes exactly the change the ledger recorded (#2553): the same name, with the same content hash.
  /// A row with no recorded hash matches only when its name identifies its content (a conversion recorded before its
  /// journal held hashes) and this build declares that name; any other is judged by who applied it, as before.
  /// </summary>
  /// <param name="change">The recorded change.</param>
  /// <param name="defined">What this build defines, by name, with each content hash; null when nothing is known.</param>
  /// <returns><see langword="true"/> when the recorded hash is the one this build produces under that name.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Versioning/UpgradeOrderTests.cs:AChangeThisBuildDefinesIdentically_IsCurrent_WhateverVersionAppliedItAsync</tests>
  public static bool IsDefinedIdentically(AppliedSchemaChange change, IReadOnlyDictionary<string, string>? defined) {
    ArgumentNullException.ThrowIfNull(change);
    if (defined is null || !defined.TryGetValue(change.Name, out var mine)) {
      return false;
    }
    // A recorded hash decides; a conversion recorded before its journal held one is identified by its name.
    return change.ContentHash is { } recorded
      ? string.Equals(mine, recorded, StringComparison.Ordinal)
      : change.NameIdentifiesContent;
  }

  /// <summary>
  /// Why the changes applied since this instance's version cannot be rolled back (#2122, owner decision 3), or null
  /// when they can. Any irreversible change refuses, naming each one and who applied it; otherwise any change whose
  /// reversal discards data refuses unless <paramref name="allowLossy"/>.
  /// </summary>
  /// <param name="own">This instance's versions.</param>
  /// <param name="since">The changes applied since (<see cref="AppliedSince"/>).</param>
  /// <param name="allowLossy">Whether the operator lets a rollback discard data (<see cref="ALLOW_LOSSY_ROLLBACK_KEY"/>).</param>
  /// <returns>The refusal, or null.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Versioning/UpgradeOrderTests.cs:AnIrreversibleChange_RefusesTheRollback_NamingOnlyWhatCannotBeReversed_AndTheVersionsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Versioning/UpgradeOrderTests.cs:ALossyChange_RefusesTheRollback_UnlessTheOperatorAllowsItAsync</tests>
  public static string? WhyNotReversible(VersionSighting own, IReadOnlyList<AppliedSchemaChange> since, bool allowLossy) {
    ArgumentNullException.ThrowIfNull(own);
    ArgumentNullException.ThrowIfNull(since);
    var irreversible = since.Where(c => c.Reversal == SchemaChangeReversal.Irreversible).ToList();
    if (irreversible.Count > 0) {
      return $"{_since(own, since)}, and {irreversible.Count} cannot be reversed: {_named(irreversible)}; a rollback runs only "
        + "when every change since this version can be reversed";
    }
    var lossy = since.Where(c => c.Reversal == SchemaChangeReversal.Lossy).ToList();
    return lossy.Count == 0 || allowLossy
      ? null
      : $"{_since(own, since)}, and reversing {lossy.Count} would discard data the newer version wrote: {_named(lossy)}; set "
        + $"{ALLOW_LOSSY_ROLLBACK_KEY} to true to roll back anyway";
  }

  /// <summary>The rollback Assess decided on (#2122): this instance's versions and every change it reverses.</summary>
  /// <param name="own">This instance's versions.</param>
  /// <param name="since">The changes applied since, every one reversible.</param>
  /// <returns>The text.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Versioning/UpgradeOrderTests.cs:TheRollback_IsDescribedWithEveryChangeItReversesAsync</tests>
  public static string DescribeRollback(VersionSighting own, IReadOnlyList<AppliedSchemaChange> since) {
    ArgumentNullException.ThrowIfNull(own);
    ArgumentNullException.ThrowIfNull(since);
    return $"{_since(own, since)}, and every one can be reversed: rolling back {_named(since)}";
  }

  private static string _since(VersionSighting own, IReadOnlyList<AppliedSchemaChange> since) =>
    $"since this instance's version (library {_text(own.LibraryVersion)}, application {_text(own.ApplicationVersion)}), "
    + $"{since.Count} schema change(s) were applied by a newer version that this version did not make";

  /// <summary>One change as the gate's errors name it: its name and who applied it.</summary>
  /// <param name="change">The change.</param>
  /// <returns>For example <c>351_X.sql (applied by library 2.0.0, application unknown)</c>.</returns>
  public static string DescribeChange(AppliedSchemaChange change) {
    ArgumentNullException.ThrowIfNull(change);
    return $"{change.Name} (applied by library {_text(change.LibraryVersion)}, application {_text(change.ApplicationVersion)})";
  }

  /// <summary>The first five changes by name with who applied each, and how many more.</summary>
  private static string _named(IReadOnlyList<AppliedSchemaChange> changes) {
    const int NAMED = 5;
    var named = string.Join(", ", changes.Take(NAMED).Select(DescribeChange));
    return changes.Count > NAMED ? $"{named}, and {changes.Count - NAMED} more" : named;
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
