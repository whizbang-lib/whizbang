using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ014 — the receptor whose constructor dependency injection cannot supply.
/// </summary>
/// <remarks>
/// The stakes are why this diagnostic exists at all: receptors are registered as a group, and an
/// un-constructible one aborts the <em>entire</em> service provider under container validation,
/// not just itself. So the failure is a service that will not start, reported against a type the
/// author may not connect to the receptor they just wrote.
///
/// <para>
/// That also sets the bar for false positives. The detector only flags what DI is genuinely
/// unlikely to hold — a delegate, or a bare primitive — and deliberately leaves anything else
/// alone, because a warning that fires on legitimate code trains people to ignore it. The tests
/// below are as much about what must stay quiet as what must fire.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class ReceptorInjectabilityDiagnosticTests {

  private static string _source(string receptorBody, string extraTypes = "") => $$"""
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Whizbang.Core;

    namespace MyApp;

    public record ProbeCommand([property: StreamId] Guid ProbeId) : ICommand;

    {{extraTypes}}

    public class ProbeReceptor : IReceptor<ProbeCommand> {
    {{receptorBody}}
      public ValueTask HandleAsync(ProbeCommand message, CancellationToken ct = default)
        => ValueTask.CompletedTask;
    }
    """;

  private static ImmutableArray<Diagnostic> _run(string receptorBody, string extraTypes = "") {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(
      _source(receptorBody, extraTypes));
    return result.Diagnostics;
  }

  private static bool _hasWhiz014(ImmutableArray<Diagnostic> diagnostics)
    => diagnostics.Any(d => d.Id == "WHIZ014");

  // ============================================================
  // What must fire
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("string", "name")]
  [Arguments("int", "count")]
  [Arguments("long", "size")]
  [Arguments("bool", "enabled")]
  [Arguments("double", "ratio")]
  [Arguments("decimal", "amount")]
  public async Task BarePrimitiveConstructorParameter_ReportsWHIZ014Async(string type, string name) {
    // A bare primitive is never registered in a container, so this receptor cannot be built —
    // and it takes the whole provider down with it rather than failing alone.
    var diagnostics = _run($$"""
      private readonly {{type}} _{{name}};
      public ProbeReceptor({{type}} {{name}}) { _{{name}} = {{name}}; }
    """);

    await Assert.That(_hasWhiz014(diagnostics)).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task DelegateConstructorParameter_ReportsWHIZ014Async() {
    // Delegates are the other common shape — a callback the author meant to pass by hand.
    var diagnostics = _run("""
      private readonly Func<int> _factory;
      public ProbeReceptor(Func<int> factory) { _factory = factory; }
    """);

    await Assert.That(_hasWhiz014(diagnostics)).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheDiagnostic_NamesTheParameterAndItsTypeAsync() {
    // The author has to be able to find the parameter. A message that only named the receptor
    // would send them reading a constructor list to guess which one DI cannot supply.
    var diagnostics = _run("""
      private readonly string _connectionName;
      public ProbeReceptor(string connectionName) { _connectionName = connectionName; }
    """);

    var whiz014 = diagnostics.FirstOrDefault(d => d.Id == "WHIZ014");
    await Assert.That(whiz014).IsNotNull();
    var message = whiz014!.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("connectionName");
    await Assert.That(message).Contains("ProbeReceptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheWidestConstructorIsTheOneCheckedAsync() {
    // The container picks the widest accessible constructor, so that is the one that has to be
    // analyzed. Checking the narrowest would clear a receptor DI will still fail to build.
    var diagnostics = _run("""
      private readonly string _name;
      public ProbeReceptor() { _name = ""; }
      public ProbeReceptor(string name) { _name = name; }
    """);

    await Assert.That(_hasWhiz014(diagnostics)).IsTrue()
      .Because("DI chooses the widest constructor — clearing this on the parameterless one would "
             + "report success for a service that cannot start");
  }

  // ============================================================
  // What must stay quiet
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ParameterlessReceptor_IsQuietAsync() {
    var diagnostics = _run("");

    await Assert.That(_hasWhiz014(diagnostics)).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task InjectedServiceDependency_IsQuietAsync() {
    // The ordinary shape. Flagging it would make the diagnostic noise.
    var diagnostics = _run("""
      private readonly IClock _clock;
      public ProbeReceptor(IClock clock) { _clock = clock; }
    """, "public interface IClock { DateTimeOffset UtcNow { get; } }");

    await Assert.That(_hasWhiz014(diagnostics)).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PrimitiveWithADefaultValue_IsQuietAsync() {
    // A defaulted parameter is not a hazard: the container simply omits it, and the receptor
    // still constructs.
    var diagnostics = _run("""
      private readonly int _retries;
      public ProbeReceptor(int retries = 3) { _retries = retries; }
    """);

    await Assert.That(_hasWhiz014(diagnostics)).IsFalse()
      .Because("the container can omit a defaulted parameter, so it never blocks construction");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AServiceDependencyBesideADefaultedPrimitive_IsQuietAsync() {
    // The scan has to keep walking past the defaulted parameter rather than stopping at it.
    var diagnostics = _run("""
      private readonly IClock _clock;
      public ProbeReceptor(IClock clock, int retries = 3) { _clock = clock; }
    """, "public interface IClock { DateTimeOffset UtcNow { get; } }");

    await Assert.That(_hasWhiz014(diagnostics)).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task OnlyAPrivateConstructor_IsQuietAsync() {
    // A receptor with no accessible constructor is not a DI hazard the container will hit in
    // this form — there is nothing for the detector to judge, so it must say nothing rather
    // than guess.
    var diagnostics = _run("""
      private readonly string _name;
      private ProbeReceptor(string name) { _name = name; }
    """);

    await Assert.That(_hasWhiz014(diagnostics)).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ComplexNonServiceType_IsQuietAsync() {
    // The detector deliberately stops at delegates and bare primitives. A plain class might be
    // registered or might not; guessing harder would warn on legitimate code.
    var diagnostics = _run("""
      private readonly Settings _settings;
      public ProbeReceptor(Settings settings) { _settings = settings; }
    """, "public sealed class Settings { public string Name { get; set; } = \"\"; }");

    await Assert.That(_hasWhiz014(diagnostics)).IsFalse()
      .Because("a warning that fires on legitimate code trains people to ignore it");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SuppressedReceptor_IsQuietAsync() {
    // The documented escape hatch for a receptor the author constructs by hand.
    var diagnostics = _run("""
      private readonly string _name;
      public ProbeReceptor(string name) { _name = name; }
    """).Where(d => d.Id == "WHIZ014").ToList();
    await Assert.That(diagnostics).IsNotEmpty()
      .Because("the un-suppressed form must fire, or the suppressed comparison below proves nothing");

    var suppressed = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>("""
      using System;
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;

      namespace MyApp;

      public record ProbeCommand([property: StreamId] Guid ProbeId) : ICommand;

      [SuppressReceptorRegistration]
      public class ProbeReceptor : IReceptor<ProbeCommand> {
        private readonly string _name;
        public ProbeReceptor(string name) { _name = name; }
        public ValueTask HandleAsync(ProbeCommand message, CancellationToken ct = default)
          => ValueTask.CompletedTask;
      }
      """).Diagnostics;

    await Assert.That(suppressed.Any(d => d.Id == "WHIZ014")).IsFalse()
      .Because("a receptor the author constructs by hand is never asked of the container");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheGeneratorStillEmitsWithoutErrorsAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(_source("""
      private readonly string _name;
      public ProbeReceptor(string name) { _name = name; }
    """));

    await Assert.That(result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsFalse()
      .Because("WHIZ014 is a warning — it must not stop the build, only report the hazard");
  }
}
