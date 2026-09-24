using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The generated way to address a model's properties as expressions.
/// </summary>
/// <remarks>
/// The point of generating this is that the alternative is reflection, which a trimmer cannot see
/// through and native compilation cannot resolve. So what is asserted is that a path a filter would
/// name is present as a case the compiler can see, that a nested path is there too, and that a path
/// the model does not have is absent rather than a failure discovered when the filter runs.
/// </remarks>
public class PerspectiveAccessorGeneratorTests {
  private const string SOURCE = """
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace MyApp.Perspectives;

    public class AddressModel {
      public string City { get; init; } = "";
      public string Postcode { get; init; } = "";
    }

    public class CustomerModel {
      [StreamId]
      public Guid CustomerId { get; init; }
      public string Name { get; init; } = "";
      public int Orders { get; init; }
      public AddressModel Address { get; init; } = new();
    }

    public record CustomerCreated([property: StreamId] Guid CustomerId) : IEvent;

    public class CustomerPerspective : IPerspectiveFor<CustomerModel, CustomerCreated> {
      public CustomerModel Apply(CustomerModel? current, CustomerCreated @event) =>
        new CustomerModel { CustomerId = @event.CustomerId };
    }
    """;

  [Test]
  public async Task Accessors_CoverTopLevelAndNestedPaths_AndLookUpByNameAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveAccessorGenerator>(SOURCE);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "MyApp_Perspectives_CustomerModelAccessors.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("public static Expression<Func<global::MyApp.Perspectives.CustomerModel, string>> Name")
      .Because("a filter naming a property needs the expression that reads it, typed as the property is");
    await Assert.That(generated).Contains("_m => _m.Address.City")
      .Because("a model with structure is the one that attracts dynamic filtering, so nested paths resolve too");

    await Assert.That(generated).Contains("case \"Address.City\":")
      .Because("the lookup is by the path a filter writes, and the dot is part of that path");
    await Assert.That(generated).Contains("case \"CustomerId\":");

    await Assert.That(generated).Contains("accessor = null;")
      .Because("a path the model does not have has to be absent rather than an error found when the "
             + "filter runs, which is what reflection would have given");
  }

  /// <summary>
  /// The walk stops rather than following a model that refers back to its own type.
  /// </summary>
  [Test]
  public async Task Accessors_DoNotRecurseForeverThroughASelfReferenceAsync() {
    const string selfReferencing = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace MyApp.Perspectives;

      public class NodeModel {
        [StreamId]
        public Guid NodeId { get; init; }
        public string Label { get; init; } = "";
        public NodeModel? Parent { get; init; }
      }

      public record NodeCreated([property: StreamId] Guid NodeId) : IEvent;

      public class NodePerspective : IPerspectiveFor<NodeModel, NodeCreated> {
        public NodeModel Apply(NodeModel? current, NodeCreated @event) =>
          new NodeModel { NodeId = @event.NodeId };
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveAccessorGenerator>(selfReferencing);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "MyApp_Perspectives_NodeModelAccessors.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("case \"Parent.Label\":")
      .Because("one step through the reference is useful and is what a filter would write");
    await Assert.That(generated).DoesNotContain("Parent.Parent.Parent.Parent")
      .Because("a bound is what keeps a model that refers to itself from generating forever");
  }

  /// <summary>
  /// A model declared inside another type is named by the whole path and is no more visible than
  /// the type it sits in.
  /// </summary>
  /// <remarks>
  /// Two models nested in sibling types share a namespace and may share a simple name, so the
  /// declaring types are part of the accessor's name for the same reason they are part of the
  /// model's. Visibility is carried for a harder reason: a public property returning a type the
  /// consumer cannot see does not compile, and it is generated code that would not compile.
  /// </remarks>
  [Test]
  public async Task Accessors_ForANestedModel_AreNamedAndSeenAsTheModelIsAsync() {
    const string nested = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace MyApp.Perspectives;

      internal static class Ledger {
        public class EntryModel {
          [StreamId]
          public Guid EntryId { get; init; }
          public string Memo { get; init; } = "";
        }
      }

      public record EntryPosted([property: StreamId] Guid EntryId) : IEvent;

      public class EntryPerspective : IPerspectiveFor<Ledger.EntryModel, EntryPosted> {
        public Ledger.EntryModel Apply(Ledger.EntryModel? current, EntryPosted @event) =>
          new Ledger.EntryModel { EntryId = @event.EntryId };
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveAccessorGenerator>(nested);
    var generated = GeneratorTestHelper.GetGeneratedSource(
      result, "MyApp_Perspectives_Ledger_EntryModelAccessors.g.cs");

    await Assert.That(generated).IsNotNull()
      .Because("the file name has to be unique across the compilation, and a simple name is not");

    await Assert.That(generated!).Contains("internal static class Ledger_EntryModelAccessors")
      .Because("a public property yielding a type the consumer cannot see is generated code that "
             + "does not compile");
  }

  /// <summary>
  /// A collection is a path a filter can name, and not a way into the elements. A model with no
  /// path at all, and a class that is not a perspective, yield nothing rather than an empty class.
  /// </summary>
  /// <remarks>
  /// An element of a collection has no path of its own that a filter could write -- there is no
  /// way to say which element -- so descending into one would generate cases nothing can reach.
  /// </remarks>
  [Test]
  public async Task Accessors_StopAtACollection_AndAreNotWrittenForAModelWithNoPathsAsync() {
    const string shapes = """
      using System;
      using System.Collections.Generic;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace MyApp.Perspectives;

      public class TagListModel {
        [StreamId]
        public Guid ListId { get; init; }
        public List<string> Tags { get; init; } = new();
      }

      public class NothingModel {
      }

      public record ListTagged([property: StreamId] Guid ListId) : IEvent;

      public class TagListPerspective : IPerspectiveFor<TagListModel, ListTagged> {
        public TagListModel Apply(TagListModel? current, ListTagged @event) =>
          new TagListModel { ListId = @event.ListId };
      }

      public class NothingPerspective : IPerspectiveFor<NothingModel, ListTagged> {
        public NothingModel Apply(NothingModel? current, ListTagged @event) => new NothingModel();
      }

      public class NotAPerspective : IDisposable {
        public void Dispose() { }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveAccessorGenerator>(shapes);

    var tags = GeneratorTestHelper.GetGeneratedSource(result, "MyApp_Perspectives_TagListModelAccessors.g.cs");
    await Assert.That(tags).IsNotNull();
    await Assert.That(tags!).Contains("case \"Tags\":")
      .Because("the collection itself is a path a filter can name");
    await Assert.That(tags).DoesNotContain("Tags.")
      .Because("an element has no path of its own, so descending would generate cases nothing reaches");

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(
      result, "MyApp_Perspectives_NothingModelAccessors.g.cs")).IsNull()
      .Because("a class with nothing to address is nothing to generate");
  }
}
