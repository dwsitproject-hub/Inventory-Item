import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { getToken, landingPath, ssoBegin, ssoInfo } from '../api'

/**
 * Auto-start entry for a DWS Hub portal launch ("Hub launch handoff", integration contract §2).
 *
 * The Hub app tile points here (…/auth/sso/start). Because the user already has a Hub session,
 * kicking off the OIDC authorize redirect returns a code with no second login prompt — the user
 * lands straight in the app. This is deliberately a distinct URL from /login: password sign-in
 * must stay available for accounts that have no Hub identity (e.g. the Super Admin), so we never
 * auto-bounce the generic login page to the Hub.
 */
export default function SsoStart() {
  const [error, setError] = useState('')
  const nav = useNavigate()
  const ran = useRef(false)

  useEffect(() => {
    if (ran.current) return          // guard React 18 StrictMode's double-invoke
    ran.current = true

    // Already signed in (tile clicked from an open session) — go straight in.
    if (getToken()) { nav(landingPath(), { replace: true }); return }

    ssoInfo()
      .then(info => {
        if (!info.enabled) { nav('/login', { replace: true }); return }
        return ssoBegin(info)         // redirects away; no return on success
      })
      .catch(e => setError(e?.message || 'Could not start DWS Hub sign-in.'))
  }, [])

  return (
    <div className="loginwrap">
      <div className="logincard" style={{ textAlign: 'center' }}>
        {error
          ? <>
              <h1 style={{ marginBottom: 8 }}>Sign-in failed</h1>
              <div className="err" style={{ marginBottom: 16 }}>{error}</div>
              <button className="btn p" style={{ width: '100%' }} onClick={() => nav('/login', { replace: true })}>
                Go to sign in
              </button>
            </>
          : <div className="loading"><span className="spin" />redirecting to DWS Hub…</div>}
      </div>
    </div>
  )
}
