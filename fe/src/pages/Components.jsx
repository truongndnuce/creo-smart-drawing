import { useEffect, useState } from 'react';
import { Badge, Button, Callout, Card, Flex, Heading, Table, Text } from '@radix-ui/themes';
import { API, api } from '../lib.js';

const range = (p) => (p.min != null && p.max != null ? `${p.min} - ${p.max}` : '');

/** Groups the parameters by FORM group, keeping the declaration order. */
function byGroup(params) {
  const groups = [];
  for (const p of params) {
    const g = p.group || '';
    let entry = groups.find((x) => x.name === g);
    if (!entry) groups.push((entry = { name: g, items: [] }));
    entry.items.push(p);
  }
  return groups;
}

function Component({ c }) {
  const [copied, setCopied] = useState(false);
  const name = encodeURIComponent(c.name);
  const include = `INCLUDE lib:${c.name}`;

  const copy = async () => {
    try { await navigator.clipboard.writeText(include); setCopied(true); setTimeout(() => setCopied(false), 1500); } catch { /* no clipboard permission */ }
  };

  return (
    <Card size="3">
      <Flex justify="between" align="start" gap="3" wrap="wrap">
        <div>
          <Heading size="4">{c.title}</Heading>
          <Text as="div" size="1" color="gray">lib:{c.name}</Text>
        </div>
        <Flex gap="1" wrap="wrap">
          {c.prefixes.map((p) => <Badge key={p} variant="soft" title="Parameter prefix">{p}</Badge>)}
          <Badge variant="soft" color="gray">{c.parameters.length} parameters</Badge>
        </Flex>
      </Flex>

      {c.summary && <Text as="p" size="2" mt="3">{c.summary}</Text>}
      {c.description && c.description !== c.summary && (
        <details className="comp-more">
          <summary><Text size="2" color="gray">Read the full description</Text></summary>
          <pre className="comp-pre">{c.description}</pre>
        </details>
      )}

      {c.errors.length > 0 && (
        <Callout.Root color="red" size="1" mt="3">
          <Callout.Text>This component has errors and cannot be used: {c.errors[0]}</Callout.Text>
        </Callout.Root>
      )}

      {c.sharedVariables.length > 0 && (
        <Text as="p" size="2" color="gray" mt="3">
          Variables shared with the script that includes it: {c.sharedVariables.map((v) => `${v.name} (${v.type.toLowerCase()})`).join(', ')}
        </Text>
      )}

      {c.parameters.length > 0 && (
        <details className="comp-more" open>
          <summary><Text size="2" color="gray">Parameters</Text></summary>
          {byGroup(c.parameters).map((g) => (
            <div key={g.name} className="table-scroll">
              {g.name && <Text as="div" size="2" weight="bold" mt="2" mb="1">{g.name}</Text>}
              <Table.Root variant="ghost" size="1">
                <Table.Header>
                  <Table.Row>
                    {['Parameter', 'Label', 'Default', 'Range', 'Unit'].map((t) => <Table.ColumnHeaderCell key={t}>{t}</Table.ColumnHeaderCell>)}
                  </Table.Row>
                </Table.Header>
                <Table.Body>
                  {g.items.map((p) => (
                    <Table.Row key={p.name}>
                      <Table.RowHeaderCell><code>{p.name}</code></Table.RowHeaderCell>
                      <Table.Cell>{p.label}{p.required ? ' *' : ''}</Table.Cell>
                      <Table.Cell>{String(p.default)}</Table.Cell>
                      <Table.Cell>{range(p)}</Table.Cell>
                      <Table.Cell>{p.unit}</Table.Cell>
                    </Table.Row>
                  ))}
                </Table.Body>
              </Table.Root>
            </div>
          ))}
        </details>
      )}

      <Text as="p" size="2" color="gray" mt="3" mb="1">
        To use it in your own script, put this line inside both BEGIN_FORM and BEGIN_BUILD:
      </Text>
      <Flex align="center" gap="2">
        <code className="comp-code">{include}</code>
        <Button size="1" variant="soft" color="gray" onClick={copy}>{copied ? 'Copied' : 'Copy'}</Button>
      </Flex>

      <Flex gap="2" mt="4" wrap="wrap">
        <Button asChild variant="soft" disabled={c.errors.length > 0}>
          <a href={`${API}/api/components/${name}/starter`} download={`${c.name}-starter.gea`} title="A commented starter script that includes this component">Download starter script (.gea)</a>
        </Button>
        <Button asChild variant="outline">
          <a href={`${API}/api/components/${name}/gea`} download={`${c.name}.gea`} title="The library file itself, with an explanation of every step">Download library (.gea)</a>
        </Button>
      </Flex>
    </Card>
  );
}

/** Components: libraries reused with INCLUDE lib:<name>. View, download files, or start a new script from it. */
export default function Components() {
  const [list, setList] = useState(null);
  const [error, setError] = useState('');

  useEffect(() => {
    api('/api/components').then(setList).catch((e) => setError(e.message));
  }, []);

  return (
    <div>
      <Heading size="5">Components</Heading>
      <Text as="p" size="2" color="gray" mt="1" mb="4">
        Reusable parts of a script. Include one in your own .gea script with a single line, or download it to read how each step works.
      </Text>
      {error && <Callout.Root color="red" size="1" mb="3"><Callout.Text>{error}</Callout.Text></Callout.Root>}
      {list === null && !error && <Text color="gray">Loading...</Text>}
      {list?.length === 0 && <Text color="gray">There are no components yet.</Text>}
      <Flex direction="column" gap="4">
        {list?.map((c) => <Component key={c.name} c={c} />)}
      </Flex>
    </div>
  );
}
