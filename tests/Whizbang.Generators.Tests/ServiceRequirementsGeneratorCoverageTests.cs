using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for ServiceRequirementsGenerator targeting: a generic invocation
/// named like a registration method but called without a receiver (both the type-based and
/// factory-based syntactic filters), a registration with more than two type arguments, an
/// implementation resolved to a generic method's own type parameter, a private implementation
/// type registered directly, a registered type with no constructor parameters, an inaccessible
/// constructor-parameter type, a non-generic call inside a factory lambda, a malformed
/// two-argument GetRequiredService call, and a referenceable generic type argument on a factory
/// service type. Complements ServiceRequirementsGeneratorTests.cs.
/// </summary>
[Category("Generators")]
public class ServiceRequirementsGeneratorCoverageTests {
  // ==================== Non-member-access generic invocations are never registrations ====================

  /// <summary>
  /// Both the type-based and factory-based scans key off a dotted call (`services.AddSingleton&lt;T&gt;()`).
  /// A local/private method that merely happens to share a registration method's name, called
  /// without a receiver, must never be mistaken for a DI registration — otherwise an unrelated
  /// helper's own parameters would show up in the startup validation manifest.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_DirectGenericCallNamedLikeARegistrationMethod_IsNotTreatedAsARegistrationAsync() {
    const string source = """
        using Microsoft.Extensions.DependencyInjection;
        namespace TestApp;
        public interface IClock { }
        public sealed class Worker {
          public Worker(IClock clock) { }
        }
        public static class Bootstrapper {
          private static void AddSingleton<T>() { }
          private static void Configure() {
            AddSingleton<Worker>();
          }
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).DoesNotContain("IClock")
      .Because("a call without a receiver is not a container registration, no matter what it's named");
  }

  // ==================== Type-argument count ====================

  /// <summary>
  /// Only one- and two-type-argument registrations have a defined "this is what gets built"
  /// implementation type. A three-argument call can't be mapped to an implementation at all, so
  /// treating it as one would inspect the wrong type's constructor.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_RegistrationWithMoreThanTwoTypeArguments_IsIgnoredAsync() {
    const string source = """
        using Microsoft.Extensions.DependencyInjection;
        namespace TestApp;
        public interface IClock { }
        public sealed class Worker {
          public Worker(IClock clock) { }
        }
        public static class Registration {
          public static IServiceCollection AddThing(this IServiceCollection services) {
            services.AddSingleton<Worker, Worker, Worker>();
            return services;
          }
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).DoesNotContain("IClock")
      .Because("a registration with more than two type arguments has no defined implementation type");
  }

  // ==================== Implementation resolves to a type parameter ====================

  /// <summary>
  /// A reusable registration helper that forwards its own type parameter to AddSingleton&lt;T&gt;()
  /// has no concrete implementation type until the helper is actually called with one — there is
  /// no constructor to inspect on a type parameter, and treating it as a named type would corrupt
  /// the manifest.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_RegistrationOverAGenericMethodsTypeParameter_IsSkippedAsync() {
    const string source = """
        using Microsoft.Extensions.DependencyInjection;
        namespace TestApp;
        public static class Registration {
          public static IServiceCollection AddGeneric<TImplementation>(this IServiceCollection services)
              where TImplementation : class {
            services.AddSingleton<TImplementation>();
            return services;
          }
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(result.Diagnostics.Any(d => d.Id == "CS8785")).IsFalse()
      .Because("a type parameter used as the implementation must not crash the generator");
    await Assert.That(generated).Contains("WhizbangServiceRequirements")
      .Because("the manifest must still be emitted even though this registration contributes nothing");
  }

  // ==================== Implementation type itself is inaccessible ====================

  /// <summary>
  /// The manifest is a source file in the same assembly, so it can only name a type that file can
  /// see. A private nested implementation type registered directly (not via a factory) must be
  /// skipped, or the generated manifest would fail to compile for everyone.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_PrivateImplementationTypeRegisteredDirectly_IsSkippedAsync() {
    const string source = """
        using Microsoft.Extensions.DependencyInjection;
        namespace TestApp;
        public interface IHook { }
        public sealed class Host {
          private sealed class PrivateWorker {
            public PrivateWorker(IHook hook) { }
          }
          public static IServiceCollection AddThing(IServiceCollection services) {
            services.AddSingleton<PrivateWorker>();
            return services;
          }
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).DoesNotContain("PrivateWorker")
      .Because("the generated manifest cannot name a private nested implementation type");
    await Assert.That(generated).DoesNotContain("IHook");
  }

  // ==================== Zero-parameter constructor ====================

  /// <summary>
  /// A type with nothing to inject has no requirement to record. Emitting an entry with an empty
  /// dependency list would be startup-manifest noise for the overwhelming majority of
  /// registrations that simply don't need anything from the container.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_RegisteredTypeWithNoConstructorParameters_ContributesNoEntryAsync() {
    const string source = """
        using Microsoft.Extensions.DependencyInjection;
        namespace TestApp;
        public sealed class NoDependencyWorker { }
        public static class Registration {
          public static IServiceCollection AddThing(this IServiceCollection services) {
            services.AddSingleton<NoDependencyWorker>();
            return services;
          }
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).DoesNotContain("NoDependencyWorker")
      .Because("a type with no constructor parameters has no requirement worth recording");
  }

  // ==================== Inaccessible constructor-parameter type ====================

  /// <summary>
  /// The manifest can only `typeof(...)` a dependency type the generated file can see. A
  /// constructor parameter whose own type is private (a real, if broken, program shape — the
  /// compiler already flags the accessibility mismatch) must be dropped rather than emitted, or
  /// the generated manifest itself fails to compile.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ConstructorDependencyTypeIsInaccessible_IsNotRecordedAsync() {
    const string source = """
        using Microsoft.Extensions.DependencyInjection;
        namespace TestApp;
        public sealed class Worker {
          private interface IPrivateDependency { }
          public Worker(IPrivateDependency dep) { }
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

    await Assert.That(generated).DoesNotContain("IPrivateDependency")
      .Because("a dependency type the generated file cannot see must never be emitted");
    await Assert.That(generated).DoesNotContain("TestApp.Worker");
    await Assert.That(result.Diagnostics.Any(d => d.Id == "CS8785")).IsFalse();
  }

  // ==================== Factory lambda body scanning ====================

  /// <summary>
  /// Factory bodies are ordinary code — a log line or other plain, non-generic method call is
  /// completely normal there. The scan for GetRequiredService&lt;T&gt;() calls must skip right over
  /// such calls instead of stumbling on them, or a routine logging statement could hide the real
  /// requirement recorded right next to it.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_FactoryLambdaWithANonGenericMemberCall_StillFindsTheRealDependencyAsync() {
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
            services.AddSingleton<ICloser>(sp => {
              System.Console.WriteLine("constructing closer");
              return new Closer(sp.GetRequiredService<IHook>());
            });
            return services;
          }
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).Contains("IHook")
      .Because("a plain non-generic call inside the factory body must not block finding the real GetRequiredService dependency");
  }

  /// <summary>
  /// GetRequiredService&lt;T&gt;() only ever takes one type argument in the real API. A call shaped
  /// differently can't be mapped to a single service type, so it must be skipped rather than
  /// guessed at — guessing wrong would record a requirement nobody actually asked for.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_FactoryLambdaWithATwoArgumentGetRequiredServiceCall_IsIgnoredAsync() {
    const string source = """
        using Microsoft.Extensions.DependencyInjection;
        namespace TestApp;
        public interface IHook { }
        public interface ICloser { }
        public sealed class Closer : ICloser { }
        public static class Registration {
          public static IServiceCollection AddThing(this IServiceCollection services) {
            services.AddSingleton<ICloser>(sp => {
              sp.GetRequiredService<IHook, ICloser>();
              return new Closer();
            });
            return services;
          }
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).DoesNotContain("IHook")
      .Because("a two-argument GetRequiredService call cannot correspond to the real single-type-argument API");
    await Assert.That(generated).DoesNotContain("TestApp.ICloser");
  }

  // ==================== Referenceable generic type argument on a factory service type ====================

  /// <summary>
  /// A generic service type's own type arguments are checked for accessibility too, but a PUBLIC
  /// type argument must not falsely trip that guard. Rejecting it would silently drop a
  /// perfectly legitimate registration (and its dependency) from the manifest.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_FactoryRegistrationOverAGenericServiceWithAPublicArgument_IsRecordedAsync() {
    const string source = """
        using Microsoft.Extensions.DependencyInjection;
        namespace TestApp;
        public interface IHook { }
        public interface IQuery<T> { }
        public sealed class Order { }
        public sealed class QueryImpl<T> : IQuery<T> {
          public QueryImpl(IHook hook) { }
        }
        public static class Registration {
          public static IServiceCollection AddThing(this IServiceCollection services) {
            services.AddSingleton<IQuery<Order>>(sp => new QueryImpl<Order>(sp.GetRequiredService<IHook>()));
            return services;
          }
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>(source);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).Contains("typeof(global::TestApp.IQuery<global::TestApp.Order>)")
      .Because("a public generic type argument must not be treated as inaccessible");
    await Assert.That(generated).Contains("typeof(global::TestApp.IHook)");
  }
}
