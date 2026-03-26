// Snippets for GuidInterceptorGenerator
// This file is excluded from compilation and embedded as a resource.

namespace Whizbang.Generators.Templates.Snippets;

internal static class GuidInterceptorSnippets {

  #region INTERCEPTOR_NEWGUID
  /// <summary>
  /// Intercepts Guid.NewGuid() and wraps result with TrackedGuid.
  /// </summary>
  [global::System.Runtime.CompilerServices.InterceptsLocation("__FILE_PATH__", __LINE__, __COLUMN__)]
  internal static global::Whizbang.Core.ValueObjects.TrackedGuid __INTERCEPTOR_NAME__() {
    return global::Whizbang.Core.ValueObjects.TrackedGuid.FromIntercepted(
        global::System.Guid.NewGuid(),
        global::Whizbang.Core.ValueObjects.GuidMetadatas.__VERSION__ | global::Whizbang.Core.ValueObjects.GuidMetadatas.__SOURCE__);
  }
  #endregion

  #region INTERCEPTOR_CREATEVERSION7
  /// <summary>
  /// Intercepts Guid.CreateVersion7() and wraps result with TrackedGuid.
  /// </summary>
  [global::System.Runtime.CompilerServices.InterceptsLocation("__FILE_PATH__", __LINE__, __COLUMN__)]
  internal static global::Whizbang.Core.ValueObjects.TrackedGuid __INTERCEPTOR_NAME__() {
    return global::Whizbang.Core.ValueObjects.TrackedGuid.FromIntercepted(
        global::System.Guid.CreateVersion7(),
        global::Whizbang.Core.ValueObjects.GuidMetadatas.__VERSION__ | global::Whizbang.Core.ValueObjects.GuidMetadatas.__SOURCE__);
  }
  #endregion

  #region INTERCEPTOR_THIRDPARTY_NEWGUID
  /// <summary>
  /// Intercepts third-party NewGuid() and wraps result with TrackedGuid.
  /// </summary>
  [global::System.Runtime.CompilerServices.InterceptsLocation("__FILE_PATH__", __LINE__, __COLUMN__)]
  internal static global::Whizbang.Core.ValueObjects.TrackedGuid __INTERCEPTOR_NAME__() {
    return global::Whizbang.Core.ValueObjects.TrackedGuid.FromIntercepted(
        __ORIGINAL_CALL__,
        global::Whizbang.Core.ValueObjects.GuidMetadatas.__VERSION__ | global::Whizbang.Core.ValueObjects.GuidMetadatas.__SOURCE__);
  }
  #endregion

}
