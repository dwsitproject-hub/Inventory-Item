import { Fragment, useEffect, useState } from 'react'
import {
  AdminUser, NotifMatrix, NotifSaveRow, RoleMatrix, RolePermRow, adminAddEntity, adminAddPermit,
  adminAddSite, adminCreateUser, adminMaster, adminResetPassword, adminSetStatus, adminUsers, can,
  me, notificationSubscriptions, rolePermissions, saveNotificationSubscriptions,
  saveRolePermissions, setPermissions
} from '../api'
import { fmtInt } from '../format'

export default function Admin() {
  const [tab, setTab] = useState<'users' | 'roles' | 'notifications' | 'master'>('users')
  return (
    <>
      <h1 className="page">Administration</h1>
      <div className="crumb">Users, roles &amp; access scope · page permissions · notification routing · master data governance</div>
      <div className="chips" style={{ marginBottom: 14 }}>
        <span className={'chk' + (tab === 'users' ? ' on' : '')} onClick={() => setTab('users')}>User Management</span>
        <span className={'chk' + (tab === 'roles' ? ' on' : '')} onClick={() => setTab('roles')}>Role Management</span>
        <span className={'chk' + (tab === 'notifications' ? ' on' : '')} onClick={() => setTab('notifications')}>Notifications</span>
        <span className={'chk' + (tab === 'master' ? ' on' : '')} onClick={() => setTab('master')}>Master Data</span>
      </div>
      {tab === 'users' ? <Users />
        : tab === 'roles' ? <RoleManagement />
        : tab === 'notifications' ? <NotificationRouting />
        : <Master />}
    </>
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

  function toggle(role: string, page: string, action: 'view' | 'insert' | 'edit') {
    setDraft(d => {
      const rows = d[role].map(r => {
        if (r.page !== page) return r
        const next = { ...r, [action]: !r[action] }
        // insert/edit imply view — a page you cannot open cannot be acted on
        if ((action === 'insert' || action === 'edit') && next[action]) next.view = true
        if (action === 'view' && !next.view) { next.insert = false; next.edit = false }
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
                <th style={{ width: 90 }}>View</th>
                <th style={{ width: 110 }}>Insert</th>
                <th style={{ width: 110 }}>Edit</th>
                <th>What insert / edit control</th>
              </tr></thead>
              <tbody>
                {data.pages.map(p => {
                  const row = draft[r.role]?.find(x => x.page === p.key)
                    ?? { page: p.key, view: false, insert: false, edit: false }
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
                      <td style={{ color: 'var(--muted)', fontSize: 11.5, whiteSpace: 'normal' }}>
                        {[p.insertMeans && `Insert: ${p.insertMeans}`, p.editMeans && `Edit: ${p.editMeans}`]
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
        even if called directly. Ticking Insert or Edit automatically grants View, since a page you
        cannot open cannot be acted on. Every change is audited and alerts the administrators.
      </div>
    </>
  )
}

function Users() {
  const [users, setUsers] = useState<AdminUser[]>([])
  const [roles, setRoles] = useState<string[]>([])
  const [error, setError] = useState('')
  const [info, setInfo] = useState('')
  const [form, setForm] = useState({ email: '', fullName: '', role: 'Site BC User', allEntities: false, entityId: '1', password: '' })
  const [showForm, setShowForm] = useState(false)

  const refresh = () => adminUsers().then(d => { setUsers(d.users); setRoles(d.roles) }).catch(e => setError(e.message))
  useEffect(() => { refresh() }, [])

  async function create() {
    setError(''); setInfo('')
    try {
      await adminCreateUser({
        email: form.email, fullName: form.fullName, role: form.role,
        allEntities: form.allEntities,
        entityId: form.allEntities ? null : Number(form.entityId), siteId: null,
        password: form.password
      })
      setInfo(`User ${form.email} created.`)
      setShowForm(false)
      setForm({ ...form, email: '', fullName: '', password: '' })
      refresh()
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
          <div className="f"><label>Scope</label>
            <select value={form.allEntities ? 'all' : 'entity'} onChange={e => setForm({ ...form, allEntities: e.target.value === 'all' })}>
              <option value="entity">Entity #1 (PT EUP)</option>
              <option value="all">All entities (Super Admin only)</option>
            </select>
          </div>
          <div className="f"><label>Initial password</label><input type="password" value={form.password} onChange={e => setForm({ ...form, password: e.target.value })} /></div>
          <button className="btn p" onClick={create}>Create</button>
        </div>
      )}
      <div className="gridscroll">
        <table className="grid">
          <thead><tr><th>Name</th><th>Email</th><th>Role</th><th>Scope</th><th>Status</th><th>Actions</th></tr></thead>
          <tbody>
            {users.map(u => (
              <tr key={u.id}>
                <td>{u.fullName}</td>
                <td>{u.email}</td>
                <td>{u.role}</td>
                <td>{u.allEntities ? 'All entities' : `${u.entityName ?? 'entity #' + u.entityId}${u.siteName ? ' · ' + u.siteName : ''}`}</td>
                <td><span className={'badge ' + (u.status === 'active' ? 'b-ok' : 'b-err')}>{u.status}</span></td>
                <td>
                  <button className="btn o" style={{ padding: '3px 9px', fontSize: 11.5, marginRight: 6 }} onClick={() => toggleStatus(u)}>
                    {u.status === 'active' ? 'Disable' : 'Enable'}
                  </button>
                  <button className="btn o" style={{ padding: '3px 9px', fontSize: 11.5 }} onClick={() => reset(u)}>Reset</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function Master() {
  const [data, setData] = useState<any>(null)
  const [error, setError] = useState('')
  const [ent, setEnt] = useState({ code: '', name: '' })
  const [site, setSite] = useState({ entityId: '1', name: '' })
  const [permit, setPermit] = useState({ entityId: '1', permitNo: '' })

  const refresh = () => adminMaster().then(setData).catch(e => setError(e.message))
  useEffect(() => { refresh() }, [])

  async function run(fn: () => Promise<any>) {
    setError('')
    try { await fn(); refresh() } catch (e: any) { setError(e.message) }
  }

  if (error && !data) return <div className="err">{error}</div>
  if (!data) return <div className="loading"><span className="spin" />loading…</div>

  return (
    <>
      {error && <div className="err" style={{ marginBottom: 12 }}>{error}</div>}
      <div className="row2" style={{ gridTemplateColumns: '1fr 1fr' }}>
        <div className="panel">
          <h3>Entities (PT)</h3>
          {data.entities.map((e: any) => <div className="item" key={e.id}><span><b>{e.code}</b> — {e.name}</span></div>)}
          <div style={{ display: 'flex', gap: 8, marginTop: 10 }}>
            <input placeholder="Code" style={{ width: 90 }} value={ent.code} onChange={e => setEnt({ ...ent, code: e.target.value })} className="f-input" />
            <input placeholder="Name" style={{ flex: 1 }} value={ent.name} onChange={e => setEnt({ ...ent, name: e.target.value })} />
            <button className="btn p" onClick={() => run(() => adminAddEntity(ent.code, ent.name))}>Add</button>
          </div>
        </div>
        <div className="panel">
          <h3>Sites</h3>
          {data.sites.map((s: any) => <div className="item" key={s.id}><span>{s.name} <small style={{ color: 'var(--muted)' }}>({s.entityName})</small></span></div>)}
          <div style={{ display: 'flex', gap: 8, marginTop: 10 }}>
            <select value={site.entityId} onChange={e => setSite({ ...site, entityId: e.target.value })}>
              {data.entities.map((e: any) => <option key={e.id} value={e.id}>{e.code}</option>)}
            </select>
            <input placeholder="Site name" style={{ flex: 1 }} value={site.name} onChange={e => setSite({ ...site, name: e.target.value })} />
            <button className="btn p" onClick={() => run(() => adminAddSite(Number(site.entityId), site.name))}>Add</button>
          </div>
        </div>
      </div>
      <div className="panel">
        <h3>TPB permits <small style={{ color: 'var(--muted)', fontWeight: 400 }}>· auto-registered from ingestion; test entries blocked on save (FR-A4)</small></h3>
        <div className="gridscroll" style={{ maxHeight: '38vh' }}>
          <table className="grid">
            <thead><tr><th>TPB Permit No</th><th>Entity</th><th>Site</th><th>Documents</th></tr></thead>
            <tbody>
              {data.permits.map((p: any) => (
                <tr key={p.id}>
                  <td>{p.permitNo}</td><td>{p.entityName}</td><td>{p.siteName ?? '—'}</td>
                  <td className="num">{fmtInt(p.documents)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div style={{ display: 'flex', gap: 8, marginTop: 10 }}>
          <select value={permit.entityId} onChange={e => setPermit({ ...permit, entityId: e.target.value })}>
            {data.entities.map((e: any) => <option key={e.id} value={e.id}>{e.code}</option>)}
          </select>
          <input placeholder="Permit no (e.g. 99/MK/WBC.16/2026)" style={{ flex: 1 }} value={permit.permitNo} onChange={e => setPermit({ ...permit, permitNo: e.target.value })} />
          <button className="btn p" onClick={() => run(() => adminAddPermit(Number(permit.entityId), null, permit.permitNo))}>Add</button>
        </div>
      </div>
    </>
  )
}
