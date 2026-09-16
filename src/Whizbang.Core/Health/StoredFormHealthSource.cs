using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Health;

/// <summary>
/// The <c>"perspective-stored-forms"</c> managed-resource health source: the streams whose stored
/// document a perspective could not read, while any are remembered.
/// </summary>
/// <remarks>
/// Degraded, never faulted. The rest of the service serves, and the rows are parked with backoff
/// in the database rather than lost. What the operator needs is to see it after the log line
/// scrolled away, with enough detail to know which perspective and where in the document.
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
/// <tests>tests/Whizbang.Core.Tests/Health/StoredFormHealthSourceTests.cs</tests>
public sealed class StoredFormHealthSource : IWhizbangHealthSource {
  private readonly StoredFormFailureRegistry _registry;

  /// <summary>Creates the source over the registry the perspective worker records into.</summary>
  /// <param name="registry">The registry.</param>
  public StoredFormHealthSource(StoredFormFailureRegistry registry) {
    ArgumentNullException.ThrowIfNull(registry);
    _registry = registry;
  }

  /// <inheritdoc />
  public string Component => "perspective-stored-forms";

  /// <inheritdoc />
  public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
    var entries = _registry.Snapshot();
    if (entries.Count == 0) {
      return ValueTask.FromResult(new ComponentHealth(ComponentState.Operational));
    }

    var first = entries[0];
    var detail = $"{entries.Count} stream(s) hold a stored form no reader of this release takes; "
      + $"for example perspective {first.PerspectiveName} stream {first.StreamId} at "
      + $"{first.Path ?? "(path not reported)"}: {first.Detail}. Their events are parked with backoff "
      + "until the stored-form rewrite converts the rows";
    return ValueTask.FromResult(new ComponentHealth(ComponentState.Degraded, detail));
  }
}
