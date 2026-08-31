using System.Reflection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.DependencyInjection;

/// <summary>
/// No framework type may store its logger in a nullable field.
/// </summary>
/// <remarks>
/// <para>
/// A null logger is silent absence of diagnostics, which is the worst possible thing to lose
/// quietly: the evidence you would use to notice anything else is missing is itself missing. Nobody
/// reports a bug about logs that were never written.
/// </para>
/// <para>
/// Unlike a service dependency, a logger always has a correct fallback that needs no registration,
/// so an injected logger field has no reason to be nullable. Most of the framework already coalesces
/// to <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger"/>; this makes that universal,
/// so the call site can keep its optional parameter without the field ever holding null.
/// </para>
/// <para>
/// Lazy resolution caches are excluded. A non-readonly nullable logger field means "not resolved
/// yet" rather than "nobody supplied one", and that is a different thing from the defect here.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/injectable-services</docs>
[Category("DependencyInjection")]
public class LoggerFieldNullabilityTests {

  [Test]
  public async Task NoFrameworkTypeStoresItsLoggerInANullableFieldAsync() {
    var assembly = typeof(Whizbang.Core.IEvent).Assembly;
    var nullability = new NullabilityInfoContext();
    var offenders = new List<string>();

    foreach (var type in assembly.GetTypes()) {
      foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)) {
        if (!typeof(ILogger).IsAssignableFrom(field.FieldType)) {
          continue;
        }
        // Only injected loggers are in scope. A non-readonly nullable field is a lazy resolution
        // cache whose null means "not resolved yet", and several types legitimately use one behind
        // a non-nullable property. Flagging those would force correct code to change in order to
        // satisfy an assertion that was drawn too wide, which is how a guard loses its credibility.
        if (!field.IsInitOnly) {
          continue;
        }
        // Auto-property backing fields belong to a property, which is a separate declaration
        // surface with its own nullability contract and, on the context records that have one,
        // part of a public API. Nullable logger PROPERTIES are worth revisiting, but folding them
        // in here would mean this test silently drove a public API change.
        if (field.Name.Contains("k__BackingField", StringComparison.Ordinal)) {
          continue;
        }
        if (nullability.Create(field).ReadState == NullabilityState.Nullable) {
          offenders.Add($"{type.Name}.{field.Name}");
        }
      }
    }

    offenders.Sort(StringComparer.Ordinal);

    await Assert.That(offenders).IsEmpty()
      .Because("a nullable logger field writes no diagnostics at all when nothing supplies one, and "
             + "the absence is invisible precisely because logging is how absence gets noticed:\n  "
             + string.Join("\n  ", offenders));
  }
}
