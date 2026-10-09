// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.ComponentModel;

namespace Whizbang.Data.Postgres.Schema;

/// <summary>The kinds of database object Whizbang manages for perspective tables.</summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
public enum ManagedObjectKind {
  /// <summary>An index of any access method.</summary>
  Index,
  /// <summary>A check, unique, exclusion or foreign-key constraint.</summary>
  Constraint,
  /// <summary>A trigger, together with the trigger function Whizbang created for it.</summary>
  Trigger,
  /// <summary>A function Whizbang generated for one perspective.</summary>
  Function,
  /// <summary>Extended statistics (<c>CREATE STATISTICS</c>).</summary>
  Statistics,
  /// <summary>A view or materialized view.</summary>
  View,
  /// <summary>A column. Holds data, so it is never dropped automatically.</summary>
  Column,
  /// <summary>A perspective table. Holds data, so it is never dropped automatically.</summary>
  Table,
  /// <summary>A row-level security policy. A security object, so it is never dropped automatically.</summary>
  Policy,
}

/// <summary>Who created a managed object.</summary>
public static class ManagedOwners {
  /// <summary>Whizbang created it; it is dropped when no longer declared and not pinned.</summary>
  public const string WHIZBANG = "whizbang";

  /// <summary>Something else created it; Whizbang records it and never drops it.</summary>
  public const string FOREIGN = "foreign";
}

/// <summary>Where a managed object stands.</summary>
public static class ManagedStatuses {
  /// <summary>Declared, or foreign: present and expected.</summary>
  public const string ACTIVE = "active";

  /// <summary>Whizbang's and no longer declared: dropped once the rules allow.</summary>
  public const string PENDING_RETIREMENT = "pending-retirement";

  /// <summary>Dropped by Whizbang.</summary>
  public const string RETIRED = "retired";
}

/// <summary>Where a pin came from.</summary>
public static class PinSources {
  /// <summary><c>[KeepSchemaObject]</c> or a code registration; applied at every start.</summary>
  public const string CODE = "code";

  /// <summary>The <c>Whizbang:Schema:Reconcile:Pins</c> setting; applied at every start.</summary>
  public const string CONFIG = "config";

  /// <summary>A <c>whizbang:pin</c> comment on the object.</summary>
  public const string DB_COMMENT = "db-comment";

  /// <summary><c>wh_pin_object</c>.</summary>
  public const string SQL = "sql";

  /// <summary><c>whizbang schema pin</c>.</summary>
  public const string CLI = "cli";
}

/// <summary>One object a model or a Whizbang feature declares.</summary>
/// <param name="Table">The perspective table it belongs to.</param>
/// <param name="Name">The object's name, as Whizbang creates it.</param>
/// <param name="Kind">What kind of object it is.</param>
/// <param name="DeclaredBy">What asked for it, for the report (e.g. <c>JobModel.Status [Indexed]</c>).</param>
public sealed record DeclaredSchemaObject(string Table, string Name, ManagedObjectKind Kind, string DeclaredBy);

/// <summary>A pin declared in code.</summary>
/// <param name="Table">The table the pinned object belongs to.</param>
/// <param name="Name">The pinned object's name.</param>
/// <param name="Reason">Why it is kept.</param>
/// <param name="DeclaredBy">Where the pin is declared, for the report.</param>
public sealed record DeclaredPin(string Table, string Name, string? Reason, string DeclaredBy);

/// <summary>
/// The objects declared by everything in Whizbang that creates database objects. Contributors add to it; the
/// reconcile reads only it.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class ManagedSchemaObjectSet {
  private readonly Dictionary<(string Table, string Name), DeclaredSchemaObject> _objects = [];
  private readonly Dictionary<(string Table, string Name), DeclaredPin> _pins = [];

  /// <summary>Every declared object.</summary>
  public IReadOnlyCollection<DeclaredSchemaObject> Objects => _objects.Values;

  /// <summary>Every pin declared in code.</summary>
  public IReadOnlyCollection<DeclaredPin> Pins => _pins.Values;

  /// <summary>Declares an index.</summary>
  public ManagedSchemaObjectSet Index(string table, string name, string declaredBy) => _add(table, name, ManagedObjectKind.Index, declaredBy);

  /// <summary>Declares a constraint.</summary>
  public ManagedSchemaObjectSet Constraint(string table, string name, string declaredBy) => _add(table, name, ManagedObjectKind.Constraint, declaredBy);

  /// <summary>Declares a trigger.</summary>
  public ManagedSchemaObjectSet Trigger(string table, string name, string declaredBy) => _add(table, name, ManagedObjectKind.Trigger, declaredBy);

  /// <summary>Declares a per-perspective function.</summary>
  public ManagedSchemaObjectSet Function(string table, string name, string declaredBy) => _add(table, name, ManagedObjectKind.Function, declaredBy);

  /// <summary>Declares extended statistics.</summary>
  public ManagedSchemaObjectSet Statistics(string table, string name, string declaredBy) => _add(table, name, ManagedObjectKind.Statistics, declaredBy);

  /// <summary>Declares a view.</summary>
  public ManagedSchemaObjectSet View(string table, string name, string declaredBy) => _add(table, name, ManagedObjectKind.View, declaredBy);

  /// <summary>Declares a column.</summary>
  public ManagedSchemaObjectSet Column(string table, string name, string declaredBy) => _add(table, name, ManagedObjectKind.Column, declaredBy);

  /// <summary>Declares an object of any kind.</summary>
  public ManagedSchemaObjectSet Add(string table, string name, ManagedObjectKind kind, string declaredBy) => _add(table, name, kind, declaredBy);

  /// <summary>Pins an object so it is never dropped.</summary>
  public ManagedSchemaObjectSet Pin(string table, string name, string? reason, string declaredBy) {
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    _pins[(table, name)] = new DeclaredPin(table, name, reason, declaredBy);
    return this;
  }

  private ManagedSchemaObjectSet _add(string table, string name, ManagedObjectKind kind, string declaredBy) {
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    _objects[(table, name)] = new DeclaredSchemaObject(table, name, kind, declaredBy);
    return this;
  }
}

/// <summary>Anything in Whizbang that creates database objects declares them through one of these.</summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public interface IManagedSchemaObjectContributor {
  /// <summary>Adds the objects this contributor creates.</summary>
  void Contribute(ManagedSchemaObjectSet objects);
}

/// <summary>An object as the database holds it.</summary>
/// <param name="Table">Its table.</param>
/// <param name="Name">Its name, as PostgreSQL stored it.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Definition">Its canonical definition (<c>pg_get_indexdef</c>, <c>pg_get_constraintdef</c>, …).</param>
/// <param name="Comment">Its comment, read for a <c>whizbang:pin</c> marker.</param>
public sealed record LiveSchemaObject(string Table, string Name, ManagedObjectKind Kind, string Definition, string? Comment);

/// <summary>A row of <c>wh_managed_objects</c>.</summary>
/// <remarks>
/// An object has two independent pins. The code pin is C#'s (code or configuration) and is set again at every
/// start. The database pin is set with SQL, the CLI or a <c>whizbang:pin</c> comment, and only an unpin
/// releases it.
/// </remarks>
public sealed record LedgerRow(
  string Table, string Name, ManagedObjectKind Kind, string? Owner, string Status,
  bool CodePinned, string? CodePinSource, string? CodePinReason,
  bool DbPinned, string? DbPinSource, string? DbPinReason, DateTimeOffset? UndeclaredSince);

/// <summary>What the reconcile does with what it finds.</summary>
public enum ReconcileMode {
  /// <summary>Creates what is declared and drops what Whizbang created and no longer declares.</summary>
  Apply,
  /// <summary>Creates what is declared; never drops.</summary>
  AddOnly,
  /// <summary>Changes nothing; records the ledger and reports the plan.</summary>
  ReportOnly,
  /// <summary>Does nothing.</summary>
  Off,
}

/// <summary>The settings a plan is made under.</summary>
/// <param name="Mode">What the reconcile may do.</param>
/// <param name="KeepKinds">Kinds never dropped, by setting (<c>Drop:&lt;Kind&gt;=false</c>).</param>
/// <param name="Pins">Configured pins: globs over <c>table:object</c>.</param>
/// <param name="FleetDeclared">
/// The objects, as <c>table:object</c>, other running instances still declare, none of which is dropped; null when
/// a running instance has not reported what it declares, so nothing is dropped.
/// </param>
public sealed record ReconcileSettings(
  ReconcileMode Mode, IReadOnlyCollection<ManagedObjectKind> KeepKinds, IReadOnlyList<string> Pins,
  IReadOnlySet<string>? FleetDeclared);

/// <summary>A ledger row to write.</summary>
/// <remarks>
/// The code pin is written as given, because C# owns it. The database pin is never overwritten: a write can only
/// add one, for a <c>whizbang:pin</c> comment (<see cref="NewDbPinReason"/> with <see cref="AddsDbPin"/>), so a
/// DBA's pin set while a reconcile runs is not lost.
/// </remarks>
public sealed record LedgerWrite(
  string Table, string Name, ManagedObjectKind Kind, string Definition, string Owner, string Status, string? DeclaredBy,
  bool CodePinned, string? CodePinSource, string? CodePinReason,
  bool AddsDbPin, string? NewDbPinReason, DateTimeOffset? UndeclaredSince);

/// <summary>An object the reconcile drops.</summary>
public sealed record PlannedDrop(string Table, string Name, ManagedObjectKind Kind, string Reason);

/// <summary>An object the reconcile would drop but keeps, and why.</summary>
public sealed record KeptObject(string Table, string Name, ManagedObjectKind Kind, string Reason);

/// <summary>What a reconcile records, drops, keeps and finds missing.</summary>
public sealed record ReconcilePlan(
  IReadOnlyList<LedgerWrite> Records, IReadOnlyList<PlannedDrop> Drops, IReadOnlyList<KeptObject> Kept,
  IReadOnlyList<DeclaredSchemaObject> Missing);
