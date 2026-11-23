// Snippets for GuidInterceptorGenerator
// This file is excluded from compilation and embedded as a resource.

namespace Whizbang.Generators.Templates.Snippets;

internal static class GuidInterceptorSnippets {

  #region INTERCEPTOR_REPLACE
    /// <summary>
    /// Replaces __ORIGINAL_CALL__ at __FILE_PATH__:__LINE__ with a framework UUIDv7 (counter precision).
    /// </summary>
    [global::System.Runtime.CompilerServices.InterceptsLocation(__LOCATION_VERSION__, "__LOCATION_DATA__")]
    internal static global::System.Guid __INTERCEPTOR_NAME__() => global::Whizbang.Core.ValueObjects.TrackedGuid.New();
  #endregion

}
