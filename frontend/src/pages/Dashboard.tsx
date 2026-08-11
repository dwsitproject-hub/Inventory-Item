import { useEffect, useState } from 'react'
import { dashboard } from '../api'

type Summary = {
  kpis: { documents: number; lines: number; filesLoaded: number; quarantined: number }
  latestIngestions: {
    id: number; fileName: string; template: string; source: string; status: string
    rowsTotal: number; rowsLoaded: number; rowsQuarantined: number; receivedAt: string
  }[]
  trend: { month: string; template: string; lines: number }[]
}

const badge = (s: string) =>
  s === 'loaded' ? 'b-ok' : s === 'partial' ? 'b-warn' : 'b-err'

export default function Dashboard() {
  const [data, setData] = useState<Summary | null>(null)
  const [error, setError] = useState('')

  useEffect(() => { dashboard().then(setData).catch(e => setError(e.message)) }, [])

  if (error) return <div className="err">{error}</div>
  if (!data) return <div className="loading"><span className="spin" />loading dashboard…</div>

  const months = [...new Set(data.trend.map(t => t.month))].sort()
  const max = Math.max(1, ...data.trend.map(t => t.lines))

  return (
    <>
      <h1 className="page">Dashboard</h1>
      <div className="crumb">Home · Overview (scoped to your access)</div>

      <div className="kpis">
        <div className="kpi"><div className="l">Customs documents</div><div className="v">{data.kpis.documents.toLocaleString()}</div></div>
        <div className="kpi"><div className="l">Report lines</div><div className="v">{data.kpis.lines.toLocaleString()}</div></div>
        <div className="kpi"><div className="l">Files loaded</div><div className="v">{data.kpis.filesLoaded.toLocaleString()}</div></div>
        <div className="kpi"><div className="l">Quarantined rows</div><div className="v" style={{ color: data.kpis.quarantined > 0 ? 'var(--warn)' : undefined }}>{data.kpis.quarantined.toLocaleString()}</div></div>
      </div>

      <div className="row2">
        <div className="panel">
          <h3>Lines per month (by template)</h3>
          <div style={{ display: 'flex', alignItems: 'flex-end', gap: 14, height: 150, paddingTop: 8 }}>
            {months.map(m => (
              <div key={m} style={{ flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 5 }}>
                <div style={{ display: 'flex', gap: 3, alignItems: 'flex-end', height: 120 }}>
                  {['BC23', 'BC40'].map(tpl => {
                    const v = data.trend.find(t => t.month === m && t.template === tpl)?.lines ?? 0
                    return <div key={tpl} title={`${tpl}: ${v}`} style={{ width: 14, height: Math.max(2, v / max * 120), background: tpl === 'BC23' ? 'var(--steel)' : 'var(--amber)' }} />
                  })}
                </div>
                <small style={{ fontSize: 10.5, color: 'var(--muted)' }}>{m}</small>
              </div>
            ))}
            {months.length === 0 && <div className="loading">no dated lines yet</div>}
          </div>
          <div style={{ display: 'flex', gap: 16, fontSize: 11.5, color: 'var(--muted)', marginTop: 8 }}>
            <span><i style={{ display: 'inline-block', width: 10, height: 10, background: 'var(--steel)', marginRight: 5 }} />BC23 (PIB)</span>
            <span><i style={{ display: 'inline-block', width: 10, height: 10, background: 'var(--amber)', marginRight: 5 }} />BC40</span>
          </div>
        </div>
        <div className="panel">
          <h3>Latest ingestion</h3>
          {data.latestIngestions.map(f => (
            <div className="item" key={f.id}>
              <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{f.fileName} <small style={{ color: 'var(--muted)' }}>({f.template} · {f.source})</small></span>
              <span className={'badge ' + badge(f.status)}>
                {f.status} · {f.rowsLoaded.toLocaleString()}{f.rowsQuarantined > 0 ? ` (+${f.rowsQuarantined} quar.)` : ''}
              </span>
            </div>
          ))}
          {data.latestIngestions.length === 0 && <div className="loading">nothing ingested yet</div>}
          <div className="note">The two sample extracts are auto-ingested at first startup through the real parser pipeline.</div>
        </div>
      </div>
    </>
  )
}
