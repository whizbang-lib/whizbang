// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;

namespace Whizbang.Data.Postgres.Schema;

/// <summary>
/// The managed-object reconcile's settings, read from <c>Whizbang:Schema:Reconcile</c>.
/// </summary>
/// <remarks>
/// Read by hand rather than bound, so no reflection is involved and a value that is not one fails at startup
/// naming its key, instead of being ignored: a mistyped drop switch that silently allowed a drop is the one
/// failure this setting exists to prevent.
/// </remarks>
/// <param name="Mode">What the reconcile may change. <c>Whizbang:Schema:Reconcile:Mode</c>; default <see cref="ReconcileMode.Apply"/>.</param>
/// <param name="KeepKinds">The kinds never dropped, from <c>Whizbang:Schema:Reconcile:Drop:&lt;Kind&gt;=false</c>.</param>
/// <param name="Pins">Globs over <c>table:object</c> pinned by configuration, from <c>Whizbang:Schema:Reconcile:Pins</c>.</param>
/// <param name="DropAfterFleetConverged">
/// Whether a drop waits until every running instance declares the same objects.
/// <c>Whizbang:Schema:Reconcile:DropAfterFleetConverged</c>; default true.
/// </param>
/// <docs>fundamentals/perspectives/managed-schema-objects#settings</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ManagedSchemaSettingsTests.cs</tests>
public sealed record ManagedSchemaSettings(
    ReconcileMode Mode,
    IReadOnlyCollection<ManagedObjectKind> KeepKinds,
    IReadOnlyList<string> Pins,
    bool DropAfterFleetConverged) {
  /// <summary>The configuration section the settings are read from.</summary>
  public const string SECTION = "Whizbang:Schema:Reconcile";

  /// <summary>
  /// Reads the settings, or the defaults when <paramref name="configuration"/> is null or sets nothing.
  /// </summary>
  /// <exception cref="InvalidOperationException">A value is not one the key accepts.</exception>
  public static ManagedSchemaSettings Read(IConfiguration? configuration) {
    var section = configuration?.GetSection(SECTION);
    if (section is null) {
      return new ManagedSchemaSettings(ReconcileMode.Apply, [], [], DropAfterFleetConverged: true);
    }

    return new ManagedSchemaSettings(
      _mode(section["Mode"]),
      _keepKinds(section.GetSection("Drop")),
      section.GetSection("Pins").GetChildren()
        .Where(p => !string.IsNullOrWhiteSpace(p.Value))
        .Select(p => p.Value!)
        .ToList(),
      _bool(section["DropAfterFleetConverged"], "DropAfterFleetConverged") ?? true);
  }

  /// <summary>
  /// The settings one reconcile pass plans with, given what the other running instances declare (null when one
  /// has not reported). Without the fleet gate the fleet is ignored.
  /// </summary>
  public ReconcileSettings ForPass(IReadOnlySet<string>? fleetDeclared) =>
    new(Mode, KeepKinds, Pins, DropAfterFleetConverged ? fleetDeclared : new HashSet<string>(StringComparer.Ordinal));

  private static ReconcileMode _mode(string? value) {
    if (string.IsNullOrWhiteSpace(value)) {
      return ReconcileMode.Apply;
    }
    return Enum.TryParse<ReconcileMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
      ? mode
      : throw new InvalidOperationException(
        $"{SECTION}:Mode is '{value}'; it must be one of Apply, AddOnly, ReportOnly, Off.");
  }

  private static List<ManagedObjectKind> _keepKinds(IConfigurationSection drop) {
    var keep = new List<ManagedObjectKind>();
    foreach (var child in drop.GetChildren()) {
      if (!Enum.TryParse<ManagedObjectKind>(child.Key, ignoreCase: true, out var kind) || !Enum.IsDefined(kind)) {
        throw new InvalidOperationException(
          $"{SECTION}:Drop:{child.Key} names no object kind; the kinds are {string.Join(", ", Enum.GetNames<ManagedObjectKind>())}.");
      }
      if (_bool(child.Value, $"Drop:{child.Key}") == false) {
        keep.Add(kind);
      }
    }
    return keep;
  }

  private static bool? _bool(string? value, string key) {
    if (string.IsNullOrWhiteSpace(value)) {
      return null;
    }
    return bool.TryParse(value, out var parsed)
      ? parsed
      : throw new InvalidOperationException($"{SECTION}:{key} is '{value}'; it must be true or false.");
  }
}
