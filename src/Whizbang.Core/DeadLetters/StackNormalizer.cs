using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Whizbang.Core.DeadLetters;

/// <summary>
/// A dead letter's normalized stack identity: the ordered consumer frames and the
/// 16-hex-char sequence hash that keys the relational stack layer, the inline metric,
/// and the maintenance backfill alike.
/// </summary>
/// <docs>operations/dead-letter-queue/canary-recovery</docs>
public sealed record StackIdentity(IReadOnlyList<string> Frames, string SequenceHash, bool IsProse);

/// <summary>
/// <para>The ONE stack-normalization implementation (P2 of plans/dlq-stack-intelligence.md).
/// It runs in exactly one language on purpose: the inline metric at dead-letter time and
/// the async maintenance backfill must produce the SAME <c>stack_id</c>, and a C#/SQL dual
/// implementation would drift and silently split cohorts — the identity canary campaigns
/// and the new-stack-after-deploy alarm both key on.</para>
/// <para>Rules: async state machinery normalizes (<c>&lt;M&gt;d__N.MoveNext</c> becomes
/// <c>M</c>, because <c>d__N</c> is compiler-assigned and changes on recompile); framework
/// frames (<c>Microsoft.*</c>, <c>System.*</c>, <c>Npgsql.*</c>) are excluded and Whizbang
/// frames serve only as a fallback when no consumer frame survives; ALL surviving frames
/// are kept (no depth cap — that belongs to the legacy 16-char fingerprint, not the
/// relational layer). Errors with no frames at all get a scrubbed first-line template
/// identity, so every dead letter has a stack id.</para>
/// </summary>
/// <docs>operations/dead-letter-queue/canary-recovery</docs>
/// <tests>tests/Whizbang.Core.Tests/DeadLetters/StackNormalizerTests.cs</tests>
public static partial class StackNormalizer {

  [GeneratedRegex(@"^\s+at\s+([^\s(]+)", RegexOptions.CultureInvariant)]
  private static partial Regex _frameLine();

  [GeneratedRegex(@"<([A-Za-z0-9_]+)>d__[0-9]+", RegexOptions.CultureInvariant)]
  private static partial Regex _asyncStateMachine();

  [GeneratedRegex(@"\.MoveNext$", RegexOptions.CultureInvariant)]
  private static partial Regex _moveNext();

  [GeneratedRegex(@"'[^']*'|""[^""]*""", RegexOptions.CultureInvariant)]
  private static partial Regex _quoted();

  [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
  private static partial Regex _guid();

  [GeneratedRegex(@"[0-9a-fA-F]{8,}", RegexOptions.CultureInvariant)]
  private static partial Regex _hexRun();

  [GeneratedRegex(@"[0-9]+", RegexOptions.CultureInvariant)]
  private static partial Regex _digits();

  [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
  private static partial Regex _whitespace();

  /// <summary>
  /// Normalizes an error text into its stack identity, or <c>null</c> when there is no
  /// text to identify — a placeholder hash would create a giant false cohort.
  /// </summary>
  public static StackIdentity? Normalize(string? errorText) {
    if (string.IsNullOrWhiteSpace(errorText)) {
      return null;
    }

    var lines = errorText.Split('\n');
    var frames = new List<string>();
    string? fallback = null;

    for (var i = 1; i < lines.Length; i++) {
      var match = _frameLine().Match(lines[i]);
      if (!match.Success) {
        continue;
      }
      var frame = _asyncStateMachine().Replace(match.Groups[1].Value, "$1");
      frame = _moveNext().Replace(frame, "");
      if (frame.StartsWith("Microsoft.", StringComparison.Ordinal)
          || frame.StartsWith("System.", StringComparison.Ordinal)
          || frame.StartsWith("Npgsql.", StringComparison.Ordinal)) {
        continue;
      }
      if (frame.StartsWith("Whizbang.", StringComparison.Ordinal)) {
        fallback ??= frame;
        continue;
      }
      frames.Add(frame);
    }
    if (frames.Count == 0 && fallback is not null) {
      frames.Add(fallback);
    }

    string combined;
    bool isProse;
    if (frames.Count > 0) {
      // Unit separator as the joiner: frames cannot contain it, so "A","BC" can never
      // collide with "AB","C" the way a bare concatenation would.
      combined = "stack:" + string.Join("\u001f", frames);
      isProse = false;
    } else {
      // Scrub order matters: quoted strings may contain digits; GUIDs before generic hex.
      var template = lines[0];
      template = _quoted().Replace(template, "<q>");
      template = _guid().Replace(template, "<g>");
      template = _hexRun().Replace(template, "<h>");
      template = _digits().Replace(template, "<n>");
      template = _whitespace().Replace(template, " ").Trim();
      combined = "prose:" + (template.Length > 160 ? template[..160] : template);
      isProse = true;
    }

    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(combined)))[..16];
    return new StackIdentity(frames, hash, isProse);
  }
}
