using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for the rule that flags an optional injected constructor parameter at its declaration.
/// </summary>
/// <remarks>
/// <para>
/// WHIZ500 catches the omission at a call site. This catches the declaration that makes omission
/// possible, at the moment it is written, which is the cheapest point to fix it. The two are
/// complementary: one stops the surface growing, the other stops today's surface causing harm.
/// </para>
/// <para>
/// Reported as information rather than a warning. The existing surface is large, and a rule that
/// turns an established codebase red on first build gets suppressed globally, which costs more than
/// it buys.
/// </para>
/// </remarks>
[Category("Analyzers")]
public class OptionalInjectedParameterAnalyzerTests {

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnOptionalInterfaceParameterIsReportedAsync() {
    const string source = """
      namespace App;
      public interface IClock { }
      public sealed class Worker {
        public Worker(IClock? clock = null) { }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<OptionalInjectedParameterAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ501")).IsNotEmpty()
      .Because("this declaration is what allows a construction site to drop the dependency silently");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ARequiredInterfaceParameterIsNotReportedAsync() {
    const string source = """
      namespace App;
      public interface IClock { }
      public sealed class Worker {
        public Worker(IClock clock) { }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<OptionalInjectedParameterAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ501")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task OptionalValueParametersAreNotReportedAsync() {
    const string source = """
      using System.Threading;
      namespace App;
      public sealed class Worker {
        public Worker(int retries = 3, string name = "x", CancellationToken token = default) { }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<OptionalInjectedParameterAnalyzer>(source);

    // A retry count with a sensible default is not a dependency. Flagging it would bury the real
    // signal under noise and get the rule turned off.
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ501")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TheDiagnosticNamesTheParameterAsync() {
    const string source = """
      namespace App;
      public interface IProbe { }
      public sealed class Worker {
        public Worker(IProbe? probe = null) { }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<OptionalInjectedParameterAnalyzer>(source);
    var message = string.Join(" ", diagnostics
      .Where(d => d.Id == "WHIZ501")
      .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)));

    await Assert.That(message).Contains("probe");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ItIsInformationalSoAnExistingCodebaseStaysBuildableAsync() {
    const string source = """
      namespace App;
      public interface IClock { }
      public sealed class Worker {
        public Worker(IClock? clock = null) { }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<OptionalInjectedParameterAnalyzer>(source);
    var reported = diagnostics.Where(d => d.Id == "WHIZ501").ToList();

    // Severity is part of the contract here, not an implementation detail: a rule that turns an
    // established codebase red on first build gets suppressed globally and then catches nothing.
    await Assert.That(reported).IsNotEmpty();
    await Assert.That(reported[0].Severity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Info);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task APositionalRecordIsNotReportedAsync() {
    // A record's parameters are values supplied at creation, not services a container resolves:
    // there is nothing to register a default for, so the rule stays silent even for an interface.
    const string source = """
      using System.Collections.Generic;
      namespace App;
      public interface ICallerInfo { }
      public sealed record ReceptorInfo(string Name, IReadOnlyList<string>? Tags = null, ICallerInfo? Caller = null);
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<OptionalInjectedParameterAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ501")).IsEmpty()
      .Because("a positional record is a data carrier; flagging its parameters would push a null literal to every construction site instead of a TryAdd default");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ABclCollectionInterfaceIsNotReportedAsync() {
    const string source = """
      using System.Collections.Generic;
      namespace App;
      public sealed class Serializer {
        public Serializer(IReadOnlyList<string>? names = null, IComparer<string>? order = null, IEnumerable<int>? sizes = null) { }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<OptionalInjectedParameterAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ501")).IsEmpty()
      .Because("IReadOnlyList<T>, IComparer<T> and IEnumerable<T> describe a value the caller shapes; the rule's remedy, a container default, has no meaning for them");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AServiceInterfaceBesideACollectionIsStillReportedAsync() {
    const string source = """
      using System.Collections.Generic;
      namespace App;
      public interface IClock { }
      public sealed class Worker {
        public Worker(IReadOnlyList<string>? names = null, IClock? clock = null) { }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<OptionalInjectedParameterAnalyzer>(source);

    var reported = diagnostics.Where(d => d.Id == "WHIZ501").ToList();
    await Assert.That(reported.Count).IsEqualTo(1)
      .Because("the collection parameter is exempt, the service parameter is not; the exemption must not swallow its neighbor");
    await Assert.That(reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("'clock'");
  }
}
