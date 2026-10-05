# Governance

How the Whizbang project makes decisions, who holds which role, and how the project continues if
someone can no longer take part.

## Model

Whizbang is a **maintainer-led** project. The maintainer makes the final decision on what is accepted,
what is planned and what is released, and does so in public: in issues, discussions and pull requests.
There is no committee or vote. The model will change if more people take on maintainer
responsibilities; any change to it is made by a pull request to this file.

## How decisions are made

| Kind of decision | Where it happens | Who decides |
|---|---|---|
| A bug report or a small change | A GitHub issue or pull request | The maintainer, when reviewing it |
| A feature idea | [Discussions → Ideas](https://github.com/whizbang-lib/whizbang/discussions/categories/ideas); an accepted idea becomes an issue | The maintainer, after discussion |
| An open design question | A GitHub issue labeled [`question`](https://github.com/whizbang-lib/whizbang/issues?q=label%3Aquestion), so the reasoning stays on record | The maintainer, recorded on the issue when settled |
| What ships in a release, and when | The [roadmap](ROADMAP.md) and the release pull request | The maintainer |
| A security vulnerability | Privately, through the process in [SECURITY.md](SECURITY.md) | The maintainer |
| A Code of Conduct report | Privately, by email to [conduct@whizba.ng](mailto:conduct@whizba.ng) ([CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)) | The maintainer |

Every change, including the maintainer's own, goes through a pull request and the same required
automated checks: the full test matrix, formatting, 100% coverage of new lines, zero SonarCloud
findings, CodeQL, and a Developer Certificate of Origin sign-off on every commit.

## Roles

| Role | Responsibilities | Held by |
|---|---|---|
| **Maintainer** | Sets direction and the roadmap; reviews and merges pull requests; triages issues and discussions; keeps the CI, the docs site and the dependencies healthy; administers the GitHub organization, the nuget.org packages and the docs domain. | [@philcarbone](https://github.com/philcarbone) |
| **Release manager** | Cuts and ships releases by the documented flow ([docs/RELEASING.md](docs/RELEASING.md)); makes sure each stable release carries its signed provenance. | The maintainer |
| **Security contact** | Receives private vulnerability reports, responds within 48 hours, coordinates the fix and the advisory, and credits the reporter unless they ask not to be named. | The maintainer |
| **Code of Conduct enforcement** | Reviews reports, decides on action using the Covenant's enforcement guidelines, and keeps reporters' details private. | The maintainer |
| **Contributor** | Anyone who opens an issue, takes part in a discussion or sends a pull request. Follows [CONTRIBUTING.md](CONTRIBUTING.md) and the Code of Conduct, and signs off every commit. | Anyone |

A contributor who consistently does good work may be invited to take on part of the maintainer's
responsibilities. Doing so means adding them to the table above in a pull request.

## Continuity

The project must be able to keep going, and to accept changes and publish releases within a week, if
the maintainer can no longer take part. That requires a second person who holds:

- owner access to the [whizbang-lib](https://github.com/whizbang-lib) GitHub organization (issues,
  pull requests, settings, Actions secrets);
- co-owner access to the project's packages on nuget.org;
- access to the `whizba.ng` domain registration that serves the docs site.

**Backup maintainer:** not yet designated. When one is, they are named here and receive the access
above, so they can act without the maintainer's involvement.
