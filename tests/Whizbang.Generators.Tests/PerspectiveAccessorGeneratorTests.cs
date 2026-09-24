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
}
