// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres.Schema;

/// <summary>
/// Decides what a reconcile records, drops and keeps: from what is declared, what the database holds and what
/// the ledger remembers. Pure, so every rule is tested without a database.
/// </summary>
/// <remarks>
/// <para>
/// Whizbang drops only an object it created (owner <see cref="ManagedOwners.WHIZBANG"/>) that nothing declares
/// any more. It never drops a foreign object, a pinned object, or a kind that holds data or security
/// (columns, tables, policies), and it drops nothing on a table's first start with the ledger, so that start
/// only records what is there and reports what the next start will retire.
/// </para>
/// <para>
/// An object has two independent pins, and either one keeps it. The code pin comes from code or configuration, is
/// computed again on every plan, and is released by removing its declaration. The database pin comes from SQL, the
/// CLI or a <c>whizbang:pin</c> comment, and is released only by an unpin; a plan never clears it.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
/// <tests>tests/Whizbang.Core.Tests/Schema/ManagedSchemaPlannerTests.cs</tests>
public static class ManagedSchemaPlanner {
  private const string PIN_COMMENT_MARKER = "whizbang:pin";
  private const string PERSPECTIVE_TABLE_PREFIX = "wh_per_";

  /// <summary>Plans one reconcile.</summary>
  public static ReconcilePlan Plan(
      ManagedSchemaObjectSet declared, IReadOnlyCollection<LiveSchemaObject> live, IReadOnlyCollection<LedgerRow> ledger,
      ReconcileSettings settings, DateTimeOffset now) {
    ArgumentNullException.ThrowIfNull(declared);
    ArgumentNullException.ThrowIfNull(live);
    ArgumentNullException.ThrowIfNull(ledger);
    ArgumentNullException.ThrowIfNull(settings);
    if (settings.Mode == ReconcileMode.Off) {
      return new ReconcilePlan([], [], [], []);
    }

    var context = new PlanContext(
      declared.Objects.ToDictionary(o => (o.Table, o.Name)),
      declared.Pins.ToDictionary(p => (p.Table, p.Name)),
      ledger.ToDictionary(r => (r.Table, r.Name)),
      ledger.Select(r => r.Table).ToHashSet(StringComparer.Ordinal),
      settings,
      now);
    var declaredByKey = context.DeclaredByKey;
    var liveKeys = live.Select(o => (o.Table, o.Name)).ToHashSet();

    var records = new List<LedgerWrite>();
    var drops = new List<PlannedDrop>();
    var kept = new List<KeptObject>();

    foreach (var obj in live) {
      var (record, keepReason) = _planObject(obj, context);
      records.Add(record);
      if (record.Status != ManagedStatuses.PENDING_RETIREMENT) {
        continue;
      }
      if (keepReason is null) {
        drops.Add(new PlannedDrop(obj.Table, obj.Name, obj.Kind, "no longer declared"));
      } else {
        kept.Add(new KeptObject(obj.Table, obj.Name, obj.Kind, keepReason));
      }
    }

    // A ledger row whose object is gone: dropped by Whizbang, or by hand. Declared objects are missing and get
    // created again; the rest are retired.
    var gone = ledger.Where(r => r.Status != ManagedStatuses.RETIRED
      && !liveKeys.Contains((r.Table, r.Name))
      && !declaredByKey.ContainsKey((r.Table, r.Name)));
    foreach (var row in gone) {
      records.Add(new LedgerWrite(row.Table, row.Name, row.Kind, string.Empty, row.Owner ?? ManagedOwners.FOREIGN,
        ManagedStatuses.RETIRED, DeclaredBy: null, CodePinned: false, CodePinSource: null, CodePinReason: null,
        AddsDbPin: false, NewDbPinReason: null, row.UndeclaredSince));
    }

    var missing = declared.Objects.Where(o => !liveKeys.Contains((o.Table, o.Name))).ToList();
    return new ReconcilePlan(records, drops, kept, missing);
  }

  private const string MOVE_SYNC_PREFIX = "wh_mv_";
  private const string MOVE_SYNC_DECLARED_BY = "physical-field move";

  private sealed record PlanContext(
    Dictionary<(string, string), DeclaredSchemaObject> DeclaredByKey,
    Dictionary<(string, string), DeclaredPin> PinsByKey,
    Dictionary<(string, string), LedgerRow> LedgerByKey,
    HashSet<string> TablesWithLedger,
    ReconcileSettings Settings,
    DateTimeOffset Now);

  /// <summary>
  /// The ledger row one live object gets, and, for one retiring, why it is kept (null when it is dropped).
  /// </summary>
  private static (LedgerWrite Record, string? KeepReason) _planObject(LiveSchemaObject obj, PlanContext context) {
    var key = (obj.Table, obj.Name);
    context.DeclaredByKey.TryGetValue(key, out var declaredObject);
    context.LedgerByKey.TryGetValue(key, out var row);
    var codePin = _codePin(obj, context.PinsByKey, context.Settings.Pins);
    var commentPin = _commentPin(obj);
    var addsDbPin = commentPin.Pinned && row is not { DbPinned: true };
    var dbPin = row is { DbPinned: true }
      ? (Pinned: true, Source: row.DbPinSource, Reason: row.DbPinReason)
      : commentPin;
    // A physical-field move's sync triggers and functions belong to the move, which drops them when it settles.
    // Recorded so they can be seen, and never a candidate here, so the reconcile cannot race the move.
    if (_isMoveSync(obj)) {
      declaredObject = new DeclaredSchemaObject(obj.Table, obj.Name, obj.Kind, MOVE_SYNC_DECLARED_BY);
    }
    var owner = declaredObject is not null || _isClassifiedWhizbang(row) || (row?.Owner is null && _isWhizbangShaped(obj))
      ? ManagedOwners.WHIZBANG
      : ManagedOwners.FOREIGN;
    var retiring = declaredObject is null && owner == ManagedOwners.WHIZBANG;
    var status = retiring ? ManagedStatuses.PENDING_RETIREMENT : ManagedStatuses.ACTIVE;
    var undeclaredSince = retiring ? row?.UndeclaredSince ?? context.Now : (DateTimeOffset?)null;

    var record = new LedgerWrite(obj.Table, obj.Name, obj.Kind, obj.Definition, owner, status, declaredObject?.DeclaredBy,
      codePin.Pinned, codePin.Source, codePin.Reason, addsDbPin, addsDbPin ? commentPin.Reason : null, undeclaredSince);
    return (record, retiring ? _keepReason(obj, row, codePin, dbPin, context.TablesWithLedger, context.Settings) : null);
  }

  private static bool _isMoveSync(LiveSchemaObject obj) =>
    obj.Kind is ManagedObjectKind.Trigger or ManagedObjectKind.Function
    && obj.Name.StartsWith(MOVE_SYNC_PREFIX, StringComparison.Ordinal);

  private static bool _isClassifiedWhizbang(LedgerRow? row) => row?.Owner == ManagedOwners.WHIZBANG;

  /// <summary>
  /// Whether an object carries Whizbang's naming for its table: the names the EF Core driver (<c>idx_&lt;short&gt;_</c>)
  /// and the Dapper driver (<c>ix_&lt;table&gt;_</c>) give indexes, and the length and size constraints (<c>ck_&lt;table&gt;_</c>).
  /// Anything else was made by someone else. (A physical-field move's sync objects are declared by the move, so they
  /// never reach this.)
  /// </summary>
  private static bool _isWhizbangShaped(LiveSchemaObject obj) {
    var shortName = obj.Table.StartsWith(PERSPECTIVE_TABLE_PREFIX, StringComparison.Ordinal)
      ? obj.Table[PERSPECTIVE_TABLE_PREFIX.Length..]
      : obj.Table;
    return obj.Kind switch {
      ManagedObjectKind.Index => obj.Name.StartsWith($"idx_{shortName}_", StringComparison.Ordinal)
        || obj.Name.StartsWith($"ix_{obj.Table}_", StringComparison.Ordinal),
      ManagedObjectKind.Constraint => obj.Name.StartsWith($"ck_{obj.Table}_", StringComparison.Ordinal),
      _ => false,
    };
  }

  private static (bool Pinned, string? Source, string? Reason) _codePin(
      LiveSchemaObject obj, Dictionary<(string, string), DeclaredPin> codePins, IReadOnlyList<string> configPins) {
    if (codePins.TryGetValue((obj.Table, obj.Name), out var codePin)) {
      return (true, PinSources.CODE, codePin.Reason ?? codePin.DeclaredBy);
    }

    var qualified = $"{obj.Table}:{obj.Name}";
    var glob = configPins.FirstOrDefault(p => Glob.IsMatch(p, qualified));
    return glob is null ? (false, null, null) : (true, PinSources.CONFIG, $"configured pin {glob}");
  }

  private static (bool Pinned, string? Source, string? Reason) _commentPin(LiveSchemaObject obj) {
    if (obj.Comment is not { } comment || !comment.StartsWith(PIN_COMMENT_MARKER, StringComparison.OrdinalIgnoreCase)) {
      return (false, null, null);
    }
    var reason = comment[PIN_COMMENT_MARKER.Length..].Trim();
    return (true, PinSources.DB_COMMENT, reason.Length == 0 ? null : reason);
  }

  private static string? _keepReason(
      LiveSchemaObject obj, LedgerRow? row, (bool Pinned, string? Source, string? Reason) codePin,
      (bool Pinned, string? Source, string? Reason) dbPin, HashSet<string> tablesWithLedger, ReconcileSettings settings) {
    var pins = new[] { codePin, dbPin }.Where(p => p.Pinned)
      .Select(p => p.Reason is null ? $"pinned by {p.Source}" : $"pinned by {p.Source}: {p.Reason}")
      .ToList();
    if (pins.Count > 0) {
      return string.Join("; ", pins);
    }
    if (obj.Kind is ManagedObjectKind.Column or ManagedObjectKind.Table or ManagedObjectKind.Policy) {
      return $"a {obj.Kind.ToString().ToLowerInvariant()} is never dropped automatically; retire it explicitly";
    }
    if (row is null || !tablesWithLedger.Contains(obj.Table)) {
      return "first start with the ledger: retired on the next start";
    }
    if (settings.Mode != ReconcileMode.Apply) {
      return $"mode {settings.Mode}";
    }
    if (settings.KeepKinds.Contains(obj.Kind)) {
      return $"Drop:{obj.Kind}=false";
    }
    if (settings.FleetDeclared is null) {
      return "a running instance has not reported what it declares; it predates the ledger";
    }
    return settings.FleetDeclared.Contains($"{obj.Table}:{obj.Name}") ? "still declared by a running instance" : null;
  }
}
