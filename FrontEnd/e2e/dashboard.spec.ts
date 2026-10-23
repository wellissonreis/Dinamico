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

test('cancelamento real e rejeição de alvo externo com mensagem clara', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('button', { name: 'Iniciar teste', exact: true })).toBeEnabled()
  await page.getByLabel('Endpoint', { exact: false }).fill('http://example.com/')
  await page.getByRole('button', { name: 'Iniciar teste', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('IP loopback literal')
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
