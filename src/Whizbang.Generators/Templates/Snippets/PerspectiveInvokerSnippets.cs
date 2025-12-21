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
      // ✅ IMPLEMENTED: PerspectiveRunner architecture for pure function perspectives
      //
      // The PerspectiveRunner architecture is now complete:
      //   - PerspectiveRunnerGenerator generates IPerspectiveRunner per perspective
      //   - Each runner implements unit-of-work pattern with pure Apply() methods
      //   - IPerspectiveRunnerRegistry provides zero-reflection lookup (AOT-compatible)
      //   - PerspectiveWorker calls runners via registry for event replay
      //
      // See:
      //   - src/Whizbang.Generators/PerspectiveRunnerGenerator.cs
      //   - src/Whizbang.Generators/PerspectiveRunnerRegistryGenerator.cs
      //   - src/Whizbang.Core/Workers/PerspectiveWorker.cs
      //   - docs/pure-function-perspectives.md
      //
      // This snippet (PERSPECTIVE_ROUTING_SNIPPET) is no longer used by the runner
      // architecture. It remains for backward compatibility with older invoker pattern.

      async Task PublishToPerspectives(IEvent evt) {
        // Invoker pattern replaced by runner architecture (see PerspectiveWorker.cs)
        await Task.CompletedTask;
      }

      return PublishToPerspectives;
    }
    #endregion
  }
}
