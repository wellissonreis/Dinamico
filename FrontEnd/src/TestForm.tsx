import { useState } from 'react'
import { ChevronDown, Info, LoaderCircle, Play, Settings2, Square } from 'lucide-react'
import type { RunRequest } from './api'
import { number } from './api'

const initial = {
  url: 'http://127.0.0.1:5000/', method: 'GET', requestsPerSecond: 10, durationSeconds: 30,
  maxConcurrency: 32, requestTimeoutSeconds: 2, rateLimitMode: 'Normal', headers: '', payload: '',
  captureMode: 'Off' as RunRequest['captureMode'], correlateRequests: false,
  testCredentialHeaderIndex: 0, testAuthorizationDeclared: false,
}

export default function TestForm({ running, submitting, databaseAvailable, onStart, onCancel }: {
  running: boolean; submitting: boolean; databaseAvailable: boolean
  onStart: (request: RunRequest) => Promise<void>; onCancel: () => void
}) {
  const [form, setForm] = useState(initial)
  const [error, setError] = useState<string | null>(null)
  const set = <K extends keyof typeof initial>(name: K, value: typeof initial[K]) => setForm(previous => ({ ...previous, [name]: value }))
  const submit = async (event: React.SubmitEvent<HTMLFormElement>) => {
    event.preventDefault()
    setError(null)
    if (form.captureMode === 'Complete' && form.requestsPerSecond * form.durationSeconds > 2000) { setError('A captura completa permite até 2.000 requisições planejadas. Reduza a taxa ou duração, ou escolha amostragem.'); return }
    if (form.correlateRequests && form.captureMode === 'Off') { setError('Para correlacionar requisições, habilite a captura amostrada ou completa.'); return }
    const headers: RunRequest['headers'] = []
    for (const line of form.headers.split('\n').filter(line => line.trim())) {
      const separator = line.indexOf(':')
      if (separator < 1) { setError('Use um header por linha, no formato Nome: valor.'); return }
      headers.push({ name: line.slice(0, separator).trim(), value: line.slice(separator + 1).trim() })
    }
    if (new TextEncoder().encode(form.payload).length > 65536) { setError('O corpo pode ter até 64 KiB.'); return }
    if (form.rateLimitMode === 'Authorized' && (headers.length === 0 || form.testCredentialHeaderIndex >= headers.length)) {
      setError('Informe o header da credencial e um índice válido para o modo autorizado.'); return
    }
    await onStart({ ...form, headers, testCredentialHeaderIndex: form.rateLimitMode === 'Authorized' ? form.testCredentialHeaderIndex : null })
  }
  return <section className="panel config-panel">
    <div className="panel-heading"><div className="step-number">01</div><div><h2>Configure seu teste</h2><p>Um endpoint. Uma carga constante.</p></div></div>
    <form onSubmit={submit}>
      <fieldset disabled={running || submitting}>
        <label htmlFor="endpoint">Endpoint <span>obrigatório</span></label>
        <div className="endpoint-group"><select aria-label="Método HTTP" value={form.method} onChange={event => set('method', event.target.value)}>{['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'].map(method => <option key={method}>{method}</option>)}</select><input id="endpoint" type="url" required autoComplete="off" spellCheck="false" value={form.url} onChange={event => set('url', event.target.value)} placeholder="http://127.0.0.1:5000/" /></div>
        <p className="field-hint">Use um IP local: 127.0.0.1 ou [::1].</p>
        <div className="form-divider" />
        <div className="load-label"><label>Perfil de carga</label><span className="subtle-tag">Constante</span></div>
        <div className="three-fields">
          <div><label htmlFor="rps">Taxa</label><div className="unit-input"><input id="rps" type="number" required min="1" max="1000" step="1" value={form.requestsPerSecond} onChange={event => set('requestsPerSecond', Number(event.target.value))} /><span>req/s</span></div></div>
          <div><label htmlFor="duration">Duração</label><div className="unit-input"><input id="duration" type="number" required min="1" max="600" step="1" value={form.durationSeconds} onChange={event => set('durationSeconds', Number(event.target.value))} /><span>s</span></div></div>
          <div><label htmlFor="concurrency">Concorrência</label><input id="concurrency" type="number" required min="1" max="256" step="1" value={form.maxConcurrency} onChange={event => set('maxConcurrency', Number(event.target.value))} /></div>
        </div>
        <div className="estimate"><span className="tiny-pulse" /><span><strong>{number(form.requestsPerSecond * form.durationSeconds || 0)}</strong> requisições planejadas</span></div>
        <details className="advanced"><summary><Settings2 size={15} /> Opções avançadas <ChevronDown size={15} /></summary><div className="advanced-content">
          <div className="two-fields"><div><label htmlFor="timeout">Timeout por requisição</label><div className="unit-input"><input id="timeout" type="number" required min="0.001" max="60" step="0.001" value={form.requestTimeoutSeconds} onChange={event => set('requestTimeoutSeconds', Number(event.target.value))} /><span>s</span></div></div><div><label htmlFor="mode">Modo</label><select id="mode" value={form.rateLimitMode} onChange={event => set('rateLimitMode', event.target.value)}><option value="Normal">Normal</option><option value="Diagnostic">Diagnóstico</option><option value="Authorized">Autorizado</option></select></div></div>
          <label htmlFor="capture">Captura de requisições</label><select id="capture" value={form.captureMode} onChange={event => set('captureMode', event.target.value as RunRequest['captureMode'])}><option value="Off">Desligada</option><option value="Sampled">Amostrada</option><option value="Complete">Completa · até 2.000 planejadas</option></select>
          <p className="field-hint">Diagnóstico opcional tem custo de coleta. A amostra é limitada e não representa cada requisição. Os contadores agregados permanecem independentes da captura.</p>
          <label className="check-label"><input type="checkbox" checked={form.correlateRequests} onChange={event => set('correlateRequests', event.target.checked)} /> Correlacionar requisições com traces</label>
          <p className="field-hint">Envia contexto de trace ao alvo quando habilitado. No modo amostrado, até 1.000 requisições recebem correlação; nem toda operação capturada terá trace. Na captura completa, o limite é 2.000 planejadas. Para investigar SQL, locks e dependências, instrumente a aplicação com OpenTelemetry e configure o backend de traces no host. A duração até headers inclui rede e espera do cliente.</p>
          <label htmlFor="headers">Headers <span>opcional</span></label><textarea id="headers" autoComplete="off" spellCheck="false" rows={3} value={form.headers} onChange={event => set('headers', event.target.value)} placeholder={'Content-Type: application/json'} /><p className="field-hint">Um por linha. Use apenas credenciais sintéticas no laboratório local. Os valores ficam somente nesta sessão.</p>
          {form.rateLimitMode === 'Authorized' && <div className="authorization"><label htmlFor="credential-index">Índice do header da credencial (começa em 0)</label><input id="credential-index" type="number" required min="0" step="1" value={form.testCredentialHeaderIndex} onChange={event => set('testCredentialHeaderIndex', Number(event.target.value))} /><label className="check-label"><input type="checkbox" required checked={form.testAuthorizationDeclared} onChange={event => set('testAuthorizationDeclared', event.target.checked)} /> Declaro que tenho autorização para este teste.</label><p className="field-hint">A declaração não altera a taxa nem remove limites.</p></div>}
          <label htmlFor="payload">Corpo da requisição <span>opcional · até 64 KiB</span></label><textarea id="payload" spellCheck="false" rows={3} value={form.payload} onChange={event => set('payload', event.target.value)} placeholder={'{"exemplo": true}'} />
          <button className="text-button clear-sensitive" type="button" onClick={() => setForm(previous => ({ ...previous, headers: '', payload: '', testAuthorizationDeclared: false }))}>Limpar headers e corpo</button>
        </div></details>
      </fieldset>
      {error && <p className="form-error" role="alert">{error}</p>}
      {running ? <button className="button cancel-button" type="button" disabled={submitting} onClick={onCancel}>{submitting ? <LoaderCircle size={17} className="spin" /> : <Square size={15} />} Interromper teste</button> : <button className="button primary-button" type="submit" disabled={submitting || !databaseAvailable}>{submitting ? <LoaderCircle size={17} className="spin" /> : <Play size={17} fill="currentColor" />} {submitting ? 'Iniciando…' : 'Iniciar teste'}</button>}
      <div className="config-footnote"><Info size={14} /><span>Somente alvos locais. Sem retries automáticos.</span></div>
    </form>
  </section>
}
