// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Build diagnostic messages say what the analyzers and generators actually do (#1428).
/// </summary>
[Category("Diagnostics")]
public class DiagnosticMessageWordingTests {
  /// <summary>Every descriptor declared in the generator assemblies, found by reflection rather than listed.</summary>
  [RequiresUnreferencedCode("Reads the descriptor fields by reflection.")]
  private static List<DiagnosticDescriptor> _allDescriptors() =>
    [.. new[] { typeof(DiagnosticDescriptors).Assembly, typeof(PerspectiveModelDictionaryAnalyzer).Assembly }
      .SelectMany(static a => a.GetTypes())
      // Analyzers declare their own descriptors as private fields; the scan has to see those too, read-only.
#pragma warning disable S3011 // Reading private descriptor fields in a test, never writing them
      .SelectMany(static t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
#pragma warning restore S3011
      .Where(static f => f.FieldType == typeof(DiagnosticDescriptor))
      .Select(static f => (DiagnosticDescriptor)f.GetValue(null)!)];

  private static string _message(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

  /// <summary>No descriptor's title, message or description contains an em-dash (house style is en-US with no em-dashes).</summary>
  [Test]
  [RequiresUnreferencedCode("Reads the descriptor fields by reflection.")]
  public async Task NoDescriptor_ContainsAnEmDashAsync() {
    var descriptors = _allDescriptors();
    var withEmDash = descriptors
      .Where(static d => (d.Title.ToString(CultureInfo.InvariantCulture) + d.MessageFormat.ToString(CultureInfo.InvariantCulture)
        + d.Description.ToString(CultureInfo.InvariantCulture)).Contains('—'))
      .Select(static d => d.Id).Distinct().Order(StringComparer.Ordinal).ToList();

    await Assert.That(descriptors.Count).IsGreaterThan(100).Because("the scan must find the descriptors it checks");
    await Assert.That(withEmDash).IsEmpty();
  }

  /// <summary>WHIZ009 names the event and fills every placeholder: only events are checked, and one argument is passed.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Whiz009_NamesTheEvent_AndLeavesNoPlaceholderAsync() {
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>("""
      using Whizbang.Core;
      namespace Shop;
      public record OrderPlaced(string Name) : IEvent;
      """);

    var message = _message(result.Diagnostics.Single(d => d.Id == "WHIZ009"));
    await Assert.That(message).StartsWith("Event 'OrderPlaced' has no property or parameter marked with [StreamId]");
    await Assert.That(message).DoesNotContain("{");
  }

  /// <summary>WHIZ009's description says events, not commands, because commands are not checked.</summary>
  [Test]
  public async Task Whiz009_Description_SaysEventsAsync() {
    var description = DiagnosticDescriptors.MissingStreamIdAttribute.Description.ToString(CultureInfo.InvariantCulture);

    await Assert.That(description).DoesNotContain("ICommand");
    await Assert.That(description).Contains("IEvent");
  }

  /// <summary>WHIZ011 does not double the word "type" for a nested type.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Whiz011_ANestedType_IsNotCalledATypeTypeAsync() {
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>("""
      using Whizbang.Core;
      namespace Shop;
      public record Address(string Street);
      public record OrderPlaced(Address ShipTo) : IEvent;
      """);

    var messages = result.Diagnostics.Where(d => d.Id == "WHIZ011").Select(_message).ToList();
    await Assert.That(messages).Contains(static m => m.Contains("'Address'", StringComparison.Ordinal));
    await Assert.That(messages).DoesNotContain(static m => m.Contains("type type", StringComparison.Ordinal));
  }

  /// <summary>WHIZ027 names the interfaces the generator matches, not one that does not exist.</summary>
  [Test]
  public async Task Whiz027_Description_NamesTheMatchedInterfacesAsync() {
    var description = DiagnosticDescriptors.PerspectiveRunnerGenerated.Description.ToString(CultureInfo.InvariantCulture);

    await Assert.That(description).DoesNotContain("IPerspectiveModel");
    await Assert.That(description).Contains("IPerspectiveFor");
  }

  /// <summary>WHIZ040 says transient, the lifetime the services are registered with.</summary>
  [Test]
  public async Task Whiz040_SaysTransientAsync() {
    var format = DiagnosticDescriptors.UserServiceDiscovered.MessageFormat.ToString(CultureInfo.InvariantCulture);

    await Assert.That(format).Contains("transient");
    await Assert.That(format).DoesNotContain("scoped");
  }

  /// <summary>WHIZ063 names the whole path from the root type to the nested type, however deep.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Whiz063_ADeeplyNestedProperty_IsNamedByItsFullPathAsync() {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>("""
      using Whizbang.Core;
      namespace Shop;
      public class Geo { public object? Raw { get; set; } }
      public class Address { public Geo? Location { get; set; } }
      public record OrderPlaced : IEvent { public Address? ShipTo { get; init; } }
      """);

    var message = _message(diagnostics.Single(d => d.Id == "WHIZ063"));
    await Assert.That(message).Contains("'OrderPlaced.ShipTo.Location'");
    await Assert.That(message).DoesNotContain("'OrderPlaced.Location'");
  }

  /// <summary>WHIZ100 names the return type the method actually has, ValueTask included.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Whiz100_NamesTheActualReturnTypeAsync() {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>("""
      using System.Threading.Tasks;
      using Whizbang.Core.Perspectives;
      namespace Shop;
      public record Order;
      public record OrderPlaced;
      public class OrderPerspective : IPerspectiveFor<Order, OrderPlaced> {
        public ValueTask<Order> Apply(Order current, OrderPlaced @event) => ValueTask.FromResult(current);
      }
      """);

    var message = _message(diagnostics.Single(d => d.Id == "WHIZ100"));
    await Assert.That(message).Contains("ValueTask<Order>");
    await Assert.That(message).DoesNotContain("'Task<");
  }

  /// <summary>WHIZ810 suggests what its description and tested fix use: a list of a key/value class.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Whiz810_SuggestsAKeyValueClassAsync() {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelDictionaryAnalyzer>("""
      using System.Collections.Generic;
      using Whizbang.Core.Perspectives;
      namespace Shop;
      public record Order { public Dictionary<string, int> Counts { get; init; } = []; }
      public record OrderPlaced;
      public class OrderPerspective : IPerspectiveFor<Order, OrderPlaced> {
        public Order Apply(Order current, OrderPlaced @event) => current;
      }
      """);

    var message = _message(diagnostics.Single(d => d.Id == "WHIZ810"));
    await Assert.That(message).DoesNotContain("KeyValuePair");
    await Assert.That(message).Contains("Key");
    await Assert.That(message).Contains("Value");
  }
}
