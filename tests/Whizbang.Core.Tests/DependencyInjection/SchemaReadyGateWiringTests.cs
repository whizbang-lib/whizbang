using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.DependencyInjection;

/// <summary>
/// The schema-readiness gate must never be optional.
/// </summary>
/// <remarks>
/// <para>
/// A null gate never meant "no gating needed". It meant the wait was skipped and the worker began
/// work against a schema nobody had confirmed was there. Every site guarded the wait behind a null
/// check, so a worker constructed without a gate started immediately and nothing reported that the
/// check had not run.
/// </para>
/// <para>
/// The parameters were optional for a stated reason: so existing fixtures construct unchanged. That
/// trade bought test convenience with a production failure mode that cannot be observed, which is
/// why this is enforced rather than left to review.
/// </para>
/// <para>
/// No inert default ships. A gate answering "ready" without checking asserts the invariant the type
/// exists to establish, which is worse than the absence it would replace. A host with genuinely
/// nothing to wait on says so with <see cref="SchemaReadyGate.AlreadyReady"/>.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/injectable-services</docs>
[Category("DependencyInjection")]
public class SchemaReadyGateWiringTests {

  [Test]
  public async Task NoTypeDeclaresTheSchemaGateAsAnOptionalParameterAsync() {
    var offenders = new List<string>();

    foreach (var type in typeof(ISchemaReadyGate).Assembly.GetTypes()) {
      foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)) {
        foreach (var p in ctor.GetParameters()) {
          if (p.ParameterType == typeof(ISchemaReadyGate) && p.IsOptional) {
            offenders.Add($"{type.Name}.{p.Name}");
          }
        }
      }
    }

    offenders.Sort(StringComparer.Ordinal);

    await Assert.That(offenders).IsEmpty()
      .Because("an optional gate lets a worker start without waiting for the schema, and the "
             + "skipped wait produces no error at all:\n  " + string.Join("\n  ", offenders));
  }

  [Test]
  public async Task NoInjectedSchemaGateFieldIsNullableAsync() {
    var nullability = new NullabilityInfoContext();
    var offenders = new List<string>();

    foreach (var type in typeof(ISchemaReadyGate).Assembly.GetTypes()) {
      foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)) {
        if (field.FieldType != typeof(ISchemaReadyGate) || !field.IsInitOnly) {
          continue;
        }
        if (nullability.Create(field).ReadState == NullabilityState.Nullable) {
          offenders.Add($"{type.Name}.{field.Name}");
        }
      }
    }

    offenders.Sort(StringComparer.Ordinal);

    await Assert.That(offenders).IsEmpty()
      .Because("a nullable gate field is the state in which the readiness wait does not happen:\n  "
             + string.Join("\n  ", offenders));
  }

  [Test]
  public async Task AnAlreadyOpenGateDoesNotMakeAWaiterWaitAsync() {
    var gate = SchemaReadyGate.AlreadyReady();

    await gate.WaitForReadyAsync(CancellationToken.None);

    // This is what a fixture with no schema step must use. Registering a real gate that nothing
    // marks ready does not fail the test, it hangs it, which is a far worse thing to leave behind.
    await Assert.That(gate.IsReady).IsTrue();
  }
}
