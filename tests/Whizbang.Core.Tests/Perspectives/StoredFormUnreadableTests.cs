using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// That a failure to read a stored document is told apart from every other failure by its type,
/// wherever in a chain of wrappers it sits.
/// </summary>
/// <remarks>
/// <para>
/// A perspective worker that cannot read a row used to log a generic error per drain cycle, with a
/// stack trace as the only clue that the stored form was the problem. Every reader of a stored
/// temporal now refuses an unreadable value with a <see cref="JsonException"/> whose message
/// names the type, the forms accepted and the token found, and the serializer adds the path. That
/// exception may surface bare, wrapped by a materializer, or inside an aggregate; the
/// classification finds it in all three and carries what the operator needs to read.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/StoredFormUnreadable.cs</code-under-test>
[Category("Core")]
[Category("Perspectives")]
public class StoredFormUnreadableTests {
  private static JsonException _unreadable() {
    try {
      // Thrown by the serializer so it carries a path, the way a real one does.
      JsonSerializer.Deserialize<Holder>("{\"At\":true}", HolderContext.Default.Holder);
    } catch (JsonException ex) {
      return ex;
    }
    throw new InvalidOperationException("the fixture did not fail");
  }

  /// <summary>A bare refusal is classified, and its path and message carried.</summary>
  [Test]
  public async Task ABareRefusalIsClassifiedAsync() {
    var cause = _unreadable();

    var classified = StoredFormUnreadable.TryClassify(cause, out var failure);

    await Assert.That(classified).IsTrue();
    await Assert.That(failure!.Path).IsEqualTo("$.At");
    await Assert.That(failure.Detail).Contains("A stored DateTime must be a number (microseconds) or a rendering", StringComparison.Ordinal);
    await Assert.That(failure.Detail).Contains("True", StringComparison.Ordinal);
    await Assert.That(failure.Cause).IsSameReferenceAs(cause);
  }

  /// <summary>A refusal a materializer wrapped is found through the wrapper.</summary>
  [Test]
  public async Task AWrappedRefusalIsClassifiedAsync() {
    var cause = _unreadable();
    var wrapped = new InvalidOperationException("An error occurred while reading a database value", cause);

    await Assert.That(StoredFormUnreadable.TryClassify(wrapped, out var failure)).IsTrue();
    await Assert.That(failure!.Cause).IsSameReferenceAs(cause);
  }

  /// <summary>A refusal inside an aggregate is found among its siblings.</summary>
  [Test]
  public async Task ARefusalInsideAnAggregateIsClassifiedAsync() {
    var cause = _unreadable();
    var aggregate = new AggregateException(new TimeoutException("unrelated"), new InvalidOperationException("outer", cause));

    await Assert.That(StoredFormUnreadable.TryClassify(aggregate, out var failure)).IsTrue();
    await Assert.That(failure!.Cause).IsSameReferenceAs(cause);
  }

  /// <summary>Anything else is not a stored-form failure, however deep the chain.</summary>
  [Test]
  public async Task AnotherFailureIsNotClassifiedAsync() {
    var chain = new InvalidOperationException("outer", new TimeoutException("inner", new AggregateException(new FormatException("deep"))));

    await Assert.That(StoredFormUnreadable.TryClassify(chain, out var failure)).IsFalse();
    await Assert.That(failure).IsNull();
  }
}

/// <summary>A holder whose instant is read by the canonical reader, as a document's would be.</summary>
public sealed class Holder {
  [System.Text.Json.Serialization.JsonConverter(typeof(CanonicalTemporalJsonConverters.InstantConverter))]
  public DateTime At { get; set; }
}

/// <summary>Source-generated metadata for <see cref="Holder"/>.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(Holder))]
public sealed partial class HolderContext : System.Text.Json.Serialization.JsonSerializerContext;
