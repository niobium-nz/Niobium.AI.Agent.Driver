import assert from 'node:assert/strict';

// Read-only/disabled local compatibility probe. Never send an agent dispatch to a cloud host.
const endpoint = new URL(process.argv[2] ?? 'http://127.0.0.1:7071/runtime/webhooks/mcp');
assert.equal(endpoint.protocol, 'http:');
assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(endpoint.hostname), 'Only loopback hosts are permitted');
let session;
let version = '2025-06-18';
let sequence = 0;
async function rpc(method, params, notification = false) {
  const id = notification ? undefined : ++sequence;
  const response = await fetch(endpoint, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json', Accept: 'application/json, text/event-stream',
      'MCP-Protocol-Version': version,
      ...(session ? { 'Mcp-Session-Id': session } : {})
    },
    body: JSON.stringify({ jsonrpc: '2.0', ...(id ? { id } : {}), method, params }),
    signal: AbortSignal.timeout(15000)
  });
  assert.ok(response.ok, `${method}: HTTP ${response.status}`);
  session = response.headers.get('mcp-session-id') ?? session;
  if (notification) { await response.body?.cancel(); return; }
  const text = await response.text();
  const messages = response.headers.get('content-type')?.includes('text/event-stream')
    ? text.split(/\r?\n/).filter(line => line.startsWith('data:')).map(line => JSON.parse(line.slice(5).trim()))
    : [JSON.parse(text)];
  const message = messages.find(item => item.id === id);
  assert.ok(message, `Missing response for ${method}`);
  assert.equal(message.error, undefined, `Protocol error for ${method}`);
  return message.result;
}
const initialized = await rpc('initialize', {
  protocolVersion: version, capabilities: {}, clientInfo: { name: 'niobium-local-compatibility', version: '1.0.0' }
});
version = initialized.protocolVersion;
await rpc('notifications/initialized', {}, true);
const discovery = await rpc('tools/list', {});
assert.deepEqual(discovery.tools.map(tool => tool.name).sort(), ['dispatch_agent', 'list_agents']);
const dispatch = discovery.tools.find(tool => tool.name === 'dispatch_agent');
assert.equal(dispatch.inputSchema.type, 'object');
assert.deepEqual(Object.keys(dispatch.inputSchema.properties).sort(), ['conversation_key', 'handoff_id', 'input', 'target_agent']);
assert.equal(dispatch.annotations?.readOnlyHint === true, false, 'Dispatch must not be declared read-only');
for (const [name, args] of [
  ['list_agents', {}],
  ['dispatch_agent', { target_agent: 'synthetic-v1', handoff_id: 'local-disabled-check', input: { task: 'Do not execute this disabled test' } }]
]) {
  const result = await rpc('tools/call', { name, arguments: args });
  assert.equal(result.isError, true);
  assert.equal(typeof result.structuredContent, 'object');
  assert.equal(result.structuredContent.error_code, 'gateway_disabled');
  const content = result.content.find(block => block.type === 'text');
  assert.deepEqual(JSON.parse(content.text), result.structuredContent, 'Text must represent the object, not a JSON-encoded string');
}
console.log(JSON.stringify({ protocolVersion: version, tools: ['list_agents', 'dispatch_agent'], result: 'passed', mode: 'local-disabled-only' }));
