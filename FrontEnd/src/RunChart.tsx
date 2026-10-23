import { useState } from 'react'
import { Activity } from 'lucide-react'
import type { Execution } from './api'

export default function RunChart({ run }: { run: Execution | null }) {
  const [metric, setMetric] = useState('rate')
  const windows = run?.windows ?? []
  const points = windows.map(window => ({
    second: window.endSeconds,
    value: metric === 'rate'
      ? window.started / Math.max(0.0001, window.endSeconds - window.startSeconds)
      : window.p95Milliseconds,
  })).filter((point): point is { second: number; value: number } => point.value !== null)
  const maximumTime = Math.max(run?.plan?.durationSeconds ?? 30, ...windows.map(window => window.endSeconds), 1)
  const target = metric === 'rate' ? run?.plan?.requestsPerSecond ?? 10 : 0
  const maximum = Math.max(target * 1.2, ...points.map(point => point.value * 1.2), 1)
  const x = (second: number) => 48 + second / maximumTime * 560
  const y = (value: number) => 174 - value / maximum * 138
  const line = points.map(point => `${x(point.second)},${y(point.value)}`).join(' ')
  const unit = metric === 'rate' ? 'req/s' : 'ms'
  return <section className="chart-section" aria-label="Gráfico da execução">
    <div className="section-heading">
      <h3><Activity size={16} /> Evolução do teste</h3>
      <select aria-label="Métrica do gráfico" className="compact-select" value={metric} onChange={event => setMetric(event.target.value)}>
        <option value="rate">Requisições / s</option><option value="latency">Latência P95</option>
      </select>
    </div>
    <div className="chart-wrap">
      <svg viewBox="0 0 640 206" role="img" aria-label={points.length ? `${points.length} janelas de ${metric === 'rate' ? 'requisições iniciadas por segundo' : 'latência P95 em milissegundos'}` : 'Aguardando dados da execução'}>
        <title>{metric === 'rate' ? 'Taxa iniciada por janela, comparada à taxa planejada' : 'P95 da duração HTTP por janela; não é o percentil global'}</title>
        <defs><linearGradient id="chart-fill" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor="#0d9488" stopOpacity=".18" /><stop offset="100%" stopColor="#0d9488" stopOpacity="0" /></linearGradient></defs>
        {[0, 1, 2, 3, 4].map(index => {
          const value = maximum * index / 4
          return <g key={index}><line x1="48" x2="608" y1={y(value)} y2={y(value)} stroke="#e9eef2" strokeDasharray="3 4" /><text x="38" y={y(value) + 4} textAnchor="end">{value.toLocaleString('pt-BR', { maximumFractionDigits: 1 })}</text></g>
        })}
        {[0, 1, 2, 3, 4].map(index => <text key={index} x={x(maximumTime * index / 4)} y="197" textAnchor="middle">{Math.round(maximumTime * index / 4)}s</text>)}
        {points.length > 0 && metric === 'rate' && <line x1="48" x2="608" y1={y(target)} y2={y(target)} stroke="#94a3b8" strokeDasharray="5 5" />}
        {points.length > 0 && <>
          <polygon points={`${x(points[0].second)},174 ${line} ${x(points[points.length - 1].second)},174`} fill="url(#chart-fill)" />
          <polyline points={line} fill="none" stroke="#0d9488" strokeWidth="2.5" strokeLinejoin="round" />
          {points.map((point, index) => <circle key={index} cx={x(point.second)} cy={y(point.value)} r="3.5" fill="#0d9488" stroke="white" strokeWidth="1.5"><title>{point.second.toFixed(1)} s: {point.value.toLocaleString('pt-BR', { maximumFractionDigits: 2 })} {unit}</title></circle>)}
        </>}
      </svg>
      {points.length === 0 && <div className="chart-empty"><span><Activity size={20} /></span><strong>{run?.state === 'Running' ? 'Preparando as primeiras métricas' : 'Seu próximo teste começa aqui'}</strong><p>{run?.state === 'Running' ? 'Os dados chegam em janelas de até 5 segundos.' : 'Inicie uma execução para visualizar o desempenho.'}</p></div>}
    </div>
    <div className="chart-legend"><span><i className="legend-dot" /> {metric === 'rate' ? 'Taxa iniciada' : 'P95 por janela'}</span>{metric === 'rate' && <span><i className="legend-dash" /> Taxa planejada</span>}<small>{windows.length} {windows.length === 1 ? 'janela recebida' : 'janelas recebidas'}</small></div>
  </section>
}
