using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Tests for the Wolverine marker interface transformer.
/// </summary>
/// <remarks>
/// This transformer exists because Whizbang declares marker interfaces under the same names
/// Wolverine uses -- IEvent, ICommand, IMessage -- so a file whose types only implement markers
/// needs nothing more than its using swapped. That makes its gate the important part: swapping
/// the using on a file that does NOT implement a marker would silently drop the Wolverine
/// namespace out from under whatever else the file was using it for.
/// </remarks>
/// <tests>Whizbang.Migrate/Transformers/MarkerInterfaceTransformer.cs:*</tests>
public class MarkerInterfaceTransformerTests {

  [Test]
  public async Task TransformAsync_RecordImplementingAMarker_SwapsTheUsingAsync() {
    var transformer = new MarkerInterfaceTransformer();
    const string source = """
      using Wolverine;

      public record OrderPlaced(string Id) : IEvent;
      """;

    var result = await transformer.TransformAsync(source, "OrderPlaced.cs");

    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core;");
    await Assert.That(result.TransformedCode).DoesNotContain("using Wolverine;");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.UsingRemoved)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_LeavesTheMarkerNameAloneAsync() {
    // The whole premise is that the names match. Rewriting IEvent would be wrong work, and
    // would break a file that is already correct once the using points at Whizbang.
    var transformer = new MarkerInterfaceTransformer();
    const string source = """
      using Wolverine;

      public record OrderPlaced(string Id) : IEvent;
      """;

    var result = await transformer.TransformAsync(source, "OrderPlaced.cs");

    await Assert.That(result.TransformedCode).Contains(": IEvent");
  }

  [Test]
  [Arguments("public class OrderPlaced : IEvent { }")]
  [Arguments("public record OrderPlaced : ICommand;")]
  [Arguments("public interface IOrderPlaced : IMessage { }")]
  [Arguments("public struct OrderPlaced : IEvent { }")]
  public async Task TransformAsync_RecognizesMarkersOnEveryTypeKindAsync(string declaration) {
    // Consumers declare messages as records, classes and occasionally structs. A type kind the
    // detector skips means that file keeps a Wolverine using that no longer resolves.
    var transformer = new MarkerInterfaceTransformer();
    var source = $"""
      using Wolverine;

      {declaration}
      """;

    var result = await transformer.TransformAsync(source, "Thing.cs");

    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core;");
  }

  [Test]
  public async Task TransformAsync_RecognizesAGenericMarkerAsync() {
    // IEvent<T> is a different syntax node than IEvent; matching only the non-generic form
    // would skip every generically-typed message.
    var transformer = new MarkerInterfaceTransformer();
    const string source = """
      using Wolverine;

      public record Envelope<T>(T Payload) : IEvent<T>;
      """;

    var result = await transformer.TransformAsync(source, "Envelope.cs");

    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core;");
  }

  [Test]
  public async Task TransformAsync_WithoutAMarkerImplementation_ChangesNothingAsync() {
    // The gate that matters. A file importing Wolverine for something other than markers must
    // keep that import: swapping it would remove the namespace whatever else it uses relies on,
    // and this transformer has no idea what that is.
    var transformer = new MarkerInterfaceTransformer();
    const string source = """
      using Wolverine;

      public class OrderService {
        public void Handle(object o) { }
      }
      """;

    var result = await transformer.TransformAsync(source, "OrderService.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(source);
    await Assert.That(result.Changes).IsEmpty();
  }

  [Test]
  public async Task TransformAsync_MarkerWithoutTheWolverineUsing_ChangesNothingAsync() {
    // Already migrated, or relying on a global using. Either way there is no import to swap,
    // and adding one would be this transformer inventing work.
    var transformer = new MarkerInterfaceTransformer();
    const string source = """
      using Whizbang.Core;

      public record OrderPlaced(string Id) : IEvent;
      """;

    var result = await transformer.TransformAsync(source, "OrderPlaced.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(source);
    await Assert.That(result.Changes).IsEmpty();
  }

  [Test]
  public async Task TransformAsync_LeavesOtherWolverineNamespacesAloneAsync() {
    // Only the exact "Wolverine" import is the marker namespace. Wolverine.Http and friends are
    // other transformers' work, and rewriting them here would produce a using that resolves to
    // nothing.
    var transformer = new MarkerInterfaceTransformer();
    const string source = """
      using Wolverine;
      using Wolverine.Http;

      public record OrderPlaced(string Id) : IEvent;
      """;

    var result = await transformer.TransformAsync(source, "OrderPlaced.cs");

    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core;");
    await Assert.That(result.TransformedCode).Contains("using Wolverine.Http;");
  }

  [Test]
  public async Task TransformAsync_PreservesUnrelatedUsingsAndTheirOrderAsync() {
    var transformer = new MarkerInterfaceTransformer();
    const string source = """
      using System;
      using Wolverine;
      using System.Text.Json;

      public record OrderPlaced(string Id) : IEvent;
      """;

    var result = await transformer.TransformAsync(source, "OrderPlaced.cs");

    var code = result.TransformedCode;
    await Assert.That(code).Contains("using System;");
    await Assert.That(code).Contains("using System.Text.Json;");
    await Assert.That(code.IndexOf("using System;", StringComparison.Ordinal))
      .IsLessThan(code.IndexOf("using Whizbang.Core;", StringComparison.Ordinal));
    await Assert.That(code.IndexOf("using Whizbang.Core;", StringComparison.Ordinal))
      .IsLessThan(code.IndexOf("using System.Text.Json;", StringComparison.Ordinal));
  }

  [Test]
  public async Task TransformAsync_AFileWithNoUsingsAtAll_ChangesNothingAsync() {
    var transformer = new MarkerInterfaceTransformer();
    const string source = "public record OrderPlaced(string Id);\n";

    var result = await transformer.TransformAsync(source, "OrderPlaced.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(source);
  }

  [Test]
  public async Task TransformAsync_NestedTypeImplementingAMarker_IsStillDetectedAsync() {
    // Messages are commonly nested inside a static container class. Walking only top-level
    // declarations would leave those files behind.
    var transformer = new MarkerInterfaceTransformer();
    const string source = """
      using Wolverine;

      public static class OrderMessages {
        public record Placed(string Id) : IEvent;
      }
      """;

    var result = await transformer.TransformAsync(source, "OrderMessages.cs");

    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core;");
  }
}
