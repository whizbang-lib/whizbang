# CI image mirror

GitHub-hosted runners pull from Docker Hub anonymously, and Docker Hub rate-limits anonymous pulls per
IP. Runners share IPs, so any suite could fail with `toomanyrequests: You have reached your
unauthenticated pull rate limit` (or a 500 from `auth.docker.io`) through no fault of the change under
test. When the Testcontainers reaper (`testcontainers/ryuk`) fails to pull, every Testcontainers fixture
fails with it, even ones whose own image comes from MCR.

CI therefore pulls every Docker Hub image it needs from a copy in GHCR,
`ghcr.io/whizbang-lib/ci-mirror/<docker hub name>:<tag>`. Local runs are unchanged and pull from
Docker Hub.

## The pieces

| Piece | What it does |
|---|---|
| `.github/ci-images.txt` | The manifest: every Docker Hub image CI pulls, as `<name>:<tag>@<index digest>` |
| `.github/workflows/mirror-ci-images.yml` | Copies each manifest image to the mirror by digest (`docker buildx imagetools create`, so every platform and the source digest are kept). Runs on dispatch, weekly, on a push to `develop` that changes the manifest, and on a same-repository pull request that changes it |
| `.github/actions/use-ci-image-mirror` | In each container suite, before the tests: logs in to GHCR and, only when every manifest image is in the mirror at its digest, sets `TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX=ghcr.io/whizbang-lib/ci-mirror/` for the rest of the job. Otherwise it warns and the job pulls from Docker Hub as before |
| `.github/scripts/Test-CiImagePulls.ps1` | The drift guard, after the tests: fails the job when it pulled a Docker Hub image the manifest does not list |
| `src/Whizbang.Testing/Containers/CiImages.cs`, `scripts/lib/CiImages.psm1` | The one prefix rule for images started with a plain `docker run`, in C# and PowerShell |

## How each image reaches the mirror

- **Testcontainers** (any builder, its default image, and the reaper) reads
  `TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX` itself and rewrites every image without a registry host to
  `<prefix>/<image>`. `rabbitmq:3.13-management-alpine` becomes
  `ghcr.io/whizbang-lib/ci-mirror/rabbitmq:3.13-management-alpine`; MCR images are left alone.
- **`docker run` fixtures** (`SharedPostgresContainer`, `SharedRabbitMqContainer`, the restart chaos
  tests, `Run-Tests.ps1`'s shared containers) name their image through `CiImages.Resolve` /
  `Resolve-CiImage`, which applies the same rule from the same variable. Never write a Docker Hub image
  straight into a `docker run`.

GHCR auth: the suites have `packages: read` and log in with `GITHUB_TOKEN`; the mirror job has
`packages: write`. No secrets.

## Adding or changing an image

1. Name it in code. For a `docker run`, add a constant to `CiImages.cs` and resolve it with
   `CiImages.Resolve`; in PowerShell use `Resolve-CiImage -Image '<name>:<tag>'`. Testcontainers images
   need nothing beyond the builder.
2. Add it to `.github/ci-images.txt` with its index digest:
   `docker buildx imagetools inspect <name>:<tag> --format '{{.Manifest.Digest}}'`.
   Use the name the code uses (`rabbitmq`, not `library/rabbitmq`). MCR and other registries are not
   listed.
3. Open the pull request. Changing the manifest runs `Mirror CI images` on it, which copies the image
   while the build runs; the suites then find the mirror complete and use it. If the mirror is not
   complete yet the suites warn and pull from Docker Hub, so the pull request never waits on it.

With the mirror active, a Testcontainers image missing from the manifest is not pulled from Docker Hub
at all: its pull fails with `not found` (or `manifest unknown`) on
`ghcr.io/whizbang-lib/ci-mirror/<image>`. That error means the same thing as the guard's: add the line.

A **Testcontainers upgrade** can move the reaper's tag (4.10.0 uses `testcontainers/ryuk:0.14.0`; see
`ResourceReaper.cs` in the Testcontainers release). Update the `testcontainers/ryuk` line in the same
pull request. Missing it fails the pull check by name.

## The drift guard

`Test-CiImagePulls.ps1` lists the images on the runner after the tests, drops those it held before
(the baseline the mirror step records), and fails on any Docker Hub image, mirrored or not, that the
manifest does not list. Because it checks what was actually pulled, it catches every route: a string
literal, a builder's default image, the reaper, a script. `.github/scripts/tests/CiImages.Tests.ps1`
proves it fails on an unlisted image and checks that the C# constants and `Run-Tests.ps1`'s images are
in the manifest and that every container suite runs the mirror step and the guard.

## When the mirror is missing

The suites never fail because of the mirror: an incomplete or unreadable mirror means a warning
("CI image mirror incomplete") and Docker Hub pulls. If that warning shows on `develop`, dispatch
`Mirror CI images`.
