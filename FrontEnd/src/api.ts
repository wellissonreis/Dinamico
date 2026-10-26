export type Breakdown = {
  schedulerLate: number; concurrencyLimit: number; policy: number; transportFailed: number
  requestTimedOut: number; cancelledInFlight: number; responseLimitExceeded: number; internalFailed: number
  statuses: Record<string, number>
}
export type ResourceSample = {
  windowIndex: number; elapsedSeconds: number; workingSetBytes: number | null; managedHeapBytes: number | null
  cpuPercentOfOneCore: number | null; totalAllocatedBytes: number | null; gcPauseTicks: number | null
  gen0: number | null; gen1: number | null; gen2: number | null; threadPoolPending: number | null
  threadPoolThreads: number | null; httpOpenConnections: number | null; httpQueueMaxMilliseconds: number | null
}
export type RequestSample = {
  arrivalIndex: number; startSeconds: number; durationMilliseconds: number; headersMilliseconds: number | null
  statusCode: number | null; outcome: string; errorKind: string; traceId: string | null; spanId: string | null
}
export type RunDiagnostics = {
  schemaVersion: number; captureMode: string; correlationEnabled: boolean; observed: number; retained: number
  discarded: number; captureComplete: boolean; resources: ResourceSample[]; requests: RequestSample[]
  scheduler: { wakeCount: number; limitedWakeCount: number; toleranceExceededDrops: number; wakeCapacityDrops: number; endBoundaryDrops: number; maxObservedLateness: string; quantum: string; latenessTolerance: string } | null; exportState: string; traceUrlTemplate: string | null
}
export type WindowMetrics = {
  index: number; startSeconds: number; endSeconds: number
  started: number; httpCompleted: number; errors: number; dropped: number; p95Milliseconds: number | null
  httpSuccess?: number; inFlight?: number; p50Milliseconds?: number | null; p99Milliseconds?: number | null
  minMilliseconds?: number | null; maxMilliseconds?: number | null; terminalP95Milliseconds?: number | null
  terminalCount?: number; httpCount?: number; quality?: string[]; breakdown?: Breakdown
}
export type Execution = {
  executionId: string; projectId: string; startedUtc: string; finishedUtc: string | null
  state: 'Running' | 'Completed' | 'Cancelled' | 'Failed'
  persistence: 'pending' | 'saved' | 'failed'; dataComplete: boolean; quality: string[]; reason: string | null
  plan: { requestsPerSecond: number; durationSeconds: number; maxConcurrency: number; method: string; rateLimitMode: string } | null
  metrics: {
    planned: number; scheduled: number; started: number; httpCompleted: number; httpSuccess: number
    errors: number; dropped: number; inFlight: number; rateLimitedPercent: number | null
    breakdown?: Breakdown; minMilliseconds?: number | null; maxMilliseconds?: number | null
    terminalP95Milliseconds?: number | null; terminalCount?: number; httpCount?: number
    p50Milliseconds: number | null; p95Milliseconds: number | null; p99Milliseconds: number | null
  }
  notScheduledAfterStop?: number | null; diagnostics?: RunDiagnostics | null
  windows: WindowMetrics[]; elapsedSeconds: number
}
export type RunRequest = {
  url: string; method: string; requestsPerSecond: number; durationSeconds: number; maxConcurrency: number
  requestTimeoutSeconds: number; rateLimitMode: string; headers: { name: string; value: string }[]
  captureMode: 'Off' | 'Sampled' | 'Complete'; correlateRequests: boolean
  payload: string; testCredentialHeaderIndex: number | null; testAuthorizationDeclared: boolean
}
export type HistoryPage = { items: Execution[]; nextCursor: string | null }
export type Health = { status: string; databaseAvailable: boolean }

export class ApiError extends Error {
  constructor(message: string, public status: number) { super(message) }
}
export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  let response: Response
  try {
    response = await fetch(`/api${path}`, {
      ...options, headers: { 'Content-Type': 'application/json', 'X-Dinamico-Client': 'local-ui', ...options.headers },
    })
  } catch {
    throw new ApiError('Não foi possível conectar ao servidor. Confira se o Dinamico está em execução.', 0)
  }
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { message?: string; errors?: { code: string; field: string }[] } | null
    const descriptions: Record<string, string> = {
      LoopbackLiteralRequired: 'Use um domínio HTTP/HTTPS ou um IP loopback literal, como 127.0.0.1 ou [::1].',
      InvalidUrl: 'Confira o endereço do endpoint. Use uma URL HTTP ou HTTPS válida, sem fragmento.',
      SchemeNotAllowed: 'O endpoint deve usar HTTP ou HTTPS.', UserInfoNotAllowed: 'Informe credenciais nos headers, não na URL.',
      InvalidHeader: 'Confira os headers: use um nome e valor válidos por linha.',
      HeaderNotAllowed: 'Um dos headers informados é gerenciado pelo transporte e não pode ser alterado.',
      DuplicateHeader: 'Há headers duplicados. Mantenha uma entrada por nome.', PayloadTooLarge: 'O corpo pode ter até 64 KiB.',
      TestCredentialRequired: 'Selecione um header de credencial válido para o modo autorizado.',
      CompleteCaptureLimitExceeded: 'A captura completa permite até 2.000 requisições planejadas.',
      TestAuthorizationRequired: 'Confirme a declaração de autorização para este teste.',
    }
    const detail = body?.errors?.map(item => item.field === 'CaptureMode' ? 'Escolha uma captura válida; a captura completa permite até 2.000 requisições planejadas.' : item.field === 'CorrelateRequests' ? 'Para correlacionar, habilite a captura e remova headers traceparent e tracestate manuais.' : descriptions[item.code]).find(Boolean)
    throw new ApiError(detail || body?.message || `A operação não foi concluída (${response.status}).`, response.status)
  }
  return response.json() as Promise<T>
}
export const projectPath = (project: string) => `/projects/${encodeURIComponent(project)}`
export const number = (value: number) => value.toLocaleString('pt-BR')
export const milliseconds = (value: number | null | undefined) => value == null ? '—' : `${value.toLocaleString('pt-BR', { maximumFractionDigits: 2 })} ms`
export const date = (value: string) => new Date(value).toLocaleString('pt-BR', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' })
export const stateLabels = { Running: 'Em execução', Completed: 'Concluído', Cancelled: 'Interrompido', Failed: 'Falhou' }
