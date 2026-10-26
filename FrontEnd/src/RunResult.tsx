import { Check, CircleAlert, Clock3, Download, LoaderCircle, Radio } from 'lucide-react'
import type { Execution } from './api'
import { date, milliseconds, number, stateLabels } from './api'
import RunChart from './RunChart'
import RunDiagnostics from './RunDiagnostics'

export function StateBadge({ run }: { run: Execution }) {
  return <span className={`state-badge state-${run.state.toLowerCase()}`}>{run.state === 'Running' ? <span className="live-dot" /> : run.state === 'Completed' ? <Check size={12} /> : <CircleAlert size={12} />}{stateLabels[run.state]}</span>
}

export default function RunResult({ run, connected }: { run: Execution | null; connected: boolean }) {
  const running = run?.state === 'Running'
  const progress = run?.plan ? Math.min(100, run.elapsedSeconds / run.plan.durationSeconds * 100) : 0
  const exportResult = () => {
    if (!run) return
    const url = URL.createObjectURL(new Blob([JSON.stringify(run, null, 2)], { type: 'application/json' }))
    const link = document.createElement('a')
    link.href = url; link.download = `dinamico-${run.executionId}.json`; link.click()
    setTimeout(() => URL.revokeObjectURL(url), 1000)
  }
  const metrics = run?.metrics
  const success = metrics && metrics.httpCompleted > 0 ? metrics.httpSuccess / metrics.httpCompleted * 100 : null
  return <section className="panel result-panel" aria-label="Resultado da execução">
    <div className="panel-heading result-heading"><div><div className="eyebrow"><Radio size={14} /> {running ? 'AO VIVO' : 'RESULTADO'}</div><h2>{run ? 'Visão da execução' : 'Acompanhe o desempenho'}</h2></div>{run ? <StateBadge run={run} /> : <span className="state-badge state-idle"><span className="idle-dot" /> Aguardando teste</span>}</div>
    <div className="execution-progress">
      <div><span><Clock3 size={14} /> {run ? `${Math.floor(run.elapsedSeconds)}s` : '0s'} <small>/ {run?.plan?.durationSeconds ?? '—'}s</small></span><span>{run ? `${Math.floor(progress)}%` : 'Pronto para começar'}</span></div>
      <div className="progress-track"><div style={{ width: `${progress}%` }} /></div>
      {running && <p>{run && run.elapsedSeconds >= (run.plan?.durationSeconds ?? Infinity) ? 'Finalizando requisições e salvando o resultado…' : connected ? 'Conectado · métricas agregadas a cada 5 s' : 'Reconectando à execução…'}{!connected && <LoaderCircle size={12} className="spin" />}</p>}
    </div>
    <div className="metric-grid">
      <div className="metric"><span>Requisições iniciadas</span><strong>{metrics ? number(metrics.started) : '—'}</strong><small>{metrics ? `${number(metrics.planned)} planejadas` : 'Contagem observada no cliente'}</small></div>
      <div className="metric"><span>Latência P95</span><strong>{milliseconds(metrics?.p95Milliseconds)}</strong><small>Respostas HTTP completas</small></div>
      <div className={`metric ${metrics?.errors ? 'metric-warning' : ''}`}><span>Sem sucesso</span><strong>{metrics ? number(metrics.errors) : '—'}</strong><small>Não 2xx e falhas de operação</small></div>
      <div className={`metric ${metrics?.dropped ? 'metric-warning' : ''}`}><span>Não iniciadas</span><strong>{metrics ? number(metrics.dropped) : '—'}</strong><small>Descartes do gerador</small></div>
    </div>
    <RunChart run={run} />
    <div className="detail-metrics"><div><span>P50</span><strong>{milliseconds(metrics?.p50Milliseconds)}</strong></div><div><span>P99</span><strong>{milliseconds(metrics?.p99Milliseconds)}</strong></div><div><span>Em voo</span><strong>{metrics ? number(metrics.inFlight) : '—'}</strong></div><div><span>HTTP 429</span><strong>{metrics?.rateLimitedPercent == null ? '—' : `${metrics.rateLimitedPercent.toLocaleString('pt-BR', { maximumFractionDigits: 1 })}%`}</strong></div></div>
    {run && <RunDiagnostics run={run} />}
    {run && !running && <>
      {!run.dataComplete && <div className="notice warning-notice"><CircleAlert size={17} /><div><strong>Resultado parcial</strong><p>As métricas representam apenas os dados confirmados. Não extrapole os valores para toda a execução.</p></div></div>}
      {run.quality.includes('GeneratorLimited') && <div className="notice warning-notice"><CircleAlert size={17} /><div><strong>O gerador não atingiu a carga planejada</strong><p>Considere os descartes ao interpretar o desempenho do alvo.</p></div></div>}
      <div className="result-footer"><div><span className={`storage-indicator ${run.persistence === 'failed' ? 'storage-error' : ''}`}>{run.persistence === 'saved' ? <Check size={14} /> : run.persistence === 'failed' ? <CircleAlert size={14} /> : <LoaderCircle size={14} className="spin" />}{run.persistence === 'saved' ? 'Salvo no histórico' : run.persistence === 'failed' ? 'Falha ao salvar no banco' : 'Salvando resultado…'}</span><small>{date(run.startedUtc)}{success != null && ` · ${success.toLocaleString('pt-BR', { maximumFractionDigits: 1 })}% 2xx entre HTTP completos`}</small></div><button className="button secondary-button small-button" onClick={exportResult}><Download size={15} /> Baixar resultado</button></div>
      <p className="result-id" title={run.executionId}>Execução {run.executionId}</p>
    </>}
  </section>
}
