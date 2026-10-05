const BASE = '/api/v1'

/** `name` is the verbatim upload header (stable key for queries, sorts, saved views);
 *  `label` is the customs-terminology display name. */
export type FieldMeta = { name: string; label: string; type: 'text' | 'number' | 'date'; group: string }
export type ReportMeta = {
  key: string; title: string; template: string
  /** which screen hosts it: 'reports' (customs documents) or 'movement' (inventory movement) */
  page: 'reports' | 'movement'
  /** owns an upload template — offered as a blank download and listed on Ingestion */
  upload: boolean
  /** offered in the report picker on its page */
  browse: boolean
  defaults: string[]; fields: FieldMeta[]
}
export type SortSpec = { field: string; dir: 'asc' | 'desc' }

export type User = {
  email: string; fullName: string; role: string
  allEntities: boolean; entityId: number | null; siteId: number | null
}

export type PagePerm = { view: boolean; insert: boolean; edit: boolean; delete: boolean }
export type PermissionMap = Record<string, PagePerm>

const NO_PERM: PagePerm = { view: false, insert: false, edit: false, delete: false }

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

/** Route for each governed page, in the order we would rather land a user on. */
const LANDING_ORDER: [string, string][] = [
  ['dashboard', '/dashboard'],
  ['reports', '/reports'],
  ['movement', '/movement'],
  ['lpm', '/lpm'],
  ['ingestion', '/ingestion'],
  ['audit', '/audit'],
  ['admin', '/admin'],
]

/**
 * Where to send a user with no particular destination — after login, or from an unknown URL.
 * Roles without Dashboard access land on Reports instead of an "access denied" panel; if that
 * is closed to them too, the first page they can actually open wins.
 */
export function landingPath(): string {
  const hit = LANDING_ORDER.find(([page]) => can(page))
  return hit ? hit[1] : '/reports'   // nothing permitted: Reports explains the situation
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
  sessionStorage.removeItem('bc.company')
}

// ---- multi-tenant company ----
/** Header company shown after login: present only when the user belongs to exactly one company. */
export type MeCompany = { id: number | null; name: string | null; logo: string | null }
export function getCompany(): MeCompany | null {
  const raw = sessionStorage.getItem('bc.company')
  return raw ? JSON.parse(raw) as MeCompany : null
}
export function setCompany(c: MeCompany | null | undefined) {
  if (c && c.id != null) sessionStorage.setItem('bc.company', JSON.stringify(c))
  else sessionStorage.removeItem('bc.company')
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
  setCompany(profile.company)
}

// ---- SSO (DWS Hub, OIDC + PKCE) ----
export type SsoInfo = { enabled: boolean; authorizeEndpoint?: string; clientId?: string; redirectUri?: string; scope?: string }
export const ssoInfo = (): Promise<SsoInfo> => request('/auth/sso/info')

function b64url(bytes: Uint8Array): string {
  let s = ''
  bytes.forEach(b => { s += String.fromCharCode(b) })
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}
function randB64url(len = 32): string { return b64url(crypto.getRandomValues(new Uint8Array(len))) }
async function s256(verifier: string): Promise<string> {
  const bytes = new TextEncoder().encode(verifier)
  // crypto.subtle exists only in secure contexts (HTTPS or localhost). Staging is served over
  // plain HTTP, where it is undefined and the button failed with "reading 'digest'". Only the
  // hash needs a fallback: crypto.getRandomValues is available in insecure contexts too.
  const digest = crypto.subtle ? new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)) : sha256(bytes)
  return b64url(digest)
}

const SHA256_K = new Uint32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
])
const ror = (x: number, n: number) => (x >>> n) | (x << (32 - n))

/** SHA-256 (FIPS 180-4) for insecure contexts without crypto.subtle; used only for the PKCE challenge. */
function sha256(data: Uint8Array): Uint8Array {
  const H = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19])
  const buf = new Uint8Array(((data.length + 9 + 63) >> 6) << 6)   // message + 0x80 + 64-bit length, padded to 64 bytes
  buf.set(data)
  buf[data.length] = 0x80
  const dv = new DataView(buf.buffer)
  const bits = data.length * 8
  dv.setUint32(buf.length - 8, Math.floor(bits / 0x100000000))
  dv.setUint32(buf.length - 4, bits >>> 0)
  const W = new Uint32Array(64)
  for (let off = 0; off < buf.length; off += 64) {
    for (let i = 0; i < 16; i++) W[i] = dv.getUint32(off + i * 4)
    for (let i = 16; i < 64; i++) {
      const s0 = ror(W[i - 15], 7) ^ ror(W[i - 15], 18) ^ (W[i - 15] >>> 3)
      const s1 = ror(W[i - 2], 17) ^ ror(W[i - 2], 19) ^ (W[i - 2] >>> 10)
      W[i] = W[i - 16] + s0 + W[i - 7] + s1
    }
    let a = H[0], b = H[1], c = H[2], d = H[3], e = H[4], f = H[5], g = H[6], h = H[7]
    for (let i = 0; i < 64; i++) {
      const t1 = (h + (ror(e, 6) ^ ror(e, 11) ^ ror(e, 25)) + ((e & f) ^ (~e & g)) + SHA256_K[i] + W[i]) | 0
      const t2 = ((ror(a, 2) ^ ror(a, 13) ^ ror(a, 22)) + ((a & b) ^ (a & c) ^ (b & c))) | 0
      h = g; g = f; f = e; e = (d + t1) | 0; d = c; c = b; b = a; a = (t1 + t2) | 0
    }
    H[0] += a; H[1] += b; H[2] += c; H[3] += d; H[4] += e; H[5] += f; H[6] += g; H[7] += h
  }
  const out = new Uint8Array(32)
  const odv = new DataView(out.buffer)
  for (let i = 0; i < 8; i++) odv.setUint32(i * 4, H[i])
  return out
}

/** Begin the OIDC authorize redirect. Stores the PKCE verifier, state and nonce for the callback. */
export async function ssoBegin(info: SsoInfo): Promise<void> {
  const verifier = randB64url(48)
  const state = randB64url()
  const nonce = randB64url()
  sessionStorage.setItem('bc.sso', JSON.stringify({ verifier, state, nonce, redirectUri: info.redirectUri }))
  const params = new URLSearchParams({
    response_type: 'code',
    client_id: info.clientId!,
    redirect_uri: info.redirectUri!,
    code_challenge: await s256(verifier),
    code_challenge_method: 'S256',
    scope: info.scope || 'openid profile email',
    state, nonce,
  })
  window.location.href = `${info.authorizeEndpoint}?${params.toString()}`
}

/**
 * Complete the flow and establish the session. Two initiation paths land here:
 *
 *  - **SP-initiated** (our own "Sign in with DWS Hub" button): ssoBegin() generated the PKCE
 *    pair and stashed the verifier + state in sessionStorage. We match state and use that verifier.
 *  - **Hub portal launch** (IdP-initiated): the Hub generated the PKCE pair, ran authorize with
 *    the user's existing Hub session, and redirected here with `code` AND `code_verifier` in the
 *    URL. There is nothing in sessionStorage. We use the URL-supplied verifier; there is no local
 *    state to compare (inherent to IdP-initiated), so trust rests on the single-use code and the
 *    id_token verification the backend performs (signature via JWKS, iss, aud, exp).
 */
export async function ssoComplete(code: string, state: string | null, verifierFromUrl?: string | null): Promise<void> {
  let payload: { code: string; codeVerifier: string; nonce?: string; redirectUri?: string }

  if (verifierFromUrl) {
    // Hub portal launch — the Hub handed us the verifier.
    sessionStorage.removeItem('bc.sso')
    payload = { code, codeVerifier: verifierFromUrl }   // backend uses its configured redirect_uri
  } else {
    // SP-initiated — our ssoBegin stored the verifier keyed to this state.
    const raw = sessionStorage.getItem('bc.sso')
    sessionStorage.removeItem('bc.sso')
    if (!raw) throw new Error('Sign-in session expired. Please try again.')
    const saved = JSON.parse(raw) as { verifier: string; state: string; nonce: string; redirectUri?: string }
    if (state !== saved.state) throw new Error('Sign-in could not be verified (state mismatch). Please try again.')
    payload = { code, codeVerifier: saved.verifier, nonce: saved.nonce, redirectUri: saved.redirectUri }
  }

  const data = await request('/auth/sso/callback', { method: 'POST', body: JSON.stringify(payload) })
  sessionStorage.setItem('bc.token', data.accessToken)
  sessionStorage.setItem('bc.user', JSON.stringify(data.user))
  const profile = await me()
  setPermissions(profile.permissions ?? {})
  setCompany(profile.company)
}

// ---- role management ----
export type RolePermRow = { page: string; view: boolean; insert: boolean; edit: boolean; delete: boolean }
export type RoleMatrix = {
  pages: { key: string; title: string; hasInsert: boolean; hasEdit: boolean; hasDelete: boolean; insertMeans: string; editMeans: string; deleteMeans: string }[]
  roles: { role: string; locked: boolean; pages: RolePermRow[] }[]
  canEdit: boolean
}
export const rolePermissions = (): Promise<RoleMatrix> => request('/admin/role-permissions')
export const saveRolePermissions = (role: string, pages: RolePermRow[]) =>
  request('/admin/role-permissions', { method: 'PUT', body: JSON.stringify({ role, pages }) })

// ---- notification routing ----
export type NotifEventPref = { event: string; inApp: boolean; email: boolean; isDefault: boolean }
export type NotifMatrix = {
  events: { key: string; title: string; description: string; defaultRoles: string[] }[]
  users: { id: number; email: string; fullName: string; role: string; status: string; events: NotifEventPref[] }[]
  smtpConfigured: boolean
  canEdit: boolean
}
export type NotifSaveRow = { userId: number; event: string; inApp: boolean; email: boolean }
export const notificationSubscriptions = (): Promise<NotifMatrix> =>
  request('/admin/notification-subscriptions')
export const saveNotificationSubscriptions = (rows: NotifSaveRow[]) =>
  request('/admin/notification-subscriptions', { method: 'PUT', body: JSON.stringify({ rows }) })

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

export async function uploadFile(file: File, companyId?: number | null): Promise<any> {
  const fd = new FormData()
  fd.append('file', file)
  if (companyId != null) fd.append('companyId', String(companyId))
  return request('/ingestions/upload', { method: 'POST', body: fd })
}

// ---- companies (multi-tenant admin) ----
export type Company = { id: number; name: string; active: boolean; hasLogo: boolean; userCount: number }
export type PickCompany = { id: number; name: string }
export const companies = (): Promise<Company[]> => request('/companies')
export const createCompany = (body: { name: string; active: boolean; logoBase64?: string | null; logoContentType?: string | null }) =>
  request('/companies', { method: 'POST', body: JSON.stringify(body) })
export const updateCompany = (id: number, body: { name: string; active: boolean; logoBase64?: string | null; logoContentType?: string | null }) =>
  request(`/companies/${id}`, { method: 'PUT', body: JSON.stringify(body) })
export const setUserCompanies = (userId: number, companyIds: number[]) =>
  request(`/admin/users/${userId}/companies`, { method: 'PUT', body: JSON.stringify({ companyIds }) })
export const pickableCompanies = (): Promise<PickCompany[]> => request('/companies/pickable')

// ---- saved views (FR-R12) ----
export type SavedView = { id: number; name: string | null; columns: string[]; sorts: SortSpec[]; pageSize: number }
export const listViews = (key: string): Promise<{ last: SavedView | null; named: SavedView[] }> =>
  request(`/reports/${key}/views`)
export const saveView = (key: string, payload: { name?: string | null; columns: string[]; sorts: SortSpec[]; pageSize: number }) =>
  request(`/reports/${key}/views`, { method: 'PUT', body: JSON.stringify(payload) })
export const deleteView = (id: number) => request(`/views/${id}`, { method: 'DELETE' })

export type DeleteRowsBody =
  | { mode: 'ids'; ids: number[] }
  | { mode: 'filter'; filters: Record<string, string> }
export const deleteReportRows = (key: string, body: DeleteRowsBody): Promise<{ deleted: number; documentsRemoved: number }> =>
  request(`/reports/${key}/delete`, { method: 'POST', body: JSON.stringify(body) })

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
  companyIds: number[]; companyNames: string
}
export const adminUsers = (): Promise<{ users: AdminUser[]; roles: string[] }> => request('/admin/users')
export const adminCreateUser = (u: {
  email: string; fullName: string; role: string; allEntities: boolean
  entityId: number | null; siteId: number | null; password: string; companyIds: number[]
}) => request('/admin/users', { method: 'POST', body: JSON.stringify(u) })
export const adminSetStatus = (id: number, status: 'active' | 'disabled') =>
  request(`/admin/users/${id}/status`, { method: 'POST', body: JSON.stringify({ status }) })
export const adminResetPassword = (id: number, password: string) =>
  request(`/admin/users/${id}/reset`, { method: 'POST', body: JSON.stringify({ password }) })
export const setUserScope = (id: number, allEntities: boolean, entityId: number | null) =>
  request(`/admin/users/${id}/scope`, { method: 'PUT', body: JSON.stringify({ allEntities, entityId }) })
export const adminMaster = () => request('/admin/master')
export const adminAddEntity = (code: string, name: string, companyId: number) =>
  request('/admin/entities', { method: 'POST', body: JSON.stringify({ code, name, companyId }) })
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
