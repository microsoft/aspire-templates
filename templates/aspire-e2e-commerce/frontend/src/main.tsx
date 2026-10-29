import { useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { District } from "./scene";
import { customerLabel, shortId, statusLabel, type Detail, type Order, type Snapshot } from "./types";
import "./style.css";

class ApiError extends Error {
  constructor(message: string, readonly status: number) { super(message); }
}
async function api<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init);
  if (!response.ok) {
    const body = await response.json().catch(() => ({ error: `Service returned ${response.status}` }));
    throw new ApiError(body.error ?? `Service returned ${response.status}`, response.status);
  }
  return response.json() as Promise<T>;
}

function App() {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [selected, setSelected] = useState<string | null>(new URLSearchParams(location.search).get("order"));
  const [detail, setDetail] = useState<Detail | null>(null);
  const [online, setOnline] = useState(false);
  const [syncError, setSyncError] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [product, setProduct] = useState("lamp");
  const [quantity, setQuantity] = useState(1);
  const [customer, setCustomer] = useState("harbor-house");
  const [outcome, setOutcome] = useState("approved");
  const [panel, setPanel] = useState(true);
  const [service, setService] = useState("ordering");
  const selectedRef = useRef(selected);
  const clockOffset = useRef(0);
  const checkout = useRef<{ key: string; fingerprint: string } | null>(null);
  const orderForm = useRef<HTMLSelectElement>(null);

  useEffect(() => { selectedRef.current = selected; }, [selected]);
  useEffect(() => {
    let active = true;
    let timer: ReturnType<typeof setTimeout>;
    const controller = new AbortController();
    async function sync() {
      try {
        const next = await api<Snapshot>("/api/snapshot", { signal: controller.signal });
        if (!active) return;
        clockOffset.current = Date.parse(next.serverTime) - Date.now();
        setSnapshot(next); setOnline(true); setSyncError("");
        const id = selectedRef.current ?? next.orders[0]?.id;
        try {
          const nextDetail = id ? await api<Detail>(`/api/orders/${encodeURIComponent(id)}`, { signal: controller.signal }) : null;
          if (!active) return;
          if (id === selectedRef.current || selectedRef.current === null) {
            setDetail(nextDetail);
            if (!selectedRef.current && id) { selectedRef.current = id; setSelected(id); }
          }
        } catch (reason) {
          if (!active) return;
          if (reason instanceof ApiError && [400, 404].includes(reason.status)) {
            if (selectedRef.current === id) {
              selectedRef.current = null; setSelected(null); setDetail(null);
              const url = new URL(location.href); url.searchParams.delete("order"); history.replaceState(null, "", url);
              setError("That order is no longer available. Showing the current district instead.");
            }
          } else {
            setError(reason instanceof Error ? `Order history: ${reason.message}` : "Order history could not be refreshed.");
          }
        }
      } catch (reason) {
        if (!active) return;
        setOnline(false); setSyncError(reason instanceof Error ? reason.message : "Connection interrupted");
      } finally {
        if (active) timer = setTimeout(sync, 1000);
      }
    }
    void sync();
    return () => { active = false; controller.abort(); clearTimeout(timer); };
  }, []);

  function choose(id: string) {
    selectedRef.current = id; setSelected(id); setDetail(null);
    const url = new URL(location.href); url.searchParams.set("order", id); history.replaceState(null, "", url);
  }
  async function submit(event: React.FormEvent) {
    event.preventDefault(); setBusy(true); setError("");
    const body = JSON.stringify({ productId: product, quantity, customerId: customer, mockOutcome: outcome });
    if (checkout.current?.fingerprint !== body) checkout.current = { key: crypto.randomUUID(), fingerprint: body };
    try {
      const order = await api<Order>("/api/orders", {
        method: "POST", headers: { "Content-Type": "application/json", "Idempotency-Key": checkout.current.key }, body
      });
      choose(order.id); checkout.current = null;
    } catch (reason) { setError(reason instanceof Error ? reason.message : "Checkout failed"); }
    finally { setBusy(false); }
  }
  async function retry() {
    if (!detail) return;
    setBusy(true); setError("");
    try {
      const order = await api<Order>(`/api/orders/${detail.order.id}/retry`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ version: detail.order.version, mockOutcome: "approved" })
      });
      setDetail({ ...detail, order });
    } catch (reason) { setError(reason instanceof Error ? reason.message : "Retry failed"); }
    finally { setBusy(false); }
  }
  const orders = snapshot?.orders ?? [];
  const current = detail?.order;
  const activeOrders = orders.filter(o => !["Delivered", "PaymentDeclined"].includes(o.status));
  const total = snapshot?.products.find(p => p.id === product)?.priceCents ?? 0;
  const latest = detail?.events.at(-1);
  return <main>
    <header className="topbar">
      <a className="brand" href="/" aria-label="Commerce district home"><span className="brand-mark">a</span><span>aspire <b>/ commerce district</b></span></a>
      <div className="top-actions"><span className={`connection ${online ? "live" : ""}`}>{online ? "Services connected" : "Reconnecting"}</span>
        {snapshot?.dashboardUrl && <a className="button subtle" href={snapshot.dashboardUrl} target="_blank" rel="noreferrer">Aspire dashboard ↗</a>}
      </div>
    </header>
    <section className="intro">
      <div><p className="eyebrow">DISTRIBUTED SYSTEMS, MADE TANGIBLE</p><h1>Small world. <em>Real orchestration.</em></h1>
        <p>Follow an order from the first click to the front door.</p></div>
      <div className="summary"><div><strong>{activeOrders.length.toString().padStart(2, "0")}</strong><span>in motion</span></div>
        <div><strong>{orders.filter(o => o.status === "Delivered").length.toString().padStart(2, "0")}</strong><span>delivered / latest 100</span></div>
        <button onClick={() => { setPanel(true); setTimeout(() => orderForm.current?.focus(), 0); }}>＋ Create an order</button>
      </div>
    </section>
    {!online && <div role="status" className="notice">Live connection paused. Showing the last confirmed state; reconnecting automatically. {syncError}</div>}
    <div className="workspace">
      <section className="world" aria-label="Interactive commerce district">
        <div className="world-top"><span className="pill">LIVE SIMULATION</span><span>Isometric district / drag to orbit / scroll to zoom</span></div>
        <District orders={orders} selected={selected} offset={clockOffset} online={online} onSelect={choose}
          onService={name => { setService(name); if (name === "ordering") { setPanel(true); orderForm.current?.focus(); } }} />
        <div className="world-bottom"><span><i className="dot" /> Real messages. Durable state. Mock payments.</span><span>Seconds stand in for shipping days.</span></div>
        <div className="service-strip" aria-label="Service inspector">
          {[["ordering", "01", "Storefront"], ["payments", "02", "Payments"], ["fulfilment", "03", "Warehouse"], ["shipping", "04", "Delivery"]].map(([key, n, label]) =>
            <button key={key} className={service === key ? "selected" : ""} onClick={() => setService(key)}><small>{n}</small>{label}<span>↗</span></button>)}
        </div>
      </section>
      <aside className="side">
        {panel && <section className="card checkout">
          <div className="section-title"><div><p className="eyebrow">THE STOREFRONT</p><h2>Make something happen.</h2></div>
            <button className="icon" aria-label="Close order form" onClick={() => setPanel(false)}>×</button></div>
          <form onSubmit={submit}>
            <label htmlFor="product">Product</label><select ref={orderForm} id="product" value={product} onChange={e => setProduct(e.target.value)}>
              {(snapshot?.products ?? [{ id: "lamp", name: "Loading catalogue", stock: 0 }]).map(p => <option key={p.id} value={p.id}>{p.name} · {p.stock} available</option>)}</select>
            <div className="fields"><div><label htmlFor="quantity">Quantity</label><input id="quantity" type="number" min="1" max="5" required value={quantity} onChange={e => setQuantity(e.target.valueAsNumber)} /></div>
              <div><label htmlFor="customer">Deliver to</label><select id="customer" value={customer} onChange={e => setCustomer(e.target.value)}>
                {["harbor-house", "garden-house", "hill-house"].map(id => <option key={id} value={id}>{customerLabel(id)}</option>)}</select></div></div>
            <label htmlFor="outcome">Mock payment scenario</label><select id="outcome" value={outcome} onChange={e => setOutcome(e.target.value)}>
              <option value="approved">Approved · the happy path</option><option value="declined">Declined · release stock & retry</option><option value="retry-once">Gateway timeout · automatic recovery</option></select>
            <div className="checkout-total"><span>Simulated total</span><strong>{new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" }).format(total * (quantity || 0) / 100)}</strong></div>
            <button className="primary full" disabled={busy || !online || !Number.isInteger(quantity)}>{busy ? "Sending order…" : "Place simulated order →"}</button>
            <p className="fine">Synthetic customers only. No card details. No real charges.</p>
          </form>
        </section>}
        {error && <div className="notice error" role="alert">{error}</div>}
        <section className="card inspector"><p className="eyebrow">UNDER THE HOOD / {service}</p><h2>{({ ordering: "A promise, persisted.", payments: "No cards. Real decisions.", fulfilment: "Reserve. Pick. Pack.", shipping: "The last mile." })[service]}</h2>
          <p>{({ ordering: "Checkout reserves stock and writes an order, history and outbox atomically in PostgreSQL.", payments: "A separate worker consumes Azure Queue messages. Declines release stock; timeouts schedule durable retries.", fulfilment: "Versioned messages and transactional state transitions prevent duplicate parcels, even after a worker restarts.", shipping: "The truck moves only after a committed dispatch. Arrival is confirmed by the delivery worker, never by animation." })[service]}</p>
          <div className="tech-tags"><span>.NET services</span><span>PostgreSQL</span><span>Storage Queues</span><span>OpenTelemetry</span></div>
        </section>
      </aside>
    </div>
    <section className="operations">
      <div className="card order-list"><div className="section-title"><div><p className="eyebrow">THE OPERATIONS DESK</p><h2>Orders in the district</h2></div><span className="count">{orders.length} / latest 100</span></div>
        {orders.length === 0 ? <div className="empty"><span>↗</span><h3>Your district is ready.</h3><p>Place an order and watch the services go to work.</p></div> :
          <div className="order-scroll">{orders.map(o => <button key={o.id} className={`order-row ${selected === o.id ? "selected" : ""}`} onClick={() => choose(o.id)}>
            <span className={`order-symbol ${o.status === "PaymentDeclined" ? "declined" : ""}`}>{o.status === "Delivered" ? "✓" : o.status === "PaymentDeclined" ? "!" : "▣"}</span>
            <span><b>#{shortId(o.id)}</b><small>{o.quantity} × {snapshot?.products.find(p => p.id === o.productId)?.name} · {customerLabel(o.customerId)}</small></span>
            <span className={`status ${o.status}`}>{statusLabel(o.status)}</span></button>)}</div>}
      </div>
      <div className="card timeline"><div className="section-title"><div><p className="eyebrow">EVENT INSPECTOR {current ? `/ #${shortId(current.id)}` : ""}</p><h2>{current ? statusLabel(current.status) : "Every handoff has a story."}</h2></div>
        {current?.status === "PaymentDeclined" && <button onClick={retry} disabled={busy || !online}>Retry with approval ↻</button>}</div>
        {!detail ? <p className="muted">{selected ? "Synchronizing durable history…" : "Select an order to inspect service handoffs, messages and trace correlation."}</p> :
          <><ol className="event-list">{detail.events.map(event => <li key={event.sequence}><span className={`event-node ${event.status === "PaymentDeclined" ? "declined" : ""}`} />
            <div><div className="event-heading"><b>{statusLabel(event.status)}</b><time dateTime={event.at}>{new Date(event.at).toLocaleTimeString()}</time></div>
              <p>{event.detail}</p><small>{event.service} · message <code>{shortId(event.messageId)}</code></small>
              <details><summary>Trace correlation</summary><code>trace {event.traceId}<br />span {event.spanId}<br />message {event.messageId}</code></details></div></li>)}</ol>
            <div className="trace-footer"><span>Attempt {current?.attempt} · version {current?.version} · {detail.events.length} committed events</span>
              {latest && <button className="subtle" onClick={() => { void navigator.clipboard.writeText(latest.traceId).catch(() => setError("Clipboard unavailable. Expand Trace correlation to copy the ID.")); }}>Copy trace ID</button>}</div></>}
      </div>
    </section>
    <footer><span>ASPIRE / END-TO-END COMMERCE</span><span>A living system, not a scripted animation. Local emulators → managed Azure services.</span></footer>
  </main>;
}

createRoot(document.getElementById("root")!).render(<App />);
