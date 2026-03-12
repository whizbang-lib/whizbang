using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for MessageTagDiscoveryGenerator output.
/// Verifies the generated IMessageTagHookDispatcher implementations have correct signatures.
/// </summary>
/// <docs>core-concepts/message-tags#generated-dispatcher</docs>
[Category("Core")]
[Category("Tags")]
[Category("Generators")]
public class MessageTagDiscoveryGeneratorTests {

  /// <summary>
  /// Verifies that the IMessageTagHookDispatcher.TryCreateContext method
  /// accepts IScopeContext? and LifecycleStage parameters.
  /// This is a lock-in test to ensure the generator produces correct signatures.
  /// </summary>
  [Test]
  public async Task TryCreateContext_AcceptsIScopeContextAndLifecycleStage_NotDictionaryAsync() {
    // Arrange - Get the interface method
    var interfaceMethod = typeof(IMessageTagHookDispatcher).GetMethod(
        "TryCreateContext",
        BindingFlags.Public | BindingFlags.Instance);

    // Assert - Method exists
    await Assert.That(interfaceMethod).IsNotNull()
        .Because("IMessageTagHookDispatcher must have TryCreateContext method");

    var parameters = interfaceMethod!.GetParameters();

    // Assert - scope parameter exists and is IScopeContext
    var scopeParameter = parameters.FirstOrDefault(p => p.Name == "scope");
    await Assert.That(scopeParameter).IsNotNull();
    await Assert.That(scopeParameter!.ParameterType).IsEqualTo(typeof(IScopeContext))
        .Because("TryCreateContext scope parameter must be IScopeContext?, not IReadOnlyDictionary<string, object?>?");

    // Assert - stage parameter exists and is LifecycleStage
    var stageParameter = parameters.FirstOrDefault(p => p.Name == "stage");
    await Assert.That(stageParameter).IsNotNull()
        .Because("TryCreateContext must accept a LifecycleStage parameter");
    await Assert.That(stageParameter!.ParameterType).IsEqualTo(typeof(LifecycleStage))
        .Because("TryCreateContext stage parameter must be LifecycleStage");
  }

  /// <summary>
  /// Verifies that TagContext.Scope property is IScopeContext?.
  /// This ensures hooks receive strongly-typed scope context.
  /// </summary>
  [Test]
  public async Task TagContext_Scope_IsIScopeContextAsync() {
    // Arrange - Get the Scope property from TagContext<T>
    var tagContextType = typeof(TagContext<>);
    var scopeProperty = tagContextType.GetProperty("Scope");

    // Assert
    await Assert.That(scopeProperty).IsNotNull();
    await Assert.That(scopeProperty!.PropertyType).IsEqualTo(typeof(IScopeContext))
        .Because("TagContext.Scope must be IScopeContext?, not dictionary");
  }

  /// <summary>
  /// Verifies that TagContext.Stage property exists and is LifecycleStage.
  /// This ensures hooks can inspect the lifecycle stage they are firing at.
  /// </summary>
  [Test]
  public async Task TagContext_Stage_IsLifecycleStageAsync() {
    // Arrange - Get the Stage property from TagContext<T>
    var tagContextType = typeof(TagContext<>);
    var stageProperty = tagContextType.GetProperty("Stage");

    // Assert
    await Assert.That(stageProperty).IsNotNull()
        .Because("TagContext must have a Stage property");
    await Assert.That(stageProperty!.PropertyType).IsEqualTo(typeof(LifecycleStage))
        .Because("TagContext.Stage must be LifecycleStage");
  }

  /// <summary>
  /// Verifies that generated dispatchers implement the correct interface signature.
  /// Finds any generated IMessageTagHookDispatcher in the test assembly and validates.
  /// </summary>
  [Test]
  public async Task GeneratedDispatcher_ImplementsCorrectSignatureAsync() {
    // Arrange - Find any generated dispatcher implementations in loaded assemblies
    var dispatcherInterface = typeof(IMessageTagHookDispatcher);

    var generatedDispatchers = AppDomain.CurrentDomain
        .GetAssemblies()
        .Where(a => !a.IsDynamic)
        .SelectMany(a => {
          try {
            return a.GetTypes();
          } catch {
            return Type.EmptyTypes;
          }
        })
        .Where(t => t.IsClass && !t.IsAbstract)
        .Where(t => dispatcherInterface.IsAssignableFrom(t))
        .Where(t => t.Name.Contains("Generated") || t.Namespace?.Contains("Generated") == true)
        .ToList();

    // If there are generated dispatchers, verify they have correct signature
    foreach (var dispatcherType in generatedDispatchers) {
      var tryCreateMethod = dispatcherType.GetMethod("TryCreateContext");

      await Assert.That(tryCreateMethod).IsNotNull()
          .Because($"{dispatcherType.Name} must implement TryCreateContext");

      var parameters = tryCreateMethod!.GetParameters();

      // Verify scope parameter
      var scopeParam = parameters.FirstOrDefault(p => p.Name == "scope");
      await Assert.That(scopeParam?.ParameterType).IsEqualTo(typeof(IScopeContext))
          .Because($"{dispatcherType.Name}.TryCreateContext scope parameter must be IScopeContext?");

      // Verify stage parameter
      var stageParam = parameters.FirstOrDefault(p => p.Name == "stage");
      await Assert.That(stageParam?.ParameterType).IsEqualTo(typeof(LifecycleStage))
          .Because($"{dispatcherType.Name}.TryCreateContext must accept LifecycleStage parameter");
    }
  }
}
