using System;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Helpers for extracting components of a CLR envelope type name like
/// <c>Whizbang.Core.Observability.MessageEnvelope`1[[InnerType, InnerAssembly]], Whizbang.Core</c>.
/// </summary>
public static class EnvelopeTypeNameHelper {
  /// <summary>
  /// Extracts the inner generic type argument from an envelope type name.
  /// </summary>
  /// <example>
  /// <c>"MessageEnvelope`1[[MyApp.Events.Foo, MyApp.Contracts]], Whizbang.Core"</c>
  /// → <c>"MyApp.Events.Foo, MyApp.Contracts"</c>
  /// </example>
  /// <returns>The inner type's assembly-qualified name, or null when the input isn't a
  /// generic envelope name with one type argument in <c>[[...]]</c> brackets.</returns>
  public static string? ExtractInnerTypeName(string envelopeTypeName) {
    if (string.IsNullOrEmpty(envelopeTypeName)) {
      return null;
    }

    var openIdx = envelopeTypeName.IndexOf("[[", StringComparison.Ordinal);
    if (openIdx < 0) {
      return null;
    }

    // Find the matching ]] by depth-tracking — handles nested generics.
    var depth = 0;
    var i = openIdx;
    var closeIdx = -1;
    while (i < envelopeTypeName.Length - 1) {
      if (envelopeTypeName[i] == '[' && envelopeTypeName[i + 1] == '[') {
        depth++;
        i += 2;
      } else if (envelopeTypeName[i] == ']' && envelopeTypeName[i + 1] == ']') {
        depth--;
        if (depth == 0) {
          closeIdx = i;
          break;
        }
        i += 2;
      } else {
        i++;
      }
    }

    if (closeIdx < 0) {
      return null;
    }

    return envelopeTypeName.Substring(openIdx + 2, closeIdx - openIdx - 2).Trim();
  }

  /// <summary>
  /// True when the envelope wraps the internal body-offload claim sentinel
  /// (<c>MessageEnvelope&lt;BodyClaimEnvelopePayload&gt;</c>). No service registers a receptor for the
  /// claim type, so the receive-side "no local consumer" gates MUST NOT drop it — the real message is
  /// rehydrated to its original type downstream by <c>BodyClaimRehydrator</c>. Matching on the inner
  /// type name keeps this driver-agnostic, shared by every transport worker's pre-serialization gate.
  /// </summary>
  public static bool IsBodyClaimEnvelope(string? envelopeTypeName) {
    if (string.IsNullOrEmpty(envelopeTypeName)) {
      return false;
    }
    var inner = ExtractInnerTypeName(envelopeTypeName);
    return inner is not null
      && inner.Contains(nameof(Whizbang.Core.Offloads.BodyClaimEnvelopePayload), StringComparison.Ordinal);
  }
}
