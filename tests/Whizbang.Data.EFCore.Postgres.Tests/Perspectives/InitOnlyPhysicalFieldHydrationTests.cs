using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #982: the hydrators the EF Core registration generates copy each promoted column into the model
/// a query materializes. For a model whose promoted fields are <c>init</c>-only they used to assign the
/// property, which does not compile. The fixtures in <see cref="InitOnlySplitModel"/>'s file compiling is the
/// first half of the regression; these tests are the second: the generated copy, run on a real row, puts
/// each column's value into the model, for a Split record, an Extracted record, and an Extracted class
/// that skips the one property it cannot assign.
/// </summary>
/// <remarks>
/// The hydrators are the generated ones, registered through the project's generated model registration
/// rather than written out by hand, so what runs is what an application runs. That registration is shared,
/// static state, hence the second constraint key.
/// </remarks>
[Category("Shard2")]
[NotInParallel(["EFCorePostgresTests", "ModelRegistrationRegistry tests share static state"])]
public class InitOnlyPhysicalFieldHydrationTests : EFCoreTestBase {
  private const string SPLIT_TABLE = "wh_per_init_only_split";
  private const string EXTRACTED_TABLE = "wh_per_init_only_extracted";
  private const string EXTRACTED_CLASS_TABLE = "wh_per_init_only_extracted_class";

  private static void _registerGeneratedHydrators() {
    GeneratedModelRegistration.Initialize();
    ModelRegistrationRegistry.InvokeRegistration(
        new ServiceCollection(), typeof(WorkCoordinationDbContext), new PostgresUpsertStrategy());
  }

  private static ServiceProvider _services<TPerspective>() where TPerspective : class {
    var services = new ServiceCollection();
    services.AddTransient<TPerspective>();
    services.AddLogging();
    return services.BuildServiceProvider();
  }

  private static async Task<Guid> _appendAsync(InMemoryEventStore eventStore, InitOnlyFieldsSetEvent payload) {
    await eventStore.AppendAsync(payload.StreamId, new MessageEnvelope<InitOnlyFieldsSetEvent> {
      MessageId = MessageId.New(),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = []
    });
    return payload.StreamId;
  }

  private static InitOnlyFieldsSetEvent _event(Guid streamId) => new() {
    StreamId = streamId,
    Status = "active",
    Priority = 7,
    Note = "document"
  };

  private async Task _executeAsync(string sql, Guid streamId) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    command.Parameters.AddWithValue("id", streamId);
    await command.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task SplitRecord_AQueryOnAHookedContext_CopiesTheInitOnlyColumnsIntoTheModelAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    await _appendAsync(eventStore, _event(streamId));
    await using (var writeContext = CreateDbContext()) {
      var provider = _services<InitOnlySplitPerspective>();
      var runner = new InitOnlySplitPerspectiveRunner(
          provider, provider.GetRequiredService<ILogger<InitOnlySplitPerspectiveRunner>>(), eventStore,
          new EFCorePostgresPerspectiveStore<InitOnlySplitModel>(writeContext, SPLIT_TABLE),
          provider.GetRequiredService<IServiceScopeFactory>());
      var result = await runner.RunAsync(streamId, SPLIT_TABLE, null, CancellationToken.None);
      await Assert.That(result.EventsProcessed).IsEqualTo(1);
    }
    _registerGeneratedHydrators();
    await using var context = CreateDbContext();
    SplitModeChangeTrackerHydrator.EnsureHooked(context);

    var row = await context.Set<PerspectiveRow<InitOnlySplitModel>>().SingleAsync(r => r.Id == streamId);

    await Assert.That(row.Data.Status).IsEqualTo("active")
      .Because("a Split field lives only in its column, and the generated copy is the only way into the model");
    await Assert.That(row.Data.Priority).IsEqualTo(7);
    await Assert.That(row.Data.Note).IsEqualTo("document")
      .Because("the copy starts from the document, so the fields that live there are kept");
    await Assert.That(context.ChangeTracker.Entries().Count()).IsEqualTo(0)
      .Because("the hydrated row is detached, as it was before the copy became a with expression");
  }

  [Test]
  public async Task ExtractedRecord_AQueryOnAHookedContext_CopiesTheInitOnlyColumnsIntoTheModelAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    await _appendAsync(eventStore, _event(streamId));
    await using (var writeContext = CreateDbContext()) {
      var provider = _services<InitOnlyExtractedPerspective>();
      var runner = new InitOnlyExtractedPerspectiveRunner(
          provider, provider.GetRequiredService<ILogger<InitOnlyExtractedPerspectiveRunner>>(), eventStore,
          new EFCorePostgresPerspectiveStore<InitOnlyExtractedModel>(writeContext, EXTRACTED_TABLE),
          provider.GetRequiredService<IServiceScopeFactory>());
      await runner.RunAsync(streamId, EXTRACTED_TABLE, null, CancellationToken.None);
    }
    // The document holds the same values, so the columns are moved away from it to show which one the model got.
    await _executeAsync($"UPDATE {EXTRACTED_TABLE} SET status = 'from-column', priority = 11 WHERE id = @id", streamId);
    _registerGeneratedHydrators();
    await using var context = CreateDbContext();
    SplitModeChangeTrackerHydrator.EnsureHooked(context);

    var row = await context.Set<PerspectiveRow<InitOnlyExtractedModel>>().SingleAsync(r => r.Id == streamId);

    await Assert.That(row.Data.Status).IsEqualTo("from-column");
    await Assert.That(row.Data.Priority).IsEqualTo(11);
  }

  [Test]
  public async Task ExtractedClass_AQueryOnAHookedContext_CopiesTheSettableColumnAndKeepsTheInitOnlyOneFromTheDocumentAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    await _appendAsync(eventStore, _event(streamId));
    await using (var writeContext = CreateDbContext()) {
      var provider = _services<InitOnlyExtractedClassPerspective>();
      var runner = new InitOnlyExtractedClassPerspectiveRunner(
          provider, provider.GetRequiredService<ILogger<InitOnlyExtractedClassPerspectiveRunner>>(), eventStore,
          new EFCorePostgresPerspectiveStore<InitOnlyExtractedClassModel>(writeContext, EXTRACTED_CLASS_TABLE),
          provider.GetRequiredService<IServiceScopeFactory>());
      await runner.RunAsync(streamId, EXTRACTED_CLASS_TABLE, null, CancellationToken.None);
    }
    await _executeAsync($"UPDATE {EXTRACTED_CLASS_TABLE} SET status = 'from-column', priority = 11 WHERE id = @id", streamId);
    _registerGeneratedHydrators();
    await using var context = CreateDbContext();
    SplitModeChangeTrackerHydrator.EnsureHooked(context);

    var row = await context.Set<PerspectiveRow<InitOnlyExtractedClassModel>>().SingleAsync(r => r.Id == streamId);

    await Assert.That(row.Data.Priority).IsEqualTo(11)
      .Because("a settable property of a class is assigned from its column");
    await Assert.That(row.Data.Status).IsEqualTo("active")
      .Because("an init-only property of a class cannot be assigned after construction, and an Extracted document holds it");
  }
}
