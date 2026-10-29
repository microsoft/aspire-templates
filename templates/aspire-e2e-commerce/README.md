# Commerce district

A miniature commerce district with real distributed state: place an order in the
React/Three.js storefront, watch a mock authorization, warehouse picking and
packing, then follow a truck to a synthetic customer's house. Each visible
handoff is backed by a committed business transition, not a scripted success.
Payments are **mocked**, addresses are synthetic, and seconds represent shipping
days. No payment credentials or personal information are accepted.

## Run

Requires .NET 10+, Node.js 22.13+ (or 24+), Docker, and an Aspire CLI supporting
external template sources. Use the same daily channel for the CLI and packages.

```bash
aspire restore --apphost CommerceDemo.AppHost/CommerceDemo.AppHost.csproj --non-interactive
aspire start --apphost CommerceDemo.AppHost/CommerceDemo.AppHost.csproj --isolated --non-interactive
aspire wait storefront --apphost CommerceDemo.AppHost/CommerceDemo.AppHost.csproj --status healthy --timeout 180 --non-interactive
aspire describe storefront --apphost CommerceDemo.AppHost/CommerceDemo.AppHost.csproj --format Json
```

Open the **storefront** URL from `describe` or the Aspire dashboard. The AppHost
builds the frontend with `npm ci`; Node is build-time only, not a deployed service.
The local dashboard uses loopback HTTP with its normal login token. Cloud ingress
is HTTPS. To display a dashboard link in the storefront, configure
`Commerce__DashboardUrl` on the storefront to your reachable dashboard base URL;
never put a dashboard login token in a public URL. Otherwise copy a trace ID from
the event inspector into the separately opened Aspire dashboard.

Stop with `aspire stop --apphost CommerceDemo.AppHost/CommerceDemo.AppHost.csproj
--non-interactive`. PostgreSQL and Azurite use named data volumes: stopping does
not reset stock or orders. Do not use `stop --force` unless deliberately deleting
the exact application's persisted demo data.

## Try the story

- **Approved**: stock reservation → authorization → picking → packed → dispatched
  → delivered. Trucks do not arrive until shipping commits `Delivered`.
- **Declined**: the payment worker releases reserved stock. Select the order and
  choose **Retry with approval** to reserve again and continue.
- **Gateway timeout**: a durable retry is scheduled before authorization succeeds.
- Place several orders, select individual trucks/orders, expand trace correlation,
  reload, or stop/restart a worker through `aspire resource <name>`. Event history
  and work survive restarts; duplicate deliveries do not create duplicate parcels.

The scene supports orbit/zoom, keyboard-operable service labels, pause motion and
reduced-motion preferences. A WebGL failure leaves the order UI and history
available. Polling is non-overlapping, resynchronizes durable state on reconnect,
and visibly reports disconnection. The operations desk shows the latest 100
orders; truck geometry is bounded to 24 models. Old orders remain retrievable by
their `/api/orders/{id}` endpoint. Database history is retained until explicitly
cleaned up; this demonstration does not implement a production retention policy.

## How it works

Four independently instrumented processes run ordering, payments, fulfilment and
shipping. Payments/fulfilment/shipping share the worker executable, configured
with different roles and separate queues. There is intentionally one relational
schema and shared state-machine library rather than pretending these are
independently owned production databases.

Checkout updates stock and atomically records the order, event and outbox row in
PostgreSQL. Relays claim due outbox rows with `FOR UPDATE SKIP LOCKED` and send
Azure Queue messages. A crash between send and commit can duplicate delivery;
workers lock the order and compare its expected version before advancing, so
replays cannot apply the transition twice. State, stock compensation, event
history and the next outbox message commit in one transaction. Timed work is
scheduled in the database, not in process-local timers.

`Idempotency-Key` must be a UUID and is bound to the initial checkout payload.
Reusing it with different details returns 409. Manual payment retries require the
declined order's current version. Quantity is capped at five; inventory cannot
go negative. Eight failed queue deliveries are retried; repeatedly failing
messages move to a poison queue with a critical log requiring operator attention.

OpenTelemetry is **observation, not transport**. W3C parent context is persisted
with each message and restored by consumers, producing one trace across the
four services, database operations and queue boundaries. Event history includes
service, trace/span, message ID and timestamp. Use `aspire otel traces --trace-id
<id>` and `aspire otel logs` against the exact AppHost for real telemetry.

## Automated business checks

```bash
dotnet run --project CommerceDemo.Tests
# After all four services are healthy:
dotnet run --project CommerceDemo.Tests -- --base-url http://localhost:<storefront-port>
```

The test-runner-free executable fails nonzero on any assertion. It checks state-machine
guards, successful delivery, all participating services, trace continuity,
duplicate and mismatched checkout keys, decline/stock compensation, automatic and
manual retry, stale retry protection and durable history reload. Run against a
quiet demo instance: stock assertions assume no concurrent orders for the tested
products. The repository's Docker E2E case runs these same checks on generated
source, replays duplicate broker work before/after commit, and stops/restarts the
shipping worker while work is pending, rather than only testing health endpoints.
For isolated local broker replay, set `COMMERCE_TEST_QUEUES` to that demo's queue
connection string; do not share credentials in logs or source.

The optional browser checks use the running storefront:

```bash
cd frontend
npx playwright install chromium
COMMERCE_URL=http://localhost:<storefront-port> npm run test:browser
```

They place an order through the actual embedded UI, assert picking/dispatch/delivery,
measure truck movement, exercise decline/retry and offline/reconnect, and cover a
missing bookmarked order, mobile layout, reduced motion and WebGL loss.
Screenshots and video are saved under `frontend/test-results/`.

## Azure

The same AppHost models a consumption Azure Container Apps environment,
PostgreSQL Flexible Server (`Standard_B1ms`), and Azure Storage queues.
`RunAsContainer` and `RunAsEmulator` are local-only; publish uses managed Azure
resources. Aspire supplies managed identity connection information and
resource-scoped permissions; the Azure Npgsql integration obtains Entra tokens.
Build static assets on a machine with Node before publishing containers.

Preview without provisioning:

```bash
aspire publish --apphost CommerceDemo.AppHost/CommerceDemo.AppHost.csproj \
  --output-path ./aspire-output --non-interactive
```

Review the generated infrastructure, select an explicitly approved subscription,
resource group and location, then use `aspire deploy` for that exact AppHost.
Actual deployment incurs charges for PostgreSQL, registry, compute, logs and
storage; publishing artifacts alone does not establish deployability. Keep
workers running (minimum one replica) so queued work is processed; verify the
generated scaling configuration before deployment. Destroy only the explicitly
approved demo scope after testing.

**This is an instructional simulator, not a production storefront.** Its public
API has no user authentication or tenant isolation, uses a global in-process
checkout rate limit, and exposes synthetic order history. Restrict network access
for any shared/cloud demo. Add authentication, authorization, retention,
dead-letter operations, per-service data ownership, service-level alerts and
production stock/payment reconciliation before real use.
