import assert from 'node:assert/strict';

// Disabled, loopback-only probe. Never dispatch work or write a job on a cloud host.
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
      'MCP-Protocol-Version': version, ...(session ? { 'Mcp-Session-Id': session } : {})
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
  protocolVersion: version, capabilities: {}, clientInfo: { name: 'niobium-local-compatibility', version: '2.0.0' }
});
version = initialized.protocolVersion;
await rpc('notifications/initialized', {}, true);
const discovery = await rpc('tools/list', {});
const fields = {
  dispatch_agent: ['conversation_key', 'handoff_id', 'input', 'target_agent'],
  get_job_result: ['job_id'],
  get_job_status: ['job_id'],
  list_agents: [],
  start_agent_job: ['completion_mode', 'continuation_ref', 'conversation_key', 'handoff_id', 'input', 'result_schema', 'target_agent'],
  submit_job_result: ['completion_capability', 'job_id', 'outcome', 'result']
};
const expected = Object.keys(fields).sort();
assert.deepEqual(discovery.tools.map(tool => tool.name).sort(), expected);
assert.equal(discovery.nextCursor ?? null, null);
for (const { name, inputSchema } of discovery.tools) {
  assert.equal(inputSchema.type, 'object');
  assert.deepEqual(Object.keys(inputSchema.properties ?? {}).sort(), fields[name]);
}
for (const name of ['dispatch_agent', 'start_agent_job', 'submit_job_result']) {
  assert.notEqual(discovery.tools.find(tool => tool.name === name).annotations?.readOnlyHint, true);
}
const syntheticId = 'job_v1_' + '0'.repeat(64);
for (const [name, args, code] of [
  ['list_agents', {}, 'gateway_disabled'],
  ['dispatch_agent', { target_agent: 'synthetic-v1', handoff_id: 'disabled-check', input: { task: 'Do not execute' } }, 'gateway_disabled'],
  ['start_agent_job', { target_agent: 'synthetic-v1', handoff_id: 'disabled-job', input: { task: 'Do not execute' }, result_schema: 'agent-result.v1', completion_mode: 'store_only' }, 'tracked_jobs_disabled'],
  ['submit_job_result', { job_id: syntheticId, completion_capability: 'synthetic-invalid', outcome: 'succeeded', result: { summary: 'Not executed', data: {} } }, 'tracked_jobs_disabled'],
  ['get_job_status', { job_id: syntheticId }, 'tracked_jobs_disabled'],
  ['get_job_result', { job_id: syntheticId }, 'tracked_jobs_disabled']
]) {
  const result = await rpc('tools/call', { name, arguments: args });
  assert.equal(result.isError, true);
  assert.equal(typeof result.structuredContent, 'object');
  assert.equal(result.structuredContent.error_code, code);
  assert.deepEqual(JSON.parse(result.content.find(block => block.type === 'text').text), result.structuredContent);
}
console.log(JSON.stringify({ protocolVersion: version, tools: expected, result: 'passed', mode: 'local-disabled-only' }));
