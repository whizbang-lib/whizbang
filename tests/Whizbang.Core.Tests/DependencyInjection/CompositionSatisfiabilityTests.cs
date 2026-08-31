using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.DependencyInjection;

/// <summary>
/// Audits real compositions for constructor dependencies that no registration satisfies.
/// </summary>
/// <remarks>
/// <para>
/// The shipped validator is reflection-free and reads a generated manifest. This audit reaches the
/// same conclusion the other way round, by reflecting over the constructors of everything an
/// <c>Add*</c> extension registers. Reflection is permitted here because this is a test assembly;
/// what it produces is the ground truth the generator will later have to reproduce.
/// </para>
/// <para>
/// It exists because the interesting question is not whether the validator works, but which
/// dependencies are unsatisfied in this codebase right now. Every one it finds is a service that is
/// silently null in a composed application today, and therefore a candidate defect in anything
/// already deployed.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/registration-validation</docs>
[Category("DependencyInjection")]
public class CompositionSatisfiabilityTests {

  /// <summary>
  /// The count of optional injected parameters as of the audit that established this baseline.
  /// </summary>
  /// <remarks>
  /// A ratchet, not a target. Existing occurrences are tracked in
  /// <c>plans/di-registration-findings.md</c> and converted to required parameters with TryAdd
  /// defaults over time; this number must only ever move down. Asserting equality would fail the
  /// build on every improvement, and asserting nothing would let the surface grow silently, which
  /// is how it reached this size.
  /// </remarks>
  private const int OPTIONAL_INJECTED_PARAMETER_BASELINE = 140;

  /// <summary>
  /// Fails when a new optional interface-typed constructor parameter is introduced.
  /// </summary>
  /// <remarks>
  /// An optional injected parameter is silently null wherever its type is hand-constructed at a
  /// registration site, and a registration site that forgets one produces no error at all. Each new
  /// occurrence is therefore a new opportunity for a feature to be absent in production while every
  /// unit test passes, because unit tests supply the argument themselves.
  /// </remarks>
  [Test]
  public async Task OptionalInjectedParameterSurfaceDoesNotGrowAsync() {
    var findings = _findOptionalInjectedParameters();

    await Assert.That(findings.Count).IsLessThanOrEqualTo(OPTIONAL_INJECTED_PARAMETER_BASELINE)
      .Because("a new optional injected parameter adds a dependency that is silently null wherever "
             + "the type is built by hand; make it required and supply a TryAdd default instead:\n"
             + _format(findings));

    // A drop means the migration advanced. Lower the baseline in the same change, or the ratchet
    // silently loosens and the surface can grow back to here unnoticed.
    await Assert.That(findings.Count).IsGreaterThanOrEqualTo(OPTIONAL_INJECTED_PARAMETER_BASELINE)
      .Because($"the optional injected parameter surface dropped to {findings.Count}; lower "
             + "OPTIONAL_INJECTED_PARAMETER_BASELINE to match and update the findings register");
  }

  /// <summary>
  /// Scans the framework assembly for constructor parameters that are optional and interface-typed.
  /// </summary>
  private static List<string> _findOptionalInjectedParameters() {
    var assembly = typeof(Whizbang.Core.IEvent).Assembly;
    var findings = new List<string>();

    foreach (var type in assembly.GetTypes()) {
      if (type.IsAbstract || type.IsInterface || type.IsEnum || type.IsGenericTypeDefinition) {
        continue;
      }

      foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)) {
        foreach (var p in ctor.GetParameters()) {
          if (!p.IsOptional || !p.ParameterType.IsInterface) {
            continue;
          }
          // Only Whizbang-owned and logging abstractions matter here; framework-external optional
          // interfaces are the consumer's business, not this guard's.
          var ns = p.ParameterType.Namespace ?? string.Empty;
          if (!ns.StartsWith("Whizbang", StringComparison.Ordinal)
              && !ns.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal)) {
            continue;
          }
          findings.Add($"{type.Name}.{p.Name}: {p.ParameterType.Name}");
        }
      }
    }

    findings.Sort(StringComparer.Ordinal);
    return findings;
  }

  private static string _format(List<string> findings) =>
    findings.Count == 0 ? "(none)" : string.Join("\n", findings.Select(f => "  - " + f));
}
