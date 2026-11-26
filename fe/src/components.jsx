import { useState } from 'react';
import { Badge, Button, Callout, Flex, Slider, Text, TextField } from '@radix-ui/themes';
import { STATUS_COLOR, api, artifactUrl, fmtDuration, reorder, usePolling } from './lib.js';

/** Page title strip: a black background with the product image (public/) fading toward the left. */
const HERO_IMAGES = { transformer: '/hero-transformer.png' };

export function Hero({ product, eyebrow, title, text, children }) {
  const img = HERO_IMAGES[product];
  return (
    <section className={`hero${img && img.endsWith('.png') ? ' cutout' : ''}`} style={img ? { backgroundImage: `url('${img}')` } : { backgroundImage: 'none' }}>
      <div className="hero-copy">
        <span className="eyebrow">{eyebrow}</span>
        <h1>{title}</h1>
        {text && <p>{text}</p>}
        {children}
      </div>
    </section>
  );
}

/** One parameter: a number box + a slider kept in sync; the box turns red when a rule is violated (reported by the server). `note` shows under the label. */
export function ParamField({ p, value, invalid, onChange, note }) {
  const step = p.integer || p.max - p.min > 50 ? 1 : 0.1;
  return (
    <div className="field">
      <Text as="label" htmlFor={p.name} size="2" weight="medium">
        {p.label}
        {note && <Text as="div" size="1" color="gray">{note}</Text>}
      </Text>
      <TextField.Root
        id={p.name} type="number" size="2" min={p.min} max={p.max} step={p.integer ? 1 : 'any'}
        value={value} color={invalid ? 'red' : undefined} variant={invalid ? 'soft' : 'surface'}
        onChange={(e) => e.target.value !== '' && onChange(Number(e.target.value))}
      />
      <Slider className="slider" size="1" min={p.min} max={p.max} step={step} value={[Math.min(p.max, Math.max(p.min, value))]}
        color={invalid ? 'red' : undefined} onValueChange={([v]) => onChange(v)} aria-label={p.label} />
      <span className="range">{`${p.min}-${p.max} ${p.unit}`.trim()}</span>
    </div>
  );
}

export function StatusBadge({ status }) {
  return <Badge color={STATUS_COLOR[status] || 'gray'} variant="soft" radius="full">{status}</Badge>;
}

/** A box for errors/warnings; shows nothing when children is null. */
export function Notice({ color = 'red', children }) {
  if (!children || (Array.isArray(children) && children.length === 0)) return null;
  return (
    <Callout.Root color={color} size="1" mb="3" role="alert">
      <Callout.Text as="div">{children}</Callout.Text>
    </Callout.Root>
  );
}

/** "Order again": pours the input of an old job into the New order form. */
export function OrderAgainButton({ job, size = '1' }) {
  return <Button variant="soft" size={size} title="Open New order with this job's inputs" onClick={() => reorder(job)}>Order again</Button>;
}

const KIND_COLOR = { preview: 'violet', export: 'orange' };

/** The kind of a job (preview or export). A normal full build (generate) shows nothing, so most rows stay quiet. */
export function KindBadge({ kind }) {
  if (!kind || kind === 'generate') return null;
  return <Badge color={KIND_COLOR[kind] || 'gray'} variant="outline" radius="full" ml="2" title={kind === 'preview' ? 'Fast preview: one picture, no drawing' : 'Rebuild of an older job'}>{kind}</Badge>;
}

/** Text for a queued job. */
export function queueNote() {
  return 'Waiting for a Creo worker...';
}

/**
 * Quick preview: orders a `preview` job (fast, one picture) with createJob() and shows the picture right here when it is done.
 * Preview jobs wait in the filter queue, so they are not stuck behind full builds.
 */
export function QuickPreview({ createJob, disabled }) {
  const [job, setJob] = useState(null);
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);
  const jobId = job?.id;
  const done = !job || ['succeeded', 'failed', 'cancelled'].includes(job.status);

  usePolling(async () => {
    if (!jobId || done) return;
    const j = await api(`/api/jobs/${encodeURIComponent(jobId)}`);
    setJob((cur) => (cur && cur.id === j.id ? j : cur)); // ignore a late answer for an older preview
  }, 1000, [jobId, done]);

  const start = async () => {
    setBusy(true); setError(null);
    try { setJob(await createJob()); }
    catch (e) { setError(e.body?.issues?.[0]?.message || e.message); }
    finally { setBusy(false); }
  };

  const image = job?.status === 'succeeded' ? job.artifacts.find((a) => a.kind === 'image') : null;
  const status = !job ? null : {
    queued: queueNote(),
    running: `Building preview: ${job.step || '...'}`,
    succeeded: image ? null : 'Preview finished, but no picture was returned.',
    failed: 'Preview failed: ' + (job.error || 'unknown error'),
    cancelled: 'Preview was cancelled.',
  }[job.status];

  return (
    <Flex direction="column" gap="3">
      <Flex align="center" gap="3" wrap="wrap">
        <Button variant="soft" disabled={disabled || busy || (!done)} onClick={start}
          title="Build a fast picture of this configuration without waiting for the full build">Quick preview</Button>
        {status && <Text size="2" color={job.status === 'failed' ? 'red' : 'gray'}>{status}</Text>}
        {job && done && <Text size="2" color="gray"><a href={`#/jobs/${job.id}`}>{job.id}</a>{job.startedAt && job.finishedAt ? ` · ${fmtDuration((new Date(job.finishedAt) - new Date(job.startedAt)) / 1000)}` : ''}</Text>}
      </Flex>
      {error && <Notice color="amber">{error}</Notice>}
      {image && <img className="picture" src={artifactUrl(job.id, image.name)} alt="Preview" />}
    </Flex>
  );
}
