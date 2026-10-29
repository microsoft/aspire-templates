# Aspire templates

Templates managed by the Aspire team. This repository supplies template content,
CLI host metadata, contribution skills, and automated validation. It does **not**
sign templates or assert their provenance.

The initial content was imported from
[microsoft/aspire-templates-lab at
`91b662728a90abdf163b3ae49f115d468e43c7ba`](https://github.com/microsoft/aspire-templates-lab/tree/91b662728a90abdf163b3ae49f115d468e43c7ba).
The lab remains a separate repository for experimentation; contributions to these
maintained templates belong in `microsoft/aspire-templates`.

## Templates

Each directory under `templates/` is an independently selectable .NET template
engine source:

| Directory / short name | AppHost | Application |
| --- | --- | --- |
| `aspire-starter` | C# | ASP.NET Core API and Blazor frontend |
| `aspire-ts-cs-starter` | C# | ASP.NET Core API and React frontend |
| `aspire-ts-starter` | TypeScript | Express API and React frontend |
| `aspire-py-starter` | TypeScript | Python FastAPI backend and React frontend |
| `aspire-empty` | C# | Empty project-based AppHost and service defaults |
| `aspire-apphost-singlefile` | C# | Empty file-based AppHost |
| `aspire-e2e-commerce` | C# | React/Three.js commerce district, durable .NET business workers, PostgreSQL and Azure queues |

The React/C# template retains its upstream name, `aspire-ts-cs-starter`.
Standalone testing and service-defaults templates are not included. The Blazor
starter retains its optional integration-test project. The empty solution uses
`aspire-empty` as its short name here (upstream uses `aspire`).

## Contributing templates

Two repository-local agent skills guide contributions:

- [Template creation](.github/skills/template-creation/SKILL.md) covers source
  placement, CLI prompts, SDK/package versions, prereleases, and automated testing.
- [Template review](.github/skills/template-review/SKILL.md) independently checks
  generated behavior, package policy, and the evidence behind test coverage.

Use creation to implement a contribution and review to challenge it before merge.
These skills describe the current conventions and should evolve with the harness.

## Try a template

Use an Aspire CLI build with local/Git template sources, Aspire host metadata,
SDK-version injection, and `nugetPackageVersion` value sources:

```bash
aspire new --template-path ./templates/aspire-starter
aspire new --template-path ./templates/aspire-ts-cs-starter
aspire new --template-path ./templates/aspire-ts-starter
aspire new --template-path ./templates/aspire-py-starter
aspire new --template-path ./templates/aspire-empty
aspire new --template-path ./templates/aspire-apphost-singlefile
aspire new --template-path ./templates/aspire-e2e-commerce
```

For unattended generation:

```bash
aspire new --template-path ./templates/aspire-py-starter \
  --name MyApp --output ./generated/MyApp \
  --non-interactive --suppress-agent-init --use-config-source false
```

`--use-config-source false` declines any separately configured initialization
source; it does not disable template sources.

With the global-source commands from
[microsoft/aspire#21329](https://github.com/microsoft/aspire/pull/21329):

```bash
aspire template source add aspire-templates --path "$PWD/templates"
aspire new
```

Choose **Search for more templates**, then search for a template name or description.
A Git source can instead use
`--repository https://github.com/microsoft/aspire-templates --path templates`
when the CLI can authenticate to the repository. This repository is currently
private; local path registration works against an authenticated checkout without
passing GitHub credentials into generated applications or test containers.
Neither registration method establishes trust or verifies provenance.

## CLI metadata and versions

Every template has `.template.config/template.json` and
`.template.config/aspirecli.host.json` (the spelling expected by the CLI).

- `symbolInfo` supplies readable prompt labels and hides internal parameters such
  as ports, framework defaults, restore controls, and computed versions.
- The TypeScript AppHosts declare a hidden `AspireVersion` parameter. The CLI
  injects its SDK identity into the generated `aspire.config.json` `sdk.version`.
- Aspire package parameters use host `valueSource` entries of kind
  `nugetPackageVersion`. Each package resolves independently against the effective
  NuGet sources, using `versionRange: "*-*"` with prereleases allowed, as in the
  template-test experiments. SDK identity and integration versions are deliberately
  separate; a package version need not equal the CLI version.
- Redis and test-package version parameters are enabled only when the corresponding
  option is selected. Version parameters are required rather than carrying stale
  build-time defaults. The Aspire CLI computes them without prompting.
- All templates opt into `createNuGetConfig` so the CLI can create a missing root
  NuGet configuration when its package channel requires one. TypeScript AppHosts
  also need this policy to restore the managed SDK server matching an injected
  daily `sdk.version`; otherwise a clean machine only sees stable NuGet packages.
- Non-Aspire .NET dependencies use the versions from the pinned upstream
  `eng/Versions.props`, replacing the upstream packaging-time tokens.

The TypeScript templates use generated dashboard/telemetry ports, lowercased
project names, and a `.dev.localhost` option. Python Redis conditionals are
implemented using template-engine preprocessing in the AppHost, Python source,
TOML dependencies, and integration configuration.

The permissive package ranges are experimental, **not** a reproducibility or
provenance policy. Future provenance work must define what is authenticated and
how versions, source revisions, and generated content are bound together.

## Upstream baseline

`aspire-e2e-commerce` is original source authored in this repository, following
the maintained C# AppHost/host-metadata conventions. See its
[guide](templates/aspire-e2e-commerce/README.md) for the architecture, simulated
payment/recovery scenarios, business-flow checks, and managed Azure deployment.

Imported from [microsoft/aspire at
`8cc1c3e68fd0173dd403692d9930b7f3adf4db99`](https://github.com/microsoft/aspire/tree/8cc1c3e68fd0173dd403692d9930b7f3adf4db99):

| Local directory | Upstream directory |
| --- | --- |
| `templates/aspire-starter` | `src/Aspire.ProjectTemplates/templates/aspire-starter` |
| `templates/aspire-ts-cs-starter` | `src/Aspire.ProjectTemplates/templates/aspire-ts-cs-starter` |
| `templates/aspire-ts-starter` | `src/Aspire.Cli/Templating/Templates/ts-starter` |
| `templates/aspire-py-starter` | `src/Aspire.Cli/Templating/Templates/py-starter` |
| `templates/aspire-empty` | `src/Aspire.ProjectTemplates/templates/aspire-empty` |
| `templates/aspire-apphost-singlefile` | `src/Aspire.ProjectTemplates/templates/aspire-apphost-singlefile` |

The polyglot templates are embedded CLI assets upstream, not .NET templates.
Here they have template-engine metadata, SDK pinning, independent package-version
symbols, and converted conditionals. C# template content, localization, and optional
features are retained, with the imported `Aspire.TemplatesLab.*` identities and
version substitutions. Those identities, template short names, and the harness's
synthetic `LabApp` project name are deliberately unchanged: they are template/test
identifiers, not repository acquisition locations. Retaining them avoids an
unnecessary template-identity compatibility change.
The single-file template omits the upstream `dotnetcli`-only discovery constraint
so the Aspire CLI can discover it too; running it still requires .NET 10+.
This baseline is an attribution/update aid, not a verified provenance assertion.
Upstream licensing is retained; see [LICENSE](LICENSE).

Host configuration patterns were informed by
[mitchdenny/aspire-template-test-aspire-starter](https://github.com/mitchdenny/aspire-template-test-aspire-starter),
[mitchdenny/aspire-template-test-aspire-ts-cs-starter](https://github.com/mitchdenny/aspire-template-test-aspire-ts-cs-starter),
and [mitchdenny/aspire-template-test-aspire-ts-starter](https://github.com/mitchdenny/aspire-template-test-aspire-ts-starter).

## Validate template generation

Requires a .NET 10+ SDK supporting file-based apps (default template target:
.NET 10; optional target: .NET 11).

```bash
dotnet run --file eng/validate-templates.cs
```

The C# file-based app installs templates into a temporary, isolated template-engine
hive, renders default and optional variants with distinct synthetic package
versions, and checks substitution, prompt metadata, ports, hostnames, selected
dependencies, C# framework/HTTPS options, and test-framework variants. The checks
do not restore dependencies, run applications, contact package feeds, or change
global template registration. Live Aspire CLI generation additionally exercises
SDK injection and package-version resolution. Python/TOML parsing and application
execution are left to the Docker-backed end-to-end checks.
`eng/discover-e2e.cs` emits the daily template-job matrix as JSON:

```bash
dotnet run --file eng/discover-e2e.cs --verbosity quiet
```

## End-to-end validation

The `Validate templates` workflow discovers all seven templates and creates one job
per template using the latest daily CLI. Each job installs
the CLI, checks out the PR's **head repository and full commit SHA**, verifies that
revision, and gives `aspire new` a read-only mount of its template source. It then
validates generated versions, restores and builds the app,
starts its exact AppHost, waits for application resources to become healthy, and
stops it. Empty AppHosts are checked through startup and resource discovery.
The lightweight generation checks above also run before the matrix.

The MSTest harness uses Hex1b Docker-backed terminals, with four cases able to run
in parallel locally. Each case has a fresh privileged Linux container and its own
Docker daemon; the host Docker socket, credentials, and package caches are not
mounted. Using the authenticated checkout outside the container also works for
this private repository without exposing its GitHub token to template code.
The image provides .NET 10, Node.js 22, Python 3.13, uv, and Docker.
**Privileged containers are not a security sandbox.** Run only trusted template
code locally; CI uses ephemeral GitHub-hosted runners, read-only permissions, and
no repository secrets.

Requires a .NET 10+ SDK and a running Docker engine supporting privileged Linux
containers (including Docker Desktop):

```bash
# Build again whenever the Dockerfile or its helper scripts change.
docker build --tag aspire-template-validation tests/Docker

# Fast harness/discovery checks; no Docker container or CLI installation.
dotnet test tests/Aspire.Templates.EndToEnd.Tests --filter TestCategory=Harness

# All seven templates, using the current working tree and latest daily CLI.
dotnet test tests/Aspire.Templates.EndToEnd.Tests --filter TestCategory=E2E

# A single case with the latest daily CLI.
dotnet test tests/Aspire.Templates.EndToEnd.Tests \
  --filter FullyQualifiedName=Aspire.Templates.EndToEnd.Tests.TemplateTests.aspire_empty
```

By default, local tests use a read-only mount of `templates/`, including uncommitted
changes. CI uses the same path-based acquisition against its verified checkout.
To additionally exercise Git-source acquisition from a public repository, set
both `ASPIRE_E2E_TEMPLATE_REPOSITORY` to its HTTPS Git URL and
`ASPIRE_E2E_TEMPLATE_REF` to its full 40-character commit SHA. Private remote Git
sources require authentication, which this harness intentionally does not pass
into containers. `ASPIRE_E2E_IMAGE` can override the prebuilt image tag.

Results are written under `TestResults/<template>-<channel>-<unique-id>/` (override
the root with `ASPIRE_E2E_RESULTS`). Terminal recordings (`terminal.cast`), CLI
version and logs, resource diagnostics, and Docker daemon logs survive failures.
CI uploads these plus TRX results, not generated dependency/build directories.
Generated apps live in the container's temporary filesystem, outside the mounted
results directory, so artifact discovery never traverses private app directories.

CI uses daily builds only because local/Git template sources
(`--template-path` / `--template-repository`) have not shipped in the stable CLI.