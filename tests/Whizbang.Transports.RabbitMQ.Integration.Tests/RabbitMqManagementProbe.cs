// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Whizbang.Testing.Containers;

namespace Whizbang.Transports.RabbitMQ.Integration.Tests;

/// <summary>
/// Reads broker topology through the management HTTP API of the shared test broker. AMQP cannot list a queue's
/// bindings or read back a queue's arguments, and dead-letter routing is exactly those two things, so a test that
/// asserts WHERE a dead-lettered message can go reads them here. Bindings and arguments live in the broker's metadata
/// store and are written before the AMQP declare or bind returns, so a read after the call is consistent: no polling.
/// </summary>
internal static class RabbitMqManagementProbe {
  /// <summary>A queue's bindings, excluding the implicit default-exchange binding every queue has.</summary>
  internal static async Task<IReadOnlyList<(string Source, string RoutingKey)>> QueueBindingsAsync(
      string queue, CancellationToken cancellationToken) {
    using var client = _client();
    using var document = await _getAsync(client, $"api/queues/%2F/{Uri.EscapeDataString(queue)}/bindings", cancellationToken);
    var bindings = new List<(string, string)>();
    foreach (var binding in document!.RootElement.EnumerateArray()) {
      var source = binding.GetProperty("source").GetString()!;
      if (source.Length > 0) {
        bindings.Add((source, binding.GetProperty("routing_key").GetString()!));
      }
    }
    return bindings;
  }

  /// <summary>A queue's declared arguments, as their raw JSON text; null when the queue does not exist.</summary>
  internal static async Task<IReadOnlyDictionary<string, string>?> QueueArgumentsAsync(
      string queue, CancellationToken cancellationToken) {
    using var client = _client();
    using var document = await _getAsync(client, $"api/queues/%2F/{Uri.EscapeDataString(queue)}", cancellationToken);
    if (document is null) {
      return null;
    }
    var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var argument in document.RootElement.GetProperty("arguments").EnumerateObject()) {
      arguments[argument.Name] = argument.Value.ValueKind == JsonValueKind.String
        ? argument.Value.GetString()!
        : argument.Value.GetRawText();
    }
    return arguments;
  }

  /// <summary>An exchange's type (<c>direct</c>, <c>fanout</c>, <c>topic</c>); null when it does not exist.</summary>
  internal static async Task<string?> ExchangeTypeAsync(string exchange, CancellationToken cancellationToken) {
    using var client = _client();
    using var document = await _getAsync(client, $"api/exchanges/%2F/{Uri.EscapeDataString(exchange)}", cancellationToken);
    return document?.RootElement.GetProperty("type").GetString();
  }

  private static HttpClient _client() {
    var userInfo = Uri.UnescapeDataString(new Uri(SharedRabbitMqContainer.ConnectionString).UserInfo);
    var client = new HttpClient { BaseAddress = SharedRabbitMqContainer.ManagementApiUri };
    client.DefaultRequestHeaders.Authorization =
      new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(userInfo)));
    return client;
  }

  private static async Task<JsonDocument?> _getAsync(HttpClient client, string path, CancellationToken cancellationToken) {
    using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
    if (response.StatusCode == HttpStatusCode.NotFound) {
      return null;
    }
    response.EnsureSuccessStatusCode();
    var body = await response.Content.ReadAsStreamAsync(cancellationToken);
    return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
  }
}
