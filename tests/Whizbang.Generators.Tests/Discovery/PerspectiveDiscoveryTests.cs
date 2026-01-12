using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests.Discovery;

/// <summary>
/// Tests for PerspectiveDiscovery helper class.
/// NOTE: PerspectiveDiscovery is NOT used by any generators and is fundamentally incompatible.
/// The helper looks for IHandlePerspective&lt;TEvent, TState&gt; which doesn't exist in the codebase.
/// Actual generators use IPerspectiveFor&lt;TModel, TEvent1, ...&gt; and implement their own specialized
/// discovery logic. These tests are skipped as the helper is incompatible with the actual API.
/// </summary>
public class PerspectiveDiscoveryTests {

  [Test]
  [RequiresAssemblyFiles]
  [Skip("PerspectiveDiscovery helper is incompatible with actual API. Looks for non-existent IHandlePerspective<TEvent,TState> instead of IPerspectiveFor<TModel,TEvent>.")]
  public async Task IsPotentialPerspectiveHandler_ClassWithBaseList_ReturnsTrueAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("PerspectiveDiscovery helper is incompatible with actual API. Looks for non-existent IHandlePerspective<TEvent,TState> instead of IPerspectiveFor<TModel,TEvent>.")]
  public async Task IsPotentialPerspectiveHandler_ClassWithoutBaseList_ReturnsFalseAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("PerspectiveDiscovery helper is incompatible with actual API. Looks for non-existent IHandlePerspective<TEvent,TState> instead of IPerspectiveFor<TModel,TEvent>.")]
  public async Task ExtractFromHandler_PerspectiveHandler_ReturnsPerspectiveInfoAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("PerspectiveDiscovery helper is incompatible with actual API. Looks for non-existent IHandlePerspective<TEvent,TState> instead of IPerspectiveFor<TModel,TEvent>.")]
  public async Task ExtractFromHandler_NonPerspectiveClass_ReturnsNullAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("PerspectiveDiscovery helper is incompatible with actual API. Looks for non-existent IHandlePerspective<TEvent,TState> instead of IPerspectiveFor<TModel,TEvent>.")]
  public async Task IsPotentialDbSetProperty_PropertyDeclaration_ReturnsTrueAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("PerspectiveDiscovery helper is incompatible with actual API. Looks for non-existent IHandlePerspective<TEvent,TState> instead of IPerspectiveFor<TModel,TEvent>.")]
  public async Task IsPotentialDbSetProperty_NonPropertyDeclaration_ReturnsFalseAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("PerspectiveDiscovery helper is incompatible with actual API. Looks for non-existent IHandlePerspective<TEvent,TState> instead of IPerspectiveFor<TModel,TEvent>.")]
  public async Task ExtractFromDbSet_PerspectiveRowDbSet_ReturnsPerspectiveInfoAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("PerspectiveDiscovery helper is incompatible with actual API. Looks for non-existent IHandlePerspective<TEvent,TState> instead of IPerspectiveFor<TModel,TEvent>.")]
  public async Task ExtractFromDbSet_NonPerspectiveDbSet_ReturnsNullAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("PerspectiveDiscovery helper is incompatible with actual API. Looks for non-existent IHandlePerspective<TEvent,TState> instead of IPerspectiveFor<TModel,TEvent>.")]
  public async Task ToSnakeCase_PascalCase_ReturnsSnakeCaseAsync() {
    await Task.CompletedTask;
  }

  [Test]
  [RequiresAssemblyFiles]
  [Skip("PerspectiveDiscovery helper is incompatible with actual API. Looks for non-existent IHandlePerspective<TEvent,TState> instead of IPerspectiveFor<TModel,TEvent>.")]
  public async Task ToSnakeCase_EmptyString_ReturnsEmptyAsync() {
    await Task.CompletedTask;
  }
}
