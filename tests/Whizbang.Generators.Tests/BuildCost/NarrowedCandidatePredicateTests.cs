// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests.BuildCost;

/// <summary>
/// The predicates narrowed for #1650 used to admit every type (or every two-argument generic parameter) in a
/// consumer. Each now admits only a declaration its transform could accept: one that can implement an interface
/// (a base list, or partial), and for the pinned-id registry one that can also carry an attribute; for the
/// auto-populate discovery one that can inherit or declare an attributed property; for the multi-model lens
/// query a parameter typed by the name <c>ILensQuery</c> itself. Generated output is unchanged, which the
/// generators' own tests and the caching tests hold; these pin each side of every narrowing.
/// </summary>
/// <docs>operations/performance/build-cost</docs>
[Category("Generators")]
[Category("BuildCost")]
public class NarrowedCandidatePredicateTests {
  [Test]
  [Arguments("public record E : IEvent;", true)]
  [Arguments("public class E : BaseEvent { }", true)]
  [Arguments("public partial record E;", true)]
  [Arguments("public struct E : ICommand { }", true)]
  [Arguments("public record E;", false)]
  [Arguments("[Marker] public class E { }", false)]
  [Arguments("public interface IE : IEvent { }", false)]
  public async Task IsCatalogTypeCandidate_NeedsAClassRecordOrStructThatCanImplementAsync(string source, bool expected) {
    await Assert.That(MessageTypeCatalogGenerator.IsCatalogTypeCandidate(_type(source))).IsEqualTo(expected);
  }

  [Test]
  [Arguments("public record E : IEvent;", true)]
  [Arguments("public class E : BaseEvent { }", true)]
  [Arguments("public partial class E { }", true)]
  [Arguments("public record E;", false)]
  [Arguments("public struct E : IEvent { }", false)]
  public async Task IsTopicMessageCandidate_NeedsAClassOrRecordThatCanImplementAsync(string source, bool expected) {
    await Assert.That(TopicRegistryGenerator.IsTopicMessageCandidate(_type(source))).IsEqualTo(expected);
  }

  [Test]
  [Arguments("[PinnedId(\"x\")] public record E : IEvent;", true)]
  [Arguments("[PinnedId(\"x\")] public struct E : IEvent { }", true)]
  [Arguments("public partial record E;", true)]
  [Arguments("public partial record E : IEvent;", true)]
  [Arguments("[PinnedId(\"x\")] public partial record E;", true)]
  [Arguments("public record E : IEvent;", false)]
  [Arguments("[PinnedId(\"x\")] public record E;", false)]
  [Arguments("[PinnedId(\"x\")] public interface IE : IEvent { }", false)]
  public async Task IsPinnedTypeCandidate_NeedsATypeThatCanImplementAndCarryAnAttributeAsync(string source, bool expected) {
    await Assert.That(PinnedIdRegistryGenerator.IsPinnedTypeCandidate(_type(source))).IsEqualTo(expected);
  }

  [Test]
  [Arguments("public class C : Base { }", true)]
  [Arguments("public partial class C { }", true)]
  [Arguments("public class C { [PopulateTimestamp(0)] public DateTime At { get; set; } }", true)]
  [Arguments("public record C([property: PopulateFromContext(0)] string User);", true)]
  [Arguments("public interface I { [PopulateTimestamp(0)] DateTime At { get; } }", true)]
  [Arguments("public class C { public DateTime At { get; set; } }", false)]
  [Arguments("public record C(string User);", false)]
  public async Task IsPopulatedTypeCandidate_NeedsATypeThatCanInheritOrDeclareAnAttributedPropertyAsync(string source, bool expected) {
    await Assert.That(AutoPopulateDiscoveryGenerator.IsPopulatedTypeCandidate(_type(source))).IsEqualTo(expected);
  }

  [Test]
  public async Task IsPopulatedTypeCandidate_NotATypeDeclaration_IsRejectedAsync() {
    await Assert.That(AutoPopulateDiscoveryGenerator.IsPopulatedTypeCandidate(SyntaxFactory.ParseExpression("a.B()"))).IsFalse();
  }

  [Test]
  [Arguments("ILensQuery<Order, Customer> query", true)]
  [Arguments("ILensQuery<Order, Customer, Product> query", true)]
  [Arguments("ILensQuery<Order> query", false)]
  [Arguments("Dictionary<string, int> totals", false)]
  [Arguments("Lenses.ILensQuery<Order, Customer> query", false)]
  [Arguments("int count", false)]
  public async Task IsMultiLensQueryParameterCandidate_NeedsAnILensQueryWithTwoOrMoreModelsAsync(string parameter, bool expected) {
    var node = SyntaxFactory.ParseParameterList($"({parameter})").Parameters[0];

    await Assert.That(EFCoreServiceRegistrationGenerator.IsMultiLensQueryParameterCandidate(node)).IsEqualTo(expected);
  }

  private static SyntaxNode _type(string source) =>
      CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().First();
}
