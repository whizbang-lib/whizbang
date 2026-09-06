using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for TopicFilterGenerator targeting the attribute-matching walk in
/// <c>_extractTopicFilters</c> and the string/enum extraction branches of
/// <c>_extractFilterString</c>. Complements TopicFilterGeneratorTests.cs.
/// </summary>
/// <remarks>
/// Line 80 (<c>semanticModel.GetDeclaredSymbol(typeDeclaration, ...) is not INamedTypeSymbol</c>)
/// is not covered here: the predicate only ever hands the transform a
/// <c>ClassDeclarationSyntax</c> or <c>RecordDeclarationSyntax</c>, and Roslyn guarantees
/// <c>GetDeclaredSymbol</c> returns an <see cref="INamedTypeSymbol"/> for those node kinds. It is
/// an API-guaranteed guard, not a reachable branch.
/// </remarks>
public class TopicFilterGeneratorCoverageTests {
  [Test]
  public async Task Generator_UnresolvableAttribute_DoesNotPreventFilterDiscoveryAsync() {
    // If an unresolvable attribute made discovery bail out entirely, a command mid-edit next to a
    // typo'd attribute would silently lose its topic filter and the routed message would never arrive.
    const string source = """

    using Whizbang.Core;

    namespace TestNamespace;

    [TotallyUnknownAttributeThatDoesNotExist]
    [TopicFilter("orders.create")]
    public record CreateOrderCommand : ICommand {
    }

    """;

    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    await Assert.That(result.Diagnostics.Any(d => d.Id == "CS8785")).IsFalse()
      .Because("an unresolvable attribute must not crash the generator");

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource).Contains("CreateOrderCommand");
    await Assert.That(registrySource).Contains("orders.create");
  }

  [Test]
  public async Task Generator_UnrelatedAttribute_IsSkippedWhileValidFilterIsStillDiscoveredAsync() {
    // An attribute that resolves fine but isn't a TopicFilter must be walked past, not mistaken
    // for one; otherwise a command decorated with an ordinary attribute would lose its routing.
    const string source = """

    using System;
    using Whizbang.Core;

    namespace TestNamespace;

    [Obsolete]
    [TopicFilter("orders.create")]
    public record CreateOrderCommand : ICommand {
    }

    """;

    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource).Contains("CreateOrderCommand");
    await Assert.That(registrySource).Contains("orders.create");
  }

  [Test]
  public async Task Generator_CommandWithOnlyUnrelatedAttributes_ContributesNoFilterAsync() {
    // A command whose only attributes are unrelated to TopicFilter must contribute nothing to the
    // registry rather than an empty or garbage entry that later confuses a consumer's routing.
    const string source = """

    using System;
    using Whizbang.Core;

    namespace TestNamespace;

    [Obsolete]
    public record UnfilteredCommand : ICommand {
    }

    [TopicFilter("orders.create")]
    public record CreateOrderCommand : ICommand {
    }

    """;

    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource).DoesNotContain("UnfilteredCommand");
    await Assert.That(registrySource).Contains("CreateOrderCommand");
  }

  [Test]
  public async Task Generator_DerivedAttributeWithNoConstructorArguments_ContributesNoFilterAsync() {
    // A TopicFilter-derived attribute that supplies its own hardcoded base-constructor argument
    // must not leak that hardcoded value into the registry; only what the attribute usage itself
    // passed counts as a real filter, or a consumer would be routed against text nobody wrote at
    // the call site.
    const string source = """

    using Whizbang.Core;

    namespace TestNamespace;

    public class ZeroArgFilterAttribute : TopicFilterAttribute {
      public ZeroArgFilterAttribute() : base("hardcoded.topic") { }
    }

    [ZeroArgFilter]
    [TopicFilter("orders.create")]
    public record CreateOrderCommand : ICommand {
    }

    """;

    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource).Contains("orders.create");
    await Assert.That(registrySource).DoesNotContain("hardcoded.topic");
  }

  [Test]
  public async Task Generator_EnumFilterReferencingUndefinedMember_IsSkippedWithoutCrashingAsync() {
    // The moment after renaming or deleting an enum member, an existing [TopicFilter<TEnum>] usage
    // references a name that no longer resolves; the generator must decline gracefully rather than
    // emit a filter string with no discoverable meaning.
    const string source = """

    using Whizbang.Core;

    namespace TestNamespace;

    public enum Topics {
      OrdersCreated
    }

    [TopicFilter<Topics>(Topics.NoSuchMember)]
    public record CreateOrderCommand : ICommand {
    }

    """;

    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    await Assert.That(result.Diagnostics.Any(d => d.Id == "CS8785")).IsFalse()
      .Because("an unresolvable enum member must not crash the generator");

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    if (registrySource is not null) {
      await Assert.That(registrySource).DoesNotContain("CreateOrderCommand");
    }
  }

  [Test]
  public async Task Generator_EnumFilterWithOutOfRangeValue_FallsBackToRawNumericValueAsync() {
    // An enum-based filter cast to a value with no matching member (e.g. after removing the member
    // the code once pointed at) must still produce a stable, inspectable filter string rather than
    // silently dropping the command from the registry.
    const string source = """

    using Whizbang.Core;

    namespace TestNamespace;

    public enum Topics {
      OrdersCreated,
      OrdersUpdated
    }

    [TopicFilter<Topics>((Topics)999)]
    public record CreateOrderCommand : ICommand {
    }

    """;

    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource).Contains("CreateOrderCommand");
    await Assert.That(registrySource).Contains("999");
  }

  [Test]
  public async Task Generator_EnumMemberWithNullDescriptionValue_FallsBackToSymbolNameAsync() {
    // [Description(null)] is a legal but empty annotation on an enum member; the generator must
    // fall back to the enum member's own name rather than emit a null or blank routing key.
    const string source = """

    using System.ComponentModel;
    using Whizbang.Core;

    namespace TestNamespace;

    public enum Topics {
      [Description(null)]
      OrdersCreated
    }

    [TopicFilter<Topics>(Topics.OrdersCreated)]
    public record CreateOrderCommand : ICommand {
    }

    """;

    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource).Contains("OrdersCreated");
  }

  [Test]
  public async Task Generator_DerivedAttributeWithNonStringNonEnumArgument_ContributesNoFilterAsync() {
    // A TopicFilter-derived attribute whose own constructor argument is neither a string nor an
    // enum (e.g. an int code translated internally before calling the base constructor) is a shape
    // the extractor cannot turn into a routing key; it must be dropped rather than emit whatever
    // the derived attribute happened to pass to its base class.
    const string source = """

    using Whizbang.Core;

    namespace TestNamespace;

    public class NumericFilterAttribute : TopicFilterAttribute {
      public NumericFilterAttribute(int code) : base("code-" + code) { }
    }

    [NumericFilter(42)]
    [TopicFilter("orders.create")]
    public record CreateOrderCommand : ICommand {
    }

    """;

    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>(source);

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource).Contains("orders.create");
    await Assert.That(registrySource).DoesNotContain("code-42");
  }
}
