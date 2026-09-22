using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707 // test method names use underscores
#pragma warning disable IDE1006 // test method names

/// <summary>
/// Coverage round 23 tail: <see cref="RawReceptorRegistry.FindByTypeName"/>'s null/empty guard.
/// Every existing test in RawReceptorRegistryTests.cs passes a non-empty type name (registered or
/// not) — none passes null or empty, so the short-circuit before normalization has never run.
/// </summary>
[Category("Messaging")]
public class RawReceptorRegistryCoverageTests {

  private sealed class FakeRawReceptor(string targetTypeName) : IRawReceptor {
    public string TargetMessageTypeName { get; } = targetTypeName;
    public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken) => Task.CompletedTask;
  }

  /// <summary>
  /// Operator impact: a dispatch-time lookup can legitimately receive an empty or missing type
  /// name (a malformed envelope, an unset header). Without the guard, that would flow into
  /// <c>EventTypeMatchingHelper.NormalizeTypeName</c> unguarded instead of resolving to "no raw
  /// receptor" — the safe, expected outcome for an unusable type name.
  /// </summary>
  [Test]
  public async Task FindByTypeName_NullTypeName_ReturnsNullAsync() {
    var registry = new RawReceptorRegistry([new FakeRawReceptor("MyApp.A, MyApp")]);

    var found = registry.FindByTypeName(null!);

    await Assert.That(found).IsNull()
      .Because("a null type name must resolve to 'no raw receptor', not flow into normalization "
             + "unguarded");
  }

  /// <summary>Operator impact: same guarantee as the null case, for an empty string.</summary>
  [Test]
  public async Task FindByTypeName_EmptyTypeName_ReturnsNullAsync() {
    var registry = new RawReceptorRegistry([new FakeRawReceptor("MyApp.A, MyApp")]);

    var found = registry.FindByTypeName(string.Empty);

    await Assert.That(found).IsNull()
      .Because("an empty type name must resolve to 'no raw receptor' via the same short-circuit "
             + "as null, not an accidental dictionary miss");
  }
}
