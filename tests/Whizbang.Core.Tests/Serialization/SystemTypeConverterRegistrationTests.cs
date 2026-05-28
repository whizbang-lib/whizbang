using System.Reflection;
using System.Text.Json.Serialization;
using TUnit.Assertions;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// Sentinel test that locks the invariant established by the JDNext -infinity outage:
/// any public sealed JsonConverter&lt;T&gt; in Whizbang.Core where T is NOT a Whizbang-owned
/// type cannot be discovered via [JsonConverter] attribute on T (we don't own T). The
/// only way consumers of JsonContextRegistry.CreateCombinedOptions() get such a converter
/// is via the global registry. This test fails on any future converter that targets a
/// non-Whizbang type without a matching JsonContextRegistry.RegisterConverter call in
/// WhizbangJsonContextInitializer.Initialize().
/// </summary>
[Category("Serialization")]
public class SystemTypeConverterRegistrationTests {

  [Test]
  public async Task EverySystemTypeConverterInCore_IsRegisteredInGlobalRegistry_Async() {
    var coreAssembly = typeof(JsonContextRegistry).Assembly;
    Type[] types;
    try {
      types = coreAssembly.GetTypes();
    } catch (ReflectionTypeLoadException ex) {
      types = [.. ex.Types.OfType<Type>()];
    }

    var systemTypeConverters = types
      .Where(t => t.IsPublic && t.IsSealed && !t.IsAbstract)
      .Where(_isJsonConverterForNonWhizbangType)
      .ToList();

    var options = JsonContextRegistry.CreateCombinedOptions();
    var registered = options.Converters.Select(c => c.GetType()).ToHashSet();

    var missing = systemTypeConverters
      .Where(t => !registered.Contains(t))
      .Select(t => t.FullName!)
      .OrderBy(s => s)
      .ToList();

    await Assert.That(missing)
      .IsEmpty()
      .Because(
        "Every public sealed JsonConverter<T> in Whizbang.Core targeting a non-Whizbang " +
        "type MUST be registered via JsonContextRegistry.RegisterConverter in " +
        "WhizbangJsonContextInitializer.Initialize(). Missing: " + string.Join(", ", missing));
  }

  private static bool _isJsonConverterForNonWhizbangType(Type candidate) {
    var baseType = candidate.BaseType;
    while (baseType is not null && baseType != typeof(object)) {
      if (baseType.IsGenericType
          && baseType.GetGenericTypeDefinition() == typeof(JsonConverter<>)) {
        var target = baseType.GetGenericArguments()[0];
        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        var targetAssemblyName = underlying.Assembly.GetName().Name;
        return targetAssemblyName is null
          || !targetAssemblyName.StartsWith("Whizbang.", StringComparison.Ordinal);
      }
      baseType = baseType.BaseType;
    }
    return false;
  }
}
