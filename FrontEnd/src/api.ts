export type WindowMetrics = {
  index: number; startSeconds: number; endSeconds: number
  started: number; httpCompleted: number; errors: number; dropped: number; p95Milliseconds: number | null
}
export type Execution = {
  executionId: string; projectId: string; startedUtc: string; finishedUtc: string | null
  state: 'Running' | 'Completed' | 'Cancelled' | 'Failed'
  persistence: 'pending' | 'saved' | 'failed'; dataComplete: boolean; quality: string[]; reason: string | null
  plan: { requestsPerSecond: number; durationSeconds: number; maxConcurrency: number; method: string; rateLimitMode: string } | null
  metrics: {
    planned: number; scheduled: number; started: number; httpCompleted: number; httpSuccess: number
    errors: number; dropped: number; inFlight: number; rateLimitedPercent: number | null
    p50Milliseconds: number | null; p95Milliseconds: number | null; p99Milliseconds: number | null
  }
  windows: WindowMetrics[]; elapsedSeconds: number
}
export type RunRequest = {
  url: string; method: string; requestsPerSecond: number; durationSeconds: number; maxConcurrency: number
  requestTimeoutSeconds: number; rateLimitMode: string; headers: { name: string; value: string }[]
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
      LoopbackLiteralRequired: 'Use um IP loopback literal, como 127.0.0.1 ou [::1]. Hostnames e destinos externos não são permitidos.',
      InvalidUrl: 'Confira o endereço do endpoint. Use uma URL HTTP ou HTTPS válida, sem fragmento.',
      SchemeNotAllowed: 'O endpoint deve usar HTTP ou HTTPS.', UserInfoNotAllowed: 'Informe credenciais nos headers, não na URL.',
      InvalidHeader: 'Confira os headers: use um nome e valor válidos por linha.',
      HeaderNotAllowed: 'Um dos headers informados é gerenciado pelo transporte e não pode ser alterado.',
      DuplicateHeader: 'Há headers duplicados. Mantenha uma entrada por nome.', PayloadTooLarge: 'O corpo pode ter até 64 KiB.',
      TestCredentialRequired: 'Selecione um header de credencial válido para o modo autorizado.',
      TestAuthorizationRequired: 'Confirme a declaração de autorização para este teste.',
    }
    const detail = body?.errors?.map(item => descriptions[item.code]).find(Boolean)
    throw new ApiError(detail || body?.message || `A operação não foi concluída (${response.status}).`, response.status)
  }
  return response.json() as Promise<T>
}
export const projectPath = (project: string) => `/projects/${encodeURIComponent(project)}`
export const number = (value: number) => value.toLocaleString('pt-BR')
export const milliseconds = (value: number | null | undefined) => value == null ? '—' : `${value.toLocaleString('pt-BR', { maximumFractionDigits: 2 })} ms`
export const date = (value: string) => new Date(value).toLocaleString('pt-BR', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' })
export const stateLabels = { Running: 'Em execução', Completed: 'Concluído', Cancelled: 'Interrompido', Failed: 'Falhou' }
