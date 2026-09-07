using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="DapperWorkCoordinator"/>'s private <c>_serializeFailures</c> empty-array
/// short-circuit. Driven by reflection against a coordinator built with a placeholder connection
/// string — the method under test does no I/O (it returns before ever touching
/// <c>JsonSerializerOptions.GetTypeInfo</c> or a connection), so no Postgres container is needed.
/// </summary>
/// <remarks>
/// <c>_buildFailuresByCategoryJson</c> calls <c>_serializeFailures([.. failures[i].Items])</c> once
/// PER CATEGORY, with no guard on that category's own item count — unlike its two sibling
/// serializers (<c>_serializeNewOutboxMessages</c> via <c>StoreOutboxMessagesAsync</c>'s
/// empty-message early return, and <c>_serializePerspectiveCompletions</c>, whose own two call sites
/// both pre-filter with a <c>cursors.Count == 0 ? "[]" : …</c> ternary before ever calling it — the
/// existing <c>DapperWorkCoordinatorGuardAndGateTests.FlushCompletionsAsync_TwoFailureCategories…</c>
/// test only ever supplies categories with at least one failure item, so this specific empty-item
/// category shape has never run.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
public class DapperWorkCoordinatorCoverageTests {

  // If this short-circuit regressed to always calling JsonSerializer.Serialize + GetTypeInfo, an
  // empty-Items category (a bookkeeping entry a caller includes even when that category currently
  // has zero failures) would either produce the same "[]" through a more expensive path, or — in
  // any JSON context where MessageFailure[] happens not to be registered — throw
  // InvalidOperationException for a category that has nothing to serialize in the first place,
  // taking down the WHOLE FlushCompletionsAsync call (including the categories that DID have real
  // failures to report) over an entry that carried no data at all.
  [Test]
  public async Task SerializeFailures_EmptyArray_ReturnsTheEmptyJsonArrayLiteralWithoutTouchingJsonOptionsAsync() {
    var coordinator = new DapperWorkCoordinator(
      "Host=unused;Database=unused",
      JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<DapperWorkCoordinator>.Instance);
    var method = typeof(DapperWorkCoordinator).GetMethod("_serializeFailures", BindingFlags.NonPublic | BindingFlags.Instance);
    await Assert.That(method).IsNotNull()
      .Because("this test targets DapperWorkCoordinator's private failure-array serializer by exact name");

    var result = (string?)method!.Invoke(coordinator, [Array.Empty<MessageFailure>()]);

    await Assert.That(result).IsEqualTo("[]")
      .Because("an empty per-category failure list must serialize to the JSON empty-array literal directly");
  }
}
