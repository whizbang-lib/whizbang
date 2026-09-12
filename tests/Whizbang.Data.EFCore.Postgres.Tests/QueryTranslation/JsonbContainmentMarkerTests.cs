using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// The containment markers refuse to run.
/// </summary>
/// <remarks>
/// <para>
/// Every <c>Matches</c> and <c>MatchesAny</c> overload is a query marker: a method whose body is never
/// meant to execute, existing so a registered translation has something to replace. The rewriter plants
/// a call to one, Entity Framework's model maps that call to SQL, and the body is skipped.
/// </para>
/// <para>
/// Which is why the body matters. Should the registration be missing, the mode be off, or the call be
/// reached through <c>AsEnumerable</c> or an in-memory list, the marker is invoked for real. Returning
/// a default would silently answer "no rows match" and look like a working query; throwing says what
/// happened. Every overload is asserted rather than a sample of them, because a marker that returns
/// <c>false</c> instead of throwing is exactly the mistake this pins, and it would be made one overload
/// at a time.
/// </para>
/// <para>
/// The list is read from the registration tables rather than written out, so an overload added without
/// a marker body, or added to only one of the two tables, fails here rather than going unchecked.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
[Category("Shard1")]
public class JsonbContainmentMarkerTests {

  /// <summary>One registered marker, named so a failure says which overload it was.</summary>
  /// <param name="Name">The overload's signature, for the failure message.</param>
  /// <param name="Overload">The method to invoke.</param>
  public sealed record MarkerCase(string Name, MethodInfo Overload) {
    /// <inheritdoc/>
    public override string ToString() => Name;
  }

  /// <summary>Every registered overload, equality and set membership alike.</summary>
  public static IEnumerable<Func<MarkerCase>> AllOverloads() {
    foreach (var overload in JsonbContainment.Overloads.Concat(JsonbContainment.SetOverloads)) {
      var signature = string.Join(
        ", ", overload.GetParameters().Select(p => p.ParameterType.Name));
      var captured = overload;
      yield return () => new MarkerCase($"{captured.Name}({signature})", captured);
    }
  }

  /// <summary>
  /// Calling a marker throws, rather than returning an answer nothing computed.
  /// </summary>
  /// <remarks>
  /// Invoked through reflection so the assertion covers the overloads as registered. A call written out
  /// in C# would prove the same thing about whichever ones someone remembered to write.
  /// </remarks>
  [Test]
  [MethodDataSource(nameof(AllOverloads))]
  public async Task CallingAMarkerThrowsAsync(MarkerCase overload) {
    var arguments = overload.Overload.GetParameters()
      .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
      .ToArray();

    var thrown = await Assert.That(() => overload.Overload.Invoke(null, arguments))
      .Throws<TargetInvocationException>()
      .Because($"{overload.Name} is a marker for a translation, so running it means the translation is "
        + "absent and a returned default would read as an empty result set");

    await Assert.That(thrown!.InnerException).IsTypeOf<NotSupportedException>();
    await Assert.That(thrown.InnerException!.Message).Contains("only valid inside a LINQ query",
        StringComparison.Ordinal)
      .Because("the message has to say where the call belongs, since the call site looks correct");
  }

  /// <summary>
  /// Both registration tables are non-empty, so the case above is not vacuously passing.
  /// </summary>
  /// <remarks>
  /// A source that yielded nothing would report this whole class as green. Asserting the tables have
  /// entries is what makes the marker cases mean something.
  /// </remarks>
  [Test]
  public async Task TheRegistrationTablesAreNotEmptyAsync() {
    await Assert.That(JsonbContainment.Overloads.Count).IsGreaterThan(0);
    await Assert.That(JsonbContainment.SetOverloads.Count).IsGreaterThan(0);
  }
}
