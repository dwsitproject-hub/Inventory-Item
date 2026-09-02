import { useEffect, useRef, useState } from 'react'
import { ReportMeta, can, downloadTemplate, getUser, ingestions, quarantine, reportCatalog, uploadFile } from '../api'
import { fmtDateTime, fmtInt } from '../format'

type FileRow = {
  id: number; fileName: string; template: string; source: string; status: string
  rowsTotal: number; rowsLoaded: number; rowsQuarantined: number
  error?: string; uploadedBy?: string; receivedAt: string
}

const badge = (s: string) =>
  s === 'loaded' ? 'b-ok' : s === 'partial' ? 'b-warn' : s === 'duplicate' ? 'b-info' : 'b-err'

export default function Ingestion() {
  const user = getUser()!
  const canUpload = can('ingestion', 'insert')
  const [files, setFiles] = useState<FileRow[]>([])
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<any>(null)
  const [qFor, setQFor] = useState<number | null>(null)
  const [qRows, setQRows] = useState<any[]>([])
  const [catalog, setCatalog] = useState<ReportMeta[]>([])
  const [openTpl, setOpenTpl] = useState<string | null>(null)
  const fileInput = useRef<HTMLInputElement>(null)

  const refresh = () => ingestions().then(setFiles).catch(e => setError(e.message))
  useEffect(() => { refresh(); reportCatalog().then(setCatalog).catch(() => { /* non-fatal */ }) }, [])

  async function doUpload() {
    const f = fileInput.current?.files?.[0]
    if (!f) return
    setBusy(true); setResult(null); setError('')
    try {
      const res = await uploadFile(f)
      setResult(res)
      refresh()
    } catch (e: any) {
      setError(e.message)
    } finally {
      setBusy(false)
    }
  }

  async function showQuarantine(id: number) {
    if (qFor === id) { setQFor(null); return }
    setQFor(id)
    setQRows(await quarantine(id))
  }

  return (
    <>
      <h1 className="page">Ingestion &amp; Upload</h1>
      <div className="crumb">Data · Manual upload (same validate → load path as automatic ingestion)</div>

      <div className="row2">
        <div className="panel">
          <h3>Ingested files</h3>
          <div className="gridscroll" style={{ maxHeight: '48vh' }}>
            <table className="grid">
              <thead><tr><th>File</th><th>Template</th><th>Source</th><th>Rows</th><th>Status</th><th>When</th><th></th></tr></thead>
              <tbody>
                {files.map(f => (
                  <>
                    <tr key={f.id}>
                      <td>{f.fileName}</td>
                      <td>{f.template}</td>
                      <td>{f.source}</td>
                      <td className="num">{fmtInt(f.rowsLoaded)}{f.rowsQuarantined > 0 && <span style={{ color: 'var(--warn)' }}> (+{fmtInt(f.rowsQuarantined)} quar.)</span>}</td>
                      <td><span className={'badge ' + badge(f.status)}>{f.status}</span></td>
                      <td>{fmtDateTime(f.receivedAt)}</td>
                      <td>{f.rowsQuarantined > 0 &&
                        <button className="btn o" style={{ padding: '3px 9px', fontSize: 11.5 }} onClick={() => showQuarantine(f.id)}>
                          {qFor === f.id ? 'hide' : 'quarantine'}
                        </button>}</td>
                    </tr>
                    {qFor === f.id && (
                      <tr key={f.id + '-q'}>
                        <td colSpan={7} style={{ background: '#fbeee4', whiteSpace: 'normal' }}>
                          {qRows.map((q, i) => (
                            <div key={i} style={{ fontSize: 12, padding: '3px 0' }}>
                              <b>row {q.rowNo}</b>: {(q.reasons as string[]).join('; ')}
                            </div>
                          ))}
                          {qRows.length === 0 && <span className="loading">loading…</span>}
                        </td>
                      </tr>
                    )}
                  </>
                ))}
                {files.length === 0 && <tr><td colSpan={7} className="loading">no files yet</td></tr>}
              </tbody>
            </table>
          </div>
        </div>

        <div className="panel">
          <h3>Manual upload</h3>
          {!canUpload && <div className="note">Your role ({user.role}) cannot upload files. An administrator can grant this under Administration → Role Management (Ingestion → Insert).</div>}
          {canUpload && (
            <>
              <div className="fld">
                <label>File (template auto-detected by content, never by extension — FR-I8)</label>
                <input type="file" ref={fileInput} />
              </div>
              <button className="btn p" style={{ width: '100%' }} onClick={doUpload} disabled={busy}>
                {busy ? 'Validating & loading…' : 'Validate & upload'}
              </button>
              {error && <div className="err" style={{ marginTop: 12 }}>{error}</div>}
              {result && (
                <div className="note" style={{ marginTop: 12 }}>
                  <b>{result.status}</b>
                  {result.template && <> · template {result.template}</>}
                  {result.rowsTotal != null && <> · {fmtInt(result.rowsLoaded)}/{fmtInt(result.rowsTotal)} rows loaded, {fmtInt(result.rowsQuarantined)} quarantined</>}
                  {result.message && <> · {result.message}</>}
                </div>
              )}
              <div className="note" style={{ marginTop: 12 }}>
                Re-uploading an identical file is rejected as a duplicate (idempotency by content hash, FR-I6).
                Bad rows are quarantined with reasons — never silently dropped (FR-I5).
              </div>
            </>
          )}
        </div>
      </div>

      <div className="panel">
        <h3>
          Supported upload templates
          <small style={{ color: 'var(--muted)', fontWeight: 400 }}>
            {' '}· detected from the file's own header row — the file name and extension are ignored (FR-I8)
          </small>
        </h3>
        <div className="gridscroll" style={{ maxHeight: '46vh' }}>
          <table className="grid">
            <thead><tr>
              <th>Template</th><th>Report</th><th>Format</th><th>Fields</th><th>Appears in</th><th>Loaded</th><th>Blank template</th><th></th>
            </tr></thead>
            <tbody>
              {catalog.filter(r => r.upload).map(r => {
                const loaded = files.filter(f => f.template === r.template && f.status !== 'failed')
                const rows = loaded.reduce((s, f) => s + f.rowsLoaded, 0)
                const fmt = r.template === 'BC23' ? 'TSV (.xls)' : r.template === 'BC40' ? 'HTML table (.xls)' : 'XLSX'
                return (
                  <>
                    <tr key={r.key} style={{ cursor: 'pointer' }} onClick={() => setOpenTpl(openTpl === r.key ? null : r.key)}>
                      <td><b>{r.template}</b></td>
                      <td>{r.title}</td>
                      <td>{fmt}</td>
                      <td className="num">{fmtInt(r.fields.length)}</td>
                      <td>{r.page === 'movement' ? 'Inventory Movement' : 'Reports'}</td>
                      <td>
                        {loaded.length === 0
                          ? <span className="badge b-warn">not yet uploaded</span>
                          : <span className="badge b-ok">{loaded.length} file{loaded.length > 1 ? 's' : ''} · {fmtInt(rows)} rows</span>}
                      </td>
                      <td onClick={e => e.stopPropagation()}>
                        <button className="btn o" style={{ padding: '3px 9px', fontSize: 11.5 }}
                          title="Download a blank .xlsx with these exact column headers, ready to fill in and upload"
                          onClick={() => downloadTemplate(r.key).catch(e => setError(e.message))}>
                          ⭳ .xlsx
                        </button>
                      </td>
                      <td><span className="act">{openTpl === r.key ? 'hide' : 'columns'}</span></td>
                    </tr>
                    {openTpl === r.key && (
                      <tr key={r.key + '-c'}>
                        <td colSpan={8} style={{ background: '#f6f8fa', whiteSpace: 'normal' }}>
                          <div style={{ fontSize: 11.5, color: 'var(--muted)', marginBottom: 6 }}>
                            Expected columns (name as uploaded → shown as):
                          </div>
                          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                            {r.fields.map(f => (
                              <span key={f.name} className="scopetag" title={f.group}
                                style={{ fontSize: 10.5, background: 'var(--chip)', borderRadius: 5, padding: '2px 6px' }}>
                                {f.name}{f.label !== f.name && <> → <b>{f.label}</b></>}
                              </span>
                            ))}
                          </div>
                        </td>
                      </tr>
                    )}
                  </>
                )
              })}
              {catalog.length === 0 && <tr><td colSpan={8} className="loading">loading templates…</td></tr>}
            </tbody>
          </table>
        </div>
        <div className="note" style={{ marginTop: 10 }}>
          <b>⭳ .xlsx</b> gives you a blank workbook with these exact headers plus a “Petunjuk” sheet
          explaining each column — fill it in and upload it straight back. Columns are matched by
          header text, so they may be reordered. Bahan Baku and Barang Jadi share an identical
          14-column header, so those two are told apart by the sheet or file name; every other
          template is identified from its columns alone.
        </div>
      </div>
    </>
  )
}
