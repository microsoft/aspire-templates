---
name: template-creation
description: >-
  Create, import, or update templates in microsoft/aspire-templates. Use for placing
  template sources, configuring template.json and aspirecli.host.json, handling
  prerelease NuGet packages and SDK injection, and adding automated validation.
  Not for creating an application from an existing template or reviewing a
  contribution without editing it; use template-review for independent review.
---

# Contribute a template

Deliver a complete, selectable template with generation and lifecycle coverage.
Prefer the smallest coherent contribution; do not weaken checks to accommodate
incomplete template support. Read the current source rather than treating this
skill as a frozen specification.

## Establish the baseline

Read [README.md](../../../README.md), including its upstream baseline and version
policy. Choose the closest existing template:

- `templates/aspire-starter` for project-based C# AppHosts and optional tests.
- `templates/aspire-apphost-singlefile` for file-based C# AppHosts.
- `templates/aspire-py-starter` or `templates/aspire-ts-starter` for TypeScript
  AppHosts, SDK injection, polyglot conditionals, and generated ports.

Inspect both `.template.config/template.json` and
`.template.config/aspirecli.host.json` in that template, plus its generated
configuration and package references. For imports, record the upstream repository,
full source commit, and source directory in the README baseline; retain licensing,
attribution, and applicable localization. This records origin, not verified
provenance. Signing and provenance verification are not implemented here.

## Place and parameterize the source

Each independently selectable template belongs at:

```text
templates/<short-name>/
  .template.config/
    template.json
    aspirecli.host.json
  ...application source and configuration...
```

Use a unique, descriptive kebab-case directory name. Its `template.json`
`shortName` must match that directory exactly, and its `identity` must be unique;
follow the retained `Aspire.TemplatesLab.*` identity convention (an identifier,
not the repository location). Supply useful
name, description, classifications, and language/type tags.

Keep all source for that template under its directory. Configure name/content
replacement, output paths, primary outputs, and exclusions so generation leaves
no source-name placeholders or `.template.config` directory in the output.
Do not include generated apps, build products, dependency directories, secrets,
or machine-specific configuration. Do not copy upstream host constraints that
prevent discovery by the Aspire CLI.

Make options consistent across every affected file: AppHost resources, application
code, package declarations, configuration, and dependency manifests. Follow the
Python template's `specialCustomOperations` when preprocessing Python/TOML;
upstream embedded-CLI placeholders and conditionals must be converted to template
engine operations. Preserve applicable framework, HTTPS, naming, and hostname
choices. Generate ports and reuse each generated value consistently across
profiles rather than assigning a fixed port.

## Configure Aspire CLI metadata

Use the exact filename **`.template.config/aspirecli.host.json`**.

- Every `symbolInfo` key must name a symbol declared in `template.json`.
- Give user-facing choices readable `displayName` labels, meaningful defaults,
  and descriptions of requirements such as a container runtime.
- Hide internal parameters with `isHidden: true`: computed package versions,
  injected SDK identity, port overrides, framework/restore implementation controls,
  and other parameters not intended as CLI prompts. Follow the nearest template's
  prompt policy rather than exposing every template-engine parameter.
- Put option dependencies in the template symbol's `isEnabled` expression.
  Optional Redis/test-package versions must only be enabled when their option is
  selected. Do not resolve packages or generate references for unused features.
- Keep any `dotnetcli.host.json` option aliases and localization consistent with
  the symbols. The generation validator uses those aliases where present.
- Set root-level `createNuGetConfig: true`, as required by the repository's current
  metadata checks. This lets the CLI create a missing root NuGet configuration
  when its effective channel needs one; it does not mean every invocation creates
  a file. Do not bake contributor-specific feed aliases or credentials into source.

## Handle SDK identity and prerelease packages

Keep SDK identity separate from integration package versions. TypeScript AppHosts
declare a required string parameter named `AspireVersion`, hide it in host
metadata, and replace their `aspire.config.json` `sdk.version` placeholder with
the identity supplied by the running CLI. Do not give it a stale default, resolve
it as an integration package, or assume every package shares that version.

For each computed NuGet package version, declare a required string parameter
with a `replaces` token in `template.json`, without a fixed `defaultValue`.
For example, merge this symbol into `symbols`:

```json
{
  "HostingAppHostVersion": {
    "type": "parameter",
    "datatype": "string",
    "isRequired": true,
    "description": "The version of Aspire.Hosting.AppHost.",
    "replaces": "HostingAppHostPackageVersion"
  }
}
```

Then merge the corresponding entry into the host's `symbolInfo`:

```json
{
  "HostingAppHostVersion": {
    "isHidden": true,
    "valueSource": {
      "kind": "nugetPackageVersion",
      "packageId": "Aspire.Hosting.AppHost",
      "versionRange": "*-*",
      "allowPrerelease": true
    }
  }
}
```

Use the real package ID and wire its token into the relevant `.csproj`,
`#:package` directive, or `aspire.config.json` package entry. Resolve each package
independently against the effective NuGet sources. The current repository policy is
`versionRange: "*-*"` **and** `allowPrerelease: true`; do not silently narrow
discovery to stable packages or pin a particular daily. These permissive ranges
are experimental, not reproducible dependency locks or provenance guarantees.

Keep `createNuGetConfig` enabled for TypeScript AppHosts too: a daily SDK identity
can require a prerelease managed SDK server unavailable on stable feeds alone.
Generation succeeding on a contributor's configured machine is not sufficient.
For non-computed dependencies, use explicit compatible versions; when importing,
resolve upstream packaging tokens from the recorded upstream baseline rather
than leaving `!!REPLACE_WITH_*` values in source.

## Wire automated coverage

Read `eng/validate-templates.cs`, `eng/discover-e2e.cs`,
`tests/Aspire.Templates.EndToEnd.Tests/TemplateTests.cs`, and
`.github/workflows/validate-templates.yml` before changing coverage.

1. Add the short name to the explicit `names` inventory in
   `eng/validate-templates.cs`. Extend default/option assertions and applicable
   framework or test-framework cases. Its generic option check currently assumes
   `LocalhostTld` and an HTTP profile; adapt coverage deliberately for a different
   template shape without inventing dummy options or weakening existing cases.
   Synthetic, distinct prerelease package versions check substitutions without
   network restore; they do not prove live CLI package resolution.
2. Add exactly one `[TestMethod]` to `TemplateTests`. Its method name must be the
   short name with `-` replaced by `_`. Use the shared `ValidateAsync` with the
   actual generated AppHost path under the test project name `LabApp`, and the
   actual application resource names to wait on. For example, adapting all three
   arguments to the new template:

   ```csharp
   [TestMethod]
   public Task aspire_example() =>
       ValidateAsync("aspire-example", "LabApp.AppHost/LabApp.AppHost.csproj", ["api"]);
   ```

   Only genuinely empty AppHosts should have an empty resource list. A correctly
   named method with an empty body is not meaningful coverage.
3. Discovery automatically emits a daily matrix entry per template directory;
   do not add a separate hand-maintained job list. A new directory without
   generation inventory or a matching E2E method must fail the discovery job.
4. If the language, project layout, or runtime is new, extend
   `tests/Docker/build-template`, `validate-output`, and `Dockerfile` as needed
   so restore/build/readiness actually cover it. The shared harness installs the
   CLI, generates from external source, validates output, restores, builds,
   starts the exact AppHost in isolated mode, waits for resources, describes it,
   and stops it. Preserve failure detection, diagnostics, and cleanup.

Run from the repository root with .NET 10+; E2E additionally needs a Docker engine
supporting privileged Linux containers:

```bash
dotnet run --file eng/validate-templates.cs --verbosity quiet
dotnet test tests/Aspire.Templates.EndToEnd.Tests --filter TestCategory=Harness
dotnet run --file eng/discover-e2e.cs --verbosity quiet
docker build --tag aspire-template-validation tests/Docker
# Replace aspire_example with the new template's normalized method name.
dotnet test tests/Aspire.Templates.EndToEnd.Tests \
  --filter FullyQualifiedName=Aspire.Templates.EndToEnd.Tests.TemplateTests.aspire_example
```

The `eng` apps require only .NET, not Python. Python/TOML parsing belongs to the
Docker build/lifecycle checks. Rebuild the image when Docker helpers change and
run `TestCategory=TerminalHarness` for harness/helper changes. Run
`TestCategory=E2E` for cross-template changes. A default lifecycle test does not
prove every optional variant builds; add focused coverage for new runtime behavior.

CI is daily-only until external template sources ship in the stable CLI. Each
matrix job checks out and verifies the PR head repository/SHA outside the
container, then mounts its templates read-only and passes an explicit
`--template-path`. Merely generating a built-in template with the same short name
does not validate the contribution.

Each Hex1b test has its own privileged Docker-in-Docker container. Never mount
the host Docker socket or pass GitHub credentials into it. Privileged containers
are not a security sandbox: do not run untrusted template code locally. Generated
apps stay in `/tmp/template-app`; preserve upload-readable diagnostics, terminal
recordings, and TRX results under `TestResults/`, outside generated app directories.

## Handoff

Update the README template table, source attribution, and any affected usage or
test-count descriptions. Report the changed template, exposed options, package
policy, exact checks run, and any verification gaps. Request an independent
[template review](../template-review/SKILL.md); do not treat successful generation
or the creation checklist as approval, and do not merge without user authorization.
