// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  // ============================================================
  // The service type a factory registration is made under
  // ============================================================
  //
  // Validation checks a requirement only when its implementation is registered in the composition
  // being validated, because the manifest also lists registration calls that a composition never
  // makes. A type-based descriptor names its implementation; a factory descriptor cannot. So for a
  // registration that builds its implementation through a factory, the manifest records the service
  // type the descriptor will carry, or a factory-built type would silently drop out of validation.

  [Test]
  [RequiresAssemblyFiles]
  public async Task AFactoryBuiltImplementationRecordsTheServiceTypeItIsRegisteredUnderAsync() {
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
          services.AddSingleton<IWorker, Worker>(sp => new Worker(null!));
          return services;
        }
      }
      """;

    var generated = _flatten(GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source));

    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.IClock),],[typeof(global::TestApp.IWorker),])")
      .Because("the descriptor will say IWorker and nothing about Worker, so the manifest must");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AFactoryHostedServiceRecordsIHostedServiceAsync() {
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IClock { }
      public sealed class Worker {
        public Worker(IClock clock) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddHostedService<Worker>(sp => new Worker(null!));
          return services;
        }
      }
      """;

    var generated = _flatten(GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source));

    // AddHostedService registers under IHostedService, whatever its type argument says.
    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.IClock),],[typeof(global::Microsoft.Extensions.Hosting.IHostedService),])");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ATypeBasedRegistrationRecordsNoFactoryServiceTypeAsync() {
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
          services.AddHostedService<Worker>();
          return services;
        }
      }
      """;

    var generated = _flatten(GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source));

    // Both descriptors name Worker as their implementation, so presence is read from them directly.
    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.IClock),]),");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AFactoryRegistrationNamedAfterItsOwnServiceTypeRecordsNoFactoryServiceTypeAsync() {
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

    var generated = _flatten(GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source));

    // The requirement is already named after the service type the descriptor carries.
    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.ICloser),[typeof(global::TestApp.IHook),]),");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task FactoryServiceTypesFromEveryRegistrationOfATypeAreMergedAsync() {
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IClock { }
      public interface IWorker { }
      public interface IOtherWorker { }
      public sealed class Worker : IWorker, IOtherWorker {
        public Worker(IClock clock) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<Worker>();
          services.AddSingleton<IWorker, Worker>(sp => new Worker(null!));
          services.AddSingleton<IOtherWorker, Worker>(sp => new Worker(null!));
          return services;
        }
      }
      """;

    var generated = _flatten(GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source));

    // One entry per type, so a factory registration made in one place must not lose the service
    // type recorded by another; whichever of them a composition makes has to count.
    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.IClock),],[typeof(global::TestApp.IOtherWorker),typeof(global::TestApp.IWorker),])");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AFactoryHostedServiceRecordsIHostedServiceForWhatItsFactoryResolvesAsync() {
    // The constructor needs no service, so only the factory names one: the requirement comes from
    // the factory scan, and that scan must record IHostedService for the same reason the
    // type-based scan does.
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IClock { }
      public sealed class Worker {
        public Worker(string name) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddHostedService<Worker>(sp => new Worker(sp.GetRequiredService<IClock>().ToString()!));
          return services;
        }
      }
      """;

    var generated = _flatten(GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source));

    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.IClock),],[typeof(global::Microsoft.Extensions.Hosting.IHostedService),])")
      .Because("the factory descriptor says IHostedService and nothing about Worker");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AFactoryServiceTypeThatDoesNotBindRecordsNoFactoryServiceTypeAsync() {
    // A service type the compilation cannot resolve has no name the manifest could emit, so the
    // implementation is still recorded, without a factory service type.
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IClock { }
      public sealed class Worker {
        public Worker(IClock clock) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<IUndeclaredWorker, Worker>(sp => new Worker(null!));
          return services;
        }
      }
      """;

    var generated = _flatten(GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source));

    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.IClock),]),");
    await Assert.That(generated).DoesNotContain("IUndeclaredWorker");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task APrivateFactoryServiceTypeIsNotRecordedAsync() {
    // The generated manifest cannot name a private nested type, so a factory registration under
    // one records the implementation without a factory service type.
    const string source = """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IClock { }
      public sealed class Host {
        private interface IPrivateWorker { }
        public sealed class Worker : IPrivateWorker {
          public Worker(IClock clock) { }
        }
        public static IServiceCollection AddThing(IServiceCollection services) {
          services.AddSingleton<IPrivateWorker, Worker>(sp => new Worker(null!));
          return services;
        }
      }
      """;

    var generated = _flatten(GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source));

    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Host.Worker),[typeof(global::TestApp.IClock),]),");
    await Assert.That(generated).DoesNotContain("IPrivateWorker");
  }

  // #1907: a factory that builds its service only when another service is registered, else a stand-in. What the building
  // branch needs is required only when that condition holds, so a host without the condition is not failed for it, and
  // a host with it is checked for all of it.

  private const string CONDITIONAL_PREAMBLE = """
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;
    namespace TestApp;
    public interface ITransport { }
    public interface IClock { }
    public interface ILog { }
    public interface IThing { }
    public sealed class Worker : IThing {
      public Worker(ITransport transport, IClock clock) { }
    }
    public sealed class StandIn : IThing {
      public StandIn(ILog log) { }
    }
    public static class Probing {
      public static T? Probe<T>(this System.IServiceProvider sp) => default;
      public static T? GetService<T, U>(this System.IServiceProvider sp) => default;
    }
    """;

  private static string _conditional(string registration) => CONDITIONAL_PREAMBLE + $$"""

    public static class Registration {
      public static IServiceCollection AddThing(this IServiceCollection services, bool enabled) {
        {{registration}}
        return services;
      }
    }
    """;

  /// <summary>Each shape of "is the condition service registered" a factory's conditional can test.</summary>
  public static IEnumerable<Func<string>> ConditionShapes() {
    // The transport consumer's own shape (#1876): no transport, no worker.
    yield return () => "sp.GetService<ITransport>() is not { } transport ? null! : new Worker(transport, sp.GetRequiredService<IClock>())";
    yield return () => "sp.GetService<ITransport>() is { } transport ? new Worker(transport, sp.GetRequiredService<IClock>()) : null!";
    yield return () => "sp.GetService<ITransport>() is null ? null! : new Worker(null!, sp.GetRequiredService<IClock>())";
    yield return () => "sp.GetService<ITransport>() is not null ? new Worker(null!, sp.GetRequiredService<IClock>()) : null!";
    yield return () => "sp.GetService<ITransport>() is ITransport transport ? new Worker(transport, sp.GetRequiredService<IClock>()) : null!";
    yield return () => "sp.GetService<ITransport>() == null ? null! : new Worker(null!, sp.GetRequiredService<IClock>())";
    yield return () => "sp.GetService<ITransport>() != null ? new Worker(null!, sp.GetRequiredService<IClock>()) : null!";
    yield return () => "null != sp.GetService<ITransport>() ? new Worker(null!, sp.GetRequiredService<IClock>()) : null!";
    yield return () => "(sp.GetService<ITransport>() is not { } transport) ? null! : (new Worker(transport, sp.GetRequiredService<IClock>()))";
  }

  [Test]
  [RequiresAssemblyFiles]
  [MethodDataSource(nameof(ConditionShapes))]
  public async Task AFactoryThatBuildsOnlyWhenAServiceIsRegistered_RequiresItsBuildingBranch_OnlyUnderThatConditionAsync(
      string factory) {
    var source = _conditional($"services.TryAdd(ServiceDescriptor.Singleton<Worker>(sp => {factory}));");

    var generated = _flatten(_runWithContainer(source));

    await Assert.That(_compileErrors(source)).IsEmpty()
      .Because("the shape is read from a source that binds, against the container's own ServiceDescriptor");
    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.IClock),]){Condition=typeof(global::TestApp.ITransport)},")
      .Because("the worker is built, and so needs its dependencies, only when a transport is registered");
    await Assert.That(generated).DoesNotContain("IClock),]),")
      .Because("nothing is required of a host that registers no transport");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TheStandInBranch_IsRequiredUnconditionally_AndTheBuildingBranchUnderItsConditionAsync() {
    var source = _conditional(
      "services.TryAdd(ServiceDescriptor.Singleton<IThing>(sp => sp.GetService<ITransport>() is null "
      + "? new StandIn(sp.GetRequiredService<ILog>()) : new Worker(null!, sp.GetRequiredService<IClock>())));");

    var generated = _flatten(_runWithContainer(source));

    // The branch taken when the condition service is absent is built in exactly the hosts the condition does not
    // describe, so it keeps the unconditional check: erring towards checking.
    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.IThing),[typeof(global::TestApp.ILog),]),");
    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.IThing),[typeof(global::TestApp.IClock),]){Condition=typeof(global::TestApp.ITransport)},");
  }

  /// <summary>Conditionals that do not say whether a service is registered.</summary>
  public static IEnumerable<Func<string>> UnreadableConditions() {
    // Not a resolve at all.
    yield return () => "enabled ? new Worker(null!, sp.GetRequiredService<IClock>()) : null!";
    // A property of the resolved service as well as its presence.
    yield return () => "sp.GetService<ITransport>() is { } t && enabled ? new Worker(t, sp.GetRequiredService<IClock>()) : null!";
    // A pattern that matches null too.
    yield return () => "sp.GetService<ITransport>() is var t ? new Worker(t!, sp.GetRequiredService<IClock>()) : null!";
    // A comparison with something other than null.
    yield return () => "sp.GetService<ITransport>() == sp.GetService<ITransport>() ? new Worker(null!, sp.GetRequiredService<IClock>()) : null!";
    // A comparison other than equality.
    yield return () => "(object?)sp.GetService<ITransport>() is > 3 ? new Worker(null!, sp.GetRequiredService<IClock>()) : null!";
    // A non-null constant.
    yield return () => "sp.GetService<object>() is \"x\" ? new Worker(null!, sp.GetRequiredService<IClock>()) : null!";
    // The negation of a pattern that says nothing either.
    yield return () => "sp.GetService<object>() is not \"x\" ? new Worker(null!, sp.GetRequiredService<IClock>()) : null!";
    // A resolve that is not generic.
    yield return () => "sp.GetService(typeof(ITransport)) is { } ? new Worker(null!, sp.GetRequiredService<IClock>()) : null!";
    // A generic call that is not a resolve.
    yield return () => "sp.Probe<ITransport>() is { } ? new Worker(null!, sp.GetRequiredService<IClock>()) : null!";
    // A resolve with two type arguments.
    yield return () => "sp.GetService<ITransport, IClock>() is { } ? new Worker(null!, sp.GetRequiredService<IClock>()) : null!";
    // A resolve with arguments: not a test of the one service.
    yield return () => "sp.GetService<ITransport>()?.ToString() is { } ? new Worker(null!, sp.GetRequiredService<IClock>()) : null!";
  }

  [Test]
  [RequiresAssemblyFiles]
  [MethodDataSource(nameof(UnreadableConditions))]
  public async Task AConditionThatDoesNotSayWhetherAServiceIsRegistered_LeavesTheRequirementUnconditionalAsync(string factory) {
    var source = _conditional($"services.TryAdd(ServiceDescriptor.Singleton<Worker>(sp => {factory}));");

    var generated = _flatten(_runWithContainer(source));

    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.IClock),]),")
      .Because("a condition the composition cannot be read for says nothing about when the worker is built");
    await Assert.That(generated).DoesNotContain("Condition=");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AResolveInTheConditionItself_IsNotPartOfEitherBranchAsync() {
    var source = _conditional(
      "services.TryAdd(ServiceDescriptor.Singleton<Worker>(sp => sp.GetRequiredService<ILog>() is { } ? new Worker(null!, sp.GetRequiredService<IClock>()) : null!));");

    var generated = _flatten(_runWithContainer(source));

    // GetRequiredService throws rather than return null, so testing what it returns is no condition on registration.
    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.ILog),typeof(global::TestApp.IClock),]),");
    await Assert.That(generated).DoesNotContain("Condition=");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ARegistrationMethodFactoryThatBuildsOnlyUnderACondition_RequiresTheConstructorUnderItTooAsync() {
    // The defect as reported: TryAddSingleton<T>(factory) was read as a plain type registration, every constructor
    // dependency of T recorded as if T were always built.
    var source = _conditional(
      "services.TryAddSingleton<Worker>(sp => sp.GetService<ITransport>() is not { } transport ? null! : new Worker(transport, sp.GetRequiredService<IClock>()));");

    var generated = _flatten(_runWithContainer(source));

    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.ITransport),typeof(global::TestApp.IClock),]){Condition=typeof(global::TestApp.ITransport)},");
    await Assert.That(generated).DoesNotContain("IClock),]),");
  }

  /// <summary>Factories whose conditional does not build the registered type only when the service is present.</summary>
  public static IEnumerable<Func<string>> FactoriesThatDoNotBuildTheTypeUnderTheCondition() {
    // Built when the service is absent.
    yield return () => "services.TryAddSingleton<Worker>(sp => sp.GetService<ITransport>() is null ? new Worker(null!, null!) : null!);";
    // Built either way.
    yield return () => "services.TryAddSingleton<Worker>(sp => sp.GetService<ITransport>() is null ? new Worker(null!, null!) : new Worker(null!, null!));";
    // Not built with new.
    yield return () => "services.TryAddSingleton<Worker>(sp => sp.GetService<ITransport>() is { } ? Make() : null!);";
    // A block body, which is not read.
    yield return () => "services.TryAddSingleton<Worker>(sp => { return sp.GetService<ITransport>() is { } ? new Worker(null!, null!) : null!; });";
    // A conditional on something else.
    yield return () => "services.TryAddSingleton<Worker>(sp => enabled ? new Worker(null!, null!) : null!);";
    // An argument that is not a lambda.
    yield return () => "services.TryAddSingleton<Worker>(Make());";
    // A resolve of a service the compilation cannot name.
    yield return () => "services.TryAddSingleton<Worker>(sp => sp.GetService<IUndeclared>() is { } ? new Worker(null!, null!) : null!);";
    // A resolve of a service the manifest cannot name.
    yield return () => "services.TryAddSingleton<Worker>(sp => sp.GetService<IPrivate>() is { } ? new Worker(null!, null!) : null!);";
  }

  [Test]
  [RequiresAssemblyFiles]
  [MethodDataSource(nameof(FactoriesThatDoNotBuildTheTypeUnderTheCondition))]
  public async Task AFactoryThatDoesNotBuildItsTypeOnlyUnderTheCondition_RequiresTheConstructorUnconditionallyAsync(string registration) {
    var source = _conditional(registration).Replace(
      "public static class Registration {",
      "public static class Registration {\n  private static Worker Make() => null!;\n  private interface IPrivate { }", StringComparison.Ordinal);

    var generated = _flatten(_runWithContainer(source));

    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.ITransport),typeof(global::TestApp.IClock),]),");
    await Assert.That(generated).DoesNotContain("Condition=");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TheSameTypeRequiredWithAndWithoutACondition_IsTwoEntriesAsync() {
    var source = _conditional("""
      services.AddSingleton<Worker>(sp => new Worker(null!, sp.GetRequiredService<IClock>()));
      services.TryAdd(ServiceDescriptor.Singleton<Worker>(sp => sp.GetService<ITransport>() is { } t ? new Worker(t, sp.GetRequiredService<IClock>()) : null!));
      """);

    var generated = _flatten(_runWithContainer(source));

    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.ITransport),typeof(global::TestApp.IClock),]),");
    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.IClock),]){Condition=typeof(global::TestApp.ITransport)},");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TwoRegistrationsOfTheSameTypeRequiringDifferentServices_ContributeTheirUnionAsync() {
    // One entry per type and condition, so what each registration requires must survive the merge, not only the first.
    var source = _conditional("""
      services.AddSingleton<Worker>();
      services.AddSingleton<Worker>(sp => { sp.GetRequiredService<ILog>(); return new Worker(null!, null!); });
      """);

    var generated = _flatten(_runWithContainer(source));

    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.ITransport),typeof(global::TestApp.IClock),typeof(global::TestApp.ILog),]),");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AConditionalHostedServiceFactory_CarriesItsConditionWithItsFactoryServiceTypeAsync() {
    var source = _conditional(
      "services.AddHostedService<Worker>(sp => sp.GetService<ITransport>() is { } t ? new Worker(t, sp.GetRequiredService<IClock>()) : null!);");

    var generated = _flatten(_runWithContainer(source));

    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.ITransport),typeof(global::TestApp.IClock),],[typeof(global::Microsoft.Extensions.Hosting.IHostedService),]){Condition=typeof(global::TestApp.ITransport)},");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AQualifiedServiceDescriptorFactory_IsReadAsync() {
    var source = _conditional(
      "services.TryAdd(Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<Worker>(sp => sp.GetService<ITransport>() is { } t ? new Worker(t, sp.GetRequiredService<IClock>()) : null!));");

    var generated = _flatten(_runWithContainer(source));

    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.IClock),]){Condition=typeof(global::TestApp.ITransport)},");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task EveryResolveInTheBuildingBranch_IsOneRequirementUnderTheConditionAsync() {
    var source = _conditional(
      "services.TryAdd(ServiceDescriptor.Singleton<Worker>(sp => sp.GetService<ITransport>() is { } "
      + "? new Worker(sp.GetRequiredService<ITransport>(), sp.GetRequiredService<IClock>()) : null!));");

    var generated = _flatten(_runWithContainer(source));

    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.ITransport),typeof(global::TestApp.IClock),]){Condition=typeof(global::TestApp.ITransport)},");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AFactoryWhoseBuildingBranchMakesAnotherType_RequiresTheConstructorUnconditionallyAsync() {
    var source = _conditional(
      "services.TryAddSingleton<IThing, Worker>(sp => sp.GetService<ITransport>() is { } ? new StandIn(null!) : null!);");

    var generated = _flatten(_runWithContainer(source));

    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.ITransport),typeof(global::TestApp.IClock),],[typeof(global::TestApp.IThing),]),")
      .Because("the registration names Worker, and the branch builds something else, so nothing says when Worker is built");
  }

  /// <summary>
  /// ServiceDescriptor factory shapes the scan does not read: two type arguments name a service type many registrations
  /// share, so the requirement would apply wherever any of them is registered; a call on another type is not a descriptor.
  /// </summary>
  public static IEnumerable<Func<string>> UnreadDescriptorFactories() {
    yield return () => "services.TryAddEnumerable(ServiceDescriptor.Singleton<IThing, Worker>(sp => new Worker(null!, sp.GetRequiredService<IClock>())));";
    yield return () => "services.TryAdd(Descriptors.Singleton<Worker>(sp => new Worker(null!, sp.GetRequiredService<IClock>())));";
    // A consumer's own type of the same name.
    yield return () => "services.TryAdd(Other.ServiceDescriptor.Singleton<Worker>(sp => new Worker(null!, sp.GetRequiredService<IClock>())));";
    // A call that binds to no overload.
    yield return () => "services.TryAdd(ServiceDescriptor.Singleton<Worker>(sp => new Worker(null!, sp.GetRequiredService<IClock>()), 42));";
  }

  [Test]
  [RequiresAssemblyFiles]
  [MethodDataSource(nameof(UnreadDescriptorFactories))]
  public async Task AServiceDescriptorFactoryNamingASharedServiceType_OrAnotherType_IsNotReadAsync(string registration) {
    var source = _conditional(registration) + """

      public static class Descriptors {
        public static ServiceDescriptor Singleton<T>(System.Func<System.IServiceProvider, T> f) where T : class
          => ServiceDescriptor.Singleton(f);
      }
      public static class Other {
        public static class ServiceDescriptor {
          public static Microsoft.Extensions.DependencyInjection.ServiceDescriptor Singleton<T>(System.Func<System.IServiceProvider, T> f)
            where T : class => Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton(f);
        }
      }
      """;

    var generated = _flatten(_runWithContainer(source));

    await Assert.That(generated).DoesNotContain("IClock),");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task EverySingleServiceDescriptorFactoryLifetime_IsReadAsync() {
    var source = _conditional("""
      services.TryAdd(ServiceDescriptor.Singleton<IThing>(sp => new Worker(null!, sp.GetRequiredService<IClock>())));
      services.TryAdd(ServiceDescriptor.Scoped<Worker>(sp => new Worker(null!, sp.GetRequiredService<IClock>())));
      services.TryAdd(ServiceDescriptor.Transient<StandIn>(sp => new StandIn(sp.GetRequiredService<ILog>())));
      """);

    var generated = _flatten(_runWithContainer(source));

    await Assert.That(_compileErrors(source)).IsEmpty();
    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.IThing),[typeof(global::TestApp.IClock),]),");
    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.Worker),[typeof(global::TestApp.IClock),]),");
    await Assert.That(generated).Contains(
      "ServiceRequirement(typeof(global::TestApp.StandIn),[typeof(global::TestApp.ILog),]),");
  }

  /// <summary>
  /// Runs the generator over a compilation that references the container's abstractions and the hosting abstractions,
  /// so ServiceDescriptor, the registration extensions and GetService bind to the real types: the descriptor check is
  /// semantic, and a source in which they do not bind would prove nothing about it.
  /// </summary>
  [RequiresAssemblyFiles]
  private static Microsoft.CodeAnalysis.GeneratorDriverRunResult _runWithContainer(string source) =>
    Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(new ServiceRequirementsGenerator())
      .RunGenerators(_containerCompilation(source))
      .GetRunResult();

  [RequiresAssemblyFiles]
  private static string _runtimeDirectory => System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!;

  [RequiresAssemblyFiles]
  private static Microsoft.CodeAnalysis.CSharp.CSharpCompilation _containerCompilation(string source) =>
    GeneratorTestHelper.CreateCompilation(source).AddReferences(
      // IServiceProvider's home, which the container's signatures name.
      SharedMetadataReferences.FromFile(System.IO.Path.Combine(_runtimeDirectory, "System.ComponentModel.dll")),
      SharedMetadataReferences.FromFile(System.IO.Path.Combine(_runtimeDirectory, "System.Collections.dll")),
      SharedMetadataReferences.FromFile(typeof(Microsoft.Extensions.DependencyInjection.ServiceDescriptor).Assembly.Location),
      SharedMetadataReferences.FromFile(typeof(Microsoft.Extensions.Hosting.IHostedService).Assembly.Location));

  /// <summary>The source's own compile errors, before the generator runs.</summary>
  [RequiresAssemblyFiles]
  private static List<string> _compileErrors(string source) =>
    [.. _containerCompilation(source).GetDiagnostics()
      .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
      .Select(d => d.ToString())];

  private static string _flatten(Microsoft.CodeAnalysis.GeneratorDriverRunResult result) =>
    string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source))
      .Replace(" ", "", StringComparison.Ordinal)
      .Replace("\r", "", StringComparison.Ordinal)
      .Replace("\n", "", StringComparison.Ordinal)
      .Replace("global::Whizbang.Core.DependencyInjection.", "", StringComparison.Ordinal);
}
