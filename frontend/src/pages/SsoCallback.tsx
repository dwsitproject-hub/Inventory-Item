import { useEffect, useRef, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { landingPath, ssoComplete } from '../api'

/**
 * Landing route for the DWS Hub OIDC redirect (redirect_uri = /auth/sso/callback).
 * Reads the code + state, completes the exchange through our backend, then lands the user.
 */
export default function SsoCallback() {
  const [params] = useSearchParams()
  const [error, setError] = useState('')
  const nav = useNavigate()
  const ran = useRef(false)

  useEffect(() => {
    if (ran.current) return          // React 18 StrictMode double-invokes effects; the code is single-use
    ran.current = true

    const err = params.get('error')
    if (err) { setError(params.get('error_description') || err); return }

    const code = params.get('code')
    const state = params.get('state')
    if (!code || !state) { setError('The sign-in response was incomplete. Please try again.'); return }

    ssoComplete(code, state)
      .then(() => nav(landingPath(), { replace: true }))
      .catch(e => setError(e.message || 'Sign-in failed.'))
  }, [])

  return (
    <div className="loginwrap">
      <div className="logincard" style={{ textAlign: 'center' }}>
        {error
          ? <>
              <h1 style={{ marginBottom: 8 }}>Sign-in failed</h1>
              <div className="err" style={{ marginBottom: 16 }}>{error}</div>
              <button className="btn p" style={{ width: '100%' }} onClick={() => nav('/login', { replace: true })}>
                Back to sign in
              </button>
            </>
          : <div className="loading"><span className="spin" />completing sign-in…</div>}
      </div>
    </div>
  )
}
