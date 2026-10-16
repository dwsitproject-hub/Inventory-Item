import { useEffect, useMemo, useState } from 'react'
import { ReportMeta, getMySites, getUser, reportCatalog } from '../api'
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
  // Narrow to one of my sites. Only worth showing when there is more than one to choose from.
  const mySites = getMySites()
  const [siteId, setSiteId] = useState('')
  const [catalog, setCatalog] = useState<ReportMeta[] | null>(null)
  const [key, setKey] = useState<string>('')
  const [dateFrom, setDateFrom] = useState('')
  const [dateTo, setDateTo] = useState('')
  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    reportCatalog()
      .then(all => {
        // browse=false marks an upload template that is read through derived views instead, or a
        // report the user's site is not configured to see (Admin → Master Data)
        // Only the columns the user's site may see; the Ingestion page keeps the full field list.
        const mine = all.filter(r => r.page === page && r.browse)
          .map(r => ({ ...r, fields: r.fields.filter(f => f.visible !== false) }))
        setCatalog(mine)
        setKey(k => (mine.some(r => r.key === k) ? k : mine[0]?.key ?? ''))
      })
      .catch(e => setError(e.message))
  }, [page])

  useEffect(() => {
    const t = setTimeout(() => setSearch(searchInput.trim()), 350)
    return () => clearTimeout(t)
  }, [searchInput])

  const report = catalog?.find(r => r.key === key)
  const filters = useMemo(() => {
    const f: Record<string, string> = {}
    if (siteId) f.siteId = siteId
    if (dateFrom) f.dateFrom = dateFrom
    if (dateTo) f.dateTo = dateTo
    if (search) f.search = search
    return f
  }, [siteId, dateFrom, dateTo, search])

  const [fromLabel, toLabel] = dateLabels ?? ['From', 'To']

  return (
    <>
      <h1 className="page">{title}</h1>
      <div className="crumb">{crumb}</div>

      <div className="filters">
        <div className="f">
          <label>Report</label>
          <select value={key} onChange={e => { setKey(e.target.value); setSearchInput('') }}>
            {(catalog ?? []).map(r => <option key={r.key} value={r.key}>{r.title}</option>)}
          </select>
        </div>
        {mySites.length > 1 && (
          <div className="f">
            <label>Site</label>
            <select value={siteId} onChange={e => setSiteId(e.target.value)}>
              <option value="">{user.allSites ? 'All sites' : 'All my sites'}</option>
              {mySites.map(s => <option key={s.id} value={String(s.id)}>{s.name}</option>)}
            </select>
          </div>
        )}
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
        : error ? null
        : catalog?.length === 0
          ? <div className="note">No reports are enabled for your site on this page. An administrator can enable them under Administration → Master Data.</div>
          : <div className="loading"><span className="spin" />loading report catalog…</div>}
    </>
  )
}
