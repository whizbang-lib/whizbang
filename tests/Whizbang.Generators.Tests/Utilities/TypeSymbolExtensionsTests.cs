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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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
    const string source = @"
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

  #region FindMethodWithAttribute Tests

  private const string ATTRIBUTED_SOURCE = @"
    public class MarkerAttribute : System.Attribute { }

    public class BaseHandler {
      [Marker]
      protected void HandleOnBase() { }
    }

    public class DerivedHandler : BaseHandler {
      [Marker]
      public void HandlePublic() { }

      [Marker]
      private void HandlePrivate() { }

      public void Unattributed() { }
    }

    public class NoMarkers {
      public void Plain() { }
    }
  ";

  [Test]
  public async Task FindMethodWithAttribute_MethodOnTheTypeItself_IsFoundAsync() {
    var typeSymbol = _getTypeSymbol(ATTRIBUTED_SOURCE, "DerivedHandler");

    var method = TypeSymbolExtensions.FindMethodWithAttribute(typeSymbol, "global::MarkerAttribute");

    await Assert.That(method).IsNotNull();
    await Assert.That(method!.GetAttributes()).IsNotEmpty();
  }

  [Test]
  public async Task FindMethodWithAttribute_NoMatch_ReturnsNullAsync() {
    var typeSymbol = _getTypeSymbol(ATTRIBUTED_SOURCE, "NoMarkers");

    var method = TypeSymbolExtensions.FindMethodWithAttribute(typeSymbol, "global::MarkerAttribute");

    await Assert.That(method).IsNull();
  }

  [Test]
  public async Task FindMethodWithAttribute_UnknownAttribute_ReturnsNullAsync() {
    var typeSymbol = _getTypeSymbol(ATTRIBUTED_SOURCE, "DerivedHandler");

    var method = TypeSymbolExtensions.FindMethodWithAttribute(typeSymbol, "global::NotAnAttribute");

    await Assert.That(method).IsNull();
  }

  [Test]
  public async Task FindMethodWithAttribute_PublicOnly_SkipsNonPublicMatchesAsync() {
    var typeSymbol = _getTypeSymbol(ATTRIBUTED_SOURCE, "DerivedHandler");

    var method = TypeSymbolExtensions.FindMethodWithAttribute(
        typeSymbol, "global::MarkerAttribute", includeNonPublic: false);

    await Assert.That(method).IsNotNull();
    await Assert.That(method!.DeclaredAccessibility).IsEqualTo(Accessibility.Public);
  }

  [Test]
  public async Task FindMethodWithAttribute_WalksUpToTheBaseTypeAsync() {
    // BaseHandler carries the only marker reachable from a type that declares none itself.
    const string source = @"
      public class MarkerAttribute : System.Attribute { }

      public class OnlyBaseHasIt {
        [Marker]
        public void FromBase() { }
      }

      public class EmptyDerived : OnlyBaseHasIt { }
    ";
    var typeSymbol = _getTypeSymbol(source, "EmptyDerived");

    var method = TypeSymbolExtensions.FindMethodWithAttribute(typeSymbol, "global::MarkerAttribute");

    await Assert.That(method).IsNotNull();
    await Assert.That(method!.Name).IsEqualTo("FromBase");
  }

  [Test]
  public async Task FindMethodWithAttribute_IgnoresNonOrdinaryMethodsAsync() {
    // Constructors and property accessors are not MethodKind.Ordinary and must not match.
    const string source = @"
      public class MarkerAttribute : System.Attribute { }

      public class CtorOnly {
        [Marker]
        public CtorOnly() { }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "CtorOnly");

    var method = TypeSymbolExtensions.FindMethodWithAttribute(typeSymbol, "global::MarkerAttribute");

    await Assert.That(method).IsNull();
  }

  [Test]
  public async Task FindMethodWithAttribute_PublicOnly_SkipsPastAnEarlierNonPublicMatchAsync() {
    // The non-public match is declared first, so the skip branch has to run before the
    // public one is reached — declaring it second would short-circuit before the skip.
    const string source = @"
      public class MarkerAttribute : System.Attribute { }

      public class PrivateFirst {
        [Marker]
        private void Hidden() { }

        [Marker]
        public void Visible() { }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "PrivateFirst");

    var method = TypeSymbolExtensions.FindMethodWithAttribute(
        typeSymbol, "global::MarkerAttribute", includeNonPublic: false);

    await Assert.That(method).IsNotNull();
    await Assert.That(method!.Name).IsEqualTo("Visible");
  }

  [Test]
  public async Task FindPropertyWithAttribute_PublicOnly_SkipsPastAnEarlierNonPublicMatchAsync() {
    const string source = @"
      public class MarkerAttribute : System.Attribute { }

      public class PrivatePropFirst {
        [Marker]
        private string Hidden { get; set; }

        [Marker]
        public string Visible { get; set; }
      }
    ";
    var typeSymbol = _getTypeSymbol(source, "PrivatePropFirst");

    var property = TypeSymbolExtensions.FindPropertyWithAttribute(
        typeSymbol, "global::MarkerAttribute", includeNonPublic: false);

    await Assert.That(property).IsNotNull();
    await Assert.That(property!.Name).IsEqualTo("Visible");
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
