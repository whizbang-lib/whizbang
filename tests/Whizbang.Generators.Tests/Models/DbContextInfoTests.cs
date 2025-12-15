using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Tests.Models;

/// <summary>
/// Tests for the DbContextInfo value record.
/// Ensures proper value equality semantics and immutability.
/// </summary>
public class DbContextInfoTests {

  [Test]
  public async Task DbContextInfo_WithSameValues_AreEqualAsync() {
    // Arrange
    var existing = ImmutableArray.Create("Perspective1", "Perspective2");
    var location = Location.None;

    var info1 = new DbContextInfo(
      ClassName: "MyDbContext",
      FullyQualifiedName: "global::MyApp.Data.MyDbContext",
      Namespace: "MyApp.Data",
      ExistingPerspectives: existing,
      Location: location
    );

    var info2 = new DbContextInfo(
      ClassName: "MyDbContext",
      FullyQualifiedName: "global::MyApp.Data.MyDbContext",
      Namespace: "MyApp.Data",
      ExistingPerspectives: existing,
      Location: location
    );

    // Act & Assert - Value equality should work
    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1.GetHashCode()).IsEqualTo(info2.GetHashCode());
  }

  [Test]
  public async Task DbContextInfo_WithDifferentClassName_AreNotEqualAsync() {
    // Arrange
    var existing = ImmutableArray.Create("Perspective1");
    var location = Location.None;

    var info1 = new DbContextInfo(
      ClassName: "MyDbContext",
      FullyQualifiedName: "global::MyApp.Data.MyDbContext",
      Namespace: "MyApp.Data",
      ExistingPerspectives: existing,
      Location: location
    );

    var info2 = new DbContextInfo(
      ClassName: "OtherDbContext",
      FullyQualifiedName: "global::MyApp.Data.OtherDbContext",
      Namespace: "MyApp.Data",
      ExistingPerspectives: existing,
      Location: location
    );

    // Act & Assert
    await Assert.That(info1).IsNotEqualTo(info2);
  }

  [Test]
  public async Task DbContextInfo_WithDifferentExistingPerspectives_AreNotEqualAsync() {
    // Arrange
    var location = Location.None;

    var info1 = new DbContextInfo(
      ClassName: "MyDbContext",
      FullyQualifiedName: "global::MyApp.Data.MyDbContext",
      Namespace: "MyApp.Data",
      ExistingPerspectives: ImmutableArray.Create("Perspective1"),
      Location: location
    );

    var info2 = new DbContextInfo(
      ClassName: "MyDbContext",
      FullyQualifiedName: "global::MyApp.Data.MyDbContext",
      Namespace: "MyApp.Data",
      ExistingPerspectives: ImmutableArray.Create("Perspective1", "Perspective2"),
      Location: location
    );

    // Act & Assert
    await Assert.That(info1).IsNotEqualTo(info2);
  }

  [Test]
  public async Task DbContextInfo_Properties_AreAccessibleAsync() {
    // Arrange
    var existing = ImmutableArray.Create("Perspective1", "Perspective2");
    var location = Location.None;

    var info = new DbContextInfo(
      ClassName: "MyDbContext",
      FullyQualifiedName: "global::MyApp.Data.MyDbContext",
      Namespace: "MyApp.Data",
      ExistingPerspectives: existing,
      Location: location
    );

    // Act & Assert
    await Assert.That(info.ClassName).IsEqualTo("MyDbContext");
    await Assert.That(info.FullyQualifiedName).IsEqualTo("global::MyApp.Data.MyDbContext");
    await Assert.That(info.Namespace).IsEqualTo("MyApp.Data");
    await Assert.That(info.ExistingPerspectives).HasCount().EqualTo(2);
    await Assert.That(info.Location).IsEqualTo(location);
  }

  [Test]
  public async Task DbContextInfo_WithEmptyExistingPerspectives_WorksCorrectlyAsync() {
    // Arrange
    var location = Location.None;

    var info = new DbContextInfo(
      ClassName: "MyDbContext",
      FullyQualifiedName: "global::MyApp.Data.MyDbContext",
      Namespace: "MyApp.Data",
      ExistingPerspectives: ImmutableArray<string>.Empty,
      Location: location
    );

    // Act & Assert
    await Assert.That(info.ExistingPerspectives).HasCount().EqualTo(0);
  }
}
