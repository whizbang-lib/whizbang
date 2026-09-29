using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Explains a failed read of a JSON-mapped perspective document as the refusal it is: which value, at
/// which path, could not be read as the property mapped there.
/// </summary>
/// <remarks>
/// <para>
/// A document mapped with <c>ComplexProperty().ToJson()</c> is read by Entity Framework's own JSON
/// materializer, which reads each scalar straight off the reader. A value of the wrong JSON type, a
/// number where the property is a string, surfaces as the reader's bare
/// <see cref="InvalidOperationException"/> ("Cannot get the value of a token type 'Number' as a
/// string"), with no path, no property and nothing that says it came from a stored document. The worker
/// classifies a stored document no reader takes by finding a <see cref="JsonException"/>
/// (<see cref="StoredFormUnreadable"/>); this failure carried none, so the stream took the generic
/// failure path: no announcement naming the path, no <c>stored_form_unreadable</c> count, and no
/// Degraded health.
/// </para>
/// <para>
/// When a read fails, the row's stored documents are walked against the same model Entity Framework
/// read them with. The first value whose JSON type the property mapped at its place cannot take is
/// reported as a <see cref="JsonException"/> carrying the path, with the original failure inside it. When
/// the walk finds nothing, the failure was not the document's and is left exactly as raised. The walk
/// costs nothing on a read that succeeds.
/// </para>
/// <para>
/// Dates, times and durations are skipped: their readers (<see cref="CanonicalTemporalJsonReaderWriters"/>)
/// already refuse with a <see cref="JsonException"/> of their own. So are a missing key and a null, which
/// are not a value of the wrong type. A property is checked as the type it is stored as, after its value
/// converter: an enum Entity Framework stores as its number is read as a number, one converted to text
/// as a string.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/MappedDocumentReadFailureTests.cs</tests>
internal static class MappedDocumentReadFailure {
  /// <summary>
  /// Whether a failed read could be a mapped document's value the materializer could not read.
  /// </summary>
  /// <remarks>
  /// The reader raises <see cref="InvalidOperationException"/> for a token of the wrong type and
  /// <see cref="FormatException"/> for a token of the right type it cannot parse, such as a string that
  /// is no identifier where the property is a <see cref="Guid"/>. A disposed context is an
  /// <see cref="InvalidOperationException"/> too, and is never the document's.
  /// </remarks>
  internal static bool MayBeDocumentRead(Exception failure) =>
    failure is (InvalidOperationException or FormatException) and not ObjectDisposedException;

  /// <summary>
  /// Reads a row's stored documents and explains a failed read of them, or returns null when they
  /// hold nothing the model cannot read.
  /// </summary>
  /// <typeparam name="TModel">The perspective's model type.</typeparam>
  /// <param name="context">The context the read failed on.</param>
  /// <param name="id">The row's id.</param>
  /// <param name="failure">The exception the read raised.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The refusal, carrying <paramref name="failure"/> as its inner exception, or null.</returns>
  internal static async Task<JsonException?> ExplainAsync<TModel>(
      DbContext context, Guid id, Exception failure, CancellationToken cancellationToken) where TModel : class {
    // Qualifying the table first also refuses, clearly, a model the context does not map.
    var table = PerspectiveRowVersionSql.QualifiedTable<TModel>(context);
    var rowType = context.Model.FindEntityType(typeof(PerspectiveRow<TModel>))!;
    var documents = new Dictionary<string, string?>(StringComparer.Ordinal);
    foreach (var complex in rowType.GetComplexProperties()) {
      if (complex.ComplexType.GetContainerColumnName() is { } column) {
        documents[column] = await PerspectiveRowVersionSql.ReadDocumentTextAsync(
          context, table, column, id, cancellationToken).ConfigureAwait(false);
      }
    }
    return Explain(rowType, typeof(TModel).Name, column => documents.GetValueOrDefault(column), failure);
  }

  /// <summary>
  /// Explains a failed read of a row's stored documents, or returns null when they hold nothing the
  /// model cannot read.
  /// </summary>
  /// <param name="rowType">The row's entity type, as the context that read it maps it.</param>
  /// <param name="modelName">The perspective model's name, for the message.</param>
  /// <param name="storedDocument">The stored text of a document column, or null when it holds none.</param>
  /// <param name="failure">The exception the read raised.</param>
  /// <returns>The refusal, carrying <paramref name="failure"/> as its inner exception, or null.</returns>
  internal static JsonException? Explain(
      IEntityType rowType, string modelName, Func<string, string?> storedDocument, Exception failure) {
    foreach (var complex in rowType.GetComplexProperties()) {
      if (complex.ComplexType.GetContainerColumnName() is not { } column || storedDocument(column) is not { } stored) {
        continue;
      }
      using var document = JsonDocument.Parse(stored);
      var mismatch = _walkComplex(complex, document.RootElement, "$");
      if (mismatch is { } found) {
        return new JsonException(
          $"The stored {column} document of a {modelName} row holds {_describe(found.Token)} at {found.Path}, where {found.Property} reads {found.Expected}",
          found.Path, lineNumber: null, bytePositionInLine: null, failure);
      }
    }
    return null;
  }

  private readonly record struct Mismatch(string Path, JsonValueKind Token, string Property, string Expected);

  private static Mismatch? _walkComplex(IComplexProperty complex, JsonElement element, string path) {
    if (element.ValueKind == JsonValueKind.Null) {
      return null;
    }
    if (complex.IsCollection) {
      if (element.ValueKind != JsonValueKind.Array) {
        return new Mismatch(path, element.ValueKind, complex.Name, "an array");
      }
      var index = 0;
      foreach (var item in element.EnumerateArray()) {
        var inner = _walkObject(complex, item, string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]"));
        if (inner is not null) {
          return inner;
        }
        index++;
      }
      return null;
    }
    return _walkObject(complex, element, path);
  }

  private static Mismatch? _walkObject(IComplexProperty complex, JsonElement element, string path) {
    if (element.ValueKind == JsonValueKind.Null) {
      return null;
    }
    if (element.ValueKind != JsonValueKind.Object) {
      return new Mismatch(path, element.ValueKind, complex.Name, "an object");
    }
    foreach (var property in complex.ComplexType.GetProperties()) {
      var key = property.GetJsonPropertyName()!;
      if (element.TryGetProperty(key, out var value)) {
        var mismatch = _checkScalar(property, value, $"{path}.{key}");
        if (mismatch is not null) {
          return mismatch;
        }
      }
    }
    foreach (var nested in complex.ComplexType.GetComplexProperties()) {
      var key = nested.GetJsonPropertyName()!;
      if (element.TryGetProperty(key, out var value)) {
        var mismatch = _walkComplex(nested, value, $"{path}.{key}");
        if (mismatch is not null) {
          return mismatch;
        }
      }
    }
    return null;
  }

  private static Mismatch? _checkScalar(IProperty property, JsonElement value, string path) {
    if (value.ValueKind == JsonValueKind.Null) {
      return null;
    }
    // A temporal is judged by its model type, not its stored one: the convention stores it as a number
    // and its reader also takes a rendering, so the number the converter declares is not all it reads.
    if (CanonicalTemporalConvention.KindOf(property.ClrType) is not null) {
      return null;
    }
    var name = $"{property.DeclaringType.ClrType.Name}.{property.Name}";
    if (property.IsPrimitiveCollection) {
      return value.ValueKind == JsonValueKind.Array ? null : new Mismatch(path, value.ValueKind, name, "an array");
    }
    var stored = property.GetTypeMapping().Converter?.ProviderClrType ?? property.ClrType;
    var type = Nullable.GetUnderlyingType(stored) ?? stored;
    var expected = _expectedFor(type);
    return expected is null || _reads(type, value) ? null : new Mismatch(path, value.ValueKind, $"{name} ({type.Name})", expected);
  }

  /// <summary>What a property of this stored type reads, or null for a type the walk leaves to its reader.</summary>
  private static string? _expectedFor(Type type) {
    if (type == typeof(string) || type == typeof(char) || type == typeof(Guid)) {
      return "a string";
    }
    if (type == typeof(bool)) {
      return "true or false";
    }
    return _isNumber(type) ? "a number" : null;
  }

  private static bool _isNumber(Type type) =>
    type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
    || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte)
    || type == typeof(decimal) || type == typeof(double) || type == typeof(float);

  /// <summary>
  /// Whether the value reads as the type. A number is checked for its token only: a value out of the
  /// type's range is rare enough to leave to the reader's own message.
  /// </summary>
  private static bool _reads(Type type, JsonElement value) {
    if (type == typeof(bool)) {
      return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }
    if (type == typeof(Guid)) {
      return value.ValueKind == JsonValueKind.String && value.TryGetGuid(out _);
    }
    return value.ValueKind == (type == typeof(string) || type == typeof(char) ? JsonValueKind.String : JsonValueKind.Number);
  }

  private static string _describe(JsonValueKind token) => token switch {
    JsonValueKind.Number => "a number",
    JsonValueKind.String => "a string",
    JsonValueKind.True or JsonValueKind.False => "a boolean",
    JsonValueKind.Array => "an array",
    _ => "an object",
  };
}
