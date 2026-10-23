import { ArrowUpRight, History, LoaderCircle, RefreshCw } from 'lucide-react'
import type { Execution } from './api'
import { date, milliseconds, number } from './api'
import { StateBadge } from './RunResult'

export default function HistoryTable({ items, loading, nextCursor, full, onSelect, onRefresh, onMore }: {
  items: Execution[]; loading: boolean; nextCursor: string | null; full: boolean
  onSelect: (run: Execution) => void; onRefresh: () => void; onMore: () => void
}) {
  return <section className="panel history-panel">
    <div className="history-heading"><div><h2>{full ? 'Histórico de execuções' : 'Execuções recentes'}</h2><p>{full ? 'Resultados salvos neste projeto local.' : 'Seus últimos testes, em um só lugar.'}</p></div><button className="button secondary-button small-button" disabled={loading} onClick={onRefresh} aria-label="Atualizar histórico"><RefreshCw size={14} className={loading ? 'spin' : ''} /><span>Atualizar</span></button></div>
    {items.length ? <div className="table-scroll"><table><thead><tr><th>Execução</th><th>Estado</th><th>Perfil</th><th>Iniciadas</th><th>P95</th><th><span className="sr-only">Detalhes</span></th></tr></thead><tbody>{items.map(run => <tr key={run.executionId}><td><button className="execution-link" onClick={() => onSelect(run)}><strong>{run.executionId.slice(0, 8)}</strong><span>{date(run.startedUtc)}</span></button></td><td><StateBadge run={run} />{!run.dataComplete && <small className="partial-label">Dados parciais</small>}</td><td>{run.plan ? <><strong>{number(run.plan.requestsPerSecond)} <span className="normal-weight">req/s</span></strong><small>{run.plan.durationSeconds}s · {run.plan.method}</small></> : '—'}</td><td>{number(run.metrics.started)}</td><td>{milliseconds(run.metrics.p95Milliseconds)}</td><td><button className="icon-button" onClick={() => onSelect(run)} aria-label={`Ver execução ${run.executionId.slice(0, 8)}`}><ArrowUpRight size={17} /></button></td></tr>)}</tbody></table></div> : <div className="empty-history"><span className="empty-icon">{loading ? <LoaderCircle size={22} className="spin" /> : <History size={23} />}</span><strong>{loading ? 'Buscando execuções…' : 'Seu histórico começa no primeiro teste'}</strong><p>Os resultados salvos aparecerão aqui para você comparar.</p></div>}
    {nextCursor && <div className="table-footer"><button className="button secondary-button small-button" disabled={loading} onClick={onMore}>{loading ? 'Carregando…' : 'Carregar mais execuções'}</button></div>}
  </section>
}
