import { useState } from 'react';
import { AlertDialog, Button, Card, Flex, Heading, Link, Text } from '@radix-ui/themes';
import { api, artifactUrl, canOrder, fmtDate, fmtSize, fmtTime, postJson, useApp, usePolling } from '../lib.js';
import { KindBadge, OrderAgainButton, StatusBadge, queueNote } from '../components.jsx';

const VIEW_LABELS = { front: 'Front', top: 'Top', right: 'Right' };
const ARTIFACT_LABELS = { step: 'STEP', pdf: 'PDF drawing', dwg: 'DWG drawing', bom: 'BOM (CSV)', log: 'Build log', prt: 'Creo part' };
/** View name from the file name: <id>_front.png -> Front; <id>.png (no suffix) is the iso view. */
const viewLabel = (name) => VIEW_LABELS[(name.match(/_([a-z]+)\.png$/) || [])[1]] || 'Isometric';

/** Image gallery: a large image + a strip of thumbnails; click a thumbnail to switch, click the large image to open it in a new tab. */
function Gallery({ images, url }) {
  const sorted = [...images].sort((a, b) =>
    (viewLabel(a.name) === 'Isometric' ? -1 : viewLabel(b.name) === 'Isometric' ? 1 : a.name.localeCompare(b.name)));
  const [index, setIndex] = useState(0);
  if (!sorted.length) return null;
  const cur = sorted[Math.min(index, sorted.length - 1)];
  return (
    <div style={{ marginBottom: 12 }}>
      <a href={url(cur)} target="_blank" rel="noopener noreferrer"><img className="picture" src={url(cur)} alt={viewLabel(cur.name)} /></a>
      <Text as="div" size="2" color="gray" mt="1">{viewLabel(cur.name)}</Text>
      <div className="thumbs">
        {sorted.map((a, i) => (
          <button key={a.name} type="button" className={`thumb${a === cur ? ' active' : ''}`} title={viewLabel(a.name)} onClick={() => setIndex(i)}>
            <img src={url(a)} alt={viewLabel(a.name)} /><span>{viewLabel(a.name)}</span>
          </button>
        ))}
      </div>
    </div>
  );
}

function CancelButton({ onConfirm }) {
  return (
    <AlertDialog.Root>
      <AlertDialog.Trigger><Button color="red" variant="soft">Cancel</Button></AlertDialog.Trigger>
      <AlertDialog.Content maxWidth="420px">
        <AlertDialog.Title>Cancel this order?</AlertDialog.Title>
        <AlertDialog.Description size="2">The job will be stopped and removed from the queue. You can order it again later.</AlertDialog.Description>
        <Flex gap="3" mt="4" justify="end">
          <AlertDialog.Cancel><Button variant="soft" color="gray">Keep it</Button></AlertDialog.Cancel>
          <AlertDialog.Action><Button color="red" onClick={onConfirm}>Cancel order</Button></AlertDialog.Action>
        </Flex>
      </AlertDialog.Content>
    </AlertDialog.Root>
  );
}

export default function Job({ id, onNav }) {
  const { project } = useApp();
  const [job, setJob] = useState(null);
  const [error, setError] = useState(null);
  const [tick, setTick] = useState(0);
  const [vaultMsg, setVaultMsg] = useState(null);
  const done = job ? ['succeeded', 'failed', 'cancelled'].includes(job.status) : false;

  // Poll until the job ends.
  usePolling(async () => {
    if (done) return;
    try {
      const j = await api(`/api/jobs/${encodeURIComponent(id)}`);
      setJob(j);
      onNav(['succeeded', 'failed', 'cancelled'].includes(j.status) ? 'dashboard' : 'orders'); // waiting/running belongs to Ordered, finished belongs to Dashboard
    } catch (e) { setError(e.message); }
  }, 1500, [id, done, tick]);

  if (error && !job) return <Card size="3"><Heading size="5">Something went wrong</Heading><Text color="gray">{error}</Text></Card>;
  if (!job) return <Text color="gray">Loading job...</Text>;

  const message = {
    queued: queueNote(),
    running: `The worker is building: ${job.step || '...'}`,
    succeeded: 'Build finished.',
    failed: 'The build failed: ' + (job.error || 'unknown error'),
    cancelled: 'This order was cancelled.',
  }[job.status];

  const pdf = job.artifacts.find((a) => a.kind === 'pdf');
  const dwg = job.artifacts.find((a) => a.kind === 'dwg');
  const url = (a) => artifactUrl(job.id, a.name);
  const cancel = async () => { await postJson(`/api/jobs/${job.id}/cancel`).catch(() => {}); setTick((t) => t + 1); };

  // Send the result files to Smart Vault again (the outbox the Vault reads).
  const saveToVault = async () => {
    setVaultMsg(null);
    try {
      const r = await postJson(`/api/jobs/${encodeURIComponent(job.id)}/vault`);
      setVaultMsg({ ok: true, text: `Sent ${r.sent} file(s) to Smart Vault.` });
    } catch (e) { setVaultMsg({ ok: false, text: e.message }); }
  };

  const material = job.textInputs?.PI_Material;
  const finish = job.textInputs?.PI_Finish;
  const meta = [
    ['Product', job.productCode],
    ['Project', job.projectId],
    ['Kind', job.kind || 'generate'],
    ['Requested by', job.requestedBy],
    ['Created', fmtDate(job.createdAt)],
    ['Started', fmtDate(job.startedAt)],
    ['Finished', fmtDate(job.finishedAt)],
    ['Worker', job.worker || '-'],
    ['Attempts', job.attempts],
    ...(material ? [['Material', material]] : []),
    ...(finish ? [['Finish', finish]] : []),
    ...(Object.keys(job.summary).length ? [['Summary', Object.entries(job.summary).map(([k, v]) => `${k}: ${v}`).join('   ')]] : []),
  ];
  const inputs = Object.entries(job.inputs);

  return (
    <Flex direction="column" gap="4">
      <Card size="3">
        <Text as="p" size="2" mb="2"><Link href={done ? '#/dashboard' : '#/orders'}>{done ? '< Back to Dashboard' : '< Back to Ordered'}</Link></Text>
        <Flex align="center" gap="3" wrap="wrap" mb="2">
          <Heading size="5">{job.id}</Heading>
          <StatusBadge status={job.status} />
          <KindBadge kind={job.kind} />
          {!done && canOrder(project) && <CancelButton onConfirm={cancel} />}
          {job.status !== 'queued' && job.status !== 'running' && <OrderAgainButton job={job} size="2" />}
        </Flex>
        <Text as="p" size="2" mb="4" color={job.status === 'failed' ? 'red' : undefined}>{message}</Text>
        <div className="cols">
          <dl className="meta">
            {meta.flatMap(([k, v]) => [<dt key={`k${k}`}>{k}</dt>, <dd key={`v${k}`}>{String(v)}</dd>])}
          </dl>
          <dl className="meta">
            {inputs.flatMap(([k, v]) => [
              <dt key={`k${k}`}>{k}</dt>,
              <dd key={`v${k}`}>{/Count$/.test(k) ? String(v) : `${v} mm`}</dd>,
            ])}
          </dl>
        </div>
      </Card>

      {job.status === 'succeeded' && (
        <Card size="3">
          <Heading size="4" mb="3">Result</Heading>
          <Gallery images={job.artifacts.filter((a) => a.kind === 'image')} url={url} />
          <Flex gap="2" wrap="wrap">
            {/* A short label per kind. The pictures are already in the gallery above. */}
            {job.artifacts.filter((a) => a.kind !== 'image').map((a) => (
              <Button key={a.name} asChild variant="outline" size="2"><a href={url(a)} download={a.name}>{`${ARTIFACT_LABELS[a.kind] || a.kind} (${fmtSize(a.size)})`}</a></Button>
            ))}
          </Flex>
          {job.artifacts.length === 0 && <Text size="2" color="gray">The worker returned no files.</Text>}
          {job.kind !== 'preview' && canOrder(project) && (
            <Flex gap="3" align="center" mt="3" wrap="wrap">
              <Button variant="soft" onClick={saveToVault}>Save to Smart Vault</Button>
              {vaultMsg && <Text size="2" color={vaultMsg.ok ? 'green' : 'red'}>{vaultMsg.text}</Text>}
            </Flex>
          )}
        </Card>
      )}

      {job.status === 'succeeded' && pdf && (
        <Card size="3">
          <Heading size="4" mb="1">2D drawing</Heading>
          <Flex gap="2" wrap="wrap" mb="3">
            <Button asChild><a href={url(pdf)} download={pdf.name}>{`Download PDF (${fmtSize(pdf.size)})`}</a></Button>
            {dwg && <Button asChild variant="outline"><a href={url(dwg)} download={dwg.name}>{`Download DWG (${fmtSize(dwg.size)})`}</a></Button>}
          </Flex>
          <embed className="pdf" src={url(pdf) + '#view=FitH'} type="application/pdf" />
        </Card>
      )}

      <Card size="3">
        <Heading size="4" mb="3">Timeline</Heading>
        <ul className="timeline">
          {[...job.events].reverse().map((e, i) => <li key={i}><time>{fmtTime(e.time)}</time>{e.message}</li>)}
        </ul>
      </Card>
    </Flex>
  );
}
