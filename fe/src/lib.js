import { createContext, useContext, useEffect, useRef, useState } from 'react';

// Same origin as src/Cadflow.Api; when running `npm run dev`, Vite forwards /api to the server.
export const API = '';

// Development sign-in: the user is named by the X-Dev-User header (replaced by Entra ID sign-in later).
let devUser = 'dev';
try { devUser = localStorage.getItem('devUser') || 'dev'; } catch { /* storage blocked */ }
export const getDevUser = () => devUser;
export const setDevUser = (u) => { devUser = u; try { localStorage.setItem('devUser', u); } catch { /* ignore */ } };

/** Current project and user, provided by App. */
export const AppContext = createContext({ project: null, user: 'dev' });
export const useApp = () => useContext(AppContext);

export async function api(path, opts = {}) {
  const res = await fetch(API + path, { ...opts, headers: { 'X-Dev-User': devUser, ...opts.headers } });
  const text = await res.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!res.ok) {
    const err = new Error(body?.error || `${res.status} ${res.statusText}`);
    err.body = body;
    throw err;
  }
  return body;
}

export const postJson = (path, data) =>
  api(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: data === undefined ? undefined : JSON.stringify(data) });

/** A URL a plain <img>/<a> can use: the browser cannot add the dev header there, so the dev user goes in the query string. */
export const withUser = (url) => `${API}${url}${url.includes('?') ? '&' : '?'}devUser=${encodeURIComponent(devUser)}`;
export const artifactUrl = (jobId, name) => withUser(`/api/jobs/${encodeURIComponent(jobId)}/artifacts/${encodeURIComponent(name)}`);

export const ROLE = { 1: 'Viewer', 2: 'Designer', 3: 'Reviewer', 4: 'ProjectAdmin', viewer: 'Viewer', designer: 'Designer', reviewer: 'Reviewer', projectAdmin: 'ProjectAdmin' };
export const canOrder = (p) => ['designer', 'reviewer', 'projectAdmin', 2, 3, 4].includes(p?.role);

export const fmtTime = (iso) => (iso ? new Date(iso).toLocaleTimeString() : '-');
export const fmtDate = (iso) => (iso ? new Date(iso).toLocaleString() : '-');
export const fmtSize = (n) => (n > 1048576 ? (n / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB');
export const fmtDuration = (s) => (s == null ? '-' : s >= 90 ? `${Math.floor(s / 60)} min ${Math.round(s % 60)} s` : `${Math.round(s)} s`);

export const productLabel = (j) => j.productCode;

export const STATUS_COLOR = { queued: 'gray', running: 'blue', succeeded: 'green', failed: 'red', cancelled: 'amber' };

/** Calls fn right away and repeats every ms; stops on unmount. fn is always the latest version. */
export function usePolling(fn, ms, deps = []) {
  const ref = useRef(fn);
  ref.current = fn;
  useEffect(() => {
    let alive = true;
    const run = () => Promise.resolve(ref.current()).catch(() => {});
    run();
    const t = setInterval(() => alive && run(), ms);
    return () => { alive = false; clearInterval(t); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ms, ...deps]);
}

/** Hash-based router (#/orders, #/jobs/<id>...): enough for a handful of screens, no library needed. */
export function useHash() {
  const [hash, setHash] = useState(location.hash || '#/');
  useEffect(() => {
    const on = () => setHash(location.hash || '#/');
    addEventListener('hashchange', on);
    return () => removeEventListener('hashchange', on);
  }, []);
  return hash;
}

export const go = (hash) => { location.hash = hash; };

/** Reopens an existing job on the New order form with the same inputs. */
export function reorder(job) {
  try { sessionStorage.setItem('cadflow.retry', JSON.stringify(job.inputs)); } catch { /* ignore */ }
  go(`#/new/${job.productCode}`);
}
