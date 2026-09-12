namespace Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

/// <summary>
/// Which mechanism compiles a perspective equality filter into a jsonb containment test.
/// </summary>
/// <remarks>
/// <para>
/// Two mechanisms exist while the second is being proven, and they differ in where they act rather
/// than in what they mean. Both build the same containment document from the same stand-down rules,
/// so choosing between them is choosing a mechanism and not a behavior. Exactly one is ever in force:
/// both running would compile a filter twice.
/// </para>
/// <para>
/// The names describe where the work happens, because that is the only thing a reader needs in order
/// to know which code is running and which set of limitations applies.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/ContainmentModeTests.cs</tests>
public enum ContainmentMode {
  /// <summary>
  /// No rewrite. A property comparison compiles to a text extraction, which is correct and reads
  /// every row.
  /// </summary>
  /// <remarks>
  /// The rollback position, and what the original two-state switch meant by off.
  /// </remarks>
  Off = 0,

  /// <summary>
  /// The LINQ tree is rewritten before Entity Framework translates it.
  /// </summary>
  /// <remarks>
  /// The mechanism that has shipped, and the default. It can see shapes Entity Framework refuses to
  /// translate, which is the only reason anything runs on the tree at all, and it cannot see a value
  /// converter's effect, which is what the other mechanism is for.
  /// </remarks>
  ExpressionTree = 1,

  /// <summary>
  /// What Entity Framework translated is reshaped afterwards.
  /// </summary>
  /// <remarks>
  /// Sees the value converter already applied to both sides of a comparison and every node carrying a
  /// type mapping, so it needs no per-type knowledge. It cannot see a shape Entity Framework refused
  /// to translate, because by then the refusal has already been raised.
  /// </remarks>
  TranslatedTree = 2,
}
