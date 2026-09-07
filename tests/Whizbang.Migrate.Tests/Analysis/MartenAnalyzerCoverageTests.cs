using Whizbang.Migrate.Analysis;

namespace Whizbang.Migrate.Tests.Analysis;

/// <summary>
/// Coverage-round tests for MartenAnalyzer branches not exercised by MartenAnalyzerTests:
/// DI calls invoked without a receiver or through a shape that isn't a call at all, a nested
/// generic aggregate type, a base type whose generic argument can't be fully parsed, and a
/// projection declared inside a block-scoped (brace) namespace rather than a file-scoped one.
/// </summary>
/// <tests>Whizbang.Migrate/Analysis/MartenAnalyzer.cs:*</tests>
public class MartenAnalyzerCoverageTests {

  // A DI registration analyzer that only recognized `x.AddMarten(...)` and missed an
  // unqualified `AddMarten(...)` call would under-report registrations that still need to be
  // replaced during migration -- the migration would then leave one Marten registration behind.
  [Test]
  public async Task AnalyzeAsync_DetectsAddMartenRegistration_CalledUnqualifiedAsync() {
    // Arrange -- no receiver at all, so the invocation's target is a bare identifier rather
    // than a member access.
    var analyzer = new MartenAnalyzer();
    const string sourceCode = """
      public static class ServiceCollectionExtensions {
        public static void AddServices() {
          AddMarten();
        }

        private static void AddMarten() { }
      }
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Extensions/ServiceCollectionExtensions.cs");

    // Assert
    await Assert.That(result.DIRegistrations.Count).IsEqualTo(1)
      .Because("an unqualified call is still a call to AddMarten and needs to be migrated");
    await Assert.That(result.DIRegistrations[0].RegistrationKind).IsEqualTo(DIRegistrationKind.AddMarten);
    await Assert.That(result.DIRegistrations[0].OriginalCode).Contains("AddMarten()");
  }

  // An invocation whose target is neither a member access nor a plain identifier (for example,
  // an immediately-invoked lambda) must be safely ignored rather than crash the analyzer or get
  // misclassified as a registration that isn't there.
  [Test]
  public async Task AnalyzeAsync_IgnoresInvocationsThatAreNeitherMemberAccessNorIdentifierAsync() {
    // Arrange
    var analyzer = new MartenAnalyzer();
    const string sourceCode = """
      public class Startup {
        public static void Configure() {
          (() => { })();
          AddMarten();
        }

        private static void AddMarten() { }
      }
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Startup.cs");

    // Assert -- only the genuine AddMarten() call is reported; the lambda invocation is inert
    await Assert.That(result.DIRegistrations.Count).IsEqualTo(1)
      .Because("the unclassifiable invocation shape must not be misreported as a registration");
    await Assert.That(result.DIRegistrations[0].RegistrationKind).IsEqualTo(DIRegistrationKind.AddMarten);
  }

  // The aggregate type of a projection can itself be a generic type. Splitting the projection's
  // type arguments on every comma without tracking nesting depth would slice a nested generic
  // like Dictionary<string, Order> into two bogus arguments -- reporting the wrong aggregate
  // type would send the migration transforming code around the wrong type entirely.
  [Test]
  public async Task AnalyzeAsync_DetectsNestedGenericAggregateTypeAsync() {
    // Arrange
    var analyzer = new MartenAnalyzer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderSummaryProjection : MultiStreamProjection<Dictionary<string, Order>, Guid> {
        public void Apply(OrderCreated @event, Dictionary<string, Order> state) { }
      }

      public class Order { }
      public record OrderCreated(string Id);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Projections/OrderSummaryProjection.cs");

    // Assert
    await Assert.That(result.Projections.Count).IsEqualTo(1);
    await Assert.That(result.Projections[0].AggregateType).IsEqualTo("Dictionary<string, Order>")
      .Because("the inner comma belongs to the nested generic and must not split the argument list early");
  }

  // A base type whose generic argument the analyzer cannot fully parse (Roslyn recovers a
  // generic name with no closing '>' at all) must still be reported as a projection -- silently
  // dropping it would leave a Marten projection out of the migration plan entirely. Reporting
  // "unknown" for the aggregate type flags the gap instead of guessing wrong.
  [Test]
  public async Task AnalyzeAsync_ReportsUnknownAggregateType_WhenGenericArgumentIsUnparseableAsync() {
    // Arrange -- the base type's generic argument list is never closed before the class body
    // opens, so Roslyn's error recovery leaves the parsed base type text without a '>' at all.
    var analyzer = new MartenAnalyzer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderProjection : SingleStreamProjection<Order {
        public void Apply(OrderCreated @event, Order state) { }
      }

      public class Order { }
      public record OrderCreated(string Id);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Projections/OrderProjection.cs");

    // Assert
    await Assert.That(result.Projections.Count).IsEqualTo(1)
      .Because("the projection is still real and still needs to be migrated, even though its generic argument couldn't be read");
    await Assert.That(result.Projections[0].AggregateType).IsEqualTo("unknown")
      .Because("an unparseable generic argument must be flagged, not silently guessed at");
  }

  // The class-to-namespace lookup falls back through file-scoped, then block-scoped namespace
  // syntax. A projection nested in an old-style brace namespace that resolved to an empty
  // namespace would produce a wrong fully-qualified name, which the migration output and any
  // per-file decision overrides key off of.
  [Test]
  public async Task AnalyzeAsync_CapturesNamespaceFromBlockScopedNamespaceAsync() {
    // Arrange
    var analyzer = new MartenAnalyzer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      namespace MyApp.Legacy {
        public class OrderProjection : SingleStreamProjection<Order> {
          public void Apply(OrderCreated @event, Order state) { }
        }

        public class Order { }
        public record OrderCreated(string Id);
      }
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Projections/OrderProjection.cs");

    // Assert
    await Assert.That(result.Projections.Count).IsEqualTo(1);
    await Assert.That(result.Projections[0].FullyQualifiedName).IsEqualTo("MyApp.Legacy.OrderProjection")
      .Because("a block-scoped namespace must resolve the same as a file-scoped one, not fall through to no namespace at all");
  }
}
