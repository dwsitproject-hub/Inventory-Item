const BASE = '/api/v1'

/** `name` is the verbatim upload header (stable key for queries, sorts, saved views);
 *  `label` is the customs-terminology display name. */
export type FieldMeta = { name: string; label: string; type: 'text' | 'number' | 'date'; group: string }
export type ReportMeta = {
  key: string; title: string; template: string
  /** which screen hosts it: 'reports' (customs documents) or 'movement' (inventory movement) */
  page: 'reports' | 'movement'
  defaults: string[]; fields: FieldMeta[]
}
export type SortSpec = { field: string; dir: 'asc' | 'desc' }

export type User = {
  email: string; fullName: string; role: string
  allEntities: boolean; entityId: number | null; siteId: number | null
}

export type PagePerm = { view: boolean; insert: boolean; edit: boolean }
export type PermissionMap = Record<string, PagePerm>

const NO_PERM: PagePerm = { view: false, insert: false, edit: false }

export function getPermissions(): PermissionMap {
  const raw = sessionStorage.getItem('bc.perms')
  return raw ? JSON.parse(raw) as PermissionMap : {}
}
/** True once permissions have been fetched for this session (see App bootstrap). */
export function hasPermissions(): boolean {
  return sessionStorage.getItem('bc.perms') !== null
}
/**
 * Effective permission for a page.
 * Super Admin always passes — it holds full access server-side and must never be
 * lockable out of the UI. Everything else is denied unless explicitly granted.
 */
export function can(page: string, action: keyof PagePerm = 'view'): boolean {
  if (getUser()?.role === 'Super Admin') return true
  const p = getPermissions()[page] ?? NO_PERM
  return !!p[action]
}
export function setPermissions(p: PermissionMap) {
  sessionStorage.setItem('bc.perms', JSON.stringify(p))
}

export function getToken(): string | null { return sessionStorage.getItem('bc.token') }
export function getUser(): User | null {
  const raw = sessionStorage.getItem('bc.user')
  return raw ? JSON.parse(raw) as User : null
}
export function clearSession() {
  sessionStorage.removeItem('bc.token')
  sessionStorage.removeItem('bc.user')
  sessionStorage.removeItem('bc.perms')
}

async function request(path: string, opts: RequestInit = {}): Promise<any> {
  const headers: Record<string, string> = { ...(opts.headers as Record<string, string> | undefined) }
  const t = getToken()
  if (t) headers['Authorization'] = 'Bearer ' + t
  if (opts.body && typeof opts.body === 'string') headers['Content-Type'] = 'application/json'
  const res = await fetch(BASE + path, { ...opts, headers })
  if (res.status === 401) { clearSession(); window.location.href = '/login'; throw new Error('Session expired') }
  if (!res.ok) {
    let detail = res.statusText
    try { const p = await res.json(); detail = p.detail || p.title || detail } catch { /* keep statusText */ }
    throw new Error(detail)
  }
  return res.json()
}

export async function login(email: string, password: string): Promise<void> {
  const res = await fetch(BASE + '/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password })
  })
  if (!res.ok) {
    let detail = 'Login failed'
    try { const p = await res.json(); detail = p.detail || detail } catch { /* ignore */ }
    throw new Error(detail)
  }
  const data = await res.json()
  sessionStorage.setItem('bc.token', data.accessToken)
  sessionStorage.setItem('bc.user', JSON.stringify(data.user))
  // effective page permissions drive nav and action gating (the API enforces them too)
  const profile = await me()
  setPermissions(profile.permissions ?? {})
}

// ---- role management ----
export type RolePermRow = { page: string; view: boolean; insert: boolean; edit: boolean }
export type RoleMatrix = {
  pages: { key: string; title: string; hasInsert: boolean; hasEdit: boolean; insertMeans: string; editMeans: string }[]
  roles: { role: string; locked: boolean; pages: RolePermRow[] }[]
  canEdit: boolean
}
export const rolePermissions = (): Promise<RoleMatrix> => request('/admin/role-permissions')
export const saveRolePermissions = (role: string, pages: RolePermRow[]) =>
  request('/admin/role-permissions', { method: 'PUT', body: JSON.stringify({ role, pages }) })

export const me = () => request('/me')
export const reportCatalog = (): Promise<ReportMeta[]> => request('/reports')
export const dashboard = () => request('/dashboard/summary')
export const ingestions = () => request('/ingestions')
export const quarantine = (id: number) => request(`/ingestions/${id}/quarantine`)

export function queryReport(key: string, body: {
  filters?: Record<string, string>
  columns?: string[]
  sort?: SortSpec[]
  page?: { size: number; offset: number }
}) {
  return request(`/reports/${key}/query`, { method: 'POST', body: JSON.stringify(body) })
}

export async function uploadFile(file: File): Promise<any> {
  const fd = new FormData()
  fd.append('file', file)
  return request('/ingestions/upload', { method: 'POST', body: fd })
}

// ---- saved views (FR-R12) ----
export type SavedView = { id: number; name: string | null; columns: string[]; sorts: SortSpec[]; pageSize: number }
export const listViews = (key: string): Promise<{ last: SavedView | null; named: SavedView[] }> =>
  request(`/reports/${key}/views`)
export const saveView = (key: string, payload: { name?: string | null; columns: string[]; sorts: SortSpec[]; pageSize: number }) =>
  request(`/reports/${key}/views`, { method: 'PUT', body: JSON.stringify(payload) })
export const deleteView = (id: number) => request(`/views/${id}`, { method: 'DELETE' })

// ---- notifications ----
export type Notification = { id: number; eventType: string; title: string; body?: string; createdAt: string; read: boolean }
export const notifications = (): Promise<{ unread: number; items: Notification[] }> => request('/notifications')
export const markAllRead = () => request('/notifications/read-all', { method: 'POST' })

/** Download the blank upload template for a report (fill in → upload back). */
export async function downloadTemplate(key: string): Promise<void> {
  const res = await fetch(`${BASE}/reports/${key}/template`, {
    headers: { Authorization: 'Bearer ' + getToken() }
  })
  if (!res.ok) throw new Error('Template download failed')
  const blob = await res.blob()
  const cd = res.headers.get('Content-Disposition') || ''
  const m = /filename\*?=(?:UTF-8'')?"?([^";]+)/i.exec(cd)
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = m ? decodeURIComponent(m[1]) : `Template_${key}.xlsx`
  a.click()
  URL.revokeObjectURL(a.href)
}

// ---- admin (FR-A1/A3/A4) ----
export type AdminUser = {
  id: number; email: string; fullName: string; role: string; status: string
  allEntities: boolean; entityId: number | null; siteId: number | null
  entityName?: string; siteName?: string; createdAt: string
}
export const adminUsers = (): Promise<{ users: AdminUser[]; roles: string[] }> => request('/admin/users')
export const adminCreateUser = (u: {
  email: string; fullName: string; role: string; allEntities: boolean
  entityId: number | null; siteId: number | null; password: string
}) => request('/admin/users', { method: 'POST', body: JSON.stringify(u) })
export const adminSetStatus = (id: number, status: 'active' | 'disabled') =>
  request(`/admin/users/${id}/status`, { method: 'POST', body: JSON.stringify({ status }) })
export const adminResetPassword = (id: number, password: string) =>
  request(`/admin/users/${id}/reset`, { method: 'POST', body: JSON.stringify({ password }) })
export const adminMaster = () => request('/admin/master')
export const adminAddEntity = (code: string, name: string) =>
  request('/admin/entities', { method: 'POST', body: JSON.stringify({ code, name }) })
export const adminAddSite = (entityId: number, name: string) =>
  request('/admin/sites', { method: 'POST', body: JSON.stringify({ entityId, name }) })
export const adminAddPermit = (entityId: number, siteId: number | null, permitNo: string) =>
  request('/admin/tpb-permits', { method: 'POST', body: JSON.stringify({ entityId, siteId, permitNo }) })

// ---- audit trail (FR-A7) ----
export type AuditEvent = {
  id: number; occurredAt: string; actorEmail: string | null; actorRole: string | null
  action: string; targetType: string | null; targetId: string | null
  summary: string | null; detailJson: string | null; ip: string | null
}
export type AuditFilters = {
  from?: string; to?: string; actor?: string; action?: string; search?: string
  limit?: number; offset?: number
}
const auditQs = (f: AuditFilters) => {
  const q = new URLSearchParams()
  Object.entries(f).forEach(([k, v]) => { if (v !== undefined && v !== '' && v !== null) q.set(k, String(v)) })
  const s = q.toString()
  return s ? '?' + s : ''
}
export const auditQuery = (f: AuditFilters): Promise<{
  rows: AuditEvent[]; total: number; page: { size: number; offset: number }; actions: string[]
}> => request('/audit' + auditQs(f))

export async function auditExport(f: AuditFilters): Promise<void> {
  const res = await fetch(`${BASE}/audit/export` + auditQs({ ...f, limit: undefined, offset: undefined }), {
    headers: { Authorization: 'Bearer ' + getToken() }
  })
  if (!res.ok) throw new Error('Audit export failed')
  const blob = await res.blob()
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = `audit_log_${new Date().toISOString().slice(0, 10)}.csv`
  a.click()
  URL.revokeObjectURL(a.href)
}

// ---- LPM / reconciliation (FR-R8) ----
export type SaldoRow = {
  material: string; description: string; uom: string; month: string
  opening: number; qtyIn: number; qtyOut: number; adjustment: number; closing: number; lines: number
}
export type VarianceRow = {
  location: string; tpbNo: string; docNo: string; docDate: string; vendor: string
  material: string; description: string; bcQty: number; uom: string
  deliveryQty: number; completeQty: number; variance: number
  tolerancePct: number | null; variancePct: number | null; beyondTolerance: boolean
}
export const lpmSaldo = (search: string): Promise<{ rows: SaldoRow[]; note: string }> =>
  request('/lpm/saldo' + (search ? `?search=${encodeURIComponent(search)}` : ''))
export const lpmVariances = (): Promise<{ rows: VarianceRow[]; summary: { withVariance: number; deliveryTracked: number; totalLines: number } }> =>
  request('/lpm/variances')

// ---- export (FR-R5/R13): download honouring visible columns/order/sort ----
export async function exportReport(key: string, format: 'xlsx' | 'csv', body: {
  filters?: Record<string, string>; columns?: string[]; sort?: SortSpec[]
}): Promise<void> {
  const res = await fetch(`${BASE}/reports/${key}/export?format=${format}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + getToken() },
    body: JSON.stringify(body)
  })
  if (!res.ok) {
    let detail = 'Export failed'
    try { const p = await res.json(); detail = p.detail || detail } catch { /* ignore */ }
    throw new Error(detail)
  }
  const blob = await res.blob()
  const cd = res.headers.get('Content-Disposition') || ''
  const m = /filename\*?=(?:UTF-8'')?"?([^";]+)/i.exec(cd)
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = m ? decodeURIComponent(m[1]) : `${key}.${format}`
  a.click()
  URL.revokeObjectURL(a.href)
}
