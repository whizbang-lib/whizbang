using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A stored document whose scalar has the wrong JSON type, a number where the model property is a
/// string, fails its read with a failure the perspective worker classifies as unreadable (issue #985).
/// </summary>
/// <remarks>
/// The worker parks a stream only when <see cref="StoredFormUnreadable.TryClassify"/> recognizes the
/// read's failure; anything it does not recognize takes the generic path. The temporal readers raise a
/// refusal of their own, and those are covered elsewhere. A plain type mismatch is raised by whichever
/// reader the provider uses for the document, so this pins what that reader raises, as the store
/// surfaces it, against a real database.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/StoredFormUnreadable.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveStore.cs</code-under-test>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Integration")]
[Category("Shard1")]
[NotInParallel("EFCorePostgresTests")]
public class StoredFormScalarMismatchReadTests : EFCoreTestBase {

  [Test]
  public async Task GetByStreamId_NumberWhereTheModelHasAString_IsClassifiedAsUnreadableWithThePathAsync() {
    var streamId = (Guid)TrackedGuid.New();
    await using (var writeContext = CreateDbContext()) {
      var writer = new EFCorePostgresPerspectiveStore<Order>(writeContext, "wh_per_order", new PostgresUpsertStrategy());
      await writer.UpsertAsync(streamId, new Order { OrderId = new TestOrderId(streamId), Amount = 5m, Status = "Created" });
    }
    await _storeNumberForStatusAsync(streamId);

    await using var readContext = CreateDbContext();
    var reader = new EFCorePostgresPerspectiveStore<Order>(readContext, "wh_per_order", new PostgresUpsertStrategy());
    Exception? raised = null;
    try {
      _ = await reader.GetByStreamIdAsync(streamId);
    } catch (Exception ex) {
      raised = ex;
    }

    await Assert.That(raised).IsNotNull().Because("a number cannot be read as a string property");
    var classified = StoredFormUnreadable.TryClassify(raised!, out var failure);
    await Assert.That(classified).IsTrue()
      .Because($"the worker parks only a failure it classifies; the store raised {_describeChain(raised!)}");
    await Assert.That(failure!.Path).IsNotNull().And.Contains("Status")
      .Because("the operator needs to know where in the document the value is");
  }

  [Test]
  public async Task GetByStreamId_StoredValueCorrected_ReadsAgainAsync() {
    var streamId = (Guid)TrackedGuid.New();
    await using (var writeContext = CreateDbContext()) {
      var writer = new EFCorePostgresPerspectiveStore<Order>(writeContext, "wh_per_order", new PostgresUpsertStrategy());
      await writer.UpsertAsync(streamId, new Order { OrderId = new TestOrderId(streamId), Amount = 5m, Status = "Created" });
    }
    await _storeNumberForStatusAsync(streamId);
    await _storeStatusAsync(streamId, "\"Corrected\"");

    await using var readContext = CreateDbContext();
    var reader = new EFCorePostgresPerspectiveStore<Order>(readContext, "wh_per_order", new PostgresUpsertStrategy());
    var model = await reader.GetByStreamIdAsync(streamId);

    await Assert.That(model).IsNotNull();
    await Assert.That(model!.Status).IsEqualTo("Corrected");
  }

  /// <summary>
  /// A read failure the stored document does not explain is raised exactly as the materializer raised it.
  /// </summary>
  /// <remarks>
  /// A number too large for the property's type has the right token, so the walk does not claim it, and
  /// the reader's own error is not reshaped into something it is not.
  /// </remarks>
  [Test]
  public async Task GetByStreamId_AFailureTheDocumentDoesNotExplain_IsRaisedAsItWasAsync() {
    var streamId = (Guid)TrackedGuid.New();
    await using (var writeContext = CreateDbContext()) {
      var writer = new EFCorePostgresPerspectiveStore<Order>(writeContext, "wh_per_order", new PostgresUpsertStrategy());
      await writer.UpsertAsync(streamId, new Order { OrderId = new TestOrderId(streamId), Amount = 5m, Status = "Created" });
    }
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "UPDATE wh_per_order SET data = jsonb_set(data, '{Amount}', '1e400'::jsonb) WHERE id = @id";
      cmd.Parameters.AddWithValue("id", streamId);
      await cmd.ExecuteNonQueryAsync();
    }

    await using var readContext = CreateDbContext();
    var reader = new EFCorePostgresPerspectiveStore<Order>(readContext, "wh_per_order", new PostgresUpsertStrategy());

    var raised = await Assert.That(async () => await reader.GetByStreamIdAsync(streamId)).Throws<FormatException>();
    await Assert.That(raised!.InnerException).IsNull().Because("the reader's own error is rethrown unwrapped");
  }

  private Task _storeNumberForStatusAsync(Guid streamId) => _storeStatusAsync(streamId, "123");

  private async Task _storeStatusAsync(Guid streamId, string jsonValue) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_per_order SET data = jsonb_set(data, '{Status}', @value::jsonb) WHERE id = @id";
    cmd.Parameters.AddWithValue("value", jsonValue);
    cmd.Parameters.AddWithValue("id", streamId);
    var updated = await cmd.ExecuteNonQueryAsync();
    await Assert.That(updated).IsEqualTo(1).Because("the row the test wrote must exist to be corrupted");
  }

  private static string _describeChain(Exception exception) {
    var parts = new List<string>();
    for (Exception? current = exception; current is not null; current = current.InnerException) {
      parts.Add($"{current.GetType().FullName}: {current.Message}");
    }
    return string.Join(" -> ", parts);
  }
}
