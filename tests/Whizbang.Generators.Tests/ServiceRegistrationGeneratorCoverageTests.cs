using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for ServiceRegistrationGenerator targeting the accessibility gate in
/// <c>_isTypeAccessible</c>, the internal-namespace skip, and the open-generic / unrelated-interface
/// branches of the direct-implementation pattern. Complements ServiceRegistrationGeneratorTests.cs.
/// </summary>
/// <remarks>
/// Line 206 (the default-to-Lens fallback at the end of <c>_getServiceCategory</c>) is not covered
/// here: that method is only ever called with a user interface for which
/// <c>_isUserInterfaceExtendingWhizbang</c> already found a matching Lens- or Perspective-prefixed
/// entry in <c>AllInterfaces</c>, using the exact same two prefix checks. The loop inside
/// <c>_getServiceCategory</c> re-scans that same <c>AllInterfaces</c> set with the same two prefixes,
/// so it cannot fail to match on the second pass — the fallback is dead by construction, not a
/// reachable branch.
/// </remarks>
public class ServiceRegistrationGeneratorCoverageTests {
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PrivateProtectedType_IsSkippedAsInaccessibleAsync() {
    // A type that is only reachable from inside its own inheritance chain cannot be referenced by
    // the generated registration code; registering it anyway would produce a DI method that fails
    // to compile for every consumer of the library.
    const string source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Widget(Guid Id);
            public interface IWidgetLens : ILensQuery<Widget> { }

            public class Container {
              private protected class InnerLens : IWidgetLens {
                public IQueryable<PerspectiveRow<Widget>> Query => throw new NotImplementedException();
                public Task<Widget?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Widget?>(null);
              }
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).DoesNotContain("InnerLens")
      .Because("a private protected nested type cannot be referenced from the generated registration code");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PubliclyAccessibleTypeInsidePrivateProtectedContainer_IsSkippedAsync() {
    // Even a type that is itself public is unreachable from outside if one of its containing types
    // is not; the accessibility walk must check every level of nesting, not just the innermost type.
    const string source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Gadget(Guid Id);
            public interface IGadgetLens : ILensQuery<Gadget> { }

            public class OuterContainer {
              private protected class MiddleContainer {
                public class InnerLens : IGadgetLens {
                  public IQueryable<PerspectiveRow<Gadget>> Query => throw new NotImplementedException();
                  public Task<Gadget?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Gadget?>(null);
                }
              }
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).DoesNotContain("InnerLens")
      .Because("a private protected containing type makes the nested type unreachable from outside, even though the nested type itself is public");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WhizbangCoreNamespacedType_IsSkippedAsync() {
    // This generator is for user code only; a type that happens to live under the Whizbang.Core
    // namespace (library-internal code) must never be auto-registered on a consumer's behalf.
    const string source = """
            using System;

            namespace Whizbang.Core;

            public class InternalLibraryType : IDisposable {
              public void Dispose() { }
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).DoesNotContain("InternalLibraryType")
      .Because("types under the Whizbang.Core namespace are library-internal and must not be auto-registered");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ClassImplementingUnrelatedInterface_IsNotRegisteredAsync() {
    // A class that happens to implement some interface unrelated to lenses or perspectives (here,
    // IDisposable) must not be swept into DI registration just because it has a base list.
    const string source = """
            using System;

            namespace TestApp;

            public class NotALensOrPerspective : IDisposable {
              public void Dispose() { }
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).DoesNotContain("NotALensOrPerspective")
      .Because("implementing an interface unrelated to ILensQuery/IPerspectiveFor must not trigger registration");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_OpenGenericDirectImplementation_IsSkippedAsync() {
    // A generic class implementing ILensQuery<TModel> with TModel still an open type parameter
    // (rather than a closed type like ILensQuery<Order>) cannot be registered against a concrete
    // service type; the generator must recognize and skip the open-generic shape.
    const string source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Widget(Guid Id);

            public class GenericLens<TModel> : ILensQuery<TModel> where TModel : class {
              public IQueryable<PerspectiveRow<TModel>> Query => throw new NotImplementedException();
              public Task<TModel?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<TModel?>(null);
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<ServiceRegistrationGenerator>(source);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "ServiceRegistrations.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).DoesNotContain("GenericLens")
      .Because("an open generic ILensQuery<TModel> implementation has no concrete type to register against");
  }
}
