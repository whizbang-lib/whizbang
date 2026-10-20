# Security Policy

## Supported Versions

Security fixes are made on `develop` and ship in the next release. Only the latest stable release on
[nuget.org](https://www.nuget.org/packages/SoftwareExtravaganza.Whizbang.Core/) is supported; upgrade
to it to receive a fix. Older releases and prereleases are not patched.

## Published Advisories

Advisories for this repository are published as GitHub Security Advisories and are the source of truth
for affected and patched versions: <https://github.com/whizbang-lib/whizbang/security/advisories>.
They also feed the OSV database, so Dependabot and SBOM tooling pick them up automatically.

Maintainers writing one: see `.claude/skills/security-advisory/SKILL.md` for the field set, the
description form, and the wording rules that keep an advisory accurate after the release it describes.

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, please use GitHub Security Advisories:

1. Go to https://github.com/whizbang-lib/whizbang/security/advisories
2. Click "Report a vulnerability"
3. Provide detailed information about the vulnerability

Please include the affected versions, the impact, and the steps or code that reproduce it.

## How a Report Is Handled

| Step | Target |
|---|---|
| Acknowledge the report | Within 48 hours |
| Confirm it and rate its severity (CVSS 4.0) | Within 7 days |
| Fix released, critical | Within 7 days of confirmation |
| Fix released, high | Within 30 days |
| Fix released, medium | Within 90 days |
| Fix released, low | In the next scheduled release |
| Public disclosure | When the fix is released, and no later than 90 days after the report |

- The work happens in a private fork attached to the advisory, so nothing is discussed in public before the fix ships.
- A CVE is requested through the GitHub advisory (GitHub is a CVE Numbering Authority) for every confirmed vulnerability in a released version.
- The advisory names the affected and patched versions, and credits the reporter unless they ask not to be credited.
- We keep you informed at each step. If a target cannot be met, we tell you why and agree on a new date with you before the original one passes.
- A report we decide is not a vulnerability is closed with the reason, and you are free to discuss it publicly.

## Verifying a Release

Every stable release is signed with [Sigstore](https://www.sigstore.dev/) by the release workflow in this
repository. There is no long-lived signing key to obtain or protect: the signature is keyless, bound to
this repository's GitHub Actions identity at build time, and no private key exists on GitHub releases or
nuget.org. Each GitHub release attaches the packages, a `whizbang-X.Y.Z.sigstore.json` bundle (a SLSA
provenance statement naming every package by its SHA-256 digest, with its signature) and the same
provenance on its own as `whizbang-X.Y.Z.intoto.jsonl`.

**A package downloaded from the GitHub release** is verified with the
[GitHub CLI](https://cli.github.com/):

```bash
gh attestation verify SoftwareExtravaganza.Whizbang.Core.X.Y.Z.nupkg --repo whizbang-lib/whizbang

# or offline, against the bundle attached to the release
gh attestation verify SoftwareExtravaganza.Whizbang.Core.X.Y.Z.nupkg \
  --bundle whizbang-X.Y.Z.sigstore.json --repo whizbang-lib/whizbang
```

It succeeds only for a package built by this repository's release workflow and not changed since.

**A package installed from nuget.org** carries nuget.org's repository signature, which nuget.org adds
on upload. That signature file changes the package's digest, so verify it with NuGet instead:

```bash
dotnet nuget verify SoftwareExtravaganza.Whizbang.Core.X.Y.Z.nupkg --all
```

Apart from that signature file, the nuget.org package has exactly the contents of the GitHub release
asset.

**A release tag** (`vX.Y.Z`, and every beta and rc tag) is signed the same keyless way, with
[gitsign](https://github.com/sigstore/gitsign), by the workflow that published the release. Verify it in a
clone of the repository with gitsign installed:

```bash
git fetch --tags origin
gitsign verify-tag vX.Y.Z \
  --certificate-identity-regexp '^https://github\.com/whizbang-lib/whizbang/\.github/workflows/(release|nuget-publish)\.yml@' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

It succeeds only for a tag created by this repository's release workflows and not moved since. GitHub's
own "Verified" badge does not check Sigstore signatures, so the tag shows as unverified on github.com; use
the command above. Tags created before signing was introduced (#1438) are unsigned.

**What a package depends on** is listed in its software bill of materials: each GitHub release attaches
`<package id>.X.Y.Z.cdx.json` beside every package, a [CycloneDX](https://cyclonedx.org/) SBOM naming every
NuGet dependency the package brings (direct and transitive, with version, license and hash) and the other
Whizbang packages it depends on. The SBOMs are subjects of the same signed provenance as the packages, so
one you downloaded is verified the same way:

```bash
gh attestation verify SoftwareExtravaganza.Whizbang.Core.X.Y.Z.cdx.json --repo whizbang-lib/whizbang
```

Releases from before SBOMs were introduced (#1440) do not carry them.

## Security Assurance

What Whizbang protects, its threat model and trust boundaries, and what an application can and cannot
expect from it are described in the [security assurance case](docs/security-assurance-case.md).

## Disclosure Policy

When we receive a security bug report, we will:

1. Confirm the problem and determine affected versions
2. Audit code to find similar problems
3. Prepare fixes for all supported versions
4. Release new security versions as soon as possible
5. Credit the reporter in the published advisory, unless they ask not to be named

## Comments on this Policy

If you have suggestions on how this process could be improved, please submit
a pull request.
