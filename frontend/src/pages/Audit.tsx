import { useEffect, useMemo, useState } from 'react'
import { AuditEvent, auditExport, auditQuery, can } from '../api'

const actionStyle = (a: string): { bg: string; fg: string } => {
  if (a.startsWith('auth.login_failed')) return { bg: 'var(--errbg)', fg: 'var(--err)' }
  if (a.startsWith('auth.')) return { bg: '#e7f0f7', fg: 'var(--steel)' }
  if (a.startsWith('report.export') || a.startsWith('audit.export')) return { bg: 'var(--warnbg)', fg: 'var(--warn)' }
  if (a.startsWith('report.')) return { bg: '#eef1f5', fg: 'var(--muted)' }
  if (a.startsWith('ingest.failed') || a.startsWith('ingest.duplicate')) return { bg: 'var(--warnbg)', fg: 'var(--warn)' }
  if (a.startsWith('ingest.')) return { bg: 'var(--okbg)', fg: 'var(--ok)' }
  if (a.startsWith('admin.') || a.startsWith('master.')) return { bg: '#f0ecf7', fg: '#6b4fa0' }
  return { bg: 'var(--chip)', fg: 'var(--navy)' }
}

export default function Audit() {
  const [rows, setRows] = useState<AuditEvent[]>([])
  const [total, setTotal] = useState(0)
  const [actions, setActions] = useState<string[]>([])
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [actor, setActor] = useState('')
  const [action, setAction] = useState('')
  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [offset, setOffset] = useState(0)
  const [size] = useState(50)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [expanded, setExpanded] = useState<number | null>(null)

  useEffect(() => {
    const t = setTimeout(() => setSearch(searchInput.trim()), 350)
    return () => clearTimeout(t)
  }, [searchInput])

  const filters = useMemo(() => ({ from, to, actor, action, search }), [from, to, actor, action, search])
  useEffect(() => { setOffset(0) }, [JSON.stringify(filters)])

  useEffect(() => {
    setLoading(true); setError('')
    auditQuery({ ...filters, limit: size, offset })
      .then(d => { setRows(d.rows); setTotal(d.total); setActions(d.actions) })
      .catch(e => setError(e.message))
      .finally(() => setLoading(false))
  }, [JSON.stringify(filters), offset])

  const page = Math.floor(offset / size) + 1
  const pages = Math.max(1, Math.ceil(total / size))

  return (
    <>
      <h1 className="page">Audit Log</h1>
      <div className="crumb">
        Immutable trail of report runs &amp; exports, ingestion events, master-data and access changes (FR-A7)
      </div>

      <div className="filters">
        <div className="f"><label>From</label><input type="date" value={from} onChange={e => setFrom(e.target.value)} /></div>
        <div className="f"><label>To</label><input type="date" value={to} onChange={e => setTo(e.target.value)} /></div>
        <div className="f"><label>Actor (email)</label><input placeholder="any user" value={actor} onChange={e => setActor(e.target.value)} /></div>
        <div className="f"><label>Action</label>
          <select value={action} onChange={e => setAction(e.target.value)}>
            <option value="">All actions</option>
            {actions.map(a => <option key={a} value={a}>{a}</option>)}
          </select>
        </div>
        <div className="f" style={{ flex: 1 }}>
          <label>Search (summary · target · detail)</label>
          <input placeholder="e.g. BC40, export, decky@" value={searchInput} onChange={e => setSearchInput(e.target.value)} />
        </div>
        <button className="btn o" onClick={() => { setFrom(''); setTo(''); setActor(''); setAction(''); setSearchInput('') }}>Clear</button>
      </div>

      {error && <div className="err">{error}</div>}

      <div className="tablewrap">
        <div className="tbar">
          <div className="info">
            {loading ? <span><span className="spin" />loading…</span>
              : <>Showing <b>{total === 0 ? 0 : offset + 1}–{Math.min(offset + size, total)}</b> of <b>{total.toLocaleString()}</b> events · newest first · click a row for detail</>}
          </div>
          {can('audit', 'edit') && (
            <button className="btn o" onClick={() => auditExport(filters).catch(e => setError(e.message))}>
              Export CSV
            </button>
          )}
        </div>
        <div className="gridscroll" style={{ maxHeight: '62vh' }}>
          <table className="grid">
            <thead><tr>
              <th>When</th><th>Actor</th><th>Role</th><th>Action</th><th>Target</th><th>Summary</th><th>IP</th>
            </tr></thead>
            <tbody>
              {rows.map(r => {
                const st = actionStyle(r.action)
                return (
                  <>
                    <tr key={r.id} style={{ cursor: r.detailJson ? 'pointer' : 'default' }}
                      onClick={() => setExpanded(expanded === r.id ? null : r.id)}>
                      <td>{new Date(r.occurredAt).toLocaleString()}</td>
                      <td>{r.actorEmail ?? '—'}</td>
                      <td>{r.actorRole ?? '—'}</td>
                      <td><span className="badge" style={{ background: st.bg, color: st.fg }}>{r.action}</span></td>
                      <td>{r.targetType ? `${r.targetType}${r.targetId ? ' · ' + r.targetId : ''}` : '—'}</td>
                      <td style={{ maxWidth: 380, overflow: 'hidden', textOverflow: 'ellipsis' }}>{r.summary}</td>
                      <td>{r.ip ?? '—'}</td>
                    </tr>
                    {expanded === r.id && r.detailJson && (
                      <tr key={r.id + '-d'}>
                        <td colSpan={7} style={{ background: '#f6f8fa', whiteSpace: 'pre-wrap', fontFamily: 'Consolas, monospace', fontSize: 11.5 }}>
                          {JSON.stringify(JSON.parse(r.detailJson), null, 2)}
                        </td>
                      </tr>
                    )}
                  </>
                )
              })}
              {!loading && rows.length === 0 && (
                <tr><td colSpan={7} style={{ color: 'var(--muted)', padding: 24 }}>No audit events match these filters.</td></tr>
              )}
            </tbody>
          </table>
        </div>
        <div className="pager">
          <span>The trail is append-only — the database rejects any UPDATE or DELETE on audit events.</span>
          <div className="pg">
            <button disabled={page <= 1} onClick={() => setOffset(0)}>«</button>
            <button disabled={page <= 1} onClick={() => setOffset(Math.max(0, offset - size))}>‹</button>
            <button className="sel">{page} / {pages}</button>
            <button disabled={page >= pages} onClick={() => setOffset(offset + size)}>›</button>
            <button disabled={page >= pages} onClick={() => setOffset((pages - 1) * size)}>»</button>
          </div>
        </div>
      </div>
    </>
  )
}
