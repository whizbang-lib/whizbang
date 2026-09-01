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

  [Test]
  [RequiresAssemblyFiles]
  public async Task AFactoryRegistrationContributesWhatItResolvesAsync() {
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IHook { }
      public interface ICloser { }
      public sealed class Closer : ICloser {
        public Closer(IHook hook) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<ICloser>(sp => new Closer(sp.GetRequiredService<IHook>()));
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    // A factory registration has no ImplementationType, so recording only type-based registrations
    // misses it entirely. That is not a small gap: factory lambdas are where services are
    // hand-constructed, which is the population this whole check exists for. A required dependency
    // resolved by a factory that nothing registers throws only when something first resolves the
    // service, which may be never in tests and always in production.
    await Assert.That(generated).Contains("IHook")
      .Because("what a factory resolves with GetRequiredService is a hard requirement of that "
             + "registration, and is invisible to a type-based scan");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnOptionalParameterIsNotAHardRequirementAsync() {
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IRequired { }
      public interface IOptionalDep { }
      public sealed class Worker {
        public Worker(IRequired required, IOptionalDep? optional = null) { }
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

    // An optional parameter has a compiler-supplied default, so it is by definition not something
    // the composition must provide. Recording it would make validation demand a registration for
    // every dependency that legitimately falls back, and since validation runs at startup by
    // default, that fails correct applications on boot.
    await Assert.That(generated).Contains("IRequired");
    await Assert.That(generated).DoesNotContain("IOptionalDep")
      .Because("demanding a registration for an optional dependency fails compositions that are "
             + "correct, which is the failure mode that gets a guard switched off");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ACollectionDependencyIsNotARequirementAsync() {
    const string source = """
      using System.Collections.Generic;
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IPlugin { }
      public sealed class Host2 {
        public Host2(IEnumerable<IPlugin> plugins) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<Host2>();
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    // The container always satisfies IEnumerable<T>, returning empty when nothing is registered.
    // Requiring a registration for it would report a gap that cannot exist.
    await Assert.That(generated).DoesNotContain("IEnumerable");
  }

  // ============================================================
  // What a factory registration must NOT contribute
  // ============================================================
  //
  // This manifest is checked at startup: anything it lists and the container cannot satisfy fails
  // composition. That makes a false positive worse than a miss — a miss lets an existing bug stay
  // hidden, but a false positive stops a correct service from starting, and the operator has no way
  // to override it.

  [Test]
  public async Task GetServiceIsNotAHardRequirementAsync() {
    // GetService returning null is a documented outcome the caller has already handled — that is
    // the whole difference from GetRequiredService. Demanding a registration for it would refuse
    // to start compositions that are correct today.
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IOptionalHook { }
      public interface ICloser { }
      public sealed class Closer : ICloser { }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<ICloser>(sp => {
            var hook = sp.GetService<IOptionalHook>();
            return new Closer();
          });
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).DoesNotContain("IOptionalHook")
      .Because("an optional resolve is not a requirement — listing it would refuse to start a "
             + "composition that is correct");
  }

  [Test]
  public async Task AFactoryThatResolvesNothingContributesNoEntryAsync() {
    // A hand-constructed service with no container dependencies is ordinary. Emitting an entry
    // with an empty requirement list is noise in a manifest read on every startup.
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface ICloser { }
      public sealed class Closer : ICloser { }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<ICloser>(sp => new Closer());
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).DoesNotContain("TestApp.ICloser");
  }

  [Test]
  public async Task AConcreteResolveIsNotRecordedAsARequirementAsync() {
    // Only interfaces are treated as service contracts. A concrete resolve is usually a type the
    // container constructs on demand, and requiring an explicit registration would be wrong.
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public sealed class ConcreteHelper { }
      public interface ICloser { }
      public sealed class Closer : ICloser {
        public Closer(ConcreteHelper helper) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<ICloser>(sp => new Closer(sp.GetRequiredService<ConcreteHelper>()));
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).DoesNotContain("ConcreteHelper");
  }

  [Test]
  public async Task APrivateServiceTypeIsNotRecordedAsync() {
    // The manifest is emitted into the same assembly but a different file. Naming a type that
    // file cannot see turns this build-time check into a build break for everyone.
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IHook { }
      public sealed class Host {
        private interface IPrivateCloser { }
        private sealed class PrivateCloser : IPrivateCloser {
          public PrivateCloser(IHook hook) { }
        }
        public static IServiceCollection AddThing(IServiceCollection services) {
          services.AddSingleton<IPrivateCloser>(sp => new PrivateCloser(sp.GetRequiredService<IHook>()));
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).DoesNotContain("IPrivateCloser")
      .Because("the generated manifest cannot name a private nested type");
  }

  [Test]
  public async Task AGenericServiceOverAPrivateArgumentIsNotRecordedAsync() {
    // The constructed type's own declaration is public, but its type argument is not — and it is
    // the full constructed name that gets emitted.
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IHook { }
      public interface IQuery<T> { }
      public sealed class Host {
        private sealed class PrivateModel { }
        public static IServiceCollection AddThing(IServiceCollection services) {
          services.AddSingleton<IQuery<PrivateModel>>(sp => null!);
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).DoesNotContain("PrivateModel")
      .Because("emitting a constructed type whose argument is invisible breaks the generated file");
  }

  [Test]
  public async Task ANonGenericFactoryRegistrationIsSkippedAsync() {
    // AddSingleton(typeof(X), factory) has no type argument to name the requirement after, and
    // naming it after the lambda would produce an entry nobody can act on.
    const string source = """
      using System;
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IHook { }
      public interface ICloser { }
      public sealed class Closer : ICloser { }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton(typeof(ICloser), sp => new Closer());
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);

    await Assert.That(result.Diagnostics.Any(d => d.Id == "CS8785")).IsFalse();
  }

  [Test]
  public async Task AnUnknownRegistrationMethodWithALambdaIsIgnoredAsync() {
    // The scan matches a known set of registration method names. A consumer's own fluent helper
    // that happens to take a lambda is not a DI registration.
    const string source = """
      using System;
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IHook { }
      public interface ICloser { }
      public static class Registration {
        public static IServiceCollection Configure<T>(this IServiceCollection services, Func<IServiceProvider, T> f)
          => services;
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.Configure<ICloser>(sp => { sp.GetRequiredService<IHook>(); return null!; });
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).DoesNotContain("TestApp.ICloser")
      .Because("a consumer's own fluent helper is not a container registration");
  }

  [Test]
  public async Task TwoRegistrationsOfTheSameTypeContributeOneEntryAsync() {
    // Registering the same implementation twice is ordinary in a modular composition. Emitting
    // the requirement twice reports one gap as two and overstates how much is broken.
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IHook { }
      public interface ICloser { }
      public sealed class Closer : ICloser {
        public Closer(IHook hook) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<ICloser>(sp => new Closer(sp.GetRequiredService<IHook>()));
          services.AddSingleton<ICloser>(sp => new Closer(sp.GetRequiredService<IHook>()));
          return services;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    var entries = generated.Split("typeof(global::TestApp.ICloser)").Length - 1;
    await Assert.That(entries).IsEqualTo(1)
      .Because("the same requirement listed twice reports one gap as two");
  }
}
