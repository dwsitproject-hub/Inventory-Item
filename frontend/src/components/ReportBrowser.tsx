import { useEffect, useMemo, useState } from 'react'
import { ReportMeta, getUser, reportCatalog } from '../api'
import Grid from './Grid'

/**
 * Report screen shared by "Reports" (customs documents) and "Inventory Movement"
 * (periodic stock/mutation reports) — same grid, filtered to the catalog's page group.
 */
export default function ReportBrowser({ page, title, crumb, dateLabels, searchHint }: {
  page: 'reports' | 'movement'
  title: string
  crumb: string
  dateLabels?: [string, string]
  searchHint?: string
}) {
  const user = getUser()!
  const [catalog, setCatalog] = useState<ReportMeta[]>([])
  const [key, setKey] = useState<string>('')
  const [dateFrom, setDateFrom] = useState('')
  const [dateTo, setDateTo] = useState('')
  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    reportCatalog()
      .then(all => {
        const mine = all.filter(r => r.page === page)
        setCatalog(mine)
        setKey(k => (mine.some(r => r.key === k) ? k : mine[0]?.key ?? ''))
      })
      .catch(e => setError(e.message))
  }, [page])

  useEffect(() => {
    const t = setTimeout(() => setSearch(searchInput.trim()), 350)
    return () => clearTimeout(t)
  }, [searchInput])

  const report = catalog.find(r => r.key === key)
  const filters = useMemo(() => {
    const f: Record<string, string> = {}
    if (dateFrom) f.dateFrom = dateFrom
    if (dateTo) f.dateTo = dateTo
    if (search) f.search = search
    return f
  }, [dateFrom, dateTo, search])

  const [fromLabel, toLabel] = dateLabels ?? ['From', 'To']

  return (
    <>
      <h1 className="page">{title}</h1>
      <div className="crumb">{crumb}</div>

      <div className="filters">
        <div className="f">
          <label>Report</label>
          <select value={key} onChange={e => { setKey(e.target.value); setSearchInput('') }}>
            {catalog.map(r => <option key={r.key} value={r.key}>{r.title}</option>)}
          </select>
        </div>
        <div className="f">
          <label>Entity {user.allEntities ? '' : '🔒'}</label>
          <select disabled={!user.allEntities}>
            <option>{user.allEntities ? 'All (my scope)' : 'PT Energi Unggul Persada'}</option>
          </select>
        </div>
        <div className="f"><label>{fromLabel}</label><input type="date" value={dateFrom} onChange={e => setDateFrom(e.target.value)} /></div>
        <div className="f"><label>{toLabel}</label><input type="date" value={dateTo} onChange={e => setDateTo(e.target.value)} /></div>
        <div className="f" style={{ flex: 1 }}>
          <label>Search</label>
          <input placeholder={searchHint ?? 'search…'} value={searchInput} onChange={e => setSearchInput(e.target.value)} />
        </div>
      </div>

      {error && <div className="err">{error}</div>}
      {report
        ? <Grid report={report} filters={filters} />
        : !error && <div className="loading"><span className="spin" />loading report catalog…</div>}
    </>
  )
}
