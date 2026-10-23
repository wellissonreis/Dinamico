import { useCallback, useEffect, useRef, useState } from 'react'
import { Activity, ArrowUpRight, Check, ChevronRight, CircleAlert, Database, Gauge, HelpCircle, History, Layers3, Plus, Settings2, ShieldCheck, X } from 'lucide-react'
import type { Execution, Health, HistoryPage, RunRequest } from './api'
import { api, ApiError, projectPath } from './api'
import TestForm from './TestForm'
import RunResult from './RunResult'
import HistoryTable from './HistoryTable'

const defaultProject = '11111111-1111-4111-8111-111111111111'
const uuidPattern = /^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i
function initialProject() { try { const stored = localStorage.getItem('dinamico.project'); return stored && uuidPattern.test(stored) ? stored : defaultProject } catch { return defaultProject } }

export default function App() {
  const [tab, setTab] = useState<'run' | 'history'>('run')
  const [project, setProject] = useState(initialProject)
  const [projectDraft, setProjectDraft] = useState(project)
  const [health, setHealth] = useState<Health | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [historyError, setHistoryError] = useState(false)
  const [run, setRun] = useState<Execution | null>(null)
  const [history, setHistory] = useState<Execution[]>([])
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [loadingHistory, setLoadingHistory] = useState(true)
  const [submitting, setSubmitting] = useState(false)
  const [connected, setConnected] = useState(false)
  const [help, setHelp] = useState(false)
  const dialog = useRef<HTMLDialogElement>(null)
  const settings = useRef<HTMLDetailsElement>(null)
  const running = run?.state === 'Running'
  const streaming = Boolean(run && (running || run.persistence === 'pending'))
  const selectedId = run?.executionId
  const projectVersion = useRef(project)
  projectVersion.current = project

  const loadHistory = useCallback(async (cursor?: string) => {
    setLoadingHistory(true)
    try {
      const page = await api<HistoryPage>(`${projectPath(project)}/executions?limit=20${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`)
      if (projectVersion.current !== project) return
      setHistory(previous => cursor ? [...previous, ...page.items.filter(item => !previous.some(existing => existing.executionId === item.executionId))] : page.items)
      setNextCursor(page.nextCursor); setHistoryError(false)
    } catch (cause) {
      if (projectVersion.current === project) { setHistoryError(true); setError(cause instanceof Error ? cause.message : 'Não foi possível carregar o histórico.') }
    } finally { if (projectVersion.current === project) setLoadingHistory(false) }
  }, [project])

  useEffect(() => {
    let disposed = false
    const check = async () => {
      try { const value = await api<Health>('/health'); if (!disposed) setHealth(value) }
      catch { if (!disposed) setHealth({ status: 'offline', databaseAvailable: false }) }
    }
    void check()
    const timer = setInterval(() => void check(), 15000)
    return () => { disposed = true; clearInterval(timer) }
  }, [])

  useEffect(() => {
    let disposed = false
    setHistory([]); setNextCursor(null); setRun(null)
    void loadHistory()
    void api<{ execution: Execution | null }>(`${projectPath(project)}/active`).then(value => {
      if (!disposed && value.execution) { setRun(value.execution); setTab('run') }
    }).catch(() => { /* The health and history states already communicate connectivity. */ })
    return () => { disposed = true }
  }, [project, loadHistory])

  useEffect(() => {
    if (!streaming || !selectedId) return
    const events = new EventSource(`/api${projectPath(project)}/executions/${selectedId}/events`)
    events.onopen = () => setConnected(true)
    events.onerror = () => setConnected(false)
    const snapshot = (event: MessageEvent<string>) => {
      try {
        const value = JSON.parse(event.data) as Execution
        if (value.executionId !== selectedId || value.projectId !== project) return
        setRun(value)
        if (value.state !== 'Running' && value.persistence !== 'pending') { events.close(); setConnected(false); void loadHistory() }
      } catch { setError('A atualização recebida é inválida. Recarregue a execução.') }
    }
    events.addEventListener('snapshot', snapshot)
    return () => { events.close(); setConnected(false) }
  }, [streaming, selectedId, project, loadHistory])

  useEffect(() => { if (help && dialog.current && !dialog.current.open) dialog.current.showModal() }, [help])

  const start = async (request: RunRequest) => {
    setError(null); setSubmitting(true)
    try {
      const value = await api<Execution>(`${projectPath(project)}/executions`, { method: 'POST', body: JSON.stringify(request) })
      setRun(value); setTab('run')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Não foi possível iniciar o teste.')
      if (cause instanceof ApiError && cause.status === 409) {
        const active = await api<{ execution: Execution | null }>(`${projectPath(project)}/active`).catch(() => null)
        if (active?.execution) setRun(active.execution)
      }
    } finally { setSubmitting(false) }
  }
  const cancel = async () => {
    if (!run) return
    setSubmitting(true); setError(null)
    try { setRun(await api<Execution>(`${projectPath(project)}/executions/${run.executionId}/cancel`, { method: 'POST' })) }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Não foi possível interromper o teste.') }
    finally { setSubmitting(false) }
  }
  const selectRun = async (selected: Execution) => {
    if (running && selected.executionId !== run.executionId) { setError('Aguarde ou interrompa o teste atual antes de abrir outra execução.'); return }
    setError(null)
    try {
      const detailed = await api<Execution>(`${projectPath(project)}/executions/${selected.executionId}`)
      setRun(detailed); setTab('run'); window.scrollTo({ top: 0, behavior: 'smooth' })
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Não foi possível abrir a execução.') }
  }
  const changeProject = (event: React.SubmitEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (running) return
    if (!uuidPattern.test(projectDraft) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(projectDraft)) { setError('Informe um UUID de projeto válido.'); return }
    try { localStorage.setItem('dinamico.project', projectDraft) } catch { /* Project remains usable in memory. */ }
    setProject(projectDraft.toLowerCase()); setError(null)
    if (settings.current) settings.current.open = false
  }

  return <div className="app-shell">
    <aside className="sidebar">
      <a className="brand" href="#" onClick={event => { event.preventDefault(); setTab('run') }} aria-label="Dinamico, início"><span className="brand-mark"><Activity size={23} /></span><span>dinamico<span className="brand-period">.</span></span></a>
      <div className="workspace-label">WORKSPACE LOCAL</div>
      <nav aria-label="Navegação principal"><button className={tab === 'run' ? 'nav-item active' : 'nav-item'} aria-current={tab === 'run' ? 'page' : undefined} onClick={() => setTab('run')}><Gauge size={18} /> Executar teste{running && <span className="live-dot" />}</button><button className={tab === 'history' ? 'nav-item active' : 'nav-item'} aria-current={tab === 'history' ? 'page' : undefined} onClick={() => setTab('history')}><History size={18} /> Histórico{history.length > 0 && <span className="nav-count">{history.length}{nextCursor ? '+' : ''}</span>}</button></nav>
      <div className="sidebar-bottom"><div className="local-card"><span className="local-icon"><Layers3 size={18} /></span><div><strong>Ambiente local</strong><span>HTTP/1.1 · loopback</span></div></div><div className={`database-status ${health?.databaseAvailable ? '' : 'offline'}`}><span className="status-dot" />{health === null ? 'Verificando conexão…' : health.databaseAvailable ? 'PostgreSQL conectado' : 'Banco indisponível'}</div><span className="app-version">Dinamico / painel local</span></div>
    </aside>
    <div className="main-shell">
      <header className="topbar"><div className="breadcrumbs"><span>Workspace</span><ChevronRight size={13} /><strong>Projeto local</strong></div><div className="topbar-actions"><span className="local-pill"><span className="status-dot" /> Local</span><details className="project-settings" ref={settings}><summary className="icon-button" aria-label="Configurar projeto" title="Configurar projeto"><Settings2 size={18} /></summary><form className="settings-popover" onSubmit={changeProject}><h3>Projeto local</h3><label htmlFor="project-id">Identificador do projeto</label><input id="project-id" value={projectDraft} onChange={event => setProjectDraft(event.target.value)} disabled={running} spellCheck="false" required /><p>O histórico é separado por este UUID. Ele não é uma credencial de acesso.</p><button className="button primary-button small-button" disabled={running}>Aplicar projeto</button></form></details><button className="icon-button" aria-label="Ajuda" title="Ajuda" onClick={() => setHelp(true)}><HelpCircle size={18} /></button></div></header>
      <main>
        <div className="page-heading"><div><div className="eyebrow">{tab === 'run' ? 'TESTES DE DESEMPENHO' : 'RESULTADOS DO PROJETO'}</div><h1>{tab === 'run' ? 'Conheça os limites da sua aplicação.' : 'Cada execução, uma nova descoberta.'}</h1><p>{tab === 'run' ? 'Configure a carga, acompanhe as respostas e entenda o resultado.' : 'Consulte seus testes anteriores e retome a análise dos resultados.'}</p></div>{tab === 'history' && <button className="button primary-button" onClick={() => setTab('run')}><Plus size={17} /> Novo teste</button>}</div>
        {error && <div className="notice error-notice" role="alert"><CircleAlert size={18} /><p>{error}</p><button className="icon-button" aria-label="Fechar mensagem" onClick={() => setError(null)}><X size={16} /></button></div>}
        {health && !health.databaseAvailable && <div className="notice warning-notice"><Database size={18} /><div><strong>{health.status === 'offline' ? 'Servidor local indisponível' : 'O banco de dados não está disponível'}</strong><p>{health.status === 'offline' ? 'Inicie o servidor Dinamico para executar testes e consultar o histórico.' : 'Confira a conexão com o PostgreSQL. Novos testes ficarão disponíveis após a reconexão.'}</p></div></div>}
        {tab === 'run' ? <>
          <div className="workbench"><TestForm running={running} submitting={submitting} databaseAvailable={health?.databaseAvailable ?? false} onStart={start} onCancel={() => void cancel()} /><RunResult run={run} connected={connected} /></div>
          <HistoryTable items={history.slice(0, 5)} loading={loadingHistory} nextCursor={null} full={false} onSelect={selected => void selectRun(selected)} onRefresh={() => void loadHistory()} onMore={() => setTab('history')} />
          {history.length > 5 && <button className="all-history text-button" onClick={() => setTab('history')}>Ver todo o histórico <ArrowUpRight size={14} /></button>}
        </> : <HistoryTable items={history} loading={loadingHistory} nextCursor={nextCursor} full onSelect={selected => void selectRun(selected)} onRefresh={() => void loadHistory()} onMore={() => void loadHistory(nextCursor ?? undefined)} />}
        {historyError && <p className="history-error">O histórico não pôde ser atualizado. Use “Atualizar” para tentar novamente.</p>}
        <footer className="page-footer"><span><ShieldCheck size={14} /> Tráfego restrito ao seu ambiente local.</span><span>Dados medidos no cliente · latências aproximadas</span></footer>
      </main>
    </div>
    {help && <dialog ref={dialog} onClose={() => setHelp(false)} className="help-dialog"><div className="dialog-heading"><span className="brand-mark"><Activity size={22} /></span><h2>Um teste simples. Dados claros.</h2><button className="icon-button" aria-label="Fechar ajuda" onClick={() => dialog.current?.close()}><X size={18} /></button></div><ol><li><strong>Prepare o alvo.</strong> Inicie sua aplicação e informe um endereço com IP loopback literal, como http://127.0.0.1:5000/.</li><li><strong>Comece pequeno.</strong> Use 10 req/s e observe se o gerador consegue manter a taxa sem descartes.</li><li><strong>Leia o resultado.</strong> P95 significa que aproximadamente 95% das respostas HTTP completas ficaram abaixo desse limite.</li></ol><div className="notice warning-notice"><CircleAlert size={17} /><p>Dados parciais e descartes mudam a interpretação. Um HTTP 2xx não comprova a conclusão da operação de negócio.</p></div><p className="help-note">Este painel é local, sem login ou execução distribuída. O perfil disponível é constante. A exportação contém a visão do painel; não substitui o relatório canônico de importação da CLI.</p><button className="button primary-button" onClick={() => dialog.current?.close()}><Check size={16} /> Entendi</button></dialog>}
  </div>
}
