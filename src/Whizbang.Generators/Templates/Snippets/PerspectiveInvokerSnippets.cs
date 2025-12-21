using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Generators.Templates.Placeholders;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Code snippets for perspective invoker generation.
/// These snippets are extracted by the generator and used to generate routing code.
/// </summary>
internal static class PerspectiveInvokerSnippets {

  // Example method showing snippet structure (for IDE support only)
  private static async Task ExampleRoutingMethod(IServiceProvider serviceProvider, IEvent @event, System.Type eventType) {
    #region PERSPECTIVE_ROUTING_SNIPPET
    if (eventType == typeof(__EVENT_TYPE__)) {
      // TODO: Implement PerspectiveRunner architecture for pure function perspectives
      // The current IPerspectiveInvoker was designed for orchestration (IPerspectiveOf) pattern.
      // Pure functions (IPerspectiveFor<TModel, TEvent>) need a different approach:
      //   1. Resolve IPerspectiveStore<TModel> from DI
      //   2. Load current model: var current = await store.GetByIdAsync(streamId)
      //   3. Resolve perspective: var perspective = sp.GetService<IPerspectiveFor<TModel, TEvent>>()
      //   4. Apply event: var updated = perspective.Apply(current, typedEvt)
      //   5. Save model: await store.UpsertAsync(streamId, updated)
      //
      // This requires knowing both TModel and TEvent at compile time, which needs a
      // generated runner per perspective, not a single invoker for all perspectives.
      //
      // For now, perspectives are discovered and registered in DI, but not invoked.
      // See docs/pure-function-perspectives.md section "The Runner is Generated"

      async Task PublishToPerspectives(IEvent evt) {
        // Temporarily disabled - needs runner architecture
        await Task.CompletedTask;
      }

      return PublishToPerspectives;
    }
    #endregion
  }
}
