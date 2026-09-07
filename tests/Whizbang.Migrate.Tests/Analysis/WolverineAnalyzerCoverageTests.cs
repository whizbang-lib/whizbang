using Whizbang.Migrate.Analysis;

namespace Whizbang.Migrate.Tests.Analysis;

/// <summary>
/// Coverage-round tests for WolverineAnalyzer branches not exercised by
/// WolverineAnalyzerTests: an IHandle interface written with an empty (unbound) generic
/// argument list, a [WolverineHandler]-attributed class whose Handle method takes no
/// parameters, a base-list entry that is a known Marten interface, and a base-list entry that
/// matches an ignored framework (FastEndpoints) pattern.
/// </summary>
/// <tests>Whizbang.Migrate/Analysis/WolverineAnalyzer.cs:*</tests>
public class WolverineAnalyzerCoverageTests {

  // [WolverineHandler] always counts the class as a handler even when the Handle method itself
  // gives up nothing readable. If a parameterless Handle method crashed or produced a wrong
  // message type instead of falling back to "unknown", the operator would get either a broken
  // report or a receptor generated for the wrong message.
  [Test]
  public async Task AnalyzeAsync_AttributeHandlerWithParameterlessHandleMethod_ReportsUnknownMessageAsync() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    const string sourceCode = """
      using Wolverine.Attributes;

      [WolverineHandler]
      public class OrderHandler {
        public Task Handle() {
          return Task.CompletedTask;
        }
      }
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/OrderHandler.cs");

    // Assert
    await Assert.That(result.Handlers.Count).IsEqualTo(1)
      .Because("the attribute marks it as a handler regardless of whether the signature is readable");
    await Assert.That(result.Handlers[0].MessageType).IsEqualTo("unknown")
      .Because("a Handle method with no parameters names no message, and there is no base "
             + "class to infer one from either");
  }

  // Marten interfaces used as a base-list entry (not just as a parameter) must not trip the
  // custom-base-class warning -- IQuerySession is infrastructure the migration already
  // understands, and flagging it would send the operator to review code that needs none.
  [Test]
  public async Task AnalyzeAsync_HandlerBaseClassIsAKnownMartenType_DoesNotWarnAsync() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    const string sourceCode = """
      using Wolverine;

      public class OrderHandler : IQuerySession, IHandle<PlaceOrder> {
        public Task Handle(PlaceOrder command) => Task.CompletedTask;
      }
      public record PlaceOrder(string Id);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/OrderHandler.cs");

    // Assert
    await Assert.That(result.Warnings.Any(w => w.WarningKind == MigrationWarningKind.CustomHandlerBaseClass))
      .IsFalse()
      .Because("IQuerySession is a known Marten type in the base list, not custom "
             + "infrastructure to flag");
  }

  // FastEndpoints' own base classes are unrelated to Marten/Wolverine and need no manual
  // migration review; flagging them as "custom" would bury the warnings that matter under one
  // for infrastructure the tool already knows to leave alone.
  [Test]
  public async Task AnalyzeAsync_HandlerBaseClassIsAnIgnoredFrameworkPattern_DoesNotWarnAsync() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    const string sourceCode = """
      using Wolverine;

      public class OrderHandler : EndpointBase, IHandle<PlaceOrder> {
        public Task Handle(PlaceOrder command) => Task.CompletedTask;
      }
      public record PlaceOrder(string Id);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/OrderHandler.cs");

    // Assert
    await Assert.That(result.Warnings.Any(w => w.WarningKind == MigrationWarningKind.CustomHandlerBaseClass))
      .IsFalse()
      .Because("EndpointBase is a FastEndpoints base class the tool explicitly ignores, not "
             + "custom infrastructure");
  }

  // An IHandle<> written with an empty (unbound) generic argument list names no message type.
  // If the empty-argument case were treated the same as a real match it would either crash on
  // an out-of-range index or fabricate a message type from nothing; either way the interface
  // path would silently misreport the handler instead of falling through to the weaker
  // convention-based detection that can still see its Handle method.
  [Test]
  public async Task AnalyzeAsync_IHandleWithEmptyGenericArguments_FallsBackToConventionDetectionAsync() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    const string sourceCode = """
      using Wolverine;

      public class OrderHandler : IHandle<> {
        public Task Handle(PlaceOrder command) => Task.CompletedTask;
      }
      public record PlaceOrder(string Id);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/OrderHandler.cs");

    // Assert
    await Assert.That(result.Handlers.Count).IsEqualTo(1)
      .Because("the class still has a public Handle method, so convention-based detection "
             + "finds it even though the unbound IHandle<> interface names no message");
    await Assert.That(result.Handlers[0].HandlerKind).IsEqualTo(HandlerKind.ConventionBased)
      .Because("an IHandle<> with no type argument is not a real interface match, so "
             + "detection must fall through to the convention-based path rather than the "
             + "interface one");
  }
}
