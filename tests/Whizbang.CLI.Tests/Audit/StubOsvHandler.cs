// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Whizbang.CLI.Tests.Audit;

/// <summary>
/// Stands in for api.osv.dev. Answers each request from <see cref="Respond"/> and records what was
/// sent, so tests can assert on the URL, headers and body of every request the client made.
/// </summary>
internal sealed class StubOsvHandler : HttpMessageHandler {
  public sealed record SentRequest(HttpMethod Method, Uri Uri, HttpRequestHeaders Headers, string? Body);

  public List<SentRequest> Requests { get; } = [];

  /// <summary>Answers a request. The default answers every batch query with no advisories.</summary>
  public Func<SentRequest, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
    (request, _) => Task.FromResult(Json(EmptyBatchFor(request.Body!)));

  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
    var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
    var sent = new SentRequest(request.Method, request.RequestUri!, request.Headers, body);
    Requests.Add(sent);
    return await Respond(sent, cancellationToken);
  }

  public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
    new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

  /// <summary>A batch answer with an empty result for each query in <paramref name="requestBody"/>.</summary>
  public static string EmptyBatchFor(string requestBody) {
    var count = System.Text.Json.JsonDocument.Parse(requestBody).RootElement.GetProperty("queries").GetArrayLength();
    return $"{{\"results\":[{string.Join(",", Enumerable.Repeat("{}", count))}]}}";
  }

  /// <summary>
  /// An advisory record in OSV's shape (as GitHub-reviewed records appear there), affecting
  /// <paramref name="affectedPackage"/> and fixed in <paramref name="fixedVersions"/>.
  /// </summary>
  public static string Advisory(
      string id,
      string affectedPackage,
      string? severity = "HIGH",
      string? cve = null,
      string[]? fixedVersions = null,
      string extraAffected = "") {
    var aliases = cve is null ? "[]" : $"[\"{cve}\"]";
    var databaseSpecific = severity is null ? "" : $"\"database_specific\":{{\"severity\":\"{severity}\",\"github_reviewed\":true}},";
    var ranges = string.Join(",", (fixedVersions ?? ["9.9.9"]).Select(f =>
      $"{{\"type\":\"ECOSYSTEM\",\"events\":[{{\"introduced\":\"0\"}},{{\"fixed\":\"{f}\"}}]}}"));
    return $$"""
      {
        "id": "{{id}}",
        "summary": "Summary of {{id}}",
        "aliases": {{aliases}},
        "modified": "2026-10-01T00:00:00Z",
        {{databaseSpecific}}
        "affected": [
          {{extraAffected}}
          {
            "package": { "name": "{{affectedPackage}}", "ecosystem": "NuGet", "purl": "pkg:nuget/{{affectedPackage}}" },
            "ranges": [ {{ranges}} ]
          }
        ],
        "schema_version": "1.9.0"
      }
      """;
  }
}
