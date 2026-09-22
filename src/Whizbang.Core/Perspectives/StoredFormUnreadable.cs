using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// A failure to read a stored perspective document, told apart from every other failure by its
/// type, wherever in a chain of wrappers it sits.
/// </summary>
/// <remarks>
/// <para>
/// Every reader of a stored temporal (<see cref="CanonicalTemporalReaders"/>) refuses a value it
/// cannot read with a <see cref="JsonException"/> that names the type, the forms accepted and the
/// token found; the serializer adds the path. That exception may surface bare, wrapped by a
/// materializer, or inside an aggregate. This finds it and carries what an operator needs: the
/// path, when the reader knew it, and the message.
/// </para>
/// <para>
/// The worker uses the classification to log the failure once per perspective and stream with its
/// own event id, to count it under <see cref="REASON"/>, and to park the stream instead of retrying
/// it every cycle. Nothing here decides what to do about it; this only says what it is.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/StoredFormUnreadableTests.cs</tests>
public sealed class StoredFormUnreadable {
#pragma warning disable CA1707
  /// <summary>The reason tag the meter and the log share for this classification.</summary>
  public const string REASON = "stored_form_unreadable";
#pragma warning restore CA1707

  private StoredFormUnreadable(JsonException cause) {
    Cause = cause;
  }

  /// <summary>The reader's refusal.</summary>
  public JsonException Cause { get; }

  /// <summary>The JSON path of the value refused, when the reader that refused it knew the path.</summary>
  public string? Path => Cause.Path;

  /// <summary>The refusal, naming the type, the forms accepted and the token found.</summary>
  public string Detail => Cause.Message;

  /// <summary>
  /// Classifies an exception as a stored-form failure when a reader's refusal is anywhere in it.
  /// </summary>
  /// <param name="exception">The exception a read raised, possibly wrapped.</param>
  /// <param name="failure">The classification, when <paramref name="exception"/> is one.</param>
  /// <returns><see langword="true"/> when a reader's refusal was found.</returns>
  public static bool TryClassify(Exception exception, [NotNullWhen(true)] out StoredFormUnreadable? failure) {
    ArgumentNullException.ThrowIfNull(exception);
    var cause = _findRefusal(exception);
    failure = cause is null ? null : new StoredFormUnreadable(cause);
    return failure is not null;
  }

  private static JsonException? _findRefusal(Exception exception) {
    for (Exception? current = exception; current is not null; current = current.InnerException) {
      if (current is JsonException refusal) {
        return refusal;
      }
      if (current is AggregateException aggregate) {
        foreach (var inner in aggregate.InnerExceptions) {
          var found = _findRefusal(inner);
          if (found is not null) {
            return found;
          }
        }
      }
    }
    return null;
  }
}
