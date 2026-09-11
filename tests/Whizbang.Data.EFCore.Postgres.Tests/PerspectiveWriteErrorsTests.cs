using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// That a write refused for a value PostgreSQL cannot store says which model and which character.
/// </summary>
/// <remarks>
/// <para>
/// jsonb has no representation for the null character, so a model carrying one in a string cannot be
/// stored. What the driver reports is a five-character SQL state and a sentence about escape
/// sequences, naming neither the perspective nor the value, on a save that may be applying a batch of
/// rows. No storage format fixes the underlying problem, because the value itself has no
/// representation, so the only improvement available is to fail somewhere a developer can act.
/// </para>
/// <para>
/// The exception shape differs by path and both have to be recognized: Entity Framework wraps a save
/// failure, while a command issued directly surfaces the driver's exception as it stands.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
[Category("Shard1")]
public class PerspectiveWriteErrorsTests {
  private sealed class NoteModel {
    public string Body { get; init; } = string.Empty;
  }

  /// <summary>
  /// Builds the driver exception for a SQL state, which is the only part of it this reads.
  /// </summary>
  private static PostgresException _postgres(string sqlState) =>
    new(messageText: "unsupported Unicode escape sequence", severity: "ERROR",
        invariantSeverity: "ERROR", sqlState: sqlState);

  /// <summary>Both spellings of the refusal are recognized, because both occur.</summary>
  [Test]
  [Arguments("22021")]
  [Arguments("22P05")]
  public async Task ANullCharacterRefusalIsTranslatedAsync(string sqlState) {
    var translated = PerspectiveWriteErrors.Translate<NoteModel>(_postgres(sqlState));

    await Assert.That(translated).IsTypeOf<InvalidOperationException>();
    await Assert.That(translated.Message).Contains("NoteModel", StringComparison.Ordinal)
      .Because("the model is the one thing that narrows a failing batch to a place in the code");
    await Assert.That(translated.Message).Contains("U+0000", StringComparison.Ordinal)
      .Because("naming the character is what makes the cause searchable");
  }

  /// <summary>
  /// The refusal is found through a wrapper, since a save failure arrives wrapped and a direct
  /// command does not.
  /// </summary>
  [Test]
  public async Task ARefusalWrappedByASaveFailureIsStillFoundAsync() {
    var wrapped = new DbUpdateException("An error occurred while saving.", _postgres("22P05"));

    var translated = PerspectiveWriteErrors.Translate<NoteModel>(wrapped);

    await Assert.That(translated).IsTypeOf<InvalidOperationException>();
    await Assert.That(translated.InnerException).IsSameReferenceAs(wrapped)
      .Because("the original has to remain reachable, since it carries the position and the statement");
  }

  /// <summary>
  /// Anything else is returned untouched. Reshaping unrelated failures would hide them.
  /// </summary>
  [Test]
  [Arguments("23505")]
  [Arguments("42601")]
  public async Task AnUnrelatedFailureIsReturnedUnchangedAsync(string sqlState) {
    var original = _postgres(sqlState);

    await Assert.That(PerspectiveWriteErrors.Translate<NoteModel>(original)).IsSameReferenceAs(original);
  }

  /// <summary>A failure with no driver exception in it at all is likewise left alone.</summary>
  [Test]
  public async Task AFailureWithNoDriverCauseIsReturnedUnchangedAsync() {
    var original = new TimeoutException("took too long");

    await Assert.That(PerspectiveWriteErrors.Translate<NoteModel>(original)).IsSameReferenceAs(original);
  }
}
