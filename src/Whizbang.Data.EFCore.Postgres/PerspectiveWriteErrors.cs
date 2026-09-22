using Npgsql;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Turns a driver-level refusal of a perspective write into a message that names what failed.
/// </summary>
/// <remarks>
/// <para>
/// The case this exists for is a value PostgreSQL cannot store in a document at all. jsonb has no
/// representation for the null character, so a model carrying one in a string is refused outright,
/// and what surfaces is a five-character SQL state and a sentence about escape sequences, naming
/// neither the perspective nor the value, on a save that may be applying a batch of rows. That is a
/// poor place to begin debugging.
/// </para>
/// <para>
/// No storage format fixes this, which is why the answer is a better failure rather than a
/// workaround: the value itself has no representation, so the only improvement available is to fail
/// where it can be understood. A <c>char</c> left at its default is the same problem from a different
/// direction and is fixed at the mapping instead, by storing it as its code point, since there the
/// value is fine and only its rendering was impossible.
/// </para>
/// <para>
/// The property is not named, only the model. Finding it would mean either reflecting over the
/// model's members or generating a validator per model, and neither is worth it for a message that
/// already narrows the search to one type and one character.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWriteErrorsTests.cs</tests>
public static class PerspectiveWriteErrors {
  /// <summary>A character outside the encoding's repertoire, which is how a parameter is refused.</summary>
  private const string CHARACTER_NOT_IN_REPERTOIRE = "22021";

  /// <summary>An escape sequence jsonb will not accept, which is how a document literal is refused.</summary>
  private const string UNSUPPORTED_ESCAPE_SEQUENCE = "22P05";

  /// <summary>
  /// The exception to surface for a failed perspective write, given the one that was raised.
  /// </summary>
  /// <typeparam name="TModel">The perspective's model type.</typeparam>
  /// <param name="failure">The exception raised by the write.</param>
  /// <returns>
  /// A clearer exception when the cause is one this recognizes, and otherwise the original, so
  /// nothing else is reshaped or swallowed.
  /// </returns>
  public static Exception Translate<TModel>(Exception failure) => Translate(failure, typeof(TModel));

  /// <summary>
  /// The exception to surface for a failed perspective write, given the one that was raised.
  /// </summary>
  /// <param name="failure">The exception raised by the write.</param>
  /// <param name="modelType">The perspective's model type.</param>
  /// <returns>
  /// A clearer exception when the cause is one this recognizes, and otherwise the original.
  /// </returns>
  /// <remarks>
  /// Returns rather than throws so a caller can add it to its own <c>catch</c> without a second
  /// layer of wrapping, and so the untranslated case costs nothing but a type test.
  /// </remarks>
  public static Exception Translate(Exception failure, Type modelType) {
    ArgumentNullException.ThrowIfNull(failure);
    ArgumentNullException.ThrowIfNull(modelType);

    if (!_isNullCharacterRefusal(failure)) {
      return failure;
    }

    return new InvalidOperationException(
        $"A '{modelType.Name}' perspective row could not be written because one of its text values "
        + "contains the null character U+0000, which PostgreSQL cannot store in a jsonb document. "
        + "Check the model's string properties for a value carrying one: a fixed-width import, a "
        + "buffer read as text, or a char property left at its default are the usual sources. There "
        + "is no storage format that can hold this character, so the value has to be cleaned before "
        + "it reaches the perspective.",
        failure);
  }

  /// <summary>
  /// Whether this failure, or anything it wraps, is PostgreSQL refusing a null character.
  /// </summary>
  /// <remarks>
  /// The chain has to be walked because the shape differs by path: Entity Framework wraps a save
  /// failure in a <c>DbUpdateException</c>, while a command issued directly surfaces the driver's
  /// exception as it is. Both codes are recognized, since a value refused as a parameter and one
  /// refused as an escape inside a document are the same problem reported differently.
  /// </remarks>
  private static bool _isNullCharacterRefusal(Exception? failure) {
    for (var current = failure; current is not null; current = current.InnerException) {
      if (current is PostgresException postgres
          && (string.Equals(postgres.SqlState, CHARACTER_NOT_IN_REPERTOIRE, StringComparison.Ordinal)
              || string.Equals(postgres.SqlState, UNSUPPORTED_ESCAPE_SEQUENCE, StringComparison.Ordinal))) {
        return true;
      }
    }

    return false;
  }
}
