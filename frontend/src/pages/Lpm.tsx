import { useEffect, useState } from 'react'
import { SaldoRow, VarianceRow, lpmSaldo, lpmVariances } from '../api'
import { fmtInt, fmtNum } from '../format'

const n = (v: number | null | undefined) => fmtNum(v)

export default function Lpm() {
  const [saldo, setSaldo] = useState<SaldoRow[]>([])
  const [note, setNote] = useState('')
  const [variances, setVariances] = useState<VarianceRow[]>([])
  const [summary, setSummary] = useState<{ withVariance: number; deliveryTracked: number; totalLines: number } | null>(null)
  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    const t = setTimeout(() => setSearch(searchInput.trim()), 350)
    return () => clearTimeout(t)
  }, [searchInput])

  useEffect(() => {
    setLoading(true)
    lpmSaldo(search)
      .then(d => { setSaldo(d.rows); setNote(d.note) })
      .catch(e => setError(e.message))
      .finally(() => setLoading(false))
  }, [search])

  useEffect(() => {
    lpmVariances().then(d => { setVariances(d.rows); setSummary(d.summary) }).catch(e => setError(e.message))
  }, [])

  return (
    <>
      <h1 className="page">LPM — Mutasi &amp; Reconciliation</h1>
      <div className="crumb">Saldo accountability per material per period · opening + in − out − adj = closing (FR-R8)</div>

      {error && <div className="err">{error}</div>}

      <div className="kpis" style={{ gridTemplateColumns: 'repeat(3,1fr)' }}>
        <div className="kpi"><div className="l">Materials tracked</div><div className="v">{fmtInt(new Set(saldo.map(s => s.material)).size)}</div></div>
        <div className="kpi"><div className="l">BC 4.0 lines with GR variance</div><div className="v" style={{ color: (summary?.withVariance ?? 0) > 0 ? 'var(--warn)' : undefined }}>{summary ? fmtInt(summary.withVariance) : '…'}</div></div>
        <div className="kpi"><div className="l">Beyond tolerance</div><div className="v" style={{ color: variances.some(v => v.beyondTolerance) ? 'var(--err)' : 'var(--ok)' }}>{fmtInt(variances.filter(v => v.beyondTolerance).length)}</div></div>
      </div>

      <div className="filters">
        <div className="f" style={{ flex: 1 }}>
          <label>Search material (code or description)</label>
          <input placeholder="e.g. 912.005.001, CITRIC, HCL" value={searchInput} onChange={e => setSearchInput(e.target.value)} />
        </div>
      </div>

      <div className="tablewrap" style={{ marginBottom: 16 }}>
        <div className="tbar">
          <div className="info">{loading ? <span><span className="spin" />loading…</span> : <><b>{saldo.length}</b> material-month rows · {note}</>}</div>
        </div>
        <div className="gridscroll" style={{ maxHeight: '46vh' }}>
          <table className="grid">
            <thead><tr>
              <th>Material</th><th>Description</th><th>UoM</th><th>Period</th>
              <th style={{ textAlign: 'right' }}>Opening</th><th style={{ textAlign: 'right' }}>In</th>
              <th style={{ textAlign: 'right' }}>Out</th><th style={{ textAlign: 'right' }}>Adj</th>
              <th style={{ textAlign: 'right' }}>Closing</th><th style={{ textAlign: 'right' }}>Lines</th>
            </tr></thead>
            <tbody>
              {saldo.map((r, i) => (
                <tr key={i}>
                  <td>{r.material}</td>
                  <td style={{ maxWidth: 320, overflow: 'hidden', textOverflow: 'ellipsis' }}>{r.description}</td>
                  <td>{r.uom}</td>
                  <td>{String(r.month).slice(0, 7)}</td>
                  <td className="num">{n(r.opening)}</td>
                  <td className="num" style={{ color: 'var(--steel)' }}>{n(r.qtyIn)}</td>
                  <td className="num">{n(r.qtyOut)}</td>
                  <td className="num">{n(r.adjustment)}</td>
                  <td className="num"><b>{n(r.closing)}</b></td>
                  <td className="num">{fmtInt(r.lines)}</td>
                </tr>
              ))}
              {!loading && saldo.length === 0 && <tr><td colSpan={10} style={{ color: 'var(--muted)', padding: 24 }}>No materials match.</td></tr>}
            </tbody>
          </table>
        </div>
      </div>

      <div className="tablewrap">
        <div className="tbar">
          <div className="info">
            <b>Goods-receipt realisation variances</b> — BC 4.0 lines where delivered ≠ declared ({summary ? `${fmtInt(summary.withVariance)} variance(s) · ${fmtInt(summary.deliveryTracked)} of ${fmtInt(summary.totalLines)} lines carry delivery data` : '…'})
          </div>
        </div>
        <div className="gridscroll" style={{ maxHeight: '40vh' }}>
          <table className="grid">
            <thead><tr>
              <th>Flag</th><th>Location</th><th>TPB No.</th><th>Document BC / No</th><th>Vendor</th><th>Material / Description</th>
              <th style={{ textAlign: 'right' }}>BC / Qty</th><th style={{ textAlign: 'right' }}>Delivery Qty</th>
              <th style={{ textAlign: 'right' }}>(+/-)</th><th style={{ textAlign: 'right' }}>Var %</th><th>Tolerance</th>
            </tr></thead>
            <tbody>
              {variances.map((v, i) => (
                <tr key={i}>
                  <td><span className={'badge ' + (v.beyondTolerance ? 'b-err' : 'b-warn')}>{v.beyondTolerance ? 'beyond tolerance' : 'within tolerance'}</span></td>
                  <td>{v.location}</td>
                  <td>{v.tpbNo}</td>
                  <td>{v.docNo}</td>
                  <td style={{ maxWidth: 180, overflow: 'hidden', textOverflow: 'ellipsis' }}>{v.vendor}</td>
                  <td style={{ maxWidth: 260, overflow: 'hidden', textOverflow: 'ellipsis' }}>{v.description}</td>
                  <td className="num">{n(v.bcQty)} {v.uom}</td>
                  <td className="num">{n(v.deliveryQty)}</td>
                  <td className="num" style={{ color: 'var(--err)' }}>{n(v.variance)}</td>
                  <td className="num">{v.variancePct != null ? fmtNum(v.variancePct) + '%' : ''}</td>
                  <td>{v.tolerancePct != null ? fmtNum(v.tolerancePct) + ' %' : '—'}</td>
                </tr>
              ))}
              {variances.length === 0 && <tr><td colSpan={11} style={{ color: 'var(--muted)', padding: 24 }}>No goods-receipt variances in the loaded data. 🎉</td></tr>}
            </tbody>
          </table>
        </div>
      </div>
    </>
  )
}
