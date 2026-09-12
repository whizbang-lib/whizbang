using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What the perspective store does with a write its strategy refused.
/// </summary>
/// <remarks>
/// <para>
/// Every perspective write funnels through one <c>catch</c>, which makes it the only place a refusal
/// can be explained in terms of the model rather than of a five-character SQL state. It also makes it
/// the only place a refusal can be accidentally reshaped or swallowed, so both directions are pinned
/// here: the recognized cause is replaced, and everything else comes back out exactly as it went in.
/// </para>
/// <para>
/// The second half is the one worth keeping. A store that wrapped every failure would bury a
/// concurrency conflict, a timeout and a connection loss under one message about the null character,
/// and a caller retrying on the original type would stop recognizing any of them.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveStore.cs</code-under-test>
[Category("Shard1")]
public class PerspectiveWriteErrorTranslationTests : EFCoreTestBase {

  public sealed class NoteModel {
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
  }

  /// <summary>Refuses every write with whatever it was given, and never touches the database.</summary>
  private sealed class RefusingStrategy(Exception failure) : IDbUpsertStrategy {
    public Task UpsertPerspectiveRowAsync<TModel>(
        DbContext context, string tableName, Guid id, TModel model,
        PerspectiveMetadata metadata, PerspectiveScope scope,
        CancellationToken cancellationToken = default) where TModel : class => throw failure;

    public Task UpsertPerspectiveRowAsync<TModel>(
        DbContext context, string tableName, Guid id, TModel model,
        PerspectiveMetadata metadata, PerspectiveScope scope, bool forceUpdateScope,
        CancellationToken cancellationToken = default) where TModel : class => throw failure;

    public Task UpsertPerspectiveRowWithPhysicalFieldsAsync<TModel>(
        DbContext context, string tableName, Guid id, TModel model,
        PerspectiveMetadata metadata, PerspectiveScope scope,
        IDictionary<string, object?> physicalFieldValues,
        CancellationToken cancellationToken = default) where TModel : class => throw failure;

    public Task UpsertPerspectiveRowWithPhysicalFieldsAsync<TModel>(
        DbContext context, string tableName, Guid id, TModel model,
        PerspectiveMetadata metadata, PerspectiveScope scope,
        IDictionary<string, object?> physicalFieldValues, bool forceUpdateScope,
        CancellationToken cancellationToken = default) where TModel : class => throw failure;
  }

  private const string TABLE = "wh_per_note";

  private static readonly PerspectiveMetadata _metadata = new() {
    EventType = "Notes.NoteWritten",
    EventId = "0199aaaa-0000-0000-0000-000000000001",
    Timestamp = DateTime.UtcNow,
  };

  private EFCorePostgresPerspectiveStore<NoteModel> _refusingWith(DbContext context, Exception failure) =>
    new(context, TABLE, new RefusingStrategy(failure));

  /// <summary>
  /// A null character is refused with a message naming the model, in place of the driver's own.
  /// </summary>
  /// <remarks>
  /// jsonb has no representation for U+0000, so what surfaces without this is a sentence about escape
  /// sequences naming neither the perspective nor the value, on a save that may be applying a batch.
  /// The original is kept as the inner exception, because the SQL state is still the evidence.
  /// </remarks>
  [Test]
  public async Task ARecognizedRefusalIsReplacedWithOneNamingTheModelAsync(CancellationToken cancellationToken) {
    await using var context = CreateDbContext();
    var driverFailure = new PostgresException(
      messageText: "unsupported Unicode escape sequence", severity: "ERROR",
      invariantSeverity: "ERROR", sqlState: "22P05");
    var store = _refusingWith(context, driverFailure);

    var thrown = await Assert.That(async () => await store.UpsertAsync(
        Guid.CreateVersion7(), new NoteModel { Text = "x" }, new PerspectiveScope(),
        forceUpdateScope: false, _metadata, cancellationToken))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains("NoteModel", StringComparison.Ordinal)
      .Because("naming the perspective is the whole improvement over the SQL state");
    await Assert.That(thrown.InnerException).IsSameReferenceAs(driverFailure)
      .Because("the driver's exception is still the evidence for what was refused");
  }

  /// <summary>
  /// Anything else comes back out as it went in, with its own type and its own stack.
  /// </summary>
  /// <remarks>
  /// This is what keeps the translation from being a trap. A caller that retries a concurrency
  /// conflict, or a host that treats a timeout differently from a bad value, recognizes the failure by
  /// its type, so reshaping one this does not understand would break both.
  /// </remarks>
  [Test]
  [Arguments("timeout")]
  [Arguments("conflict")]
  [Arguments("wrong state")]
  public async Task AnUnrecognizedRefusalIsRethrownUnchangedAsync(string kind, CancellationToken cancellationToken) {
    await using var context = CreateDbContext();
    Exception failure = kind switch {
      "timeout" => new TimeoutException("the write did not complete"),
      "conflict" => new DbUpdateConcurrencyException("the row moved underneath this write"),
      "wrong state" => new PostgresException(
        messageText: "deadlock detected", severity: "ERROR",
        invariantSeverity: "ERROR", sqlState: "40P01"),
      _ => throw new InvalidOperationException(kind),
    };
    var store = _refusingWith(context, failure);

    var thrown = await Assert.That(async () => await store.UpsertAsync(
        Guid.CreateVersion7(), new NoteModel { Text = "x" }, new PerspectiveScope(),
        forceUpdateScope: false, _metadata, cancellationToken))
      .Throws<Exception>();

    await Assert.That(thrown).IsSameReferenceAs(failure)
      .Because("a failure this does not recognize must reach the caller with the type it was raised "
        + "with, or every retry and every handler keyed on that type stops matching");
  }
}
