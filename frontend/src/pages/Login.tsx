import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { SsoInfo, landingPath, login, ssoBegin, ssoInfo } from '../api'

export default function Login() {
  const [email, setEmail] = useState('admin@energi-up.com')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [sso, setSso] = useState<SsoInfo | null>(null)
  const nav = useNavigate()

  useEffect(() => { ssoInfo().then(setSso).catch(() => setSso({ enabled: false })) }, [])

  async function startSso() {
    setBusy(true); setError('')
    try { await ssoBegin(sso!) }            // redirects away; no return on success
    catch (e: any) { setError(e.message || 'Could not start DWS Hub sign-in'); setBusy(false) }
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true); setError('')
    try {
      await login(email, password)
      // login() has already fetched this user's permissions, so this resolves to a page
      // they can actually open — Reports for roles without Dashboard access.
      nav(landingPath(), { replace: true })
    } catch (err: any) {
      setError(err.message || 'Login failed')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="loginwrap">
      <form className="logincard" onSubmit={submit}>
        <div className="brand" style={{ background: 'transparent', border: 'none', padding: 0 }}>
          <div className="logo">PT SPC</div>
          <div style={{ color: 'var(--navy)' }}><b>BC Inventory</b><span style={{ color: 'var(--muted)' }}>Reporting System</span></div>
        </div>
        <h1>Sign in</h1>
        <div className="sub">Reporting System</div>
        {error && <div className="err">{error}</div>}
        {sso?.enabled && (
          <>
            <button type="button" className="btn p" style={{ width: '100%', marginBottom: 4 }} disabled={busy} onClick={startSso}>
              {busy ? 'Redirecting…' : 'Sign in with DWS Hub'}
            </button>
            <div className="ssodiv"><span>or sign in with email</span></div>
          </>
        )}
        <div className="fld">
          <label>Email</label>
          <input value={email} onChange={e => setEmail(e.target.value)} autoComplete="username" />
        </div>
        <div className="fld">
          <label>Password</label>
          <input type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password" />
        </div>
        <button className="btn p" style={{ width: '100%', marginTop: 6 }} disabled={busy}>
          {busy ? 'Signing in…' : 'Sign in'}
        </button>
        {/* Local development only. On any networked deployment this handed an attacker two
            valid usernames and their passwords (AR-14). import.meta.env.DEV is false in the
            production build that ships to staging. */}
        {import.meta.env.DEV && (
          <div className="note" style={{ marginTop: 16 }}>
            Test accounts: <b>admin@energi-up.com</b> / Admin123! (Super Admin) ·{' '}
            <b>bc.bontang@energi-up.com</b> / Bontang123! (Site BC, scope-locked)
          </div>
        )}
      </form>
    </div>
  )
}
