using System.Collections;
using System.Reflection;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Direct coverage tests for <c>Whizbang.Generators.Ledger.PinnedTypeLedger</c> — a plain data class
/// (ledger parse / serialize / query), not itself a source generator. <c>PinnedTypeLedger</c> and its
/// nested record types are declared <c>internal</c>, and this project deliberately does not use
/// <c>InternalsVisibleTo</c> for the generators assembly (see <c>src/Whizbang.Generators/AssemblyInfo.cs</c> —
/// it conflicts with PolySharp polyfills), so these tests reach the type via reflection instead of a
/// direct reference. That also means they can construct ledger shapes — a blank pinned id that made it
/// into <c>Types</c>, an explicit JSON <c>"types": null</c> — that <c>PinnedTypeLedgerGenerator</c>'s own
/// discovery pipeline can never hand this class, so exercising them here is cheaper and more direct than
/// driving a full Roslyn compilation through the generator.
/// </summary>
[Category("RenamePlatform")]
public class PinnedTypeLedgerCoverageTests {
  private static readonly Assembly _generatorsAssembly = typeof(PinnedTypeLedgerGenerator).Assembly;

  private static readonly Type _ledgerType = _generatorsAssembly.GetType("Whizbang.Generators.Ledger.PinnedTypeLedger")
    ?? throw new InvalidOperationException("Whizbang.Generators.Ledger.PinnedTypeLedger not found — check the type's namespace/name.");

  private static readonly Type _entryType = _generatorsAssembly.GetType("Whizbang.Generators.Ledger.PinnedTypeLedgerEntry")
    ?? throw new InvalidOperationException("Whizbang.Generators.Ledger.PinnedTypeLedgerEntry not found — check the type's namespace/name.");

  private static object? _tryParse(string? json) {
    var method = _ledgerType.GetMethods()
      .SingleOrDefault(m => m.Name == "TryParse" && m.GetParameters().Length == 1)
      ?? throw new InvalidOperationException("PinnedTypeLedger.TryParse(string?) not found.");
    return method.Invoke(null, [json]);
  }

  private static object _newEntry(string pinnedId, string clrTypeName, params string[] formerNames) {
    var entry = Activator.CreateInstance(_entryType)!;
    _entryType.GetProperty("PinnedId")!.SetValue(entry, pinnedId);
    _entryType.GetProperty("ClrTypeName")!.SetValue(entry, clrTypeName);
    _entryType.GetProperty("FormerNames")!.SetValue(entry, formerNames.ToList());
    return entry;
  }

  private static object _newLedgerWithEntries(params object[] entries) {
    var ledger = Activator.CreateInstance(_ledgerType)!;
    var listType = typeof(List<>).MakeGenericType(_entryType);
    var list = (IList)Activator.CreateInstance(listType)!;
    foreach (var entry in entries) {
      list.Add(entry);
    }
    _ledgerType.GetProperty("Types")!.SetValue(ledger, list);
    return ledger;
  }

  /// <summary>Invokes a zero-arg, no-parameter instance method returning <c>IEnumerable&lt;TRecord&gt;</c>
  /// (an internal record type) and flattens each yielded item's two named properties into a tuple, so the
  /// caller never needs to reference the internal record type itself.</summary>
  private static List<(string A, string B)> _invokeToPairs(object ledger, string methodName, string propA, string propB) {
    var method = _ledgerType.GetMethod(methodName)
      ?? throw new InvalidOperationException($"PinnedTypeLedger.{methodName}() not found.");
    var items = (IEnumerable)method.Invoke(ledger, null)!;
    var pairs = new List<(string, string)>();
    foreach (var item in items) {
      var itemType = item.GetType();
      var a = (string)itemType.GetProperty(propA)!.GetValue(item)!;
      var b = (string)itemType.GetProperty(propB)!.GetValue(item)!;
      pairs.Add((a, b));
    }
    return pairs;
  }

  private static bool _knowsName(object entry, string name) {
    var method = _entryType.GetMethod("KnowsName")
      ?? throw new InvalidOperationException("PinnedTypeLedgerEntry.KnowsName(string) not found.");
    return (bool)method.Invoke(entry, [name])!;
  }

  // ==================== TryParse: explicit null "types" ====================

  /// <summary>
  /// A hand-edited or corrupted ledger with <c>"types": null</c> parses as valid JSON but carries no
  /// usable entries. If TryParse returned a ledger object here instead of null, every caller that
  /// checks "is the ledger present" (the governance analyzer, the VSCode extension) would treat a
  /// gutted ledger as a legitimate empty one instead of surfacing it as a broken file to fix.
  /// </summary>
  [Test]
  public async Task TryParse_JsonWithExplicitNullTypesArray_ReturnsNullAsync() {
    const string json = """{"version": 1, "types": null}""";

    var ledger = _tryParse(json);

    await Assert.That(ledger).IsNull()
      .Because("a ledger whose \"types\" array is explicitly null carries no usable entries and must be treated the same as an absent ledger");
  }

  // ==================== ToRenameAliases: no-op alias skip ====================

  /// <summary>
  /// A former name identical to the entry's current CLR name is a no-op alias (can happen if a rename
  /// was reverted but the history entry was never cleaned up). Surfacing it as a rename alias would
  /// tell a consumer "this type was renamed from itself" — meaningless, and if fed into a
  /// resolve-old-name-to-new-name lookup, a self-referential entry is at best wasted work.
  /// </summary>
  [Test]
  public async Task ToRenameAliases_FormerNameEqualsCurrentClrTypeName_SkipsTheNoOpAliasAsync() {
    var entry = _newEntry(
      "11111111-1111-1111-1111-111111111111",
      "MyApp.Orders.OrderPlaced",
      "MyApp.Orders.OrderCreated", "MyApp.Orders.OrderPlaced");
    var ledger = _newLedgerWithEntries(entry);

    var aliases = _invokeToPairs(ledger, "ToRenameAliases", "FormerClrTypeName", "CurrentClrTypeName");

    await Assert.That(aliases.Any(a => a.A == "MyApp.Orders.OrderCreated" && a.B == "MyApp.Orders.OrderPlaced")).IsTrue()
      .Because("a genuine former name must still resolve to the current name");
    await Assert.That(aliases.Any(a => a.A == "MyApp.Orders.OrderPlaced")).IsFalse()
      .Because("a former name identical to the current CLR name is a no-op alias and must not be yielded");
  }

  // ==================== ToPinnedFormerNames: blank pinned id skip ====================

  /// <summary>
  /// <c>ToPinnedFormerNames</c> exists so a caller can look up a former name BY pinned id. An entry
  /// with a blank pinned id can never be found by that lookup key, so yielding its former names would
  /// produce pairs no caller could ever retrieve — dead weight in a rename analyzer that indexes this
  /// output by pinned id.
  /// </summary>
  [Test]
  public async Task ToPinnedFormerNames_EntryWithBlankPinnedId_SkipsEntryEntirelyAsync() {
    var blankIdEntry = _newEntry("   ", "MyApp.Orders.Untracked", "MyApp.Orders.UntrackedOld");
    var trackedEntry = _newEntry(
      "22222222-2222-2222-2222-222222222222", "MyApp.Orders.Tracked", "MyApp.Orders.TrackedOld");
    var ledger = _newLedgerWithEntries(blankIdEntry, trackedEntry);

    var formerNames = _invokeToPairs(ledger, "ToPinnedFormerNames", "PinnedId", "FormerClrTypeName");

    await Assert.That(formerNames.Any(f => f.A == "22222222-2222-2222-2222-222222222222" && f.B == "MyApp.Orders.TrackedOld")).IsTrue();
    await Assert.That(formerNames.Any(f => f.B == "MyApp.Orders.UntrackedOld")).IsFalse()
      .Because("an entry with no usable pinned id can never be found by a pinned-id lookup, so its former names must not be yielded");
  }

  // ==================== ToPinnedFormerNames: no-op alias skip ====================

  /// <summary>
  /// Mirrors the no-op-alias skip in <c>ToRenameAliases</c>, but for the pinned-id-keyed variant: a
  /// former name equal to the current CLR name must not be yielded here either, or a caller indexing
  /// by pinned id would see a type reported as renamed from its own current name.
  /// </summary>
  [Test]
  public async Task ToPinnedFormerNames_FormerNameEqualsCurrentClrTypeName_SkipsTheNoOpAliasAsync() {
    var entry = _newEntry(
      "33333333-3333-3333-3333-333333333333",
      "MyApp.Orders.OrderPlaced",
      "MyApp.Orders.OrderCreated", "MyApp.Orders.OrderPlaced");
    var ledger = _newLedgerWithEntries(entry);

    var formerNames = _invokeToPairs(ledger, "ToPinnedFormerNames", "PinnedId", "FormerClrTypeName");

    await Assert.That(formerNames.Any(f => f.A == "33333333-3333-3333-3333-333333333333" && f.B == "MyApp.Orders.OrderCreated")).IsTrue();
    await Assert.That(formerNames.Any(f => f.B == "MyApp.Orders.OrderPlaced")).IsFalse()
      .Because("a former name identical to the current name is a no-op alias and must not be yielded");
  }

  // ==================== PinnedTypeLedgerEntry.KnowsName ====================

  /// <summary>
  /// An old event/message name recorded as a former name must still resolve as "known" — historic
  /// events already written to the append-only log carry that old name, and failing to recognize it
  /// would make the rename-governance analyzer treat every already-replayed event's type as an
  /// unrecognized, un-pinned type.
  /// </summary>
  [Test]
  public async Task KnowsName_NameMatchesAFormerNameAsync() {
    var entry = _newEntry(
      "44444444-4444-4444-4444-444444444444",
      "MyApp.Orders.OrderPlaced",
      "MyApp.Orders.OrderRaised", "MyApp.Orders.OrderCreated");

    var knows = _knowsName(entry, "MyApp.Orders.OrderCreated");

    await Assert.That(knows).IsTrue()
      .Because("a recorded former name must resolve to \"known\", or replayed events under the old name would look unpinned");
  }

  /// <summary>
  /// A name that is neither the current CLR name nor any recorded former name must be reported as
  /// unknown — otherwise an unrelated type could be mistaken for a renamed pinned type and silently
  /// inherit its identity/history.
  /// </summary>
  [Test]
  public async Task KnowsName_NameMatchesNeitherCurrentNorAnyFormerNameAsync() {
    var entry = _newEntry(
      "55555555-5555-5555-5555-555555555555",
      "MyApp.Orders.OrderPlaced",
      "MyApp.Orders.OrderRaised");

    var knows = _knowsName(entry, "MyApp.Orders.SomeUnrelatedType");

    await Assert.That(knows).IsFalse()
      .Because("a name that is neither the current name nor a recorded former name must not be reported as known");
  }
}
