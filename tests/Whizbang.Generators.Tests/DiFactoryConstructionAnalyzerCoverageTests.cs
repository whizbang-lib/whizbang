using System.Diagnostics.CodeAnalysis;
using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="DiFactoryConstructionAnalyzer"/>, complementing
/// <c>tests/Whizbang.Generators.Tests/Analyzers/DiFactoryConstructionAnalyzerTests.cs</c>. That
/// file exercises the "omitted dependency" happy path through implicit <c>sp =&gt;</c> lambdas;
/// these tests target the operation-model bail-out, the explicitly-typed-parameter recognition
/// path, and the ancestor walk falling all the way through a non-factory lambda.
/// </summary>
/// <remarks>
/// Line 77 (<c>parameter is null</c> inside the DefaultValue-argument loop) is not covered here.
/// <c>IArgumentOperation.Parameter</c> is documented ("This can be null for __arglist parameters")
/// to be null only for <c>__arglist</c> arguments, and a <c>__arglist</c> parameter cannot be the
/// source of a compiler-synthesized <c>ArgumentKind.DefaultValue</c> argument — varargs parameters
/// cannot declare a default value in the first place, since <c>__arglist</c> is not a regular,
/// optional parameter. The two conditions this guard checks together (DefaultValue argument kind,
/// null Parameter) can never both hold in reachable code: dead by construction, tracing back to how
/// the operation model only ever produces a DefaultValue argument for a declared optional
/// parameter.
/// </remarks>
public class DiFactoryConstructionAnalyzerCoverageTests {

  private const string PRELUDE = """
    namespace Microsoft.Extensions.DependencyInjection {
      public interface IServiceCollection { }
      public static class Ext {
        public static IServiceCollection AddSingleton<T>(this IServiceCollection s, System.Func<System.IServiceProvider, T> f) => s;
      }
    }
    namespace App {
      using Microsoft.Extensions.DependencyInjection;
      public interface ILog { }
      public interface IStore { }
      public sealed class Store : IStore {
        public Store(ILog log, ILog? extra = null) { }
      }
    }
    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task ConstructingAnUnresolvedTypeInsideAFactoryIsNotReportedAsync() {
    var source = PRELUDE + """
      namespace App2 {
        using Microsoft.Extensions.DependencyInjection;
        public static class Reg {
          public static void Add(IServiceCollection services) {
            services.AddSingleton<object>(sp => new ThisTypeDoesNotExistAnywhere(null!));
          }
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<DiFactoryConstructionAnalyzer>(source);

    // An unresolved type turns the "new" expression into an invalid operation rather than a
    // proper object-creation operation — there is no real constructor or parameter list to check
    // for an omitted dependency, so the rule must stay silent instead of guessing at one.
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ500")).IsEmpty()
      .Because("a construction the compiler could not bind has no constructor to check for omitted parameters");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ExplicitlyTypedServiceProviderParameterIsRecognizedAsAFactoryAsync() {
    var source = PRELUDE + """
      namespace App2 {
        using Microsoft.Extensions.DependencyInjection;
        using App;
        public static class Reg {
          public static void Add(IServiceCollection services) {
            services.AddSingleton<IStore>((System.IServiceProvider container) => new Store(null!));
          }
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<DiFactoryConstructionAnalyzer>(source);

    // The registration lambda spells out its parameter type instead of using the conventional
    // "sp" name — a dependency omitted in this equally valid lambda form must still be reported,
    // or the rule only catches half of real registration code.
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ500")).IsNotEmpty()
      .Because("an explicitly-typed IServiceProvider parameter is conclusive evidence of a registration factory, independent of the parameter's name");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ConstructionInsideAnUnrelatedLambdaIsNotReportedAsync() {
    var source = PRELUDE + """
      namespace App2 {
        using App;
        public static class Reg {
          public static void Make() {
            System.Func<int, IStore> factory = count => new Store(null!);
          }
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<DiFactoryConstructionAnalyzer>(source);

    // A lambda whose parameter is neither typed as IServiceProvider nor named by the sp/provider/
    // serviceProvider convention is an ordinary callback, not a registration factory standing in
    // for the container. The ancestor walk must keep climbing past it rather than stopping here
    // and guessing.
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ500")).IsEmpty()
      .Because("a general-purpose callback lambda parameter must not be mistaken for the DI container");
  }
}
