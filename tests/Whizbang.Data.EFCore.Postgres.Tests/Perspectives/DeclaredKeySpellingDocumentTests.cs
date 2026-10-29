// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The rewrite of one stored document to its declared spelling (#1874): every key that matches a declared name only
/// case-insensitively moves to the declared name, at any depth and in every element of a collection; a key that is
/// already declared, or that matches nothing, is left alone; both spellings with the same value merge, and with
/// different values the document is left alone and the conflict reported with its path.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Perspectives/DeclaredKeySpellingDocument.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Perspectives/DeclaredKeyShape.cs</code-under-test>
public class DeclaredKeySpellingDocumentTests {
  private static readonly DeclaredKeyShape _point = DeclaredKeyShape.ForObject(
    new DeclaredMember("x", IsRequired: true), new DeclaredMember("y", IsRequired: true));

  private static readonly DeclaredKeyShape _picture = DeclaredKeyShape.ForObject(
    new DeclaredMember("title"),
    new DeclaredMember("Objects", Shape: DeclaredKeyShape.ForArray(DeclaredKeyShape.ForObject(
      new DeclaredMember("color"),
      new DeclaredMember("Points", Shape: DeclaredKeyShape.ForArray(_point))))));

  private static string _canonical(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);

  [Test]
  public async Task KeysInAnotherCase_MoveToTheDeclaredName_AtEveryDepthAsync() {
    var result = DeclaredKeySpellingDocument.Rewrite(
      """{"Title":"t","Objects":[{"Color":"red","Points":[{"X":327,"Y":362},{"X":1.25,"y":2}]},{"color":"b","Points":[]}]}""",
      _picture);

    await Assert.That(result.Conflicts).IsEmpty();
    await Assert.That(_canonical(result.Json!)).IsEqualTo(_canonical(
      """{"title":"t","Objects":[{"color":"red","Points":[{"x":327,"y":362},{"x":1.25,"y":2}]},{"color":"b","Points":[]}]}"""));
    await Assert.That(result.Renames.Select(r => $"{r.Path}: {r.From} -> {r.To}").Distinct().Order(StringComparer.Ordinal))
      .IsEquivalentTo(["$.Objects[*].Points[*]: X -> x", "$.Objects[*].Points[*]: Y -> y", "$.Objects[*]: Color -> color", "$: Title -> title"]);
  }

  [Test]
  public async Task ADocumentAlreadyDeclared_IsUnchangedAsync() {
    var result = DeclaredKeySpellingDocument.Rewrite("""{"title":"t","Objects":[{"color":"r","Points":[{"x":1,"y":2}]}]}""", _picture);

    await Assert.That(result.Json).IsNull().Because("nothing to write means no update is issued");
    await Assert.That(result.Renames).IsEmpty();
  }

  [Test]
  public async Task KeysNoDeclarationNames_AndValuesThatAreNotObjects_AreLeftAloneAsync() {
    var result = DeclaredKeySpellingDocument.Rewrite(
      """{"title":"t","Extra":{"X":1},"Objects":[null,7,{"color":"r","Points":null}]}""", _picture);

    await Assert.That(result.Json).IsNull()
      .Because("an unknown key is not a declared one in another case, and a null or a number has no keys");
  }

  [Test]
  public async Task ADocumentThatIsNotTheDeclaredShape_IsLeftAloneAsync() {
    await Assert.That(DeclaredKeySpellingDocument.Rewrite("""[{"Title":"t"}]""", _picture).Json).IsNull();
    await Assert.That(DeclaredKeySpellingDocument.Rewrite("""{"Objects":{"Color":"r"}}""", _picture).Json).IsNull()
      .Because("an object where the declaration expects a collection is not walked as an element");
  }

  [Test]
  public async Task BothSpellingsWithTheSameValue_MergeIntoTheDeclaredOneAsync() {
    var result = DeclaredKeySpellingDocument.Rewrite("""{"x":{"a":[1,2]},"X":{"a":[1,2]},"y":3}""",
      DeclaredKeyShape.ForObject(new DeclaredMember("x"), new DeclaredMember("y")));

    await Assert.That(result.Conflicts).IsEmpty();
    await Assert.That(_canonical(result.Json!)).IsEqualTo(_canonical("""{"x":{"a":[1,2]},"y":3}"""));
  }

  [Test]
  public async Task BothSpellingsWithDifferentValues_AreAConflict_AndTheDocumentIsLeftAloneAsync() {
    var result = DeclaredKeySpellingDocument.Rewrite(
      """{"title":"t","Objects":[{"color":"r","Points":[{"x":1,"y":2},{"x":5,"X":6,"y":7}]}]}""", _picture);

    await Assert.That(result.Json).IsNull();
    var conflict = result.Conflicts.Single();
    await Assert.That(conflict.Path).IsEqualTo("$.Objects[0].Points[1]");
    await Assert.That(conflict.Stored).IsEqualTo("X");
    await Assert.That(conflict.Declared).IsEqualTo("x");
  }

  [Test]
  public async Task AKeyMatchingTwoDeclaredNames_IsAConflictAsync() {
    var result = DeclaredKeySpellingDocument.Rewrite("""{"Ab":1}""",
      DeclaredKeyShape.ForObject(new DeclaredMember("ab"), new DeclaredMember("AB")));

    await Assert.That(result.Json).IsNull();
    await Assert.That(result.Conflicts.Single().Declared).IsEqualTo("ab or AB")
      .Because("which member the value belongs to cannot be decided by case alone");
  }

  [Test]
  public async Task TwoDeclaredNamesDifferingInCase_AreEachLeftAloneAsync() {
    var result = DeclaredKeySpellingDocument.Rewrite("""{"ab":1,"AB":2}""",
      DeclaredKeyShape.ForObject(new DeclaredMember("ab"), new DeclaredMember("AB")));

    await Assert.That(result.Json).IsNull();
    await Assert.That(result.Conflicts).IsEmpty();
  }

  [Test]
  public async Task ValuesKeepTheirExactTextAsync() {
    var result = DeclaredKeySpellingDocument.Rewrite("""{"X":330.7060738825059,"Y":1E+2}""", _point);

    await Assert.That(result.Json!).Contains("330.7060738825059").And.Contains("1E+2")
      .Because("a number is moved as written, never re-rendered through a double");
  }

  [Test]
  public async Task TheShape_DescribesItselfStably_ForTheJournalAsync() {
    await Assert.That(_picture.Describe())
      .IsEqualTo("{\"title\",\"Objects\":[{\"color\",\"Points\":[{\"x\"!,\"y\"!}]}]}");
    await Assert.That(DeclaredKeyShape.ForObject(new DeclaredMember("a\"b")).Describe()).IsEqualTo("{\"a\\\"b\"}");
  }

  [Test]
  public async Task ShapeFactories_RefuseNullsAsync() {
    await Assert.That(() => DeclaredKeyShape.ForArray(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => DeclaredKeyShape.ForObject((IEnumerable<DeclaredMember>)null!)).Throws<ArgumentNullException>();
    await Assert.That(() => DeclaredKeySpellingDocument.Rewrite(null!, _point)).Throws<ArgumentNullException>();
    await Assert.That(() => DeclaredKeySpellingDocument.Rewrite("{}", null!)).Throws<ArgumentNullException>();
  }
}
