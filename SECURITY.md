# Security Policy

## Supported Versions

Security fixes are made on `develop` and ship in the next release. Only the latest stable release on
[nuget.org](https://www.nuget.org/packages/SoftwareExtravaganza.Whizbang.Core/) is supported; upgrade
to it to receive a fix. Older releases and prereleases are not patched.

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, please use GitHub Security Advisories:

1. Go to https://github.com/whizbang-lib/whizbang/security/advisories
2. Click "Report a vulnerability"
3. Provide detailed information about the vulnerability

**Expected response time:** Within 48 hours

We will send you a response indicating the next steps in handling your report.
After the initial reply, we will keep you informed of the progress towards a
fix and full announcement.

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
