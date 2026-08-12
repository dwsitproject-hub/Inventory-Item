import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { landingPath, login } from '../api'

export default function Login() {
  const [email, setEmail] = useState('admin@energi-up.com')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const nav = useNavigate()

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
          <div className="logo">KPN</div>
          <div style={{ color: 'var(--navy)' }}><b>BC Inventory</b><span style={{ color: 'var(--muted)' }}>Reporting System</span></div>
        </div>
        <h1>Sign in</h1>
        <div className="sub">Local Docker test environment</div>
        {error && <div className="err">{error}</div>}
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
        <div className="note" style={{ marginTop: 16 }}>
          Test accounts: <b>admin@energi-up.com</b> / Admin123! (Super Admin) ·{' '}
          <b>bc.bontang@energi-up.com</b> / Bontang123! (Site BC, scope-locked)
        </div>
      </form>
    </div>
  )
}
