import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ReportMeta, SavedView, SortSpec, can, deleteView, exportReport, listViews, queryReport, saveView } from '../api'

/**
 * Server-side report grid (FR-R4, FR-R9..R13).
 * Column names are the exact upload template headers; the server receives the visible
 * column projection, sorts and page — only that data comes back.
 */
export default function Grid({ report, filters }: { report: ReportMeta; filters: Record<string, string> }) {
  const fieldsByName = useMemo(() => new Map(report.fields.map(f => [f.name, f])), [report])
  const [cols, setCols] = useState<string[]>(report.defaults)
  const [sorts, setSorts] = useState<SortSpec[]>([])
  const [pageSize, setPageSize] = useState(25)
  const [offset, setOffset] = useState(0)
  const [rows, setRows] = useState<Record<string, unknown>[]>([])
  const [total, setTotal] = useState(0)
  const [meta, setMeta] = useState<{ asOf?: string; sourceFile?: string; elapsedMs?: number }>({})
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [chooserOpen, setChooserOpen] = useState(false)
  const [namedViews, setNamedViews] = useState<SavedView[]>([])
  const [exporting, setExporting] = useState(false)
  const dragKey = useRef<string | null>(null)
  const seq = useRef(0)
  const layoutReady = useRef(false)

  // report switch: restore the user's last layout (FR-R12), else defaults
  useEffect(() => {
    layoutReady.current = false
    setSorts([]); setOffset(0); setCols(report.defaults)
    listViews(report.key).then(v => {
      setNamedViews(v.named)
      if (v.last) {
        const valid = v.last.columns.filter(c => fieldsByName.has(c))
        if (valid.length > 0) setCols(valid)
        setSorts((v.last.sorts || []).filter(s => fieldsByName.has(s.field)))
        setPageSize(v.last.pageSize || 25)
      }
      layoutReady.current = true
    }).catch(() => { layoutReady.current = true })
  }, [report.key])

  // auto-persist layout per user per report, debounced
  useEffect(() => {
    if (!layoutReady.current) return
    const t = setTimeout(() => {
      saveView(report.key, { columns: cols, sorts, pageSize }).catch(() => { /* non-fatal */ })
    }, 1200)
    return () => clearTimeout(t)
  }, [cols, sorts, pageSize])

  useEffect(() => { setOffset(0) }, [JSON.stringify(filters), pageSize])

  function applyView(v: SavedView) {
    setCols(v.columns.filter(c => fieldsByName.has(c)))
    setSorts((v.sorts || []).filter(s => fieldsByName.has(s.field)))
    setPageSize(v.pageSize || 25)
  }

  async function saveNamed() {
    const name = window.prompt('View name:')
    if (!name?.trim()) return
    await saveView(report.key, { name: name.trim(), columns: cols, sorts, pageSize })
    setNamedViews((await listViews(report.key)).named)
  }

  async function removeView(id: number) {
    await deleteView(id)
    setNamedViews(v => v.filter(x => x.id !== id))
  }

  async function doExport(format: 'xlsx' | 'csv') {
    setExporting(true)
    try { await exportReport(report.key, format, { filters, columns: cols, sort: sorts }) }
    catch (e: any) { setError(e.message) }
    finally { setExporting(false) }
  }

  const fetchPage = useCallback(async () => {
    // On a report switch, `cols`/`sorts` still hold the previous report's fields for one render.
    // Firing that request would 400 ("unknown column"), so wait for the catalog to line up.
    if (!cols.every(c => fieldsByName.has(c)) || !sorts.every(s => fieldsByName.has(s.field))) return

    const mySeq = ++seq.current
    setLoading(true); setError('')
    const t0 = performance.now()
    try {
      const res = await queryReport(report.key, {
        filters, columns: cols, sort: sorts, page: { size: pageSize, offset }
      })
      if (mySeq !== seq.current) return
      setRows(res.data.rows)
      setTotal(res.meta.total)
      setMeta({ ...res.meta, elapsedMs: Math.round(performance.now() - t0) })
    } catch (e: any) {
      if (mySeq === seq.current) setError(e.message)
    } finally {
      if (mySeq === seq.current) setLoading(false)
    }
  }, [report.key, fieldsByName, JSON.stringify(filters), cols, sorts, pageSize, offset])

  useEffect(() => { fetchPage() }, [fetchPage])

  function toggleSort(name: string, multi: boolean) {
    setSorts(prev => {
      const i = prev.findIndex(s => s.field === name)
      if (!multi) {
        if (i === -1) return [{ field: name, dir: 'asc' }]
        if (prev[i].dir === 'asc') return [{ field: name, dir: 'desc' }]
        return []
      }
      const next = [...prev]
      if (i === -1) next.push({ field: name, dir: 'asc' })
      else if (next[i].dir === 'asc') next[i] = { field: name, dir: 'desc' }
      else next.splice(i, 1)
      return next
    })
  }

  function moveCol(from: string, to: string) {
    setCols(prev => {
      const a = [...prev]
      const fi = a.indexOf(from); if (fi === -1) return prev
      a.splice(fi, 1)
      const ti = a.indexOf(to); if (ti === -1) return prev
      a.splice(fi < ti + 1 ? ti + 1 : ti, 0, from)
      return a
    })
  }

  function fmt(name: string, v: unknown): string {
    if (v == null) return ''
    const f = fieldsByName.get(name)
    if (f?.type === 'number' && typeof v === 'number') return v.toLocaleString()
    if (f?.type === 'number') { const n = Number(v); if (!Number.isNaN(n)) return n.toLocaleString() }
    if (f?.type === 'date' && typeof v === 'string') return v.slice(0, 10)
    return String(v)
  }

  const page = Math.floor(offset / pageSize) + 1
  const pages = Math.max(1, Math.ceil(Math.min(total, 100000) / pageSize))
  const hidden = report.fields.filter(f => !cols.includes(f.name))

  return (
    <div className="tablewrap">
      <div className="tbar">
        <div className="info">
          {loading ? <span><span className="spin" />loading…</span> : (
            <>Showing <b>{total === 0 ? 0 : offset + 1}–{Math.min(offset + pageSize, total)}</b> of <b>{total.toLocaleString()}{total > 100000 ? '+' : ''}</b> rows
              · server-side · {meta.elapsedMs ?? '–'} ms
              {meta.sourceFile && <> · source: {meta.sourceFile}</>}</>
          )}
        </div>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          {namedViews.length > 0 && (
            <select style={{ border: '1px solid var(--line)', borderRadius: 8, padding: '7px 10px', fontSize: 12.5 }}
              value="" onChange={e => { const v = namedViews.find(x => x.id === Number(e.target.value)); if (v) applyView(v) }}>
              <option value="" disabled>My views…</option>
              {namedViews.map(v => <option key={v.id} value={v.id}>{v.name}</option>)}
            </select>
          )}
          <button className="btn o" onClick={saveNamed} title="Save current layout as a named view (FR-R12)">＋ View</button>
          <button className="btn o" onClick={() => setChooserOpen(v => !v)}>⚙ Columns</button>
          {can(report.page, 'edit') ? (<>
            <button className="btn o" disabled={exporting} onClick={() => doExport('xlsx')} title="Export honours visible columns, order and sort (FR-R13)">Excel</button>
            <button className="btn o" disabled={exporting} onClick={() => doExport('csv')}>CSV</button>
          </>) : (
            <span style={{ fontSize: 11.5, color: 'var(--muted)' }} title="Your role cannot export from this page">export not permitted</span>
          )}
        </div>
        {chooserOpen && (
          <div className="colpanel" onClick={e => e.stopPropagation()}>
            <h4>Shown ({cols.length}) — ↑↓ to reorder</h4>
            {cols.map((c, i) => (
              <div className="colrow" key={c} title={`upload field: ${c}`}>
                <input type="checkbox" checked onChange={() => setCols(prev => prev.filter(x => x !== c))} disabled={cols.length === 1} />
                <span>{fieldsByName.get(c)?.label ?? c}</span>
                <span className="grp">{fieldsByName.get(c)?.group}</span>
                <span className="mv">
                  <button onClick={() => i > 0 && setCols(p => { const a = [...p]; [a[i - 1], a[i]] = [a[i], a[i - 1]]; return a })}>↑</button>
                  <button onClick={() => i < cols.length - 1 && setCols(p => { const a = [...p]; [a[i + 1], a[i]] = [a[i], a[i + 1]]; return a })}>↓</button>
                </span>
              </div>
            ))}
            <h4 style={{ marginTop: 10 }}>Available fields ({hidden.length})</h4>
            {hidden.map(f => (
              <div className="colrow off" key={f.name} title={`upload field: ${f.name}`}>
                <input type="checkbox" checked={false} onChange={() => setCols(prev => [...prev, f.name])} />
                <span>{f.label}</span>
                <span className="grp">{f.group}</span>
              </div>
            ))}
            {namedViews.length > 0 && (
              <>
                <h4 style={{ marginTop: 10 }}>My saved views</h4>
                {namedViews.map(v => (
                  <div className="colrow" key={v.id}>
                    <span style={{ cursor: 'pointer', color: 'var(--steel)' }} onClick={() => applyView(v)}>{v.name}</span>
                    <span className="grp">{v.columns.length} cols</span>
                    <span className="mv"><button onClick={() => removeView(v.id)} title="Delete view">✕</button></span>
                  </div>
                ))}
              </>
            )}
            <div style={{ display: 'flex', gap: 8, marginTop: 10 }}>
              <button className="btn o" style={{ flex: 1 }} onClick={() => { setCols(report.defaults); setSorts([]) }}>Reset to default</button>
              <button className="btn p" style={{ flex: 1 }} onClick={() => setChooserOpen(false)}>Done</button>
            </div>
            <div className="note" style={{ marginTop: 8 }}>
              Columns show customs terminology; hover any field to see the upload header it maps to
              (PRD v1.2 App. D–E). Stored field names stay verbatim, so lineage is unchanged.
            </div>
          </div>
        )}
      </div>

      {error && <div className="err" style={{ margin: 12 }}>{error}</div>}

      <div className="gridscroll">
        <table className="grid">
          <thead>
            <tr>
              <th style={{ cursor: 'default' }}>No</th>
              {cols.map(c => {
                const si = sorts.findIndex(s => s.field === c)
                return (
                  <th key={c} draggable
                    title={`${fieldsByName.get(c)?.label ?? c}\nupload field: ${c}\nClick: sort · Shift+click: multi-sort · Drag: reorder`}
                    onClick={e => toggleSort(c, e.shiftKey)}
                    onDragStart={() => { dragKey.current = c }}
                    onDragOver={e => { e.preventDefault(); (e.target as HTMLElement).classList.add('dragover') }}
                    onDragLeave={e => (e.target as HTMLElement).classList.remove('dragover')}
                    onDrop={e => {
                      (e.target as HTMLElement).classList.remove('dragover')
                      if (dragKey.current && dragKey.current !== c) moveCol(dragKey.current, c)
                    }}>
                    {fieldsByName.get(c)?.label ?? c}
                    {si > -1 && <span className="si">{sorts[si].dir === 'asc' ? '▲' : '▼'}{sorts.length > 1 && <sup>{si + 1}</sup>}</span>}
                  </th>
                )
              })}
            </tr>
          </thead>
          <tbody>
            {rows.map((r, i) => (
              <tr key={i}>
                <td className="num">{offset + i + 1}</td>
                {cols.map(c => (
                  <td key={c} className={fieldsByName.get(c)?.type === 'number' ? 'num' : ''}>{fmt(c, r[c])}</td>
                ))}
              </tr>
            ))}
            {!loading && rows.length === 0 && (
              <tr><td colSpan={cols.length + 1} style={{ color: 'var(--muted)', padding: 24 }}>No rows for the current filters.</td></tr>
            )}
          </tbody>
        </table>
      </div>

      <div className="pager">
        <span>
          Rows per page:{' '}
          <select value={pageSize} onChange={e => setPageSize(Number(e.target.value))}>
            {[25, 50, 100].map(n => <option key={n} value={n}>{n}</option>)}
          </select>
        </span>
        <div className="pg">
          <button disabled={page <= 1} onClick={() => setOffset(0)}>«</button>
          <button disabled={page <= 1} onClick={() => setOffset(Math.max(0, offset - pageSize))}>‹</button>
          <button className="sel">{page} / {pages}</button>
          <button disabled={page >= pages} onClick={() => setOffset(offset + pageSize)}>›</button>
          <button disabled={page >= pages} onClick={() => setOffset((pages - 1) * pageSize)}>»</button>
        </div>
      </div>
    </div>
  )
}
