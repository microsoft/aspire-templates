# Media Processing Studio

A compact end-to-end media accelerator: a React/Vite processing desk, .NET API
and worker, private Azure Blob/Queue Storage, and durable SQL/EF metadata.
Upload a PNG/JPEG, watch Queued -> Processing -> Ready, download the 640px JPEG,
then explore a deliberate three-attempt failure and recover the same job.

## Run locally

Requires .NET 10+, Node.js 22.13+ (or 24+), Python 3 for business assertions,
a supported container runtime, and a current daily Aspire CLI. SQL Server's
Linux image is amd64: Apple Silicon requires Docker's amd64 emulation support
and sufficient memory (recommend 8 GiB allocated to Docker).
SkiaSharp provides native image decoding/encoding. Its macOS assets and explicit
Linux no-dependencies assets cover development and published Linux containers;
validate the actual container architecture before deploying another platform.

From the generated application root:

```bash
aspire restore --apphost MediaStudio.AppHost/MediaStudio.AppHost.csproj --non-interactive
aspire start --apphost MediaStudio.AppHost/MediaStudio.AppHost.csproj --isolated --non-interactive
aspire wait frontend --status healthy --timeout 180 --apphost MediaStudio.AppHost/MediaStudio.AppHost.csproj --non-interactive
aspire describe --apphost MediaStudio.AppHost/MediaStudio.AppHost.csproj --format Json
```

Open the frontend URL reported by `describe`. Connect with the local-only token
`local-media-walkthrough`. Choose **Create a sample rendition**, then **Explore
failure & recovery**. The second example stops at Failed after three attempts;
**Retry processing** clears the intentional fault and generates its rendition.
The sample images are generated in your browser, not downloaded from a service.

Use the API URL from `describe` to exercise real business assertions:

```bash
python3 tests/business_flow.py http://localhost:API_PORT --frontend-url http://localhost:FRONTEND_PORT --save /tmp/media-proof.json
aspire stop --apphost MediaStudio.AppHost/MediaStudio.AppHost.csproj --non-interactive
aspire start --apphost MediaStudio.AppHost/MediaStudio.AppHost.csproj --isolated --non-interactive
aspire wait api --status healthy --timeout 180 --apphost MediaStudio.AppHost/MediaStudio.AppHost.csproj --non-interactive
# Discover the new API URL, then:
python3 tests/business_flow.py http://localhost:NEW_API_PORT --frontend-url http://localhost:NEW_FRONTEND_PORT --verify /tmp/media-proof.json
```

`stop` preserves local SQL/Azurite data volumes. Do not use `stop --force` unless
you intend to remove persistent resources. Ordinary HTTP health only proves
readiness; the script asserts the actual output image dimensions, authorization,
invalid input, duplicate requests, bounded failures, and retry outcome.

## Reliability model

SQL is the source of truth and the durable outbox. The API dispatcher periodically
re-sends queued rows, repairing a crash between SQL commit and queue send.
A conditional SQL update claims a job with a 90-second lease and increments its
attempt count; duplicate messages cannot claim active or completed jobs.
Expired processing leases become queued again. Three attempts is the limit,
including worker crashes. Terminal processing errors are recorded in SQL and
copied to `media-poison`; malformed queue messages are also quarantined.
An expired final lease is recorded as Failed in SQL.

Queue delivery is **at least once**, not exactly once. Renditions use a
deterministic blob name; completing a job is fenced by its lease and generation.
A retry increments the generation, so stale messages cannot claim new work.
Processing must remain bounded below the lease duration; extend the lease or
add renewals before using this pattern for large video workloads.
Originals are written before SQL commit; an abrupt ingest crash can leave an
unreferenced private blob. A production retention/reconciliation job should
remove those according to your organization's retention policy.

The API persists idempotency keys and content hashes. Repeating an upload with
the same key/content returns the same job; different content returns 409.
Use a new UUID per intended upload. Explicit retry is only valid for Failed jobs.
Retry clears this walkthrough's simulated fault; real corrupt/inaccessible inputs
still need an operator fix. The UI shows the current attempt and generation,
not an immutable audit log. Add a history table if audit retention is required.

Pipeline activities carry the original upload trace context through SQL and
queue messages. Structured worker logs include job ID, generation and attempts.
Inspect them in the Aspire dashboard; use the job's processing details to
correlate upload, dispatch, failure and retry.

## Deploy to Azure

The same model provisions managed Storage/SQL and an Azure Container Apps API
plus an event-triggered processing job. The frontend is built and copied into the
API container's `wwwroot`; no Vite development server is deployed.
The job scales on `media-jobs` queue depth and exits when the queue is empty.
The API keeps one replica for durable-outbox dispatch and expired-lease recovery.
Managed identities authenticate Storage and SQL connections; there are no
storage account keys or SQL passwords embedded in application source.

Before deploying, select a subscription, region and **new dedicated resource
group**, approve resource creation and scoped role assignments, and review costs.
Requires Azure CLI login, permission to provision resources and assign roles, a
container build engine, and the daily Aspire CLI's Azure deployment prerequisites.

Configure a unique secret of at least 32 random characters for the `studio-token`
parameter through your deployment environment's secret configuration. Never reuse
the local token or commit secrets. Use `aspire deploy --help` with your CLI version
for environment/subscription/location inputs, then deploy the exact AppHost:

```bash
aspire deploy --apphost MediaStudio.AppHost/MediaStudio.AppHost.csproj --non-interactive
# Use the deployed HTTPS API origin; STUDIO_TOKEN must contain the deployed secret.
python3 tests/business_flow.py https://YOUR_API_ORIGIN --timeout 420
```

Artifact/Bicep generation alone is **not deployment proof**. Confirm resource
provisioning, actual image pushes, successful job executions, and the same
business script against the deployed URL. Do not send secrets to a public
recording, screenshot, command log or source-control history.

The static shell and health endpoint are public. Every `/api` route, including
rendition downloads, requires a bearer token; Blob containers are private.
The UI holds the supplied token only in session storage. This is a shared-operator
walkthrough, **not multitenant identity isolation**. For production, replace the
shared token with Entra/OIDC authentication, per-asset authorization, token
rotation, rate limits and your retention policy. Always use HTTPS when deployed.
Uploads are limited to 5 MiB, 16 megapixels and decoded PNG/JPEG formats; output
metadata is stripped. This template does not implement malware scanning.

Cost drivers include SQL's baseline tier, ACR Basic, the API's minimum replica,
Log Analytics ingestion, stored originals/renditions and worker executions.
Scale-to-zero workers do not make the entire system free. Set budgets and
retention limits. After obtaining separate deletion approval, remove only the
dedicated resource group:

```bash
az group delete --subscription YOUR_APPROVED_SUBSCRIPTION --name YOUR_DEDICATED_RG
```

This deletes SQL metadata, originals and renditions permanently. Never run it
against a shared or unrelated resource group.

## Origin and scope

New application implementation inspired by the resource/deployment model in
[`microsoft/aspire-samples`, `samples/image-gallery` at
`a70601c3e8c1c629abe9dd733cff8edd91eabebf`](https://github.com/microsoft/aspire-samples/tree/a70601c3e8c1c629abe9dd733cff8edd91eabebf/samples/image-gallery).
Repository MIT licensing applies. Per-package Aspire versions are independently
resolved by the generating CLI using the effective daily feeds; SDK identity is
not substituted for integration package versions.
