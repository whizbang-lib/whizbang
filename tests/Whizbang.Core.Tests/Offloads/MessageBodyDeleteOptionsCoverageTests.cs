using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// Targeted coverage for <see cref="MessageBodyDeleteOptions.ProviderHints"/> — the sibling
/// property the broader <see cref="MessageBodyStoreContractTests"/> suite locks defaults for on
/// <see cref="MessageBodyUploadOptions"/> and <see cref="MessageBodyDownloadOptions"/>, but not
/// here.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Offloads/MessageBodyDeleteOptions.cs</code-under-test>
public class MessageBodyDeleteOptionsCoverageTests {

  [Test]
  public async Task ProviderHints_Default_IsNullAsync() {
    // A provider's DeleteAsync must be able to check options?.ProviderHints without a bespoke
    // null-check every call site (the same contract MessageBodyUploadOptions and
    // MessageBodyDownloadOptions already lock) — if this defaulted to a non-null empty dictionary
    // instead, a provider branching on "were hints supplied?" would misread every unhinted delete.
    var options = new MessageBodyDeleteOptions();

    await Assert.That(options.ProviderHints).IsNull();
  }

  [Test]
  public async Task ProviderHints_Explicit_PreservesTheGivenDictionaryAsync() {
    var hints = new Dictionary<string, object?> {
      ["storageClass"] = "cold",
    };

    var options = new MessageBodyDeleteOptions { ProviderHints = hints };

    await Assert.That(options.ProviderHints).IsNotNull();
    await Assert.That(options.ProviderHints!["storageClass"]).IsEqualTo("cold");
  }
}
