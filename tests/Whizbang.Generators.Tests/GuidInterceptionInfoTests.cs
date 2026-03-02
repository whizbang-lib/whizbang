namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <see cref="GuidInterceptionInfo"/> record.
/// </summary>
public class GuidInterceptionInfoTests {
  [Test]
  public async Task GuidInterceptionInfo_Constructor_SetsPropertiesAsync() {
    // Arrange & Act
    var info = new GuidInterceptionInfo(
      FilePath: "src/MyApp/Services/OrderService.cs",
      LineNumber: 42,
      ColumnNumber: 15,
      OriginalMethod: "NewGuid",
      FullyQualifiedTypeName: "global::System.Guid",
      GuidVersion: "Version4",
      GuidSource: "SourceMicrosoft",
      InterceptorMethodName: "Intercept_OrderService_NewGuid_42_15"
    );

    // Assert
    await Assert.That(info.FilePath).IsEqualTo("src/MyApp/Services/OrderService.cs");
    await Assert.That(info.LineNumber).IsEqualTo(42);
    await Assert.That(info.ColumnNumber).IsEqualTo(15);
    await Assert.That(info.OriginalMethod).IsEqualTo("NewGuid");
    await Assert.That(info.FullyQualifiedTypeName).IsEqualTo("global::System.Guid");
    await Assert.That(info.GuidVersion).IsEqualTo("Version4");
    await Assert.That(info.GuidSource).IsEqualTo("SourceMicrosoft");
    await Assert.That(info.InterceptorMethodName).IsEqualTo("Intercept_OrderService_NewGuid_42_15");
  }

  [Test]
  public async Task GuidInterceptionInfo_ValueEquality_ComparesFieldsAsync() {
    // Arrange
    var info1 = new GuidInterceptionInfo(
      "path/file.cs", 10, 5, "NewGuid", "global::System.Guid", "Version4", "SourceMicrosoft", "Method1"
    );
    var info2 = new GuidInterceptionInfo(
      "path/file.cs", 10, 5, "NewGuid", "global::System.Guid", "Version4", "SourceMicrosoft", "Method1"
    );
    var info3 = new GuidInterceptionInfo(
      "path/file.cs", 10, 5, "CreateVersion7", "global::System.Guid", "Version7", "SourceMarten", "Method2"
    );

    // Assert
    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1).IsNotEqualTo(info3);
    await Assert.That(info1.GetHashCode()).IsEqualTo(info2.GetHashCode());
  }

  [Test]
  public async Task GuidInterceptionInfo_ValueEquality_DifferentLineNumber_NotEqualAsync() {
    // Arrange
    var info1 = new GuidInterceptionInfo(
      "path/file.cs", 10, 5, "NewGuid", "global::System.Guid", "Version4", "SourceMicrosoft", "Method1"
    );
    var info2 = new GuidInterceptionInfo(
      "path/file.cs", 20, 5, "NewGuid", "global::System.Guid", "Version4", "SourceMicrosoft", "Method1"
    );

    // Assert
    await Assert.That(info1).IsNotEqualTo(info2);
  }

  [Test]
  public async Task GuidInterceptionInfo_ValueEquality_DifferentColumn_NotEqualAsync() {
    // Arrange
    var info1 = new GuidInterceptionInfo(
      "path/file.cs", 10, 5, "NewGuid", "global::System.Guid", "Version4", "SourceMicrosoft", "Method1"
    );
    var info2 = new GuidInterceptionInfo(
      "path/file.cs", 10, 15, "NewGuid", "global::System.Guid", "Version4", "SourceMicrosoft", "Method1"
    );

    // Assert
    await Assert.That(info1).IsNotEqualTo(info2);
  }

  [Test]
  public async Task GuidInterceptionInfo_Deconstruction_WorksCorrectlyAsync() {
    // Arrange
    var info = new GuidInterceptionInfo(
      "src/file.cs", 100, 20, "CreateVersion7", "global::System.Guid", "Version7", "SourceMarten", "Intercept_Test"
    );

    // Act
    var (filePath, lineNumber, columnNumber, originalMethod, fullyQualifiedTypeName, guidVersion, guidSource, interceptorMethodName) = info;

    // Assert
    await Assert.That(filePath).IsEqualTo("src/file.cs");
    await Assert.That(lineNumber).IsEqualTo(100);
    await Assert.That(columnNumber).IsEqualTo(20);
    await Assert.That(originalMethod).IsEqualTo("CreateVersion7");
    await Assert.That(fullyQualifiedTypeName).IsEqualTo("global::System.Guid");
    await Assert.That(guidVersion).IsEqualTo("Version7");
    await Assert.That(guidSource).IsEqualTo("SourceMarten");
    await Assert.That(interceptorMethodName).IsEqualTo("Intercept_Test");
  }

  [Test]
  public async Task GuidInterceptionInfo_Version7_PropertiesSetCorrectlyAsync() {
    // Arrange & Act
    var info = new GuidInterceptionInfo(
      FilePath: "src/Services/IdGenerator.cs",
      LineNumber: 25,
      ColumnNumber: 10,
      OriginalMethod: "CreateVersion7",
      FullyQualifiedTypeName: "global::System.Guid",
      GuidVersion: "Version7",
      GuidSource: "SourceMarten",
      InterceptorMethodName: "Intercept_IdGenerator_CreateVersion7_25_10"
    );

    // Assert
    await Assert.That(info.OriginalMethod).IsEqualTo("CreateVersion7");
    await Assert.That(info.GuidVersion).IsEqualTo("Version7");
    await Assert.That(info.GuidSource).IsEqualTo("SourceMarten");
  }

  [Test]
  public async Task GuidInterceptionInfo_HashCode_ConsistentForEqualObjectsAsync() {
    // Arrange
    var info1 = new GuidInterceptionInfo(
      "path/file.cs", 10, 5, "NewGuid", "global::System.Guid", "Version4", "SourceMicrosoft", "Method"
    );
    var info2 = new GuidInterceptionInfo(
      "path/file.cs", 10, 5, "NewGuid", "global::System.Guid", "Version4", "SourceMicrosoft", "Method"
    );

    // Act
    var hash1 = info1.GetHashCode();
    var hash2 = info2.GetHashCode();

    // Assert
    await Assert.That(hash1).IsEqualTo(hash2);
  }
}
