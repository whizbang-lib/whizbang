#pragma warning disable IDE1006 // Naming Styles

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Reusable code snippets for WhizbangId JsonTypeInfo generation.
/// These snippets are extracted and used by the WhizbangIdGenerator.
/// </summary>
internal class WhizbangIdSnippets {

  #region WHIZBANGID_TYPE_CHECK
  if (type == typeof(__FULLY_QUALIFIED_NAME__)) {
    // Create JsonTypeInfo for __TYPE_NAME__ using the generated converter
    var converter = new __FULLY_QUALIFIED_NAME__JsonConverter();
    var jsonTypeInfo = JsonMetadataServices.CreateValueInfo<__FULLY_QUALIFIED_NAME__>(options, converter);
    return jsonTypeInfo;
  }
  #endregion

  #region WHIZBANGID_NULLABLE_TYPE_CHECK
  if (type == typeof(__FULLY_QUALIFIED_NAME__?)) {
    // Create JsonTypeInfo for __TYPE_NAME__? using nullable converter wrapper
    var converter = JsonMetadataServices.GetNullableConverter<__FULLY_QUALIFIED_NAME__>(options);
    var jsonTypeInfo = JsonMetadataServices.CreateValueInfo<__FULLY_QUALIFIED_NAME__?>(options, converter);
    return jsonTypeInfo;
  }
  #endregion

  #region DERIVE_FROM_PINNED
  /// <summary>
  /// The pinned id of __TYPE_NAME__: the input <see cref="DeriveFrom"/> derives from, so renaming or moving the type keeps
  /// every id it derives.
  /// </summary>
  public const string PinnedId = "__PINNED_ID__";

  /// <summary>
  /// Derives the __TYPE_NAME__ for <paramref name="purpose"/> from <paramref name="handling"/>: the handled message, the
  /// service, the handler, the receptor and the pinned id, so a retry of the handling derives the same id.
  /// </summary>
  /// <param name="handling">The handling the id is created in: <c>Handling.Current</c> inside a receptor body.</param>
  /// <param name="purpose">What the id is for in the handling: one id per purpose, so name the item in it for a collection.</param>
  /// <returns>A UUIDv7 that sorts with the handled message.</returns>
  /// <exception cref="InvalidOperationException">
  /// Outside a receptor handling, after the handling ended, or for a purpose already derived for __TYPE_NAME__ in it.
  /// </exception>
  public static __TYPE_NAME__ DeriveFrom(global::Whizbang.Core.Messaging.Handling handling, string purpose) =>
    new(TrackedGuid.FromExternal(global::Whizbang.Core.Messaging.StrongIdDerivation.Derive(handling, PinnedId, purpose, "__TYPE_NAME__")));
  #endregion

  #region DERIVE_FROM_UNPINNED
  /// <summary>
  /// Not available until __TYPE_NAME__ is pinned: a derived id needs an input that survives a rename. Pin the type with
  /// <c>[PinnedId("...")]</c> on its declaration, or <c>[WhizbangId(PinnedId = "...")]</c> where it is declared. A call
  /// does not compile (WHIZ118).
  /// </summary>
  /// <param name="handling">The handling the id would be created in.</param>
  /// <param name="purpose">What the id would be for.</param>
  /// <returns>Never returns.</returns>
  /// <exception cref="NotSupportedException">Always: __TYPE_NAME__ has no pinned id.</exception>
  public static __TYPE_NAME__ DeriveFrom(global::Whizbang.Core.Messaging.Handling handling, string purpose) =>
    throw global::Whizbang.Core.Messaging.StrongIdDerivation.NotPinned("__TYPE_NAME__");
  #endregion

  // Placeholder declarations for IDE validation
  internal class __FULLY_QUALIFIED_NAME__ { }
  internal class __TYPE_NAME__ { }
}

#pragma warning restore IDE1006
