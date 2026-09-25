namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// The jsonb value both Postgres collective adapters assign to an array property for
/// <c>ICollectiveSetters.UpsertElement</c>: the array with the element whose key matches replaced
/// where it stands, or the element appended when none matches.
/// </summary>
/// <remarks>
/// <para>
/// Shared so the EF Core and Dapper adapters cannot drift on what an upsert means. The expression reads
/// the row's own <c>data</c>, so it rides the same <c>jsonb_set</c> chain as every other setter and the
/// whole spec stays one set-based UPDATE.
/// </para>
/// <para>
/// Keys compare as stored JSON (<c>element-&gt;'Key' = new-&gt;'Key'</c>), so a string, number or
/// identifier key needs no cast. <c>WITH ORDINALITY</c> keeps every other element in its place. An
/// absent or null array, or any non-array value, is treated as empty.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events#keyed-array-elements</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveElementUpsertSqlTests.cs</tests>
public static class CollectiveElementUpsertSql {

  /// <summary>Builds the value expression.</summary>
  /// <param name="arrayProperty">The model's array property, as stored in <c>data</c>.</param>
  /// <param name="keyProperty">The element property that identifies an element.</param>
  /// <param name="element">SQL for the new element as jsonb, e.g. <c>@p0::jsonb</c>.</param>
  /// <returns>A jsonb expression over the row's <c>data</c>.</returns>
  public static string ValueSql(string arrayProperty, string keyProperty, string element) {
    _ensureIdentifier(arrayProperty, nameof(arrayProperty));
    _ensureIdentifier(keyProperty, nameof(keyProperty));
    ArgumentException.ThrowIfNullOrWhiteSpace(element);

    var array = $"data->'{arrayProperty}'";
    var matches = $"wh_e.v->'{keyProperty}' = ({element})->'{keyProperty}'";
    return $"CASE WHEN jsonb_typeof({array}) = 'array' THEN "
      + $"CASE WHEN EXISTS (SELECT 1 FROM jsonb_array_elements({array}) AS wh_e(v) WHERE {matches}) "
      + $"THEN (SELECT jsonb_agg(CASE WHEN {matches} THEN {element} ELSE wh_e.v END ORDER BY wh_e.i) "
      + $"FROM jsonb_array_elements({array}) WITH ORDINALITY AS wh_e(v, i)) "
      + $"ELSE {array} || jsonb_build_array({element}) END "
      + $"ELSE jsonb_build_array({element}) END";
  }

  // The names come from the model's own C# members, never from input, but they are embedded in SQL,
  // so anything that is not a plain identifier is refused rather than trusted.
  private static void _ensureIdentifier(string name, string parameter) {
    if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) {
      throw new ArgumentException($"'{name}' is not a plain identifier and cannot be embedded in the upsert expression.", parameter);
    }
  }
}
