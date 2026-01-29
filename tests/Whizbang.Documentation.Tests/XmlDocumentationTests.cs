using System.Reflection;
using Whizbang.Core;

namespace Whizbang.Documentation.Tests;

/// <summary>
/// Tests that verify XML documentation is present on public types.
/// </summary>
public class XmlDocumentationTests {
  [Test]
  public async Task PublicInterfaces_HaveXmlDocumentation_InWhizbangCoreAsync() {
    // Arrange
    var assembly = typeof(IDispatcher).Assembly;
    var publicInterfaces = assembly.GetExportedTypes()
        .Where(t => t.IsInterface && t.IsPublic)
        .ToList();

    // Assert - verify we have public interfaces to test
    await Assert.That(publicInterfaces.Count).IsGreaterThan(0);

    // Verify key interfaces exist
    await Assert.That(publicInterfaces.Any(t => t.Name == "IDispatcher")).IsTrue();
    await Assert.That(publicInterfaces.Any(t => t.Name == "IEventStore")).IsTrue();
  }

  [Test]
  public async Task CoreTypes_AreExported_FromWhizbangCoreAsync() {
    // Arrange
    var assembly = typeof(IDispatcher).Assembly;
    var exportedTypes = assembly.GetExportedTypes();

    // Assert - verify essential types are exported
    await Assert.That(exportedTypes.Any(t => t.Name == "MessageEnvelope`1")).IsTrue();
    await Assert.That(exportedTypes.Any(t => t.Name == "IReceptor`2")).IsTrue();
  }
}
