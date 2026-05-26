using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for MessageTypeCatalogGenerator.
/// Emits an AOT-safe IMessageTypeCatalog listing every concrete IMessage and
/// IPerspectiveFor&lt;&gt; type with its kind and optional pinned_id.
/// </summary>
public class MessageTypeCatalogGeneratorTests {

  [Test]
  public async Task Generator_WithEventsCommandsAndPerspectives_EmitsAllAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      using Whizbang.Core.Perspectives;

      namespace MyApp;

      [PinnedId("11111111-1111-1111-1111-111111111111")]
      public record OrderPlacedEvent : IEvent;

      public record UnpinnedEvent : IEvent;

      [PinnedId("22222222-2222-2222-2222-222222222222")]
      public record PlaceOrderCommand : ICommand;

      public record OrderView;

      [PinnedId("33333333-3333-3333-3333-333333333333")]
      public class OrderPerspective : IPerspectiveFor<OrderView, OrderPlacedEvent> {
        public OrderView Apply(OrderView? current, OrderPlacedEvent @event) => current ?? new();
      }

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);

    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains(": IMessageTypeCatalog");

    // Entries for each discovered type
    await Assert.That(code!).Contains("typeof(global::MyApp.OrderPlacedEvent)");
    await Assert.That(code!).Contains("typeof(global::MyApp.UnpinnedEvent)");
    await Assert.That(code!).Contains("typeof(global::MyApp.PlaceOrderCommand)");
    await Assert.That(code!).Contains("typeof(global::MyApp.OrderPerspective)");

    // Pinned entries surface their pinned_id; unpinned surface null
    await Assert.That(code!).Contains("\"11111111-1111-1111-1111-111111111111\"");
    await Assert.That(code!).Contains("\"33333333-3333-3333-3333-333333333333\"");

    // Kinds
    await Assert.That(code!).Contains("\"event\"");
    await Assert.That(code!).Contains("\"command\"");
    await Assert.That(code!).Contains("\"perspective\"");
  }

  [Test]
  public async Task Generator_SkipsAbstractTypesAsync() {
    const string source = """

      using Whizbang.Core;

      namespace MyApp;

      public abstract record BaseEvent : IEvent;
      public record ConcreteEvent : BaseEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code!).DoesNotContain("typeof(global::MyApp.BaseEvent)");
    await Assert.That(code!).Contains("typeof(global::MyApp.ConcreteEvent)");
  }

  [Test]
  public async Task Generator_WithNoTypes_GeneratesNothingAsync() {
    const string source = """
      namespace MyApp;
      public class PlainService { }
""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");

    await Assert.That(code).IsNull();
  }

  [Test]
  public async Task Generator_EmitsModuleInitializerThatRegistersViaAddWhizbangAsync() {
    const string source = """

      using Whizbang.Core;

      namespace MyApp;

      public record SomeEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("[System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(code!).Contains("ServiceRegistrationCallbacks.MessageTypeCatalog");
    await Assert.That(code!).Contains("AddSingleton<IMessageTypeCatalog, GeneratedMessageTypeCatalog>");
  }

  [Test]
  public async Task Generator_WithIPerspectiveWithActionsFor_EmitsPerspectiveEntryAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      using Whizbang.Core.Perspectives;

      namespace MyApp;

      public record OrderShippedEvent : IEvent;
      public record OrderView;

      [PinnedId("44444444-4444-4444-4444-444444444444")]
      public class ActionsOrderPerspective : IPerspectiveWithActionsFor<OrderView, OrderShippedEvent> {
        public ApplyResult<OrderView> Apply(OrderView? current, OrderShippedEvent @event) =>
          ApplyResult<OrderView>.Update(current ?? new());
      }

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("typeof(global::MyApp.ActionsOrderPerspective)");
    await Assert.That(code!).Contains("\"perspective\"");
    await Assert.That(code!).Contains("\"44444444-4444-4444-4444-444444444444\"");
  }

  [Test]
  public async Task Generator_EntryContainsNullWhenUnpinnedAsync() {
    const string source = """

      using Whizbang.Core;

      namespace MyApp;

      public record UnpinnedEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");

    await Assert.That(code).IsNotNull();
    // Unpinned entry must surface a null in the pinned_id position
    await Assert.That(code!).Contains("typeof(global::MyApp.UnpinnedEvent)");
    await Assert.That(code!).Contains("null");
  }
}
