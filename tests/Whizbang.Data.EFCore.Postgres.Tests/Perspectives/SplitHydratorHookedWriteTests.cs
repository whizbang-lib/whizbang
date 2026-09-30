using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #984: a lens hooks <see cref="SplitModeChangeTrackerHydrator"/> onto the context it reads through,
/// and the hydrator copies a Split row's columns into its model and detaches it. It did so on every
/// <c>Tracked</c> event, so an entity the application added or attached for an update on that context was
/// detached as soon as it was tracked, and its save wrote nothing. It now acts only on a row a query
/// materialized.
/// </summary>
/// <remarks>
/// The hydrator is the one the project's generated registration registers, which is shared, static state,
/// hence the second constraint key.
/// </remarks>
[Category("Shard2")]
[NotInParallel(["EFCorePostgresTests", "ModelRegistrationRegistry tests share static state"])]
public class SplitHydratorHookedWriteTests : EFCoreTestBase {
  private const string TABLE_NAME = "wh_per_init_only_split";

  [Before(Test)]
  public void RegisterGeneratedHydrators() {
    GeneratedModelRegistration.Initialize();
    ModelRegistrationRegistry.InvokeRegistration(
        new ServiceCollection(), typeof(WorkCoordinationDbContext), new PostgresUpsertStrategy());
  }

  private static PerspectiveRow<InitOnlySplitModel> _row(Guid id, string note) => new() {
    Id = id,
    Data = new InitOnlySplitModel { Id = id, Note = note },
    Metadata = new PerspectiveMetadata { EventType = "Seed", EventId = Guid.CreateVersion7().ToString(), Timestamp = DateTime.UtcNow },
    Scope = new PerspectiveScope(),
    CreatedAt = DateTime.UtcNow,
    UpdatedAt = DateTime.UtcNow,
    Version = 1
  };

  private sealed record Stored(string? Status, int Priority, string? Note);

  private async Task<Stored?> _readAsync(Guid id) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
        $"SELECT status, priority, data ->> 'Note' FROM {TABLE_NAME} WHERE id = @id", connection);
    command.Parameters.AddWithValue(nameof(id), id);
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return null;
    }
    return new Stored(
        await reader.IsDBNullAsync(0) ? null : reader.GetString(0),
        reader.GetInt32(1),
        await reader.IsDBNullAsync(2) ? null : reader.GetString(2));
  }

  private async Task<Guid> _addAsync(string status, int priority, string note, bool hooked) {
    var id = Guid.CreateVersion7();
    await using var context = CreateDbContext();
    if (hooked) {
      SplitModeChangeTrackerHydrator.EnsureHooked(context);
    }
    var row = _row(id, note);

    context.Set<PerspectiveRow<InitOnlySplitModel>>().Add(row);
    await Assert.That(context.Entry(row).State).IsEqualTo(EntityState.Added)
      .Because("an entity the application adds is not a row a query materialized, so the hydrator leaves it tracked");

    context.Entry(row).Property("status").CurrentValue = status;
    context.Entry(row).Property("priority").CurrentValue = priority;
    await context.SaveChangesAsync();
    return id;
  }

  [Test]
  public async Task Add_OnAHookedContext_SavesTheSplitRowAsync() {
    var id = await _addAsync("added", 4, "first", hooked: true);

    var stored = await _readAsync(id);
    await Assert.That(stored).IsNotNull()
      .Because("the save writes the row the context was given");
    await Assert.That(stored!.Status).IsEqualTo("added");
    await Assert.That(stored.Priority).IsEqualTo(4);
    await Assert.That(stored.Note).IsEqualTo("first");
  }

  [Test]
  public async Task Update_OnAHookedContext_SavesTheChangeAsync() {
    var id = await _addAsync("added", 4, "first", hooked: false);
    await using var context = CreateDbContext();
    SplitModeChangeTrackerHydrator.EnsureHooked(context);
    // The whole row, as an application that holds it would hand it back.
    var read = _row(id, "second");

    context.Set<PerspectiveRow<InitOnlySplitModel>>().Update(read);
    await Assert.That(context.Entry(read).State).IsEqualTo(EntityState.Modified)
      .Because("an entity attached for an update is not a row a query materialized");
    context.Entry(read).Property("status").CurrentValue = "updated";
    context.Entry(read).Property("priority").CurrentValue = 5;
    await context.SaveChangesAsync();

    var stored = await _readAsync(id);
    await Assert.That(stored!.Note).IsEqualTo("second");
    await Assert.That(stored.Status).IsEqualTo("updated");
    await Assert.That(stored.Priority).IsEqualTo(5);
  }

  [Test]
  public async Task Query_OnAHookedContext_StillHydratesAndDetachesTheRowAsync() {
    var id = await _addAsync("queried", 8, "document", hooked: false);
    await using var context = CreateDbContext();
    SplitModeChangeTrackerHydrator.EnsureHooked(context);

    var row = await context.Set<PerspectiveRow<InitOnlySplitModel>>().SingleAsync(r => r.Id == id);

    await Assert.That(row.Data.Status).IsEqualTo("queried")
      .Because("a row a query materialized is hydrated from its columns");
    await Assert.That(row.Data.Priority).IsEqualTo(8);
    await Assert.That(context.Entry(row).State).IsEqualTo(EntityState.Detached);
  }
}
