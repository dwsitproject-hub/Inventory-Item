import { useEffect, useState } from 'react'
import { Navigate, NavLink, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { can, clearSession, getToken, getUser, hasPermissions, landingPath, me, setPermissions } from './api'
import Bell from './components/Bell'
import Login from './pages/Login'
import Dashboard from './pages/Dashboard'
import Reports from './pages/Reports'
import Ingestion from './pages/Ingestion'
import Movement from './pages/Movement'
import Lpm from './pages/Lpm'
import Admin from './pages/Admin'
import Audit from './pages/Audit'

function Layout({ children }: { children: React.ReactNode }) {
  const user = getUser()!
  const nav = useNavigate()
  const initials = user.fullName.split(' ').map(s => s[0]).slice(0, 2).join('').toUpperCase()
  return (
    <div className="app">
      <div className="brand">
        <div className="logo">KPN</div>
        <div><b>BC Inventory</b><span>Reporting System</span></div>
      </div>
      <div className="top">
        <div className="scope">
          Scope: <b>{user.allEntities ? 'All entities' : `Entity #${user.entityId}${user.siteId ? ' · Site #' + user.siteId : ''}`}</b>
          {' '}· local Docker test
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 18 }}>
          <Bell />
          <div className="user">
            <div className="avatar">{initials}</div>
            <div><b style={{ fontSize: 12.5 }}>{user.fullName}</b><small>{user.role}</small></div>
          </div>
        </div>
      </div>
      <div className="side">
        <div className="grp">Main</div>
        {can('dashboard') && <NavLink className={({ isActive }) => 'nav' + (isActive ? ' active' : '')} to="/dashboard"><span>▚</span> Dashboard</NavLink>}
        {can('reports') && <NavLink className={({ isActive }) => 'nav' + (isActive ? ' active' : '')} to="/reports"><span>▤</span> Reports</NavLink>}
        {can('movement') && <NavLink className={({ isActive }) => 'nav' + (isActive ? ' active' : '')} to="/movement"><span>⇄</span> Inventory Movement</NavLink>}
        {can('ingestion') && <NavLink className={({ isActive }) => 'nav' + (isActive ? ' active' : '')} to="/ingestion"><span>⭳</span> Ingestion &amp; Upload</NavLink>}
        {can('lpm') && <NavLink className={({ isActive }) => 'nav' + (isActive ? ' active' : '')} to="/lpm"><span>⚖</span> LPM / Reconciliation</NavLink>}
        {(can('admin') || can('audit')) && (<>
          <div className="grp">Administration</div>
          {can('admin') && <NavLink className={({ isActive }) => 'nav' + (isActive ? ' active' : '')} to="/admin"><span>👥</span> Admin</NavLink>}
          {can('audit') && <NavLink className={({ isActive }) => 'nav' + (isActive ? ' active' : '')} to="/audit"><span>🗒</span> Audit Log</NavLink>}
        </>)}
        <div className="grp">Account</div>
        <a className="nav" onClick={() => { clearSession(); nav('/login') }}><span>⇦</span> Sign out</a>
      </div>
      <div className="main">{children}</div>
    </div>
  )
}

function Protected({ children, page }: { children: React.ReactNode; page?: string }) {
  const loc = useLocation()
  if (!getToken()) return <Navigate to="/login" state={{ from: loc }} replace />
  // Only refuse when permissions are actually known — never guess "denied" from missing data.
  if (page && hasPermissions() && !can(page)) {
    return (
      <Layout>
        <h1 className="page">Access denied</h1>
        <div className="crumb">Your role does not have access to this page</div>
        <div className="panel">
          <div className="err">
            Your role (<b>{getUser()!.role}</b>) is not permitted to view this page.
            An administrator can grant access under <b>Administration → Role Management</b>.
          </div>
          {landingPath() !== loc.pathname && (
            <a className="btn p" style={{ display: 'inline-block', textDecoration: 'none' }} href={landingPath()}>
              Go to my start page
            </a>
          )}
        </div>
      </Layout>
    )
  }
  return <Layout>{children}</Layout>
}

export default function App() {
  // Permissions must be present before any page can be gated — a session created before
  // permissions existed (or any reload) would otherwise be treated as "no access".
  const [ready, setReady] = useState(() => !getToken() || hasPermissions())

  useEffect(() => {
    if (ready) return
    me()
      .then(p => setPermissions(p.permissions ?? {}))
      .catch(() => { /* 401 already redirects to login; the API stays the real gate */ })
      .finally(() => setReady(true))
  }, [ready])

  if (!ready) return <div className="loading" style={{ padding: 40 }}><span className="spin" />loading your access…</div>

  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route path="/dashboard" element={<Protected page="dashboard"><Dashboard /></Protected>} />
      <Route path="/reports" element={<Protected page="reports"><Reports /></Protected>} />
      <Route path="/movement" element={<Protected page="movement"><Movement /></Protected>} />
      <Route path="/ingestion" element={<Protected page="ingestion"><Ingestion /></Protected>} />
      <Route path="/lpm" element={<Protected page="lpm"><Lpm /></Protected>} />
      <Route path="/admin" element={<Protected page="admin"><Admin /></Protected>} />
      <Route path="/audit" element={<Protected page="audit"><Audit /></Protected>} />
      <Route path="*" element={<Navigate to={getToken() ? landingPath() : '/login'} replace />} />
    </Routes>
  )
}
