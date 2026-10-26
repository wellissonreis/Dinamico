import { test, expect } from '@playwright/test'
import { createServer } from 'node:http'
import { randomUUID } from 'node:crypto'
import { mkdir } from 'node:fs/promises'
import type { AddressInfo } from 'node:net'

let target: ReturnType<typeof createServer>
let targetUrl: string
let received = 0
test.beforeAll(async () => {
  target = createServer((_request, response) => { received++; response.writeHead(202, { 'Content-Type': 'text/plain' }); response.end('ok') })
  await new Promise<void>(resolve => target.listen(0, '127.0.0.1', resolve))
  targetUrl = `http://127.0.0.1:${(target.address() as AddressInfo).port}/`
  await mkdir('../TestResults/Web', { recursive: true })
})
test.afterAll(async () => { target.closeAllConnections(); await new Promise<void>(resolve => target.close(() => resolve())) })
test.beforeEach(async ({ page }) => {
  await page.addInitScript(project => localStorage.setItem('dinamico.project', project), randomUUID())
})

test('execução real, SSE, persistência, histórico e exportação', async ({ page }) => {
  const exceptions: string[] = []
  page.on('pageerror', error => exceptions.push(error.message))
  await page.setViewportSize({ width: 1440, height: 1050 })
  await page.goto('/')
  await expect(page.getByText('PostgreSQL conectado')).toBeVisible()
  await page.screenshot({ path: '../TestResults/Web/desktop-empty.png', fullPage: true })
  await page.getByLabel('Endpoint', { exact: false }).fill(targetUrl)
  await page.getByLabel('Duração', { exact: true }).fill('2')
  await page.getByRole('button', { name: 'Iniciar teste', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Interromper teste' })).toBeVisible()
  await expect(page.getByText('Salvo no histórico', { exact: true })).toBeVisible()
  await expect(page.getByRole('img', { name: /janelas de requisições/ })).toBeVisible()
  await expect(page.getByText('Concluído', { exact: true }).first()).toBeVisible()
  await page.screenshot({ path: '../TestResults/Web/desktop-result.png', fullPage: true })
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Baixar resultado' }).click()
  expect((await download).suggestedFilename()).toMatch(/^dinamico-.*\.json$/)
  await page.getByRole('navigation').getByRole('button', { name: /^Histórico/ }).click()
  await expect(page.getByRole('heading', { name: 'Histórico de execuções' })).toBeVisible()
  await expect(page.locator('tbody tr')).toHaveCount(1)
  await page.getByRole('button', { name: /^Ver execução/ }).click()
  await expect(page.getByText('Salvo no histórico', { exact: true })).toBeVisible()
  await page.reload()
  await page.getByRole('navigation').getByRole('button', { name: /^Histórico/ }).click()
  await expect(page.locator('tbody tr')).toHaveCount(1)
  expect(exceptions).toEqual([])
})

test('cancelamento real e rejeição de URL com fragmento com mensagem clara', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('button', { name: 'Iniciar teste', exact: true })).toBeEnabled()
  await page.getByLabel('Endpoint', { exact: false }).fill('http://127.0.0.1/#fragment')
  await page.getByRole('button', { name: 'Iniciar teste', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('sem fragmento')
  await page.getByLabel('Endpoint', { exact: false }).fill(targetUrl)
  await page.getByLabel('Duração', { exact: true }).fill('30')
  const before = received
  await page.getByRole('button', { name: 'Iniciar teste', exact: true }).click()
  await expect.poll(() => received).toBeGreaterThan(before)
  const project = await page.evaluate(() => localStorage.getItem('dinamico.project'))
  const activeBefore = await (await page.request.get(`/api/projects/${project}/active`)).json()
  await page.reload()
  await expect(page.getByRole('button', { name: 'Interromper teste' })).toBeVisible()
  const activeAfter = await (await page.request.get(`/api/projects/${project}/active`)).json()
  expect(activeAfter.execution.executionId).toBe(activeBefore.execution.executionId)
  await page.getByRole('button', { name: 'Interromper teste' }).click()
  await expect(page.getByText('Interrompido', { exact: true }).first()).toBeVisible()
  await expect(page.getByText('Salvo no histórico', { exact: true })).toBeVisible()
})

test('mobile, ajuda, projeto e indisponibilidade sem dados inventados', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Conheça os limites da sua aplicação.' })).toBeVisible()
  await expect(page.getByText('Seu histórico começa no primeiro teste')).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await page.screenshot({ path: '../TestResults/Web/mobile.png', fullPage: true })
  await page.getByRole('button', { name: 'Ajuda', exact: true }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog')).not.toBeVisible()
  await page.getByLabel('Configurar projeto').click()
  await page.getByLabel('Identificador do projeto').fill(randomUUID())
  await page.getByRole('button', { name: 'Aplicar projeto' }).click()
  await expect(page.getByRole('button', { name: 'Iniciar teste', exact: true })).toBeEnabled()
  await page.route('**/api/health', route => route.fulfill({ json: { status: 'degraded', databaseAvailable: false } }))
  await page.reload()
  await expect(page.getByText('O banco de dados não está disponível', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Iniciar teste', exact: true })).toBeDisabled()
})

test('captura completa real mantém contagens e diagnóstico após reconexão e histórico', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('button', { name: 'Iniciar teste', exact: true })).toBeEnabled()
  await page.getByLabel('Endpoint', { exact: false }).fill(targetUrl)
  await page.getByLabel('Duração', { exact: true }).fill('8')
  await page.getByText('Opções avançadas', { exact: false }).click()
  await expect(page.getByLabel('Captura de requisições')).toHaveValue('Off')
  await expect(page.getByLabel('Correlacionar requisições com traces')).not.toBeChecked()
  await page.getByLabel('Captura de requisições').selectOption('Complete')
  await page.getByLabel('Correlacionar requisições com traces').check()
  // Reject a capture budget overflow before it reaches the API.
  await page.getByLabel('Taxa', { exact: true }).fill('1000')
  let starts = 0
  page.on('request', request => { if (request.method() === 'POST' && /\/executions$/.test(request.url())) starts++ })
  await page.getByRole('button', { name: 'Iniciar teste', exact: true }).click()
  await expect(page.getByText(/A captura completa permite até 2.000/)).toBeVisible()
  expect(starts).toBe(0)
  await page.getByLabel('Taxa', { exact: true }).fill('10')
  const posted = page.waitForRequest(request => request.method() === 'POST' && /\/executions$/.test(request.url()))
  await page.getByRole('button', { name: 'Iniciar teste', exact: true }).click()
  expect((await posted).postDataJSON()).toMatchObject({ captureMode: 'Complete', correlateRequests: true })
  await expect(page.getByRole('button', { name: 'Interromper teste' })).toBeVisible()
  const project = await page.evaluate(() => localStorage.getItem('dinamico.project'))
  const active = await (await page.request.get(`/api/projects/${project}/active`)).json()
  await page.reload()
  await expect(page.getByRole('button', { name: 'Interromper teste' })).toBeVisible()
  await expect(page.getByText('Salvo no histórico', { exact: true })).toBeVisible()
  const path = `/api/projects/${project}/executions/${active.execution.executionId}`
  const completed = await (await page.request.get(path)).json()
  expect(completed.diagnostics.captureMode).toBe('Complete')
  expect(completed.diagnostics.captureComplete).toBe(true)
  expect(completed.diagnostics.requests.length).toBe(completed.metrics.started)
  expect(completed.diagnostics.retained).toBe(completed.diagnostics.observed)
  expect(completed.diagnostics.requests.every((item: { traceId: string | null }) => /^[a-f0-9]{32}$/i.test(item.traceId ?? ''))).toBe(true)
  expect(completed.metrics.scheduled).toBe(completed.metrics.started + completed.metrics.dropped)
  await expect(page.getByText(/Dados agregados completos. Isso não significa/)).toBeVisible()
  await expect(page.getByText(`Requisições capturadas (${completed.diagnostics.requests.length})`, { exact: true })).toBeVisible()
  await page.getByLabel('Métrica do gráfico').selectOption('memory')
  await page.getByText('Consultar tabela por janela').click()
  await expect(page.getByRole('columnheader', { name: 'Working set', exact: true })).toBeVisible()
  await page.getByRole('navigation').getByRole('button', { name: /^Histórico/ }).click()
  await page.getByRole('button', { name: /^Ver execução/ }).click()
  await expect(page.getByText(`Requisições capturadas (${completed.diagnostics.requests.length})`, { exact: true })).toBeVisible()
  await page.screenshot({ path: '../TestResults/Web/desktop-diagnostics.png', fullPage: true })
  const restored = await (await page.request.get(path)).json()
  expect(restored.diagnostics).toEqual(completed.diagnostics)
  expect(restored.metrics).toEqual(completed.metrics)
  await page.setViewportSize({ width: 390, height: 844 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await page.screenshot({ path: '../TestResults/Web/mobile-diagnostics.png', fullPage: true })
})

test('histórico anterior sem diagnóstico permanece legível e não inventa recursos', async ({ page }) => {
  const executionId = randomUUID()
  const legacy = {
    executionId, projectId: randomUUID(), startedUtc: '2026-01-01T12:00:00Z', finishedUtc: '2026-01-01T12:00:01Z',
    state: 'Completed', persistence: 'saved', dataComplete: true, quality: [], reason: null,
    plan: { requestsPerSecond: 10, durationSeconds: 1, maxConcurrency: 32, method: 'GET', rateLimitMode: 'Normal' },
    metrics: { planned: 10, scheduled: 10, started: 10, httpCompleted: 10, httpSuccess: 10, errors: 0, dropped: 0, inFlight: 0, rateLimitedPercent: 0, p50Milliseconds: 1, p95Milliseconds: 2, p99Milliseconds: 3 },
    windows: [{ index: 0, startSeconds: 0, endSeconds: 1, started: 10, httpCompleted: 10, errors: 0, dropped: 0, p95Milliseconds: 2 }], elapsedSeconds: 1,
  }
  await page.route('**/api/health', route => route.fulfill({ json: { status: 'healthy', databaseAvailable: true } }))
  await page.route('**/api/projects/*/active', route => route.fulfill({ json: { execution: null } }))
  await page.route('**/api/projects/*/executions?*', route => route.fulfill({ json: { items: [legacy], nextCursor: null } }))
  await page.route(`**/api/projects/*/executions/${executionId}`, route => route.fulfill({ json: legacy }))
  await page.goto('/')
  await page.getByRole('button', { name: /^Ver execução/ }).click()
  await expect(page.getByText('Sem dados de diagnóstico para esta execução.', { exact: true })).toBeVisible()
  await expect(page.getByText('Sem dados de causas para esta execução histórica.', { exact: true })).toBeVisible()
  await page.getByLabel('Métrica do gráfico').selectOption('cpu')
  await expect(page.getByText('Sem dados para esta métrica', { exact: true })).toBeVisible()
  await page.getByText('Consultar tabela por janela').click()
  const table = page.getByRole('table').filter({ has: page.getByRole('columnheader', { name: 'CPU', exact: true }) })
  await expect(table.locator('tbody td').first()).toHaveText('Sem dados')
})

test('histórico com detalhes removidos pela retenção conserva cobertura original', async ({ page }) => {
  const executionId = randomUUID()
  const pruned = {
    executionId, projectId: randomUUID(), startedUtc: '2026-01-01T12:00:00Z', finishedUtc: '2026-01-01T12:00:01Z',
    state: 'Completed', persistence: 'saved', dataComplete: true, quality: [], reason: null,
    plan: { requestsPerSecond: 10, durationSeconds: 1, maxConcurrency: 32, method: 'GET', rateLimitMode: 'Normal' },
    metrics: { planned: 10, scheduled: 10, started: 10, httpCompleted: 10, httpSuccess: 10, errors: 0, dropped: 0, inFlight: 0, rateLimitedPercent: 0, p50Milliseconds: 1, p95Milliseconds: 2, p99Milliseconds: 3 },
    windows: [], elapsedSeconds: 1,
    diagnostics: { schemaVersion: 1, captureMode: 'Complete', correlationEnabled: false, observed: 10, retained: 10, discarded: 0, captureComplete: true, resources: [], requests: [], scheduler: null, exportState: 'disabled', traceUrlTemplate: null },
  }
  await page.route('**/api/health', route => route.fulfill({ json: { status: 'healthy', databaseAvailable: true } }))
  await page.route('**/api/projects/*/active', route => route.fulfill({ json: { execution: null } }))
  await page.route('**/api/projects/*/executions?*', route => route.fulfill({ json: { items: [pruned], nextCursor: null } }))
  await page.route(`**/api/projects/*/executions/${executionId}`, route => route.fulfill({ json: pruned }))
  await page.goto('/')
  await page.getByRole('button', { name: /^Ver execução/ }).click()
  await expect(page.getByText('Detalhes individuais removidos pela retenção. 10 capturadas originalmente; 0 disponíveis atualmente.', { exact: true })).toBeVisible()
  await expect(page.getByText(/Cobertura da captura original: todos os terminais observados capturados/)).toBeVisible()
  await expect(page.getByText(/A cobertura original não indica quais detalhes ainda estão disponíveis/)).toBeVisible()
  await page.getByText('Requisições disponíveis (0)', { exact: true }).click()
  await expect(page.getByText('Sem requisições capturadas.', { exact: true })).toHaveCount(0)
})
