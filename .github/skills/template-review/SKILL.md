---
name: template-review
description: >-
  Independently review template contributions in microsoft/aspire-templates for source
  layout, Aspire CLI prompts, prerelease package resolution, SDK identity, output
  correctness, and meaningful automated coverage. Use for template PRs and
  pre-merge review, not implementation; template-creation handles authoring.
---

# Review a template contribution

Try to disprove that the contribution produces the intended, runnable app from
this repository. Independently inspect source, generated output, and test evidence;
the author's checklist and a green badge are not substitutes. Use
[template-creation](../template-creation/SKILL.md) as the contribution contract,
not as evidence that its requirements were met.

Do not edit source, regenerate dependency locks, post review comments, or merge
unless asked. Running validation may create local test artifacts. Do not execute
untrusted template code in privileged containers; report that verification boundary
and use an appropriately approved CI run instead.

## Establish scope and evidence

Read the diff, changed template content, README baseline, both host/template
metadata files, and affected checks. Follow a changed symbol through declarations,
host prompts/value sources, replacements, optional branches, and generated files.
Review imports against their recorded upstream commit and path, including licenses,
localization, removed constraints, and converted embedded-template operations.

Use the current repository as the authority:

- `eng/validate-templates.cs`: explicit template inventory and fast generation
  assertions using an isolated template-engine hive and synthetic package versions.
- `eng/discover-e2e.cs`: directory-to-daily-job mapping and exact test filters.
- `tests/Aspire.Templates.EndToEnd.Tests/TemplateTests.cs`: inventory guard and
  actual test bodies, AppHost paths, resource names, and lifecycle commands.
- `tests/Aspire.Templates.EndToEnd.Tests/TemplateTerminal.cs`: external source
  arguments, container boundaries, terminal exit markers, and cleanup.
- `tests/Docker/build-template` and `tests/Docker/validate-output`: actual language
  build and output checks, not just intended behavior.
- `.github/workflows/validate-templates.yml`: checked-out revision, matrix,
  single-test execution guard, permissions, and uploaded evidence.

## Challenge the source and user experience

- Is the template wholly under `templates/<short-name>/`, with exactly matching
  `shortName` and a unique retained `Aspire.TemplatesLab.*` identity? Can the Aspire CLI discover it, or did an
  imported host-only constraint hide it?
- Are names, paths, primary outputs, and `aspire.config.json` AppHost selection
  correct after generation? Does metadata or an upstream source-name token leak?
- Are display labels, defaults, descriptions, aliases, and localization consistent?
  Do internal versions, SDK identity, ports, and restore controls stay out of prompts?
- Does every `symbolInfo` key refer to a real symbol? Are option-specific version
  parameters enabled only for the selected feature, using `isEnabled` on the
  template symbol rather than a made-up host field?
- Do both selected and unselected branches agree across AppHost resources,
  application code, references, configuration, and dependency manifests?
  Check preprocessing syntax in each file type, not only C#.
- Are generated ports valid, distinct where needed, and consistent across profiles?
  Does hostname/name casing match the option and package-manager requirements?
- Does the contribution avoid credentials, contributor-specific feed aliases,
  machine paths, build products, and unwanted generation-time side effects?

## Challenge version and prerelease behavior

For every computed package, verify a required string symbol with a real replacement,
no stale fixed default, and a hidden host `valueSource` containing the correct
`packageId`, `kind: "nugetPackageVersion"`, `versionRange: "*-*"`, and
`allowPrerelease: true`. Check the package reference/configuration receiving the
value; checking the presence of host metadata alone is insufficient.

Reject silent stable-only filtering, hardcoded daily versions, unresolved upstream
build tokens, and assumptions that the CLI, SDK server, and all integrations share
one version. TypeScript AppHosts need the hidden `AspireVersion` injection into
`sdk.version`; package versions must resolve independently from effective sources.
Check explicit non-computed dependency versions against the intended baseline.

Verify `createNuGetConfig: true` and consider a clean consumer without the author's
feeds/caches. TypeScript AppHosts with a daily SDK identity need the corresponding
managed SDK server to restore. The opt-in delegates missing root configuration
creation to CLI channel policy; it is not proof of portable generated feed content
or of a successful restore.

Distinguish synthetic substitution checks from actual package resolution and
restore. This repository permits prereleases but does not promise deterministic latest
versions, compatibility of arbitrary future packages, signatures, or verified
provenance. Do not characterize a pinned import commit or read-only mount as
cryptographic provenance.

## Challenge automated coverage

Require both an entry in the generation validator's `names` inventory and exactly
one correctly normalized E2E method. Inspect its body: the reflection-based
`EveryTemplateHasExactlyOneEndToEndTest` guard proves method names, not that those
methods exercise the right template or do any work.

Check that:

- The new template has relevant default/option assertions. New behavior is tested
  on both sides of its conditions; unsupported options are not disguised with
  dummy symbols, and existing assertions are not weakened to make it pass.
- The default E2E method passes the actual template name, AppHost path, and
  meaningful application resources to `ValidateAsync`. Empty resource lists are
  appropriate only for genuinely empty AppHosts. New language/layout requirements
  are implemented in Docker build/output helpers rather than silently skipped.
- Runtime behavior added behind an option has focused lifecycle coverage where
  needed. The current per-template matrix exercises the default lifecycle;
  generation-only option checks do not prove that variant restores or starts.
- CI discovery emits the expected exact fully qualified filter. The TRX guard
  requires exactly one executed, passing test; zero tests must not be a success.
- CI validates the PR head repository and full SHA, mounts that checkout's
  templates read-only, and calls the explicit external-source path. A test of
  `aspire new <built-in-short-name>` is not evidence for this repository's template.
- Daily-only CI remains appropriate while external template sources are unshipped
  in stable. An older stable CLI failure is not a reason to weaken validation.
- The lifecycle includes generated-output checks, restore, build, isolated startup
  of the exact AppHost, healthy waits for real resources, description, and stop.
  Review terminal failure markers and failure-path diagnostics when those change.
- Isolation still uses an independent Docker daemon without the host socket or
  credentials. Generated app/private build directories stay outside uploaded
  results, and exported diagnostics remain readable by the artifact uploader.
- Workflow action references remain full commit SHAs with readable release
  comments; permissions stay read-only and no repository secrets enter template
  execution.

## Gather verification evidence

Run the fast checks from the repository root when safe:

```bash
dotnet run --file eng/validate-templates.cs --verbosity quiet
dotnet test tests/Aspire.Templates.EndToEnd.Tests --filter TestCategory=Harness
dotnet run --file eng/discover-e2e.cs --verbosity quiet
```

Inspect generated output for the changed branches rather than relying solely on
assertion names. For trusted source and a Docker engine with privileged Linux
container support, rebuild the image and run the affected case:

```bash
docker build --tag aspire-template-validation tests/Docker
# Replace aspire_example with the affected template's normalized method name.
dotnet test tests/Aspire.Templates.EndToEnd.Tests \
  --filter FullyQualifiedName=Aspire.Templates.EndToEnd.Tests.TemplateTests.aspire_example
```

Use `TestCategory=E2E` for cross-template effects and
`TestCategory=TerminalHarness` for harness/helper changes. The fast apps need .NET
10+, not Python; generated Python/TOML is parsed in Docker build/lifecycle checks.
Do not claim these checks ran if Docker, network access, or trust constraints
prevented them.

For CI evidence, verify the run belongs to the current PR head and that the
discovery job and all expected template jobs completed successfully. Inspect
`TestResults/` artifacts as needed: `terminal.cast`, CLI version/logs, diagnostics,
TRX counters, and the recorded template-source revision. Separate an actual
template defect from an infrastructure failure, but report either as unverified
when it prevents the required validation.

## Report a verdict

Lead with **Ready**, **Changes required**, or **Verification blocked**. Present
concrete findings ordered by impact, each with a file/line reference, affected
template/option, observable failure or missing invariant, supporting evidence,
and an actionable correction. Distinguish reproduced failures from source-based
risks and coverage gaps; do not invent defects or fill the report with style nits.

If there are no findings, say so and identify the checks actually observed and
any remaining verification gaps. Do not call a contribution ready solely because
its creator completed the creation skill, metadata parses, or an earlier revision
passed CI. Approval and merge remain explicit user decisions.
