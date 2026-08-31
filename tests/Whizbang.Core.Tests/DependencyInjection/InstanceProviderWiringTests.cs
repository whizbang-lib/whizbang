using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.DependencyInjection;

/// <summary>
/// Pattern A conversion for <see cref="IServiceInstanceProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// This dependency was optional and nullable at nine construction sites. One of them, the audit
/// event-store decorator, was verified to receive null in every composed application, because the
/// registration built it by hand and never passed the argument. The other eight had the same shape
/// and the same absence of any test that could observe it: a test that constructs a type directly
/// supplies the argument itself, so it can never detect that the container does not.
/// </para>
/// <para>
/// The provider is registered unconditionally, so the fix is not a new registration but removing
/// the optionality that let every hand-construction skip it.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/injectable-services</docs>
[Category("DependencyInjection")]
public class InstanceProviderWiringTests {

  [Test]
  public async Task NoTypeDeclaresTheInstanceProviderAsAnOptionalParameterAsync() {
    var offenders = _optionalInstanceProviderParameters();

    await Assert.That(offenders).IsEmpty()
      .Because("an optional instance provider is silently null wherever the type is constructed by "
             + "hand, and every record it writes is then unattributable to the instance that "
             + "produced it:\n  " + string.Join("\n  ", offenders));
  }

  [Test]
  public async Task NoTypeStoresTheInstanceProviderInANullableFieldAsync() {
    var assembly = typeof(IServiceInstanceProvider).Assembly;
    var offenders = new List<string>();
    var nullability = new NullabilityInfoContext();

    foreach (var type in assembly.GetTypes()) {
      foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)) {
        if (field.FieldType != typeof(IServiceInstanceProvider)) {
          continue;
        }
        if (nullability.Create(field).ReadState == NullabilityState.Nullable) {
          offenders.Add($"{type.Name}.{field.Name}");
        }
      }
    }

    // A non-nullable field is the property that makes the dependency's absence impossible rather
    // than merely unlikely: there is no state in which the field holds null.
    await Assert.That(offenders).IsEmpty()
      .Because("a nullable field is what allows the dependency to be absent at run time:\n  "
             + string.Join("\n  ", offenders));
  }

  private static List<string> _optionalInstanceProviderParameters() {
    var assembly = typeof(IServiceInstanceProvider).Assembly;
    var offenders = new List<string>();

    foreach (var type in assembly.GetTypes()) {
      foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)) {
        foreach (var p in ctor.GetParameters()) {
          if (p.ParameterType == typeof(IServiceInstanceProvider) && p.IsOptional) {
            offenders.Add($"{type.Name}.{p.Name}");
          }
        }
      }
    }

    offenders.Sort(StringComparer.Ordinal);
    return offenders;
  }
}
