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

  // #2604's strong-id discovery admitted every attributed property and parameter, so the registry looked up a symbol
  // for every [JsonPropertyName] or [Required] in a consumer (145 ms to 860 ms). It now admits only a declaration
  // that names [WhizbangId], however it is written.
  [Test]
  [Arguments("public class C { [WhizbangId] public OrderId Id { get; set; } }", true)]
  [Arguments("public class C { [WhizbangIdAttribute] public OrderId Id { get; set; } }", true)]
  [Arguments("public class C { [Whizbang.Core.WhizbangId] public OrderId Id { get; set; } }", true)]
  [Arguments("public class C { [global::Whizbang.Core.WhizbangId] public OrderId Id { get; set; } }", true)]
  [Arguments("public class C { [global::WhizbangId] public OrderId Id { get; set; } }", true)]
  [Arguments("public class C { [Required, WhizbangId(PinnedId = \"x\")] public OrderId Id { get; set; } }", true)]
  [Arguments("public class C { [JsonPropertyName(\"id\")] public Guid Id { get; set; } }", false)]
  [Arguments("public class C { public OrderId Id { get; set; } }", false)]
  public async Task IsStrongIdPropertyCandidate_NeedsAWhizbangIdAttributeAsync(string source, bool expected) {
    var node = _first<PropertyDeclarationSyntax>(source);
    await Assert.That(PinnedIdRegistryGenerator.IsStrongIdPropertyCandidate(node)).IsEqualTo(expected);
    await Assert.That(PinnedTypeLedgerGenerator.IsStrongIdPropertyCandidate(node)).IsEqualTo(expected);
  }

  [Test]
  [Arguments("public record R([WhizbangId] LineId Id);", true)]
  [Arguments("public record R([FromBody] LineId Id);", false)]
  [Arguments("public record R(LineId Id);", false)]
  public async Task IsStrongIdParameterCandidate_NeedsAWhizbangIdAttributeAsync(string source, bool expected) {
    var node = _first<ParameterSyntax>(source);
    await Assert.That(PinnedIdRegistryGenerator.IsStrongIdParameterCandidate(node)).IsEqualTo(expected);
    await Assert.That(PinnedTypeLedgerGenerator.IsStrongIdParameterCandidate(node)).IsEqualTo(expected);
  }

  [Test]
  [Arguments("[WhizbangId] public readonly partial struct ProductId;", true)]
  [Arguments("[StructLayout(LayoutKind.Auto)] public struct S { }", false)]
  [Arguments("[WhizbangId] public partial class ProductId;", false)]
  public async Task IsStrongIdStructCandidate_NeedsAWhizbangIdAttributeAsync(string source, bool expected) {
    var node = _first<TypeDeclarationSyntax>(source);
    await Assert.That(PinnedIdRegistryGenerator.IsStrongIdStructCandidate(node)).IsEqualTo(expected);
  }

  // Each generator discovers pinned types and every strong-id route through ONE provider: a provider per route ran its
  // predicate on every node of every file, which is what made #2604's registry and ledger slow.
  [Test]
  [Arguments("[PinnedId(\"x\")] public record E : IEvent;", true)]
  [Arguments("[WhizbangId] public readonly partial struct ProductId;", true)]
  [Arguments("public class C { [WhizbangId] public OrderId Id { get; set; } }", true)]
  [Arguments("public record R([WhizbangId] LineId Id);", true)]
  [Arguments("public class C { [Required] public string Name { get; set; } = \"\"; }", false)]
  [Arguments("public record R([FromBody] LineId Id);", false)]
  public async Task IsPinnedIdCandidate_AdmitsEachRouteAndNothingElseAsync(string source, bool expected) {
    var admitted = _admits(source, PinnedIdRegistryGenerator.IsPinnedIdCandidate);
    await Assert.That(admitted).IsEqualTo(expected);
  }

  [Test]
  [Arguments("[PinnedId(\"x\")] public record E : IEvent;", true)]
  [Arguments("[WhizbangId] public readonly partial struct ProductId;", true)]
  [Arguments("public class C { [WhizbangId] public OrderId Id { get; set; } }", true)]
  [Arguments("public record R([WhizbangId] LineId Id);", true)]
  [Arguments("public class C { [Required] public string Name { get; set; } = \"\"; }", false)]
  [Arguments("public record R([FromBody] LineId Id);", false)]
  public async Task IsLedgerCandidate_AdmitsEachRouteAndNothingElseAsync(string source, bool expected) {
    var admitted = _admits(source, PinnedTypeLedgerGenerator.IsLedgerCandidate);
    await Assert.That(admitted).IsEqualTo(expected);
  }

  private static bool _admits(string source, Func<SyntaxNode, bool> predicate) =>
      CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().Any(predicate);

  private static T _first<T>(string source) where T : SyntaxNode =>
      CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<T>().First();

  private static TypeDeclarationSyntax _type(string source) =>
      CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().First();
}
