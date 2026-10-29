import React, { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import './style.css';

type Job = {
  id: string; name: string; state: 'Queued' | 'Processing' | 'Ready' | 'Failed';
  attempts: number; generation: number; error: string | null; created: string; traceParent: string | null;
};

function Studio() {
  const [token, setToken] = useState(sessionStorage.getItem('studio-token') ?? '');
  const [input, setInput] = useState(token);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [simulate, setSimulate] = useState(false);

  async function request(path: string, init?: RequestInit) {
    const response = await fetch(`/api${path}`, {
      ...init, headers: { ...init?.headers, Authorization: `Bearer ${token}` }
    });
    if (!response.ok) {
      if (response.status === 401) throw new Error('Access denied. Enter the studio access token.');
      const body = await response.text();
      throw new Error(`${response.status}: ${body}`);
    }
    return response;
  }
  async function refresh() {
    try { setJobs(await (await request('/jobs')).json()); setError(''); }
    catch (e) { setError(String(e)); }
  }
  useEffect(() => {
    if (!token) return;
    void refresh();
    const interval = setInterval(() => void refresh(), 2000);
    return () => clearInterval(interval);
  }, [token]);

  async function upload(file: File, failure: boolean) {
    setBusy(true); setError('');
    try {
      const form = new FormData();
      form.append('image', file);
      form.append('simulateFailure', String(failure));
      await request('/jobs', { method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID() }, body: form });
      await refresh();
    } catch (e) { setError(String(e)); }
    finally { setBusy(false); }
  }
  async function walkthrough(failure: boolean) {
    const canvas = document.createElement('canvas');
    canvas.width = 1200; canvas.height = 800;
    const context = canvas.getContext('2d')!;
    const css = getComputedStyle(document.documentElement);
    context.fillStyle = css.getPropertyValue('--cp-bg').trim();
    context.fillRect(0, 0, 1200, 800);
    context.fillStyle = css.getPropertyValue('--cp-accent').trim();
    context.fillRect(100, 100, 1000, 600);
    context.fillStyle = css.getPropertyValue('--cp-accent-fg').trim();
    context.font = 'bold 72px Segoe UI';
    context.fillText(failure ? 'Recovery is a feature.' : 'Make something useful.', 140, 400);
    const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/png'));
    if (blob) await upload(new File([blob], failure ? 'recovery-walkthrough.png' : 'first-rendition.png', { type: 'image/png' }), failure);
  }
  return <main>
    <header><span className="brand">M / STUDIO</span><span className="eyebrow">ASPIRE END-TO-END / MEDIA</span></header>
    <section className="intro"><p className="eyebrow">FROM ORIGINAL TO DELIVERY</p>
      <h1>Good media.<br />A reliable pipeline.</h1>
      <p>Upload an image. Follow its journey. Recover when things go wrong.</p>
    </section>
    <form className="access" onSubmit={event => {
      event.preventDefault(); sessionStorage.setItem('studio-token', input); setToken(input);
    }}>
      <label htmlFor="token">Studio access token</label>
      <input id="token" type="password" value={input} onChange={event => setInput(event.target.value)} placeholder="Enter your token" required />
      <button>Connect</button>
      <small>Local walkthrough: <code>local-media-walkthrough</code>. Never use this token in Azure.</small>
    </form>
    {error && <p role="alert" className="error">{error}</p>}
    <section className="workspace">
      <aside className="panel">
        <p className="eyebrow">01 / INGEST</p><h2>Start with an original</h2>
        <p>PNG or JPEG, up to 5 MiB and 16 megapixels. Your original stays private; a 640px JPEG is created for delivery.</p>
        <label className="upload">Choose an image<input aria-label="Choose an image" type="file" accept="image/png,image/jpeg"
          disabled={!token || busy} onChange={event => {
            const file = event.target.files?.[0]; if (file) void upload(file, simulate); event.target.value = '';
          }} /></label>
        <label className="checkbox"><input type="checkbox" checked={simulate} onChange={event => setSimulate(event.target.checked)} />
          Simulate a processing failure</label>
        <hr /><h3>A two-minute walkthrough</h3>
        <p>These generated images require no external downloads.</p>
        <button disabled={!token || busy} onClick={() => void walkthrough(false)}>1. Create a sample rendition</button>
        <button className="secondary" disabled={!token || busy} onClick={() => void walkthrough(true)}>2. Explore failure &amp; recovery</button>
        <p className="note">The failure example exhausts three attempts, then stops. Retry clears the simulated fault while preserving the original and job history generation.</p>
      </aside>
      <section className="library">
        <div className="section-title"><div><p className="eyebrow">02 / PROCESS &amp; DELIVER</p><h2>Your processing desk</h2></div>
          <span>{jobs.length} recent assets</span></div>
        <div className="stats">{(['Queued', 'Processing', 'Ready', 'Failed'] as const).map(state =>
          <div key={state}><strong>{jobs.filter(job => job.state === state).length}</strong><span>{state}</span></div>)}</div>
        {jobs.length === 0 && <div className="empty"><h3>Your next image starts here.</h3><p>Connect, then create a sample or upload your own image.</p></div>}
        <div className="cards">{jobs.map(job => <article className="panel asset" key={job.id}>
          <Rendition job={job} token={token} />
          <div className="asset-body"><span className={`badge ${job.state.toLowerCase()}`}>{job.state}</span>
            <h3>{job.name}</h3><p>Attempt {job.attempts} / 3 · Generation {job.generation + 1}</p>
            {job.error && <p className="error">{job.error}</p>}
            {job.state === 'Failed' && <button onClick={async () => {
              try { await request(`/jobs/${job.id}/retry`, { method: 'POST' }); await refresh(); }
              catch (e) { setError(String(e)); }
            }}>Retry processing</button>}
            <details><summary>Processing details</summary><code>{job.id}</code>
              <p>{new Date(job.created).toLocaleString()}</p><code>{job.traceParent ?? 'No trace context'}</code></details>
          </div>
        </article>)}</div>
      </section>
    </section>
    <footer>Private originals. Durable state. Bounded retries. Built with Aspire.</footer>
  </main>;
}

function Rendition({ job, token }: { job: Job; token: string }) {
  const [url, setUrl] = useState('');
  const [error, setError] = useState('');
  useEffect(() => {
    if (job.state !== 'Ready') return;
    let active = true;
    let objectUrl = '';
    fetch(`/api/jobs/${job.id}/rendition`, { headers: { Authorization: `Bearer ${token}` } })
      .then(async response => { if (!response.ok) throw new Error('Rendition unavailable'); return response.blob(); })
      .then(blob => { if (active) { objectUrl = URL.createObjectURL(blob); setUrl(objectUrl); } })
      .catch(e => { if (active) setError(String(e)); });
    return () => { active = false; if (objectUrl) URL.revokeObjectURL(objectUrl); };
  }, [job.id, job.state, token]);
  return url ? <a href={url} download={`${job.name}.jpg`}><img src={url} alt={`Processed ${job.name}`} /></a>
    : <div className="placeholder">{error || (job.state === 'Failed' ? 'A recoverable pause.' : job.state === 'Ready' ? 'Loading rendition...' : 'Making your rendition...')}</div>;
}

createRoot(document.getElementById('root')!).render(<React.StrictMode><Studio /></React.StrictMode>);
