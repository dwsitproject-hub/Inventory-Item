import { Fragment, useEffect, useState } from 'react'
import {
  AdminUser, Company, NotifMatrix, NotifSaveRow, RoleMatrix, RolePermRow, adminAddPermit,
  adminAddSite, adminCreateUser, adminMaster, adminResetPassword, adminSetSiteReports, adminSetStatus, adminUsers, can,
  companies as listCompanies, createCompany, getUser, me, notificationSubscriptions, rolePermissions,
  saveNotificationSubscriptions, saveRolePermissions, setPermissions, setUserCompanies, setUserSites, updateCompany
} from '../api'
import { fmtInt } from '../format'

export default function Admin() {
  const [tab, setTab] = useState<'users' | 'companies' | 'roles' | 'notifications' | 'master'>('users')
  const isSuper = getUser()?.role === 'Super Admin'
  return (
    <>
      <h1 className="page">Administration</h1>
      <div className="crumb">Companies · users, roles &amp; site access · page permissions · notification routing · master data governance</div>
      <div className="chips" style={{ marginBottom: 14 }}>
        <span className={'chk' + (tab === 'users' ? ' on' : '')} onClick={() => setTab('users')}>User Management</span>
        {isSuper && <span className={'chk' + (tab === 'companies' ? ' on' : '')} onClick={() => setTab('companies')}>Companies</span>}
        <span className={'chk' + (tab === 'roles' ? ' on' : '')} onClick={() => setTab('roles')}>Role Management</span>
        <span className={'chk' + (tab === 'notifications' ? ' on' : '')} onClick={() => setTab('notifications')}>Notifications</span>
        <span className={'chk' + (tab === 'master' ? ' on' : '')} onClick={() => setTab('master')}>Master Data</span>
      </div>
      {tab === 'users' ? <Users />
        : tab === 'companies' ? <CompaniesTab />
        : tab === 'roles' ? <RoleManagement />
        : tab === 'notifications' ? <NotificationRouting />
        : <Master />}
    </>
  )
}

/** Read a File as a base64 payload (data-URI). The backend accepts the whole data-URI. */
function fileToBase64(file: File): Promise<{ base64: string; type: string }> {
  return new Promise((resolve, reject) => {
    const r = new FileReader()
    r.onload = () => resolve({ base64: String(r.result), type: file.type })
    r.onerror = reject
    r.readAsDataURL(file)
  })
}

function CompaniesTab() {
  const [rows, setRows] = useState<Company[]>([])
  const [error, setError] = useState('')
  const [info, setInfo] = useState('')
  const [form, setForm] = useState<{ id: number | null; name: string; active: boolean; logo: string | null; logoType: string | null }>(
    { id: null, name: '', active: true, logo: null, logoType: null })
  const [busy, setBusy] = useState(false)

  const refresh = () => listCompanies().then(setRows).catch(e => setError(e.message))
  useEffect(() => { refresh() }, [])

  const edit = (c: Company) => { setForm({ id: c.id, name: c.name, active: c.active, logo: null, logoType: null }); setInfo(''); setError('') }
  const reset = () => setForm({ id: null, name: '', active: true, logo: null, logoType: null })

  async function pickLogo(f: File | undefined) {
    if (!f) { setForm(s => ({ ...s, logo: null, logoType: null })); return }
    if (f.size > 512 * 1024) { setError('Logo must be 512 KB or smaller.'); return }
    const { base64, type } = await fileToBase64(f)
    setForm(s => ({ ...s, logo: base64, logoType: type }))
  }

  async function save() {
    setBusy(true); setError(''); setInfo('')
    try {
      const body = { name: form.name, active: form.active, logoBase64: form.logo, logoContentType: form.logoType }
      if (form.id) { await updateCompany(form.id, body); setInfo(`Company ${form.name} updated.`) }
      else { await createCompany(body); setInfo(`Company ${form.name} registered.`) }
      reset(); refresh()
    } catch (e: any) { setError(e.message) }
    finally { setBusy(false) }
  }

  return (
    <div className="row2" style={{ gridTemplateColumns: '1.3fr 1fr' }}>
      <div className="panel">
        <h3>Companies (PT)</h3>
        {error && <div className="err" style={{ marginBottom: 10 }}>{error}</div>}
        {info && <div className="note" style={{ marginBottom: 10 }}>{info}</div>}
        <div className="gridscroll" style={{ maxHeight: '52vh' }}>
          <table className="grid">
            <thead><tr><th>Company</th><th>Logo</th><th>Users</th><th>Status</th><th></th></tr></thead>
            <tbody>
              {rows.map(c => (
                <tr key={c.id}>
                  <td><b>{c.name}</b></td>
                  <td>{c.hasLogo ? <span className="badge b-ok">logo set</span> : <span className="badge b-warn">no logo</span>}</td>
                  <td className="num">{fmtInt(c.userCount)}</td>
                  <td><span className={'badge ' + (c.active ? 'b-ok' : 'b-err')}>{c.active ? 'active' : 'inactive'}</span></td>
                  <td><button className="btn o" style={{ padding: '3px 9px', fontSize: 11.5 }} onClick={() => edit(c)}>Edit</button></td>
                </tr>
              ))}
              {rows.length === 0 && <tr><td colSpan={5} className="loading">no companies yet</td></tr>}
            </tbody>
          </table>
        </div>
      </div>

      <div className="panel">
        <h3>{form.id ? 'Edit company' : 'Register a new company (PT)'}</h3>
        <div className="fld"><label>Company name</label>
          <input value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} placeholder="e.g. PT Priscolin" />
        </div>
        <div className="fld"><label>Logo (PNG, JPEG, SVG or WebP · ≤512 KB · shown in the header for this company's users)</label>
          <input type="file" accept="image/png,image/jpeg,image/svg+xml,image/webp" onChange={e => pickLogo(e.target.files?.[0])} />
          {form.logo && <div style={{ marginTop: 8 }}><img src={form.logo} alt="preview" style={{ maxHeight: 48, maxWidth: 180 }} /></div>}
          {form.id && !form.logo && <div className="note" style={{ marginTop: 6 }}>Leave empty to keep the current logo.</div>}
        </div>
        <div className="fld"><label>Status</label>
          <select value={form.active ? 'active' : 'inactive'} onChange={e => setForm({ ...form, active: e.target.value === 'active' })}>
            <option value="active">Active</option>
            <option value="inactive">Inactive (hidden from upload / assignment)</option>
          </select>
        </div>
        <div style={{ display: 'flex', gap: 8 }}>
          <button className="btn p" onClick={save} disabled={busy || !form.name.trim()}>{busy ? 'Saving…' : form.id ? 'Save changes' : 'Register company'}</button>
          {form.id && <button className="btn o" onClick={reset} disabled={busy}>New</button>}
        </div>
      </div>
    </div>
  )
}

function NotificationRouting() {
  const [data, setData] = useState<NotifMatrix | null>(null)
  const [draft, setDraft] = useState<Record<string, { inApp: boolean; email: boolean }>>({})
  const [error, setError] = useState('')
  const [info, setInfo] = useState('')
  const [saving, setSaving] = useState(false)

  const key = (userId: number, event: string) => userId + '|' + event

  const load = async () => {
    try {
      const d = await notificationSubscriptions()
      setData(d)
      const next: Record<string, { inApp: boolean; email: boolean }> = {}
      for (const u of d.users) for (const e of u.events) next[key(u.id, e.event)] = { inApp: e.inApp, email: e.email }
      setDraft(next)
    } catch (e: any) { setError(e.message) }
  }
  useEffect(() => { load() }, [])

  const toggle = (userId: number, event: string, channel: 'inApp' | 'email') => {
    const k = key(userId, event)
    setDraft(d => ({ ...d, [k]: { ...d[k], [channel]: !d[k][channel] } }))
    setInfo('')
  }

  // Only rows the administrator actually changed are sent. Anything still sitting on its role
  // default stays implicit, so a later change to an event's defaults still reaches everyone who
  // never explicitly opted out.
  const changed = (): NotifSaveRow[] => {
    if (!data) return []
    const out: NotifSaveRow[] = []
    for (const u of data.users) for (const e of u.events) {
      const d = draft[key(u.id, e.event)]
      if (d && (d.inApp !== e.inApp || d.email !== e.email))
        out.push({ userId: u.id, event: e.event, inApp: d.inApp, email: d.email })
    }
    return out
  }

  const save = async () => {
    setSaving(true); setError(''); setInfo('')
    try {
      const rows = changed()
      await saveNotificationSubscriptions(rows)
      setInfo(`Saved routing for ${rows.length} setting(s).`)
      load()
    } catch (e: any) { setError(e.message) }
    finally { setSaving(false) }
  }

  if (error && !data) return <div className="err">{error}</div>
  if (!data) return <div className="loading"><span className="spin" />loading notification routing…</div>

  const dirty = changed().length

  return (
    <>
      {error && <div className="err" style={{ marginBottom: 12 }}>{error}</div>}
      {info && <div className="note" style={{ marginBottom: 12 }}>{info}</div>}
      {!data.smtpConfigured && (
        <div className="note" style={{ marginBottom: 12 }}>
          <b>E-mail sending is switched off.</b> No SMTP relay is configured, so the Email column is
          recorded but nothing is sent — in-app notifications are unaffected. Set <code>SMTP_HOST</code> on
          the API server to turn the channel on; only events raised after that are e-mailed, the
          existing backlog is not flushed.
        </div>
      )}
      {!data.canEdit && (
        <div className="note" style={{ marginBottom: 12 }}>
          You can review who receives what, but changing it needs edit rights on Administration.
        </div>
      )}

      <div className="tablewrap">
        <div className="tbar">
          <div className="info">
            Who receives which alert, and on which channel. Boxes still on the role default are
            marked as such.
          </div>
          {data.canEdit && (
            <button className="btn p" disabled={!dirty || saving} onClick={save}>
              {saving ? 'Saving…' : dirty ? `Save ${dirty} change(s)` : 'Saved'}
            </button>
          )}
        </div>
        <div style={{ overflow: 'auto' }}>
          <table className="grid">
            <thead>
              <tr>
                <th rowSpan={2} style={{ minWidth: 190 }}>User</th>
                <th rowSpan={2} style={{ minWidth: 105 }}>Role</th>
                {data.events.map(e => (
                  <th key={e.key} colSpan={2} style={{ textAlign: 'center' }} title={e.description}>{e.title}</th>
                ))}
              </tr>
              <tr>
                {data.events.map(e => (
                  <Fragment key={e.key}>
                    <th style={{ width: 64, textAlign: 'center', fontWeight: 500 }}>In-app</th>
                    <th style={{ width: 64, textAlign: 'center', fontWeight: 500 }}>Email</th>
                  </Fragment>
                ))}
              </tr>
            </thead>
            <tbody>
              {data.users.map(u => {
                const inactive = u.status !== 'active'
                return (
                  <tr key={u.id}>
                    <td style={{ opacity: inactive ? .55 : 1, whiteSpace: 'normal' }}>
                      <b>{u.fullName}</b>
                      <div style={{ color: 'var(--muted)', fontSize: 11.5 }}>{u.email}</div>
                      {inactive && <span className="badge b-warn">disabled — receives nothing</span>}
                    </td>
                    <td style={{ opacity: inactive ? .55 : 1 }}>{u.role}</td>
                    {u.events.map(e => {
                      const d = draft[key(u.id, e.event)] ?? { inApp: e.inApp, email: e.email }
                      const onDefault = e.isDefault && d.inApp === e.inApp && d.email === e.email
                      return (
                        <Fragment key={e.event}>
                          <td style={{ textAlign: 'center' }}>
                            <input type="checkbox" checked={d.inApp} disabled={!data.canEdit || inactive}
                              onChange={() => toggle(u.id, e.event, 'inApp')} />
                            {onDefault && <div style={{ fontSize: 9, color: 'var(--muted)' }}>default</div>}
                          </td>
                          <td style={{ textAlign: 'center' }}>
                            <input type="checkbox" checked={d.email} disabled={!data.canEdit || inactive}
                              onChange={() => toggle(u.id, e.event, 'email')} />
                          </td>
                        </Fragment>
                      )
                    })}
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      </div>

      <div className="note" style={{ marginTop: 12 }}>
        {data.events.map(e => (
          <div key={e.key} style={{ marginBottom: 4 }}>
            <b>{e.title}</b> — {e.description} Default recipients: {e.defaultRoles.join(', ')}.
          </div>
        ))}
        Disabled users receive nothing on either channel, whatever is ticked here. Every change is
        written to the audit log.
      </div>
    </>
  )
}

function RoleManagement() {
  const [data, setData] = useState<RoleMatrix | null>(null)
  const [draft, setDraft] = useState<Record<string, RolePermRow[]>>({})
  const [error, setError] = useState('')
  const [info, setInfo] = useState('')
  const [saving, setSaving] = useState('')

  const load = () => rolePermissions()
    .then(d => {
      setData(d)
      const map: Record<string, RolePermRow[]> = {}
      d.roles.forEach(r => { map[r.role] = r.pages.map(p => ({ ...p })) })
      setDraft(map)
    })
    .catch(e => setError(e.message))
  useEffect(() => { load() }, [])

  function toggle(role: string, page: string, action: 'view' | 'insert' | 'edit' | 'delete') {
    setDraft(d => {
      const rows = d[role].map(r => {
        if (r.page !== page) return r
        const next = { ...r, [action]: !r[action] }
        // insert/edit/delete imply view — a page you cannot open cannot be acted on
        if ((action === 'insert' || action === 'edit' || action === 'delete') && next[action]) next.view = true
        if (action === 'view' && !next.view) { next.insert = false; next.edit = false; next.delete = false }
        return next
      })
      return { ...d, [role]: rows }
    })
  }

  async function save(role: string) {
    setSaving(role); setError(''); setInfo('')
    try {
      await saveRolePermissions(role, draft[role])
      setInfo(`Permissions saved for ${role}.`)
      // if we changed our own role, refresh our effective permissions immediately
      const profile = await me()
      setPermissions(profile.permissions ?? {})
      load()
    } catch (e: any) { setError(e.message) }
    finally { setSaving('') }
  }

  if (error && !data) return <div className="err">{error}</div>
  if (!data) return <div className="loading"><span className="spin" />loading role matrix…</div>

  const dirty = (role: string) => {
    const orig = data.roles.find(r => r.role === role)?.pages ?? []
    return JSON.stringify(orig) !== JSON.stringify(draft[role])
  }

  return (
    <>
      {error && <div className="err" style={{ marginBottom: 12 }}>{error}</div>}
      {info && <div className="note" style={{ marginBottom: 12 }}>{info}</div>}
      {!data.canEdit && (
        <div className="note" style={{ marginBottom: 12 }}>
          You can review the matrix, but only a <b>Super Admin</b> may change it — letting an
          administrator grant themselves extra rights would be privilege escalation.
        </div>
      )}

      {data.roles.map(r => (
        <div className="tablewrap" key={r.role} style={{ marginBottom: 14 }}>
          <div className="tbar">
            <div className="info">
              <b style={{ color: 'var(--navy)', fontSize: 13 }}>{r.role}</b>
              {r.locked && <span className="badge b-info" style={{ marginLeft: 8 }}>always full access</span>}
            </div>
            {!r.locked && data.canEdit && (
              <button className="btn p" disabled={!dirty(r.role) || saving === r.role}
                onClick={() => save(r.role)}>
                {saving === r.role ? 'Saving…' : dirty(r.role) ? 'Save changes' : 'Saved'}
              </button>
            )}
          </div>
          <div style={{ overflow: 'auto' }}>
            <table className="grid">
              <thead><tr>
                <th style={{ minWidth: 200 }}>Page</th>
                <th style={{ width: 80 }}>View</th>
                <th style={{ width: 90 }}>Insert</th>
                <th style={{ width: 90 }}>Edit</th>
                <th style={{ width: 90 }}>Delete</th>
                <th>What insert / edit / delete control</th>
              </tr></thead>
              <tbody>
                {data.pages.map(p => {
                  const row = draft[r.role]?.find(x => x.page === p.key)
                    ?? { page: p.key, view: false, insert: false, edit: false, delete: false }
                  const disabled = r.locked || !data.canEdit
                  return (
                    <tr key={p.key}>
                      <td>{p.title}</td>
                      <td><input type="checkbox" checked={row.view} disabled={disabled}
                        onChange={() => toggle(r.role, p.key, 'view')} /></td>
                      <td>{p.hasInsert
                        ? <input type="checkbox" checked={row.insert} disabled={disabled}
                          onChange={() => toggle(r.role, p.key, 'insert')} />
                        : <span style={{ color: 'var(--muted)' }}>—</span>}</td>
                      <td>{p.hasEdit
                        ? <input type="checkbox" checked={row.edit} disabled={disabled}
                          onChange={() => toggle(r.role, p.key, 'edit')} />
                        : <span style={{ color: 'var(--muted)' }}>—</span>}</td>
                      <td>{p.hasDelete
                        ? <input type="checkbox" checked={row.delete} disabled={disabled}
                          onChange={() => toggle(r.role, p.key, 'delete')} />
                        : <span style={{ color: 'var(--muted)' }}>—</span>}</td>
                      <td style={{ color: 'var(--muted)', fontSize: 11.5, whiteSpace: 'normal' }}>
                        {[p.insertMeans && `Insert: ${p.insertMeans}`, p.editMeans && `Edit: ${p.editMeans}`, p.deleteMeans && `Delete: ${p.deleteMeans}`]
                          .filter(Boolean).join(' · ') || 'view only'}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        </div>
      ))}
      <div className="note">
        Permissions are enforced by the API, not just hidden in the UI — a revoked page returns 403
        even if called directly. Ticking Insert, Edit or Delete automatically grants View, since a page you
        cannot open cannot be acted on. Delete permanently removes ingested report rows and is
        off for every role except Super Admin by default. Every change is audited and alerts the administrators.
      </div>
    </>
  )
}

type SiteOpt = { id: number; companyId: number | null; name: string }

/**
 * Tick the sites a user is linked to, grouped by company — only the companies the user is
 * assigned to are offered. "All sites" (Super-Admin-granted) covers every site of those companies.
 */
function SiteChecklist({ cos, sites, allSites, siteIds, canGrantAll, onChange }:
  { cos: Company[]; sites: SiteOpt[]; allSites: boolean; siteIds: number[]; canGrantAll: boolean
    onChange: (allSites: boolean, siteIds: number[]) => void }) {
  const toggle = (id: number) => onChange(false, siteIds.includes(id) ? siteIds.filter(x => x !== id) : [...siteIds, id])
  return (
    <div>
      {(canGrantAll || allSites) && (
        <label className="cochk" style={{ display: 'flex', margin: '0 0 6px' }}>
          <input type="checkbox" checked={allSites} disabled={!canGrantAll} onChange={e => onChange(e.target.checked, [])} />
          All sites of the assigned company/companies
        </label>
      )}
      {!allSites && cos.map(c => {
        const cs = sites.filter(st => st.companyId === c.id)
        return (
          <div key={c.id} style={{ marginBottom: 6 }}>
            <div style={{ fontSize: 12, fontWeight: 600 }}>{c.name}</div>
            <div className="cosel">
              {cs.map(st => (
                <label key={st.id} className="cochk">
                  <input type="checkbox" checked={siteIds.includes(st.id)} onChange={() => toggle(st.id)} /> {st.name}
                </label>
              ))}
              {cs.length === 0 && <span className="note">No sites yet — add one in the Master Data tab.</span>}
            </div>
          </div>
        )
      })}
      {!allSites && cos.length === 0 && <span className="note">Choose a company first to list its sites.</span>}
    </div>
  )
}

function Users() {
  const [users, setUsers] = useState<AdminUser[]>([])
  const [roles, setRoles] = useState<string[]>([])
  const [cos, setCos] = useState<Company[]>([])
  const [sites, setSites] = useState<SiteOpt[]>([])
  const [error, setError] = useState('')
  const [info, setInfo] = useState('')
  const [form, setForm] = useState({ email: '', fullName: '', role: 'Site BC User', allSites: false, siteIds: [] as number[], password: '', companyIds: [] as number[] })
  const [showForm, setShowForm] = useState(false)
  const [editCosFor, setEditCosFor] = useState<number | null>(null)
  const [editCos, setEditCos] = useState<number[]>([])
  const [editSitesFor, setEditSitesFor] = useState<number | null>(null)
  const [editSites, setEditSites] = useState<{ allSites: boolean; siteIds: number[] }>({ allSites: false, siteIds: [] })
  const iAmSuper = getUser()?.role === 'Super Admin'

  const refresh = () => {
    adminUsers().then(d => { setUsers(d.users); setRoles(d.roles) }).catch(e => setError(e.message))
    listCompanies().then(setCos).catch(() => { /* non-fatal */ })
    adminMaster().then((d: any) => setSites(d.sites ?? [])).catch(() => { /* non-fatal */ })
  }
  useEffect(() => { refresh() }, [])

  const activeCos = cos.filter(c => c.active)
  const isSuperForm = form.role === 'Super Admin'
  const siteName = (id: number) => sites.find(st => st.id === id)?.name
  // A site belongs to a company: unticking a company drops its sites from the selection.
  const keepSitesOf = (companyIds: number[], siteIds: number[]) =>
    siteIds.filter(id => companyIds.includes(sites.find(st => st.id === id)?.companyId ?? -1))
  const toggleFormCompany = (id: number) => setForm(f => {
    const companyIds = f.companyIds.includes(id) ? f.companyIds.filter(x => x !== id) : [...f.companyIds, id]
    return { ...f, companyIds, siteIds: keepSitesOf(companyIds, f.siteIds) }
  })

  async function create() {
    setError(''); setInfo('')
    try {
      await adminCreateUser({
        email: form.email, fullName: form.fullName, role: form.role,
        allSites: form.allSites, siteIds: form.allSites ? [] : form.siteIds,
        password: form.password,
        companyIds: isSuperForm ? [] : form.companyIds
      })
      setInfo(`User ${form.email} created.`)
      setShowForm(false)
      setForm({ ...form, email: '', fullName: '', password: '', companyIds: [], siteIds: [], allSites: false })
      refresh()
    } catch (e: any) { setError(e.message) }
  }

  function openCompanyEdit(u: AdminUser) {
    if (editCosFor === u.id) { setEditCosFor(null); return }
    setEditCosFor(u.id); setEditSitesFor(null); setEditCos(u.companyIds ?? []); setError(''); setInfo('')
  }
  async function saveCompanyEdit(u: AdminUser) {
    setError('')
    const dropped = (u.siteIds ?? []).filter(id => !keepSitesOf(editCos, [id]).length).map(siteName)
    if (dropped.length && !window.confirm(`This also removes ${u.email} from site(s) ${dropped.join(', ')}. Continue?`)) return
    try { await setUserCompanies(u.id, editCos); setInfo('Company assignment updated.'); setEditCosFor(null); refresh() }
    catch (e: any) { setError(e.message) }
  }
  const toggleEditCompany = (cid: number) =>
    setEditCos(cs => cs.includes(cid) ? cs.filter(x => x !== cid) : [...cs, cid])

  function openSitesEdit(u: AdminUser) {
    if (editSitesFor === u.id) { setEditSitesFor(null); return }
    setEditSitesFor(u.id); setEditCosFor(null)
    setEditSites({ allSites: u.allSites, siteIds: u.siteIds ?? [] })
    setError(''); setInfo('')
  }
  async function saveSitesEdit(u: AdminUser) {
    setError('')
    try {
      await setUserSites(u.id, editSites.allSites, editSites.allSites ? [] : editSites.siteIds)
      setInfo(`Sites updated for ${u.email}.`); setEditSitesFor(null); refresh()
    } catch (e: any) { setError(e.message) }
  }

  async function toggleStatus(u: AdminUser) {
    setError('')
    try { await adminSetStatus(u.id, u.status === 'active' ? 'disabled' : 'active'); refresh() }
    catch (e: any) { setError(e.message) }
  }

  async function reset(u: AdminUser) {
    const pw = window.prompt(`New password for ${u.email} (min 8 chars):`)
    if (!pw) return
    setError('')
    try { await adminResetPassword(u.id, pw); setInfo(`Password reset for ${u.email}.`) }
    catch (e: any) { setError(e.message) }
  }

  return (
    <div className="tablewrap">
      <div className="tbar">
        <div className="info"><b>{users.length}</b> users · <b>{users.filter(u => u.status === 'active').length}</b> active — no hard delete, disable only (PRD §6.3)</div>
        <button className="btn r" onClick={() => setShowForm(v => !v)}>{showForm ? 'Cancel' : '+ Add user'}</button>
      </div>
      {error && <div className="err" style={{ margin: 12 }}>{error}</div>}
      {info && <div className="note" style={{ margin: 12 }}>{info}</div>}
      {showForm && (
        <div className="filters" style={{ margin: 12, border: '1px dashed var(--line)' }}>
          <div className="f"><label>Email</label><input value={form.email} onChange={e => setForm({ ...form, email: e.target.value })} placeholder="name@energi-up.com" /></div>
          <div className="f"><label>Full name</label><input value={form.fullName} onChange={e => setForm({ ...form, fullName: e.target.value })} /></div>
          <div className="f"><label>Role</label>
            <select value={form.role} onChange={e => setForm({ ...form, role: e.target.value })}>
              {roles.map(r => <option key={r}>{r}</option>)}
            </select>
          </div>
          <div className="f"><label>Initial password</label><input type="password" value={form.password} onChange={e => setForm({ ...form, password: e.target.value })} /></div>
          {isSuperForm
            ? <div className="f" style={{ flexBasis: '100%' }}><label>Companies &amp; sites</label><span className="note">A Super Admin spans every site of every company automatically.</span></div>
            : <>
                <div className="f" style={{ flexBasis: '100%' }}><label>Companies (assign at least one)</label>
                  <div className="cosel">
                    {activeCos.map(c => (
                      <label key={c.id} className="cochk">
                        <input type="checkbox" checked={form.companyIds.includes(c.id)} onChange={() => toggleFormCompany(c.id)} /> {c.name}
                      </label>
                    ))}
                    {activeCos.length === 0 && <span className="note">No active companies — register one in the Companies tab first.</span>}
                  </div>
                </div>
                <div className="f" style={{ flexBasis: '100%' }}><label>Sites (assign at least one) — decides which rows, reports &amp; columns the user sees</label>
                  <SiteChecklist cos={activeCos.filter(c => form.companyIds.includes(c.id))} sites={sites}
                    allSites={form.allSites} siteIds={form.siteIds} canGrantAll={iAmSuper}
                    onChange={(allSites, siteIds) => setForm({ ...form, allSites, siteIds })} />
                </div>
              </>}
          <button className="btn p" onClick={create}>Create</button>
        </div>
      )}
      <div className="gridscroll">
        <table className="grid">
          <thead><tr><th>Name</th><th>Email</th><th>Role</th><th>Companies</th><th>Sites</th><th>Status</th><th>Actions</th></tr></thead>
          <tbody>
            {users.map(u => {
              const superU = u.role === 'Super Admin'
              return (
              <Fragment key={u.id}>
              <tr>
                <td>{u.fullName}</td>
                <td>{u.email}</td>
                <td>{u.role}</td>
                <td style={{ whiteSpace: 'normal' }}>{superU ? <span style={{ color: 'var(--muted)' }}>All companies</span> : (u.companyNames || <span className="badge b-warn">none</span>)}</td>
                <td style={{ whiteSpace: 'normal' }}>{u.allSites ? <span style={{ color: 'var(--muted)' }}>All sites</span> : (u.siteNames || <span className="badge b-warn">none</span>)}</td>
                <td><span className={'badge ' + (u.status === 'active' ? 'b-ok' : 'b-err')}>{u.status}</span></td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  <button className="btn o" style={{ padding: '3px 9px', fontSize: 11.5, marginRight: 6 }} onClick={() => toggleStatus(u)}>
                    {u.status === 'active' ? 'Disable' : 'Enable'}
                  </button>
                  <button className="btn o" style={{ padding: '3px 9px', fontSize: 11.5, marginRight: 6 }} onClick={() => reset(u)}>Reset</button>
                  {!superU && <button className="btn o" style={{ padding: '3px 9px', fontSize: 11.5, marginRight: 6 }} onClick={() => openCompanyEdit(u)}>
                    {editCosFor === u.id ? 'Close' : 'Companies'}
                  </button>}
                  {!superU && <button className="btn o" style={{ padding: '3px 9px', fontSize: 11.5 }} onClick={() => openSitesEdit(u)}>
                    {editSitesFor === u.id ? 'Close' : 'Sites'}
                  </button>}
                </td>
              </tr>
              {editSitesFor === u.id && (
                <tr>
                  <td colSpan={7} style={{ background: '#f6f8fa', whiteSpace: 'normal' }}>
                    <SiteChecklist cos={cos.filter(c => (u.companyIds ?? []).includes(c.id))} sites={sites}
                      allSites={editSites.allSites} siteIds={editSites.siteIds} canGrantAll={iAmSuper}
                      onChange={(allSites, siteIds) => setEditSites({ allSites, siteIds })} />
                    <button className="btn p" style={{ padding: '4px 12px', fontSize: 12, marginTop: 6 }}
                      disabled={!editSites.allSites && editSites.siteIds.length === 0}
                      onClick={() => saveSitesEdit(u)}>Save sites</button>
                  </td>
                </tr>
              )}
              {editCosFor === u.id && (
                <tr>
                  <td colSpan={7} style={{ background: '#f6f8fa', whiteSpace: 'normal' }}>
                    <div className="cosel" style={{ marginBottom: 8 }}>
                      {activeCos.map(c => (
                        <label key={c.id} className="cochk">
                          <input type="checkbox" checked={editCos.includes(c.id)} onChange={() => toggleEditCompany(c.id)} /> {c.name}
                        </label>
                      ))}
                    </div>
                    <button className="btn p" style={{ padding: '4px 12px', fontSize: 12 }} onClick={() => saveCompanyEdit(u)}>Save assignment</button>
                  </td>
                </tr>
              )}
              </Fragment>
            )})}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function Master() {
  const [data, setData] = useState<any>(null)
  const [cos, setCos] = useState<Company[]>([])
  const [error, setError] = useState('')

  const refresh = () => {
    adminMaster().then(setData).catch(e => setError(e.message))
    listCompanies().then(setCos).catch(() => { /* non-fatal */ })
  }
  useEffect(() => { refresh() }, [])
  const activeCos = cos.filter(c => c.active)

  async function run(fn: () => Promise<any>) {
    setError('')
    try { await fn(); refresh() } catch (e: any) { setError(e.message) }
  }

  if (error && !data) return <div className="err">{error}</div>
  if (!data) return <div className="loading"><span className="spin" />loading…</div>

  // The Company → Sites/Permits tree. Companies come from the Companies tab.
  const sitesOf = (companyId: number) => data.sites.filter((s: any) => s.companyId === companyId)
  // Show every active company, plus any (inactive) company that still owns sites.
  const shownCos = [...activeCos, ...cos.filter(c => !c.active && sitesOf(c.id).length > 0)]

  return (
    <>
      {error && <div className="err" style={{ marginBottom: 12 }}>{error}</div>}
      <div className="note" style={{ marginBottom: 12 }}>
        Company → Sites &amp; TPB permits. Users are linked to sites in User Management; a site decides which rows, reports and columns its users see.
        Use <b>Reports</b> on a site to choose which reports — and which columns of each — its users see on Reports and Inventory Movement.
        TPB permits also auto-register from ingestion; test entries are blocked on save (FR-A4).
      </div>

      {shownCos.map(c => (
        <div className="panel" key={c.id} style={{ marginBottom: 14 }}>
          <h3 style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            {c.name}
            {!c.active && <span className="badge b-warn">inactive</span>}
            <small style={{ color: 'var(--muted)', fontWeight: 400 }}>· {sitesOf(c.id).length} site{sitesOf(c.id).length === 1 ? '' : 's'}</small>
          </h3>
          <CompanyNode company={c}
            sites={sitesOf(c.id)}
            permits={data.permits.filter((p: any) => p.companyId === c.id)}
            reports={data.reports ?? []} siteReports={data.siteReports ?? {}} siteColumns={data.siteColumns ?? {}}
            run={run} />
        </div>
      ))}
    </>
  )
}

type SiteReportField = { name: string; label: string; group: string; optIn: boolean }
type SiteReport = { key: string; title: string; page: 'reports' | 'movement'; fields: SiteReportField[] }
/** The columns a site starts from for a report: everything except opt-in columns. */
const starterColumns = (r: SiteReport) => r.fields.filter(f => !f.optIn).map(f => f.name)
const REPORT_PAGES: { page: SiteReport['page']; title: string }[] = [
  { page: 'reports', title: 'Reports' },
  { page: 'movement', title: 'Inventory Movement' },
]

/** One company in the Master-data tree: its sites and TPB permits, each with an inline add control. */
function CompanyNode({ company, sites, permits, reports, siteReports, siteColumns, run }:
  { company: Company; sites: any[]; permits: any[]; reports: SiteReport[]; siteReports: Record<string, string[]>
    siteColumns: Record<string, Record<string, string[]>>; run: (fn: () => Promise<any>) => Promise<void> }) {
  const [siteName, setSiteName] = useState('')
  const [reportsFor, setReportsFor] = useState<number | null>(null)

  // Per page, nothing ticked means every report on that page is shown.
  const summary = (keys: string[]) => REPORT_PAGES.map(pg => {
    const all = reports.filter(r => r.page === pg.page)
    const n = all.filter(r => keys.includes(r.key)).length
    return `${pg.title}: ${n === 0 ? 'all' : `${n} of ${all.length}`}`
  }).join(' · ')
  const [permitNo, setPermitNo] = useState('')
  const [permitSite, setPermitSite] = useState('')

  return (
    <div className="row2" style={{ gridTemplateColumns: '1.6fr 1fr', gap: 14 }}>
      <div>
        <div style={{ fontSize: 12, color: 'var(--muted)', marginBottom: 4 }}>Sites</div>
        {sites.map(s => {
          const keys = siteReports[String(s.id)] ?? []
          return (
            <Fragment key={s.id}>
              <div className="item" style={{ alignItems: 'center', gap: 8 }}>
                <span>{s.name} <small style={{ color: 'var(--muted)' }}>· {fmtInt(s.users)} user{s.users === 1 ? '' : 's'}</small>
                  <br /><small style={{ color: 'var(--muted)' }}>{summary(keys)}</small></span>
                {can('admin', 'edit') && (
                  <button className="btn o" style={{ padding: '3px 9px', fontSize: 11.5 }}
                    onClick={() => setReportsFor(reportsFor === s.id ? null : s.id)}>
                    {reportsFor === s.id ? 'Close' : 'Reports'}
                  </button>
                )}
              </div>
              {reportsFor === s.id && (
                <SiteReportsEditor site={s} reports={reports} initial={keys} initialColumns={siteColumns[String(s.id)] ?? {}}
                  onSave={(next, cols) => run(async () => { await adminSetSiteReports(s.id, next, cols); setReportsFor(null) })} />
              )}
            </Fragment>
          )
        })}
        {sites.length === 0 && <div className="note" style={{ marginBottom: 4 }}>No sites yet.</div>}
        {company.active && (
          <div style={{ display: 'flex', gap: 6, marginTop: 6 }}>
            <input placeholder="Site name (e.g. BONTANG)" style={{ flex: 1 }} value={siteName} onChange={e => setSiteName(e.target.value)} />
            <button className="btn o" disabled={!siteName} style={{ padding: '4px 10px', fontSize: 12 }}
              onClick={() => run(async () => { await adminAddSite(company.id, siteName); setSiteName('') })}>+ Site</button>
          </div>
        )}
      </div>
      <div>
        <div style={{ fontSize: 12, color: 'var(--muted)', marginBottom: 4 }}>TPB permits</div>
        {permits.map(p => (
          <div className="item" key={p.id}>
            <span>{p.permitNo} {p.siteName && <small style={{ color: 'var(--muted)' }}>· {p.siteName}</small>}</span>
            <small style={{ color: 'var(--muted)' }}>{fmtInt(p.documents)} docs</small>
          </div>
        ))}
        {permits.length === 0 && <div className="note" style={{ marginBottom: 4 }}>No permits.</div>}
        {company.active && (
          <div style={{ display: 'flex', gap: 6, marginTop: 6, flexWrap: 'wrap' }}>
            <input placeholder="Permit no (e.g. 99/MK/WBC.16/2026)" style={{ flex: 1, minWidth: 150 }} value={permitNo} onChange={e => setPermitNo(e.target.value)} />
            <select value={permitSite} onChange={e => setPermitSite(e.target.value)}>
              <option value="">Company-level</option>
              {sites.map(s => <option key={s.id} value={String(s.id)}>{s.name}</option>)}
            </select>
            <button className="btn o" disabled={!permitNo} style={{ padding: '4px 10px', fontSize: 12 }}
              onClick={() => run(async () => { await adminAddPermit(company.id, permitSite ? Number(permitSite) : null, permitNo); setPermitNo(''); setPermitSite('') })}>+ Permit</button>
          </div>
        )}
      </div>
    </div>
  )
}

/**
 * Pick the reports a site's users see on Reports and Inventory Movement, and the columns of each.
 * Each page is decided on its own: leaving a page with nothing ticked keeps every report on it
 * visible. Columns start from the report's starter set; opt-in columns can be switched on.
 */
function SiteReportsEditor({ site, reports, initial, initialColumns, onSave }:
  { site: { id: number; name: string }; reports: SiteReport[]; initial: string[]
    initialColumns: Record<string, string[]>; onSave: (keys: string[], columns: Record<string, string[]>) => Promise<void> }) {
  const [keys, setKeys] = useState<string[]>(initial)
  // Only customised reports are held here; a report absent from the map uses its starter set.
  const [columns, setColumns] = useState<Record<string, string[]>>(initialColumns)
  const [colsFor, setColsFor] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const colsOf = (r: SiteReport) => columns[r.key] ?? starterColumns(r)
  const setColsOf = (r: SiteReport, next: string[]) => setColumns(c => ({ ...c, [r.key]: next }))
  const toggle = (k: string) => setKeys(ks => ks.includes(k) ? ks.filter(x => x !== k) : [...ks, k])
  const setPage = (page: string, on: boolean) => {
    const pageKeys = reports.filter(r => r.page === page).map(r => r.key)
    setKeys(ks => on ? [...new Set([...ks, ...pageKeys])] : ks.filter(k => !pageKeys.includes(k)))
  }
  async function save() {
    setSaving(true)
    try { await onSave(keys, columns) } finally { setSaving(false) }
  }

  return (
    <div style={{ background: '#f6f8fa', border: '1px solid var(--line)', borderRadius: 6, padding: 10, margin: '4px 0 8px' }}>
      <div style={{ fontSize: 12, marginBottom: 8 }}>
        Reports shown to users of <b>{site.name}</b>. Leave a page with nothing ticked to show all of its reports.
      </div>
      <div className="row2" style={{ gridTemplateColumns: '1fr 1fr', gap: 12 }}>
        {REPORT_PAGES.map(pg => {
          const list = reports.filter(r => r.page === pg.page)
          const n = list.filter(r => keys.includes(r.key)).length
          return (
            <div key={pg.page}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
                <b style={{ fontSize: 12 }}>{pg.title}</b>
                <small style={{ color: 'var(--muted)' }}>
                  {n === 0 ? 'all shown' : `${n} of ${list.length}`}
                  {' · '}<a style={{ cursor: 'pointer' }} onClick={() => setPage(pg.page, n !== list.length)}>{n === list.length ? 'clear' : 'tick all'}</a>
                </small>
              </div>
              {list.map(r => {
                const shown = colsOf(r)
                const custom = r.key in columns && !sameSet(shown, starterColumns(r))
                return (
                  <Fragment key={r.key}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 6, margin: '2px 0' }}>
                      <label className="cochk" style={{ display: 'flex', flex: 1, margin: 0, fontSize: 12 }}>
                        <input type="checkbox" checked={keys.includes(r.key)} onChange={() => toggle(r.key)} /> {r.title}
                      </label>
                      <a style={{ cursor: 'pointer', fontSize: 11, whiteSpace: 'nowrap' }}
                        title="Choose which columns this site sees"
                        onClick={() => setColsFor(colsFor === r.key ? null : r.key)}>
                        {colsFor === r.key ? 'close' : `columns ${shown.length}/${r.fields.length}`}{custom ? ' ●' : ''}
                      </a>
                    </div>
                    {colsFor === r.key && (
                      <ColumnPicker report={r} shown={shown} onChange={next => setColsOf(r, next)} />
                    )}
                  </Fragment>
                )
              })}
            </div>
          )
        })}
      </div>
      <button className="btn p" style={{ padding: '4px 12px', fontSize: 12, marginTop: 8 }} disabled={saving} onClick={save}>
        {saving ? 'Saving…' : 'Save reports'}
      </button>
    </div>
  )
}

const sameSet = (a: string[], b: string[]) => a.length === b.length && a.every(x => b.includes(x))

/**
 * The columns one site sees for one report, grouped as the upload template groups them. Opt-in
 * columns (e.g. BC 2.7 Out's Dokumen Asal Barang) start unticked.
 */
function ColumnPicker({ report, shown, onChange }:
  { report: SiteReport; shown: string[]; onChange: (next: string[]) => void }) {
  const groups = [...new Set(report.fields.map(f => f.group))]
  const toggle = (name: string) => onChange(shown.includes(name) ? shown.filter(x => x !== name) : [...shown, name])
  const setGroup = (g: string, on: boolean) => {
    const names = report.fields.filter(f => f.group === g).map(f => f.name)
    onChange(on ? [...new Set([...shown, ...names])] : shown.filter(x => !names.includes(x)))
  }
  const isStarter = sameSet(shown, starterColumns(report))
  return (
    <div style={{ background: '#fff', border: '1px dashed var(--line)', borderRadius: 6, padding: 8, margin: '2px 0 8px 18px', fontSize: 12 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 4 }}>
        <span>{shown.length} of {report.fields.length} columns {isStarter ? <small style={{ color: 'var(--muted)' }}>(starter)</small> : <span className="badge b-warn">custom</span>}</span>
        {!isStarter && <a style={{ cursor: 'pointer' }} onClick={() => onChange(starterColumns(report))}>reset to starter</a>}
      </div>
      {groups.map(g => {
        const fs = report.fields.filter(f => f.group === g)
        const all = fs.every(f => shown.includes(f.name))
        const optIn = fs.some(f => f.optIn)
        return (
          <div key={g} style={{ marginTop: 4 }}>
            <label className="cochk" style={{ display: 'flex', margin: 0, fontWeight: 600 }}>
              <input type="checkbox" checked={all} onChange={() => setGroup(g, !all)} /> {g || 'Other'}
              {optIn && <span className="badge b-ok" style={{ marginLeft: 6 }}>optional</span>}
            </label>
            {fs.map(f => (
              <label key={f.name} className="cochk" style={{ display: 'flex', margin: '1px 0 1px 18px' }} title={`upload field: ${f.name}`}>
                <input type="checkbox" checked={shown.includes(f.name)}
                  disabled={shown.length === 1 && shown.includes(f.name)}
                  onChange={() => toggle(f.name)} /> {f.label.startsWith(g + ' / ') ? f.label.slice(g.length + 3) : f.label}
              </label>
            ))}
          </div>
        )
      })}
    </div>
  )
}
