using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Coverage for two <see cref="TransportNamespaceConnectionStrings"/> branches the AzureServiceBus
/// registration suite doesn't reach: a namespace entry with a blank connection value skipped
/// during <c>Read</c>, and <c>MergeAndValidate</c> actually merging a non-empty configured map over
/// the code map. A half-written override must not mint a nameless/valueless namespace, and a
/// configured namespace that never gets merged in would mean an operator's configuration override
/// silently does nothing.
/// </summary>
public class TransportNamespaceConnectionStringsCoverageTests {

  /// <summary>What breaks: a half-written configuration override (key present, value blank) must
  /// not mint an entry with no connection — a namespace with a blank value can never open a
  /// client, so skipping it here is what keeps <c>Read</c>'s contract honest.</summary>
  [Test]
  public async Task Read_BlankValueEntry_IsSkippedButOtherEntriesStillResolveAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Transports:Test:Namespaces:blank"] = "",
        ["Whizbang:Transports:Test:Namespaces:default"] = "literal-connection",
      })
      .Build();

    var map = TransportNamespaceConnectionStrings.Read(configuration, "Whizbang:Transports:Test");

    await Assert.That(map.ContainsKey("blank")).IsFalse()
      .Because("a blank connection value is a half-written override, not a real namespace — minting it would produce a namespace no client can ever open");
    await Assert.That(map["default"]).IsEqualTo("literal-connection")
      .Because("a sibling entry with a real value must still resolve even when another entry in the same section is blank");
  }

  /// <summary>What breaks: if the configured map never actually merged over the code map, an
  /// operator's <c>Whizbang__Transports__...__Namespaces__*</c> override — the entire point of
  /// this seam — would silently do nothing, and only the code-supplied defaults would ever apply.</summary>
  [Test]
  public async Task MergeAndValidate_ConfiguredEntriesOverrideAndExtendTheCodeMapAsync() {
    var code = new Dictionary<string, string> {
      [TransportNamespaces.DefaultKey] = "code-default-connection",
    };
    var configured = new Dictionary<string, string> {
      ["bulk"] = "configured-bulk-connection",
    };

    var merged = TransportNamespaceConnectionStrings.MergeAndValidate(code, configured, "namespaceConnectionStrings");

    await Assert.That(merged.Count).IsEqualTo(2)
      .Because("configuration must be able to ADD a traffic-class namespace beyond whatever the code registered");
    await Assert.That(merged["bulk"]).IsEqualTo("configured-bulk-connection")
      .Because("a configured namespace entry must actually reach the merged map — this is the entire point of the configuration override seam");
    await Assert.That(merged[TransportNamespaces.DefaultKey]).IsEqualTo("code-default-connection")
      .Because("an untouched code entry must survive a merge that only adds a different key");
  }
}
