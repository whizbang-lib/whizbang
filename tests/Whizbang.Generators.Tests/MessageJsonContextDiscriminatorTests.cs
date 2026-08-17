using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests the <c>$type</c> discriminators the generator assigns to a polymorphic base's derived types.
///
/// <para>The discriminator is the simple type name, and that is the wire format already present in
/// stored payloads — so it must stay the simple name whenever the names are distinct. Simple names
/// are not unique in general though: two types sharing a name under one base make STJ throw
/// <c>InvalidOperationException: … has already specified a type discriminator …</c> when it
/// configures the base, which takes down the entire base typeinfo rather than just the colliding
/// pair.</para>
/// </summary>
[Category("SourceGenerators")]
[Category("JsonSerialization")]
public class MessageJsonContextDiscriminatorTests {

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithDistinctSimpleNames_KeepsSimpleNameDiscriminatorAsync() {
    const string source = @"
using Whizbang.Core;

namespace ConsumerApp.Events;

public class OrderEventBase : IEvent {
  public System.Guid MessageId { get; set; }
}

public sealed class OrderPlaced : OrderEventBase { public string OrderId { get; set; } = """"; }
public sealed class OrderShipped : OrderEventBase { public string OrderId { get; set; } = """"; }
";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    await Assert.That(code!).Contains("new JsonDerivedType(typeof(global::ConsumerApp.Events.OrderPlaced), \"OrderPlaced\")")
      .Because("The simple name is the discriminator already written into stored payloads — it must not change when nothing collides.");
    await Assert.That(code!).Contains("new JsonDerivedType(typeof(global::ConsumerApp.Events.OrderShipped), \"OrderShipped\")");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithCollidingSimpleNames_DisambiguatesWithFullNameAsync() {
    const string source = @"
using Whizbang.Core;

namespace ConsumerApp.Events {
  public class OrderEventBase : IEvent {
    public System.Guid MessageId { get; set; }
  }
}

namespace ConsumerApp.Events.Retail {
  public sealed class OrderPlaced : ConsumerApp.Events.OrderEventBase { public string OrderId { get; set; } = """"; }
}

namespace ConsumerApp.Events.Wholesale {
  public sealed class OrderPlaced : ConsumerApp.Events.OrderEventBase { public string OrderId { get; set; } = """"; }
}
";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // Both collide on "OrderPlaced" — each falls back to its fully qualified name, which is unique.
    await Assert.That(code!).Contains(
        "new JsonDerivedType(typeof(global::ConsumerApp.Events.Retail.OrderPlaced), \"ConsumerApp.Events.Retail.OrderPlaced\")")
      .Because("A duplicate discriminator makes STJ throw when it configures the base, disabling polymorphism for every derived type under it — not just the colliding pair.");
    await Assert.That(code!).Contains(
        "new JsonDerivedType(typeof(global::ConsumerApp.Events.Wholesale.OrderPlaced), \"ConsumerApp.Events.Wholesale.OrderPlaced\")");
    await Assert.That(code!).DoesNotContain("), \"OrderPlaced\")")
      .Because("Neither colliding type may keep the ambiguous short discriminator.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMixedCollision_LeavesUncollidedNamesShortAsync() {
    const string source = @"
using Whizbang.Core;

namespace ConsumerApp.Events {
  public class OrderEventBase : IEvent {
    public System.Guid MessageId { get; set; }
  }

  public sealed class OrderShipped : OrderEventBase { public string OrderId { get; set; } = """"; }
}

namespace ConsumerApp.Events.Retail {
  public sealed class OrderPlaced : ConsumerApp.Events.OrderEventBase { public string OrderId { get; set; } = """"; }
}

namespace ConsumerApp.Events.Wholesale {
  public sealed class OrderPlaced : ConsumerApp.Events.OrderEventBase { public string OrderId { get; set; } = """"; }
}
";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    await Assert.That(code!).Contains("new JsonDerivedType(typeof(global::ConsumerApp.Events.OrderShipped), \"OrderShipped\")")
      .Because("Disambiguation is scoped to the types that actually collide — an uninvolved sibling keeps its existing wire discriminator.");
    await Assert.That(code!).Contains(
        "new JsonDerivedType(typeof(global::ConsumerApp.Events.Retail.OrderPlaced), \"ConsumerApp.Events.Retail.OrderPlaced\")");
  }
}
