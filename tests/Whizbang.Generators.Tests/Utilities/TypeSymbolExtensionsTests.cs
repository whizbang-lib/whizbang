extern alias shared;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using TypeSymbolExtensions = shared::Whizbang.Generators.Shared.Utilities.TypeSymbolExtensions;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// Unit tests for TypeSymbolExtensions.
/// Tests inheritance-walking utilities for getting properties and methods from type hierarchies.
/// </summary>
public class TypeSymbolExtensionsTests {
  #region GetAllProperties Tests

  [Test]
  public async Task GetAllProperties_SingleClassNoInheritance_ReturnsAllPublicPropertiesAsync() {
    // Arrange
    var source = @"
      public class Order {
        public string Id { get; set; }
        public string Name { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "Order");

    // Act
    var properties = TypeSymbolExtensions.GetAllProperties(typeSymbol).ToList();

    // Assert
    await Assert.That(properties).Count().IsEqualTo(2);
    await Assert.That(properties.Select(p => p.Name)).Contains("Id");
    await Assert.That(properties.Select(p => p.Name)).Contains("Name");
  }

  [Test]
  public async Task GetAllProperties_TwoLevelInheritance_IncludesBaseClassPropertiesAsync() {
    // Arrange
    var source = @"
      public class BaseEvent {
        public string StreamId { get; set; }
      }
      public class OrderCreatedEvent : BaseEvent {
        public string OrderId { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "OrderCreatedEvent");

    // Act
    var properties = TypeSymbolExtensions.GetAllProperties(typeSymbol).ToList();

    // Assert
    await Assert.That(properties).Count().IsEqualTo(2);
    await Assert.That(properties.Select(p => p.Name)).Contains("StreamId");
    await Assert.That(properties.Select(p => p.Name)).Contains("OrderId");
  }

  [Test]
  public async Task GetAllProperties_ThreeLevelInheritance_IncludesAllAncestorPropertiesAsync() {
    // Arrange
    var source = @"
      public class GrandparentEvent {
        public string EventId { get; set; }
      }
      public class ParentEvent : GrandparentEvent {
        public string StreamId { get; set; }
      }
      public class ChildEvent : ParentEvent {
        public string OrderId { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "ChildEvent");

    // Act
    var properties = TypeSymbolExtensions.GetAllProperties(typeSymbol).ToList();

    // Assert
    await Assert.That(properties).Count().IsEqualTo(3);
    await Assert.That(properties.Select(p => p.Name)).Contains("EventId");
    await Assert.That(properties.Select(p => p.Name)).Contains("StreamId");
    await Assert.That(properties.Select(p => p.Name)).Contains("OrderId");
  }

  [Test]
  public async Task GetAllProperties_OverriddenProperty_DerivedTakesPrecedenceAsync() {
    // Arrange
    var source = @"
      public class BaseClass {
        public virtual string Name { get; set; }
      }
      public class DerivedClass : BaseClass {
        public override string Name { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "DerivedClass");

    // Act
    var properties = TypeSymbolExtensions.GetAllProperties(typeSymbol).ToList();

    // Assert - Should only have one Name property (derived class version)
    await Assert.That(properties).Count().IsEqualTo(1);
    await Assert.That(properties.Single().Name).IsEqualTo("Name");
    await Assert.That(properties.Single().ContainingType.Name).IsEqualTo("DerivedClass");
  }

  [Test]
  public async Task GetAllProperties_StaticPropertyExcluded_ExcludesStaticByDefaultAsync() {
    // Arrange
    var source = @"
      public class Order {
        public string Id { get; set; }
        public static string DefaultStatus { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "Order");

    // Act
    var properties = TypeSymbolExtensions.GetAllProperties(typeSymbol).ToList();

    // Assert - Should not include static property
    await Assert.That(properties).Count().IsEqualTo(1);
    await Assert.That(properties.Single().Name).IsEqualTo("Id");
  }

  [Test]
  public async Task GetAllProperties_IncludeStatic_IncludesStaticPropertiesAsync() {
    // Arrange
    var source = @"
      public class Order {
        public string Id { get; set; }
        public static string DefaultStatus { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "Order");

    // Act
    var properties = TypeSymbolExtensions.GetAllProperties(typeSymbol, includeStatic: true).ToList();

    // Assert - Should include both properties
    await Assert.That(properties).Count().IsEqualTo(2);
    await Assert.That(properties.Select(p => p.Name)).Contains("Id");
    await Assert.That(properties.Select(p => p.Name)).Contains("DefaultStatus");
  }

  [Test]
  public async Task GetAllProperties_NonPublicExcluded_ExcludesNonPublicByDefaultAsync() {
    // Arrange
    var source = @"
      public class Order {
        public string Id { get; set; }
        private string InternalId { get; set; }
        protected string ProtectedId { get; set; }
        internal string InternalOnlyId { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "Order");

    // Act
    var properties = TypeSymbolExtensions.GetAllProperties(typeSymbol).ToList();

    // Assert - Should only include public property
    await Assert.That(properties).Count().IsEqualTo(1);
    await Assert.That(properties.Single().Name).IsEqualTo("Id");
  }

  [Test]
  public async Task GetAllProperties_IncludeNonPublic_IncludesAllAccessibilitiesAsync() {
    // Arrange
    var source = @"
      public class Order {
        public string Id { get; set; }
        private string PrivateId { get; set; }
        protected string ProtectedId { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "Order");

    // Act
    var properties = TypeSymbolExtensions.GetAllProperties(typeSymbol, includeNonPublic: true).ToList();

    // Assert - Should include all properties
    await Assert.That(properties).Count().IsEqualTo(3);
    await Assert.That(properties.Select(p => p.Name)).Contains("Id");
    await Assert.That(properties.Select(p => p.Name)).Contains("PrivateId");
    await Assert.That(properties.Select(p => p.Name)).Contains("ProtectedId");
  }

  [Test]
  public async Task GetAllProperties_StopsAtSystemObject_DoesNotIncludeObjectPropertiesAsync() {
    // Arrange
    var source = @"
      public class Order {
        public string Id { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "Order");

    // Act
    var properties = TypeSymbolExtensions.GetAllProperties(typeSymbol, stopAtSystemObject: true).ToList();

    // Assert - Should not include System.Object members (like GetType, etc.)
    await Assert.That(properties).Count().IsEqualTo(1);
    await Assert.That(properties.Single().Name).IsEqualTo("Id");
  }

  #endregion

  #region GetAllPublicPropertyNames Tests

  [Test]
  public async Task GetAllPublicPropertyNames_ReturnsStringArrayOfNamesAsync() {
    // Arrange
    var source = @"
      public class BaseEvent {
        public string StreamId { get; set; }
      }
      public class OrderEvent : BaseEvent {
        public string OrderId { get; set; }
        public string CustomerName { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "OrderEvent");

    // Act
    var propertyNames = TypeSymbolExtensions.GetAllPublicPropertyNames(typeSymbol);

    // Assert
    await Assert.That(propertyNames).Count().IsEqualTo(3);
    await Assert.That(propertyNames).Contains("StreamId");
    await Assert.That(propertyNames).Contains("OrderId");
    await Assert.That(propertyNames).Contains("CustomerName");
  }

  #endregion

  #region FindPropertyWithAttribute Tests

  [Test]
  public async Task FindPropertyWithAttribute_DeclaredProperty_FindsPropertyAsync() {
    // Arrange
    var source = @"
      using System;
      [AttributeUsage(AttributeTargets.Property)]
      public class StreamIdAttribute : Attribute { }

      public class OrderEvent {
        [StreamId]
        public string OrderId { get; set; }
        public string Name { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "OrderEvent");

    // Act
    var property = TypeSymbolExtensions.FindPropertyWithAttribute(typeSymbol, "global::StreamIdAttribute");

    // Assert
    await Assert.That(property).IsNotNull();
    await Assert.That(property!.Name).IsEqualTo("OrderId");
  }

  [Test]
  public async Task FindPropertyWithAttribute_InheritedProperty_FindsPropertyInBaseClassAsync() {
    // Arrange
    var source = @"
      using System;
      [AttributeUsage(AttributeTargets.Property)]
      public class StreamIdAttribute : Attribute { }

      public class BaseEvent {
        [StreamId]
        public string EventId { get; set; }
      }
      public class OrderEvent : BaseEvent {
        public string OrderId { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "OrderEvent");

    // Act
    var property = TypeSymbolExtensions.FindPropertyWithAttribute(typeSymbol, "global::StreamIdAttribute");

    // Assert
    await Assert.That(property).IsNotNull();
    await Assert.That(property!.Name).IsEqualTo("EventId");
  }

  [Test]
  public async Task FindPropertyWithAttribute_NoMatch_ReturnsNullAsync() {
    // Arrange
    var source = @"
      using System;
      [AttributeUsage(AttributeTargets.Property)]
      public class StreamIdAttribute : Attribute { }

      public class OrderEvent {
        public string OrderId { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "OrderEvent");

    // Act
    var property = TypeSymbolExtensions.FindPropertyWithAttribute(typeSymbol, "global::StreamIdAttribute");

    // Assert
    await Assert.That(property).IsNull();
  }

  [Test]
  public async Task FindPropertyWithAttribute_MultipleInheritanceLevels_FindsDeepestMatchAsync() {
    // Arrange
    var source = @"
      using System;
      [AttributeUsage(AttributeTargets.Property)]
      public class StreamIdAttribute : Attribute { }

      public class GrandparentEvent {
        [StreamId]
        public string RootId { get; set; }
      }
      public class ParentEvent : GrandparentEvent {
        public string ParentData { get; set; }
      }
      public class ChildEvent : ParentEvent {
        public string ChildData { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "ChildEvent");

    // Act
    var property = TypeSymbolExtensions.FindPropertyWithAttribute(typeSymbol, "global::StreamIdAttribute");

    // Assert
    await Assert.That(property).IsNotNull();
    await Assert.That(property!.Name).IsEqualTo("RootId");
  }

  #endregion

  #region GetAllMethods Tests

  [Test]
  public async Task GetAllMethods_SingleClass_ReturnsAllPublicMethodsAsync() {
    // Arrange
    var source = @"
      public class OrderHandler {
        public void Process() { }
        public void Handle() { }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "OrderHandler");

    // Act
    var methods = TypeSymbolExtensions.GetAllMethods(typeSymbol).ToList();

    // Assert
    await Assert.That(methods.Select(m => m.Name)).Contains("Process");
    await Assert.That(methods.Select(m => m.Name)).Contains("Handle");
  }

  [Test]
  public async Task GetAllMethods_InheritedMethods_IncludesBaseMethodsAsync() {
    // Arrange
    var source = @"
      public class BaseHandler {
        public void BaseProcess() { }
      }
      public class DerivedHandler : BaseHandler {
        public void DerivedProcess() { }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "DerivedHandler");

    // Act
    var methods = TypeSymbolExtensions.GetAllMethods(typeSymbol).ToList();

    // Assert
    await Assert.That(methods.Select(m => m.Name)).Contains("BaseProcess");
    await Assert.That(methods.Select(m => m.Name)).Contains("DerivedProcess");
  }

  [Test]
  public async Task GetAllMethods_OverriddenMethod_DerivedTakesPrecedenceAsync() {
    // Arrange
    var source = @"
      public class BaseHandler {
        public virtual void Process() { }
      }
      public class DerivedHandler : BaseHandler {
        public override void Process() { }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "DerivedHandler");

    // Act
    var methods = TypeSymbolExtensions.GetAllMethods(typeSymbol).ToList();

    // Assert - Should only have one Process method (derived class version)
    var processMethods = methods.Where(m => m.Name == "Process").ToList();
    await Assert.That(processMethods).Count().IsEqualTo(1);
    await Assert.That(processMethods.Single().ContainingType.Name).IsEqualTo("DerivedHandler");
  }

  [Test]
  public async Task GetAllMethods_MethodOverloads_IncludesAllOverloadsAsync() {
    // Arrange
    var source = @"
      public class Handler {
        public void Apply(string data) { }
        public void Apply(int data) { }
        public void Apply(string data, int count) { }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "Handler");

    // Act
    var methods = TypeSymbolExtensions.GetAllMethods(typeSymbol).ToList();

    // Assert - Should include all three Apply overloads
    var applyMethods = methods.Where(m => m.Name == "Apply").ToList();
    await Assert.That(applyMethods).Count().IsEqualTo(3);
  }

  [Test]
  public async Task GetAllMethods_InheritedOverloads_IncludesBaseOverloadsAsync() {
    // Arrange
    var source = @"
      public class BaseHandler {
        public void Apply(string data) { }
      }
      public class DerivedHandler : BaseHandler {
        public void Apply(int data) { }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "DerivedHandler");

    // Act
    var methods = TypeSymbolExtensions.GetAllMethods(typeSymbol).ToList();

    // Assert - Should include both Apply methods (different signatures)
    var applyMethods = methods.Where(m => m.Name == "Apply").ToList();
    await Assert.That(applyMethods).Count().IsEqualTo(2);
  }

  #endregion

  #region Helper Methods

  private static INamedTypeSymbol _getTypeSymbol(string source, string typeName) {
    var compilation = GeneratorTestHelper.CreateCompilation(source);
    var typeSymbol = compilation.GetTypeByMetadataName(typeName);
    // Try without namespace
    typeSymbol ??= compilation.Assembly.GetTypeByMetadataName(typeName);
    // Search all types
    typeSymbol ??= compilation.GetSymbolsWithName(typeName, SymbolFilter.Type)
        .OfType<INamedTypeSymbol>()
        .FirstOrDefault();
    return typeSymbol ?? throw new InvalidOperationException($"Type '{typeName}' not found in compilation");
  }

  #endregion
}
