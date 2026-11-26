// Snippets for GuidOrderingSuppressionRegistryGenerator
// This file is excluded from compilation and embedded as a resource.

namespace Whizbang.Generators.Templates.Snippets;

internal static class GuidOrderingSuppressionSnippets {

  internal static void Registrations() {
    #region REGISTRATION
    global::Whizbang.Core.Validation.GuidOrderingSuppressions.Register("__TYPE_NAME__");
    #endregion
  }

}
