// Real, bounded local smoke test. Requires the web app, TelemetryDemo and Jaeger.
// Run with the demo exclusively: its request counter is the independent evidence.
import { randomUUID } from 'node:crypto'
import { mkdir, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'

function localBase(value) {
  let url
  try { url = new URL(value) } catch { throw new Error('Invalid local service base URL.') }
  if (url.protocol !== 'http:' || !['127.0.0.1', '[::1]'].includes(url.hostname)
      || url.username || url.password || url.search || url.hash || url.pathname !== '/')
    throw new Error('Use a loopback literal HTTP base URL without credentials, path or query.')
  return url.origin
}
const web = localBase(process.env.DINAMICO_WEB_URL ?? 'http://127.0.0.1:5080')
const target = localBase(process.env.DINAMICO_TELEMETRY_TARGET ?? 'http://127.0.0.1:5095')
const jaeger = localBase(process.env.DINAMICO_JAEGER_URL ?? 'http://127.0.0.1:16686')
const output = resolve(process.env.DINAMICO_TELEMETRY_EVIDENCE ?? 'TestResults/Diagnostics/Telemetry/verified.json')
const pause = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds))
function check(condition, message) { if (!condition) throw new Error(message) }
async function json(url, options = {}) {
  const response = await fetch(url, { ...options, signal: AbortSignal.timeout(5000) })
  check(response.ok, `Local service returned HTTP ${response.status}.`)
  return response.json()
}
async function run({ rate, duration, concurrency, delay, captureMode, correlateRequests }) {
  const before = await json(`${target}/health`)
  check(Number.isSafeInteger(before.requestCount), 'Target has no independent request counter.')
  const projectId = randomUUID()
  const request = {
    url: `${target}/work?delayMs=${delay}`, method: 'GET', requestsPerSecond: rate,
    durationSeconds: duration, maxConcurrency: concurrency, requestTimeoutSeconds: 2,
    rateLimitMode: 'Diagnostic', headers: [{ name: 'Authorization', value: 'Bearer SYNTHETIC_TELEMETRY_CANARY' }],
    payload: '', testCredentialHeaderIndex: null, testAuthorizationDeclared: false,
    captureMode, correlateRequests,
  }
  const initial = await json(`${web}/api/projects/${projectId}/executions`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Dinamico-Client': 'local-ui' },
    body: JSON.stringify(request),
  })
  const route = `${web}/api/projects/${projectId}/executions/${initial.executionId}`
  let result
  const deadline = Date.now() + (duration + 20) * 1000
  do {
    result = await json(route)
    if (result.state !== 'Running' && result.persistence !== 'pending') break
    await pause(200)
  } while (Date.now() < deadline)
  check(result.state === 'Completed' && result.persistence === 'saved', 'Execution did not complete and persist.')
  check(result.dataComplete, 'Canonical metric coverage is partial.')
  const after = await json(`${target}/health`)
  const received = after.requestCount - before.requestCount
  const m = result.metrics
  check(received === m.started && received === m.httpCompleted && received === m.httpSuccess,
    'Target/client counts differ. Run this check without other traffic to the demo.')
  check(m.planned === m.scheduled && m.scheduled === m.started + m.dropped && m.inFlight === 0 && m.errors === 0,
    'Request accounting did not reconcile.')
  check(result.diagnostics.resources.length > 0, 'No generator resource samples were retained.')
  check(!JSON.stringify(result).includes('SYNTHETIC_TELEMETRY_CANARY'), 'Synthetic secret reached the public result.')
  return { result, received }
}

async function main() {
  check((await json(`${web}/api/health`)).databaseAvailable, 'Web database is unavailable.')
  await json(`${jaeger}/api/services`)
  const traced = await run({ rate: 2, duration: 3, concurrency: 8, delay: 150, captureMode: 'Complete', correlateRequests: true })
  const diagnostics = traced.result.diagnostics
  check(diagnostics.exportState === 'exported' && diagnostics.captureComplete,
    'Configure the web host OTLP endpoint and enable complete capture/correlation.')
  check(diagnostics.requests.length > 0 && diagnostics.requests.length === traced.result.metrics.started,
    'Complete capture did not preserve every started operation.')
  const sample = diagnostics.requests[0]
  check(/^[0-9a-f]{32}$/.test(sample.traceId) && /^[0-9a-f]{16}$/.test(sample.spanId), 'Invalid correlation IDs.')
  let trace, endpointSpan, dependencySpan
  const deadline = Date.now() + 12000
  do {
    const response = await json(`${jaeger}/api/traces/${sample.traceId}`)
    trace = response.data?.[0]
    endpointSpan = trace?.spans?.find(span => span.operationName === 'GET /work')
    dependencySpan = trace?.spans?.find(span => span.operationName === 'controlled-dependency.delay')
    if (trace?.spans?.some(span => span.spanID === sample.spanId) && endpointSpan && dependencySpan) break
    await pause(300)
  } while (Date.now() < deadline)
  check(trace?.spans?.some(span => span.spanID === sample.spanId && span.operationName === 'load.request'),
    'Jaeger did not store the generator span.')
  check(endpointSpan?.references?.some(ref => ref.refType === 'CHILD_OF' && ref.spanID === sample.spanId),
    'Target endpoint does not reference the generated request span.')
  check(dependencySpan?.references?.some(ref => ref.refType === 'CHILD_OF' && ref.spanID === endpointSpan.spanID),
    'Dependency does not reference the target endpoint span.')
  check(dependencySpan.duration > 0, 'No dependency duration was measured.')
  check(!JSON.stringify(trace).includes('SYNTHETIC_TELEMETRY_CANARY'), 'Synthetic secret reached stored telemetry.')

  const limited = await run({ rate: 1000, duration: 5, concurrency: 32, delay: 64, captureMode: 'Sampled', correlateRequests: false })
  check(limited.result.metrics.breakdown.concurrencyLimit > 0, 'Concurrency pressure was not observed.')
  check(limited.result.diagnostics.exportState === 'disabled', 'Opt-out unexpectedly exported generator traces.')
  const evidence = {
    schemaVersion: 1, verifiedUtc: new Date().toISOString(),
    scope: 'Local controlled target with synthetic delay; not a reproduction of an unknown user execution.',
    correlation: {
      executionId: traced.result.executionId, projectId: traced.result.projectId,
      started: traced.result.metrics.started, received: traced.received, exportState: diagnostics.exportState,
      traceId: sample.traceId, requestSpanId: sample.spanId, endpointSpanId: endpointSpan.spanID,
      dependencySpanId: dependencySpan.spanID, dependencyDurationMicroseconds: dependencySpan.duration,
    },
    concurrency: {
      executionId: limited.result.executionId, projectId: limited.result.projectId,
      planned: limited.result.metrics.planned, started: limited.result.metrics.started, received: limited.received,
      dropped: limited.result.metrics.dropped, breakdown: limited.result.metrics.breakdown,
      droppedPercentOfScheduled: 100 * limited.result.metrics.dropped / limited.result.metrics.scheduled,
      dataComplete: limited.result.dataComplete, captureRetained: limited.result.diagnostics.retained,
    },
  }
  await mkdir(dirname(output), { recursive: true })
  await writeFile(output, JSON.stringify(evidence, null, 2) + '\n')
  console.log(JSON.stringify(evidence))
}
main().catch(error => { console.error(error.message); process.exitCode = 1 })
