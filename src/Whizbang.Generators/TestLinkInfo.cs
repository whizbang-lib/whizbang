namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered code-to-test link.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="SourceFile">Fully qualified path to the source file being tested (e.g., "src/Whizbang.Core/IDispatcher.cs")</param>
/// <param name="SourceLine">Line number in source file where the code element is defined</param>
/// <param name="SourceSymbol">Symbol name being tested (e.g., "IDispatcher", "Dispatch")</param>
/// <param name="SourceType">Type of source element (Interface, Class, Method, Property, etc.)</param>
/// <param name="TestFile">Fully qualified path to the test file (e.g., "tests/Whizbang.Core.Tests/DispatcherTests.cs")</param>
/// <param name="TestLine">Line number in test file where the test method is defined</param>
/// <param name="TestMethod">Test method name (e.g., "Dispatcher_Send_RoutesToCorrectReceptorAsync")</param>
/// <param name="TestClass">Test class name (e.g., "DispatcherTests")</param>
/// <param name="LinkSource">How this link was discovered (Convention, SemanticAnalysis, or XmlTag)</param>
internal sealed record TestLinkInfo(
  string SourceFile,
  int SourceLine,
  string SourceSymbol,
  string SourceType,
  string TestFile,
  int TestLine,
  string TestMethod,
  string TestClass,
  TestLinkSource LinkSource
);

/// <summary>
/// Indicates how a test link was discovered.
/// </summary>
internal enum TestLinkSource {
  /// <summary>
  /// Link discovered via naming convention (e.g., DispatcherTests tests Dispatcher).
  /// </summary>
  Convention,

  /// <summary>
  /// Link discovered via semantic analysis of test method body.
  /// </summary>
  SemanticAnalysis,

  /// <summary>
  /// Link explicitly specified via &lt;tests&gt; XML tag in source code.
  /// </summary>
  XmlTag
}
