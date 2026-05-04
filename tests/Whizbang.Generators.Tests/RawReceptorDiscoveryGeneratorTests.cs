using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for RawReceptorDiscoveryGenerator. Emits a module initializer that registers
/// discovered IRawReceptor implementations + a singleton IRawReceptorRegistry.
/// </summary>
public class RawReceptorDiscoveryGeneratorTests {

  [Test]
  public async Task Generator_WithRawReceptors_EmitsModuleInitializerAsync() {
    const string source = """

      using System.Text.Json;
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core.Messaging;

      namespace MyApp;

      public class FooRawReceptor : IRawReceptor {
        public string TargetMessageTypeName => "MyApp.Events.Foo, MyApp.Contracts";
        public Task HandleAsync(JsonElement payload, CancellationToken ct) => Task.CompletedTask;
      }

      public class BarRawReceptor : IRawReceptor {
        public string TargetMessageTypeName => "MyApp.Events.Bar, MyApp.Contracts";
        public Task HandleAsync(JsonElement payload, CancellationToken ct) => Task.CompletedTask;
      }

""";

    var result = GeneratorTestHelper.RunGenerator<RawReceptorDiscoveryGenerator>(source);

    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var code = GeneratorTestHelper.GetGeneratedSource(result, "RawReceptors.g.cs");
    await Assert.That(code).IsNotNull();

    // ModuleInitializer wires the callback
    await Assert.That(code!).Contains("[ModuleInitializer]");
    await Assert.That(code!).Contains("ServiceRegistrationCallbacks.RawReceptors");

    // Both receptors registered
    await Assert.That(code!).Contains("services.AddSingleton<IRawReceptor, global::MyApp.FooRawReceptor>();");
    await Assert.That(code!).Contains("services.AddSingleton<IRawReceptor, global::MyApp.BarRawReceptor>();");

    // Registry singleton registered once
    await Assert.That(code!).Contains("services.AddSingleton<IRawReceptorRegistry, RawReceptorRegistry>();");
  }

  [Test]
  public async Task Generator_NoRawReceptors_SkipsEmissionAsync() {
    const string source = """

      using Whizbang.Core;

      namespace MyApp;

      public record JustAnEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<RawReceptorDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "RawReceptors.g.cs");
    await Assert.That(code).IsNull();
  }

  [Test]
  public async Task Generator_AbstractRawReceptor_SkippedAsync() {
    const string source = """

      using System.Text.Json;
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core.Messaging;

      namespace MyApp;

      public abstract class AbstractRawReceptor : IRawReceptor {
        public abstract string TargetMessageTypeName { get; }
        public Task HandleAsync(JsonElement payload, CancellationToken ct) => Task.CompletedTask;
      }

      public class ConcreteRawReceptor : AbstractRawReceptor {
        public override string TargetMessageTypeName => "MyApp.Events.Concrete, MyApp";
      }

""";

    var result = GeneratorTestHelper.RunGenerator<RawReceptorDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "RawReceptors.g.cs");
    await Assert.That(code).IsNotNull();

    // Concrete subclass IS registered
    await Assert.That(code!).Contains("services.AddSingleton<IRawReceptor, global::MyApp.ConcreteRawReceptor>();");
    // Abstract base is NOT registered
    await Assert.That(code!).DoesNotContain("global::MyApp.AbstractRawReceptor");
  }
}
