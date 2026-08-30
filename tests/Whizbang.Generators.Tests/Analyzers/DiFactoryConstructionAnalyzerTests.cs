using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for the rule that catches a hand-constructed service dropping a dependency.
/// </summary>
/// <remarks>
/// <para>
/// A service built with <c>new</c> inside a DI factory silently defaults any parameter the author
/// omits. The compiler is satisfied, the container is satisfied, and the feature is simply absent.
/// That is how an audit decorator shipped with its logger and instance provider null in every
/// composed application while every unit test passed, because a test that constructs the type
/// supplies the argument itself and therefore cannot observe that the container does not.
/// </para>
/// <para>
/// The rule is syntactic: it does not care which service is being dropped, so it covers loggers,
/// telemetry providers and anything added later without needing a rule per dependency.
/// </para>
/// </remarks>
[Category("Analyzers")]
public class DiFactoryConstructionAnalyzerTests {

  private const string PRELUDE = """
    using System;
    namespace Microsoft.Extensions.DependencyInjection {
      public interface IServiceCollection { }
      public interface IServiceProvider2 { }
      public static class Ext {
        public static IServiceCollection AddSingleton<T>(this IServiceCollection s, Func<System.IServiceProvider, T> f) => s;
      }
    }
    namespace App {
      using Microsoft.Extensions.DependencyInjection;
      public interface ILog { }
      public interface IProbe { }
      public interface IStore { }
      public sealed class Store : IStore {
        public Store(ILog log, IProbe? probe = null) { }
      }
    }
    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task OmittingAnOptionalDependencyInsideAFactoryIsReportedAsync() {
    var source = PRELUDE + """
      namespace App2 {
        using Microsoft.Extensions.DependencyInjection;
        using App;
        public static class Reg {
          public static void Add(IServiceCollection services) {
            services.AddSingleton<IStore>(sp => new Store(null!));
          }
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<DiFactoryConstructionAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ500")).IsNotEmpty()
      .Because("the probe is silently null here, and nothing else in the toolchain says so");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SupplyingEveryDependencyIsNotReportedAsync() {
    var source = PRELUDE + """
      namespace App2 {
        using Microsoft.Extensions.DependencyInjection;
        using App;
        public static class Reg {
          public static void Add(IServiceCollection services) {
            services.AddSingleton<IStore>(sp => new Store(null!, null!));
          }
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<DiFactoryConstructionAnalyzer>(source);

    // Passing null explicitly is a decision the author made and can be seen in review. The rule
    // exists to stop omission, which is invisible, not to forbid a deliberate null.
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ500")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ConstructionOutsideAFactoryIsNotReportedAsync() {
    var source = PRELUDE + """
      namespace App2 {
        using App;
        public static class Elsewhere {
          public static object Make() => new Store(null!);
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<DiFactoryConstructionAnalyzer>(source);

    // Ordinary code and tests construct these types all the time and supply what they need. The
    // defect is specific to a registration standing in for the container.
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ500")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task NonInterfaceOptionalParametersAreNotReportedAsync() {
    var source = PRELUDE + """
      namespace App3 {
        using Microsoft.Extensions.DependencyInjection;
        public interface IThing { }
        public sealed class Thing : IThing {
          public Thing(int retries = 3, string name = "x") { }
        }
        public static class Reg {
          public static void Add(IServiceCollection services) {
            services.AddSingleton<IThing>(sp => new Thing());
          }
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<DiFactoryConstructionAnalyzer>(source);

    // Values with sensible defaults are not container-resolved services; flagging them would make
    // the rule fire on correct code, which is how a rule gets suppressed.
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ500")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TheDiagnosticNamesTheOmittedParameterAsync() {
    var source = PRELUDE + """
      namespace App2 {
        using Microsoft.Extensions.DependencyInjection;
        using App;
        public static class Reg {
          public static void Add(IServiceCollection services) {
            services.AddSingleton<IStore>(sp => new Store(null!));
          }
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<DiFactoryConstructionAnalyzer>(source);
    var message = string.Join(" ", diagnostics.Where(d => d.Id == "WHIZ500").Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)));

    // "Something is missing" sends the reader back to the constructor to work out what. Naming it
    // is the difference between a fix and an investigation.
    await Assert.That(message).Contains("probe");
  }
}
