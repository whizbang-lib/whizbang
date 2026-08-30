using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for the generator that records what each registered type's constructor requires.
/// </summary>
/// <remarks>
/// <para>
/// Registration validation has to know which service types a registered implementation needs.
/// Discovering that at run time would mean reading constructors reflectively, which this framework
/// does not permit, so the generator writes the answer down at compile time and validation becomes
/// a comparison of type handles.
/// </para>
/// <para>
/// The requirements are derived from code that already has to exist: a registration call, and the
/// constructor of the type it registers. Nothing is annotated, so a contributor who adds a
/// dependency is covered without knowing this generator exists. That property is the whole point;
/// a manifest that must be remembered would fail exactly like the defect it guards against.
/// </para>
/// </remarks>
[Category("Generators")]
public class ServiceRequirementsGeneratorTests {

  [Test]
  [RequiresAssemblyFiles]
  public async Task RegisteredTypeContributesItsConstructorDependenciesAsync() {
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IClock { }
      public interface IStore { }
      public sealed class Worker {
        public Worker(IClock clock, IStore store) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<Worker>();
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).Contains("Worker");
    await Assert.That(generated).Contains("IClock");
    await Assert.That(generated).Contains("IStore")
      .Because("every constructor parameter is a dependency the container must be able to satisfy");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ImplementationTypeOfATwoArgumentRegistrationIsTheOneInspectedAsync() {
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IClock { }
      public interface IWorker { }
      public sealed class Worker : IWorker {
        public Worker(IClock clock) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<IWorker, Worker>();
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    // The service type has no constructor to inspect; the implementation is what gets built.
    await Assert.That(generated).Contains("IClock");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TryAddIsTreatedAsARegistrationAsync() {
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      using Microsoft.Extensions.DependencyInjection.Extensions;
      namespace TestApp;
      public interface IClock { }
      public sealed class Worker {
        public Worker(IClock clock) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.TryAddSingleton<Worker>();
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    // TryAdd registers exactly as Add does; ignoring it would leave the turnkey defaults unchecked,
    // and those are the registrations most likely to be the only provider of a dependency.
    await Assert.That(generated).Contains("IClock");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task NonInterfaceParametersAreNotTreatedAsServicesAsync() {
    const string source = """
      using System;
      using System.Threading;
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IClock { }
      public sealed class Worker {
        public Worker(IClock clock, string name, int retries, CancellationToken token) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<Worker>();
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    // A validator that demanded a registration for string or int would fail every composition,
    // and a guard that always fails is a guard that gets switched off.
    await Assert.That(generated).DoesNotContain("typeof(string)");
    await Assert.That(generated).DoesNotContain("typeof(int)");
    await Assert.That(generated).Contains("IClock");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ATypeWithNoRegistrationIsNotInspectedAsync() {
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IClock { }
      public interface INeverWanted { }
      public sealed class Unregistered {
        public Unregistered(INeverWanted never) { }
      }
      public sealed class Worker {
        public Worker(IClock clock) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<Worker>();
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    // Requirements describe what a composition must satisfy. A type nobody registers imposes no
    // obligation, and demanding one would make the validator report gaps that are not gaps.
    await Assert.That(generated).DoesNotContain("INeverWanted");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ACompilationWithNoRegistrationsStillEmitsAValidManifestAsync() {
    const string source = """
      namespace TestApp;
      public sealed class Nothing { }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    // Emitting nothing would break the call site that references the manifest, so an empty
    // composition must still produce an empty array rather than no type at all.
    await Assert.That(generated).Contains("WhizbangServiceRequirements");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task GeneratorProducesNoDiagnosticsAsync() {
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IClock { }
      public sealed class Worker { public Worker(IClock clock) { } }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<Worker>();
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);

    await Assert.That(result.Diagnostics.Where(d =>
        d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsEmpty();
  }
}
