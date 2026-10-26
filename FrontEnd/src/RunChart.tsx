import { useState } from 'react'
import { Activity } from 'lucide-react'
import type { Execution, ResourceSample, WindowMetrics } from './api'

const valueLabel = (value: number | null | undefined) => value == null ? 'Sem dados' : value.toLocaleString('pt-BR', { maximumFractionDigits: 2 })
type Series = { name: string; values: (number | null)[]; color?: string }
const colors = ['#0d8175', '#526ec5', '#c0792b', '#af5063', '#687985', '#854ba7', '#487e21', '#946444']
export default function RunChart({ run }: { run: Execution | null }) {
  const [metric, setMetric] = useState('rate')
  const windows = run?.windows ?? []
  const resources = run?.diagnostics?.resources ?? []
  const perSecond = (window: WindowMetrics, value: number | undefined) => value == null ? null : value / Math.max(0.0001, window.endSeconds - window.startSeconds)
  const resource = (field: keyof ResourceSample, scale = 1) => windows.map(window => {
    const sample = resources.find(item => item.windowIndex === window.index)
    const value = sample?.[field]
    return typeof value === 'number' ? value / scale : null
  })
  let unit = 'req/s'
  let series: Series[]
  if (metric === 'rate') series = [
    { name: 'Planejada', values: windows.map(w => run?.plan ? run.plan.requestsPerSecond * Math.max(0, Math.min(w.endSeconds, run.plan.durationSeconds) - Math.max(0, w.startSeconds)) / Math.max(0.0001, w.endSeconds - w.startSeconds) : null) },
    { name: 'Iniciada', values: windows.map(w => perSecond(w, w.started)) },
    { name: 'HTTP completa', values: windows.map(w => perSecond(w, w.httpCompleted)) },
    { name: 'HTTP 2xx', values: windows.map(w => perSecond(w, w.httpSuccess)) },
  ]
  else if (metric === 'latency') {
    unit = 'ms'
    series = [
      { name: 'HTTP P50', values: windows.map(w => w.p50Milliseconds ?? null) },
      { name: 'HTTP P95', values: windows.map(w => w.p95Milliseconds) },
      { name: 'HTTP P99', values: windows.map(w => w.p99Milliseconds ?? null) },
      { name: 'HTTP mínimo', values: windows.map(w => w.minMilliseconds ?? null) },
      { name: 'HTTP máximo', values: windows.map(w => w.maxMilliseconds ?? null) },
      { name: 'Terminal P95 (inclui falhas)', values: windows.map(w => w.terminalP95Milliseconds ?? null) },
    ]
  } else if (metric === 'drops') {
    unit = 'requisições'
    const causes = { schedulerLate: 'Atraso do scheduler', concurrencyLimit: 'Limite de concorrência', policy: 'Política', transportFailed: 'Transporte', requestTimedOut: 'Timeout', cancelledInFlight: 'Cancelamento em voo', responseLimitExceeded: 'Limite de resposta', internalFailed: 'Falha interna' } as const
    series = Object.entries(causes).map(([key, name]) => ({ name, values: windows.map(w => w.breakdown?.[key as keyof typeof causes] ?? null) }))
  } else if (metric === 'memory') {
    unit = 'MiB do gerador'
    series = [{ name: 'Working set', values: resource('workingSetBytes', 1048576) }, { name: 'Heap gerenciado', values: resource('managedHeapBytes', 1048576) }]
  } else if (metric === 'cpu') {
    unit = '% de um núcleo · gerador'
    series = [{ name: 'CPU', values: resource('cpuPercentOfOneCore') }]
  } else if (metric === 'queue') {
    unit = 'ms · gerador'
    series = [{ name: 'Fila HTTP máxima desde início', values: resource('httpQueueMaxMilliseconds') }]
  } else {
    unit = 'quantidade · gerador'
    series = [{ name: 'ThreadPool pendentes', values: resource('threadPoolPending') }, { name: 'Threads', values: resource('threadPoolThreads') }, { name: 'Conexões HTTP', values: resource('httpOpenConnections') }]
  }
  series = series.map((item, index) => ({ ...item, color: colors[index % colors.length] }))
  const hasData = series.some(item => item.values.some(value => value != null))
  const resourceMetric = ['memory', 'cpu', 'queue', 'threads'].includes(metric)
  const pointTime = (index: number) => resourceMetric ? resources.find(item => item.windowIndex === windows[index].index)?.elapsedSeconds ?? windows[index].endSeconds : windows[index].endSeconds
  const maximumTime = Math.max(run?.plan?.durationSeconds ?? 30, ...windows.map(w => w.endSeconds), ...resources.map(sample => sample.elapsedSeconds), 1)
  const maximum = Math.max(...series.flatMap(item => item.values.filter((v): v is number => v != null).map(v => v * 1.15)), 1)
  const x = (second: number) => 48 + second / maximumTime * 560
  const minimum = Math.min(0, ...series.flatMap(item => item.values.filter((v): v is number => v != null)))
  const axisValue = (index: number) => minimum + (maximum - minimum) * index / 4
  const y = (value: number) => 174 - (value - minimum) / (maximum - minimum) * 138
  return <section className="chart-section" aria-label="Gráfico da execução">
    <div className="section-heading"><h3><Activity size={16} /> Evolução do teste</h3><select aria-label="Métrica do gráfico" className="compact-select" value={metric} onChange={event => setMetric(event.target.value)}>
      <option value="rate">Requisições / s</option><option value="latency">Latências</option><option value="drops">Descartes e falhas</option><option value="memory">Memória do gerador</option><option value="cpu">CPU do gerador</option><option value="queue">Fila HTTP do gerador</option><option value="threads">Threads e conexões do gerador</option>
    </select></div>
    <p className="chart-note">{unit}. Cada ponto usa o intervalo real da janela. Lacunas significam dados indisponíveis.{metric === 'latency' && ' Percentis por janela; não são o percentil global. HTTP considera somente respostas completas; terminal inclui falhas.'}</p>
    <div className="chart-wrap"><svg viewBox="0 0 640 206" role="img" aria-label={hasData ? `${windows.length} janelas de ${metric === 'rate' ? 'requisições por segundo' : unit}` : 'Sem dados da execução'}>
      <title>Evolução por janela em {unit}; detalhes disponíveis na tabela</title>
      {[0, 1, 2, 3, 4].map(index => <g key={index}><line x1="48" x2="608" y1={y(axisValue(index))} y2={y(axisValue(index))} stroke="#e9eef2" strokeDasharray="3 4" /><text x="38" y={y(axisValue(index)) + 4} textAnchor="end">{valueLabel(axisValue(index))}</text><text x={x(maximumTime * index / 4)} y="197" textAnchor="middle">{Math.round(maximumTime * index / 4)}s</text></g>)}
      {series.map(item => <g key={item.name}>{item.values.map((value, index) => {
        if (value == null) return null
        const previous = index ? item.values[index - 1] : null
        return <g key={index}>{previous != null && <line x1={x(pointTime(index - 1))} y1={y(previous)} x2={x(pointTime(index))} y2={y(value)} stroke={item.color} strokeWidth="2" />}<circle cx={x(pointTime(index))} cy={y(value)} r="3" fill={item.color}><title>{windows[index].startSeconds}–{windows[index].endSeconds} s · medido em {pointTime(index).toFixed(3)} s · {item.name}: {valueLabel(value)} {unit}</title></circle></g>
      })}</g>)}
    </svg>{!hasData && <div className="chart-empty"><span><Activity size={20} /></span><strong>{run ? 'Sem dados para esta métrica' : 'Seu próximo teste começa aqui'}</strong><p>{run?.state === 'Running' ? 'Aguardando as primeiras janelas.' : run ? 'Esta execução não possui os dados selecionados.' : 'Inicie uma execução para visualizar o desempenho.'}</p></div>}</div>
    <div className="chart-legend">{series.map(item => <span key={item.name}><i className="legend-dot" style={{ background: item.color }} />{item.name}</span>)}</div>
    {windows.length > 0 && <details className="window-details"><summary>Consultar tabela por janela</summary><div className="table-scroll"><table><caption>Valores em {unit}. Sem dados indica ausência, nunca zero presumido.</caption><thead><tr><th>Intervalo (s)</th>{series.map(item => <th key={item.name}>{item.name}</th>)}<th>HTTP completos</th><th>Terminais</th><th>Em voo</th><th>Status HTTP</th><th>Qualidade</th></tr></thead><tbody>{windows.map((window, index) => <tr key={window.index}><th scope="row">{window.startSeconds}–{window.endSeconds}</th>{series.map(item => <td key={item.name}>{valueLabel(item.values[index])}</td>)}<td>{valueLabel(window.httpCount ?? window.httpCompleted)}</td><td>{valueLabel(window.terminalCount)}</td><td>{valueLabel(window.inFlight)}</td><td>{window.breakdown ? Object.entries(window.breakdown.statuses).map(([status, count]) => `${status}: ${count}`).join(' · ') || 'Nenhum' : 'Sem dados'}</td><td>{window.quality?.join(', ') || (window.quality ? 'Sem alertas' : 'Sem dados')}</td></tr>)}</tbody></table></div></details>}
  </section>
}
