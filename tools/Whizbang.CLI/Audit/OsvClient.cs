// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.CLI.Audit;

/// <summary>
/// An advisory OSV reports for one resolved package version.
/// </summary>
/// <param name="Package">The package version the advisory affects.</param>
/// <param name="Advisory">The full advisory record.</param>
internal sealed record OsvMatch(ResolvedPackage Package, OsvVulnerability Advisory);

/// <summary>
/// Asks the OSV database (https://api.osv.dev) which published advisories affect exactly the
/// package versions a project resolves.
/// </summary>
/// <remarks>
/// <para>
/// OSV is the same published data NuGet Audit, Dependabot and the documentation site's Security
/// Advisories page read. Every request is anonymous: no token and no authorization header. That is
/// deliberate, not a convenience: GitHub's own advisory API returns draft advisories to an
/// authenticated maintainer, and an audit must report only what has been published.
/// </para>
/// <para>
/// The whole check runs under one deadline. A database that does not answer in time is a check that
/// did not run, reported as <see cref="AuditCheckException"/>, never as an empty result.
/// </para>
/// </remarks>
/// <docs>tools/cli-audit#what-it-checks</docs>
internal sealed class OsvClient : IDisposable {
  /// <summary>The OSV API every request goes to.</summary>
  public static readonly Uri BaseAddress = new("https://api.osv.dev/");

  private const string ECOSYSTEM = "NuGet";
  private readonly HttpClient _http;
  private readonly TimeSpan _timeout;
  private readonly TimeProvider _timeProvider;

  /// <summary>
  /// Creates a client.
  /// </summary>
  /// <param name="handler">The transport; tests pass a stub, the command a real socket handler. Disposed with the client.</param>
  /// <param name="userAgent">The User-Agent sent on every request, <c>whizbang-cli/&lt;version&gt;</c>.</param>
  /// <param name="timeout">The deadline for the whole check, every request and page included.</param>
  /// <param name="timeProvider">The clock the deadline runs on.</param>
  public OsvClient(HttpMessageHandler handler, string userAgent, TimeSpan timeout, TimeProvider timeProvider) {
    // The deadline below is the only timeout; HttpClient's own 100 seconds would outlast it.
    _http = new HttpClient(handler) { BaseAddress = BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
    _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    _timeout = timeout;
    _timeProvider = timeProvider;
  }

  /// <summary>
  /// Finds every published advisory that affects one of <paramref name="packages"/>.
  /// </summary>
  /// <param name="packages">The resolved package versions to check.</param>
  /// <param name="cancellationToken">Cancels the check at the caller's request.</param>
  /// <returns>One match per affected package version and advisory, with the full advisory record.</returns>
  /// <exception cref="AuditCheckException">OSV could not be reached, answered with an error or something unreadable, or did not answer within the deadline.</exception>
  /// <exception cref="OperationCanceledException">The caller canceled.</exception>
  public async Task<IReadOnlyList<OsvMatch>> FindAdvisoriesAsync(IReadOnlyList<ResolvedPackage> packages, CancellationToken cancellationToken) {
    using var deadline = new CancellationTokenSource(_timeout, _timeProvider);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
    try {
      var hits = await _queryAllPagesAsync(packages, linked.Token);
      var records = new Dictionary<string, OsvVulnerability>(StringComparer.Ordinal);
      foreach (var id in hits.Select(h => h.AdvisoryId).Distinct(StringComparer.Ordinal)) {
        records[id] = await _fetchRecordAsync(id, linked.Token);
      }

      return [.. hits.Select(h => new OsvMatch(h.Package, records[h.AdvisoryId]))];
    } catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) {
      throw new AuditCheckException(
        $"{BaseAddress.Host} did not answer within {_timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds. "
        + "Nothing was checked; try again, or raise --timeout.", ex);
    } catch (HttpRequestException ex) {
      throw new AuditCheckException($"Could not reach {BaseAddress.Host}: {ex.Message}", ex);
    }
  }

  /// <inheritdoc />
  public void Dispose() => _http.Dispose();

  // Each result pages on its own: a result with a next_page_token is asked again, together with the
  // others still paging, until none is left. Results answer queries by position.
  private async Task<List<(ResolvedPackage Package, string AdvisoryId)>> _queryAllPagesAsync(
      IReadOnlyList<ResolvedPackage> packages, CancellationToken cancellationToken) {
    var hits = new List<(ResolvedPackage, string)>();
    var pending = packages.Select(p => (Package: p, PageToken: (string?)null)).ToList();
    while (pending.Count > 0) {
      var request = new OsvBatchRequest {
        Queries = [.. pending.Select(q => new OsvQuery {
          Package = new OsvPackage { Name = q.Package.Id, Ecosystem = ECOSYSTEM },
          Version = q.Package.Version,
          PageToken = q.PageToken,
        })],
      };
      using var response = await _http.PostAsJsonAsync("v1/querybatch", request, AuditJsonContext.Default.OsvBatchRequest, cancellationToken);
      var answer = await _readAsync(response, AuditJsonContext.Default.OsvBatchResponse, "the batch query", cancellationToken);
      if (answer.Results.Count != pending.Count) {
        throw new AuditCheckException(
          $"{BaseAddress.Host} answered {pending.Count} queries with {answer.Results.Count} result(s), so they cannot be matched up.");
      }

      var next = new List<(ResolvedPackage Package, string? PageToken)>();
      for (var i = 0; i < pending.Count; i++) {
        var package = pending[i].Package;
        var result = answer.Results[i];
        hits.AddRange(result.Vulns.Select(v => (package, v.Id)));
        if (result.NextPageToken is { } token) {
          next.Add((package, token));
        }
      }

      pending = next;
    }

    return hits;
  }

  private async Task<OsvVulnerability> _fetchRecordAsync(string id, CancellationToken cancellationToken) {
    using var response = await _http.GetAsync($"v1/vulns/{Uri.EscapeDataString(id)}", cancellationToken);
    return await _readAsync(response, AuditJsonContext.Default.OsvVulnerability, $"advisory {id}", cancellationToken);
  }

  private static async Task<T> _readAsync<T>(
      HttpResponseMessage response, JsonTypeInfo<T> typeInfo, string what, CancellationToken cancellationToken) where T : class {
    if (!response.IsSuccessStatusCode) {
      throw new AuditCheckException(
        $"{BaseAddress.Host} answered {what} with {(int)response.StatusCode} {response.ReasonPhrase}. Nothing was checked; try again later.");
    }

    T? value;
    try {
      value = await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken);
    } catch (JsonException ex) {
      throw new AuditCheckException($"{BaseAddress.Host} answered {what} with something that is not an OSV record: {ex.Message}", ex);
    }

    return value ?? throw new AuditCheckException($"{BaseAddress.Host} answered {what} with an empty record.");
  }
}
