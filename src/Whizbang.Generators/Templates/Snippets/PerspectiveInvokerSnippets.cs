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
      var perspectives = _serviceProvider.GetServices<__PERSPECTIVE_INTERFACE__<__EVENT_TYPE__>>();

      async Task PublishToPerspectives(IEvent evt) {
        var typedEvt = (__EVENT_TYPE__)evt;
        foreach (var perspective in perspectives) {
          await perspective.Update(typedEvt);
        }
      }

      return PublishToPerspectives;
    }
    #endregion
  }
}
