# DWS Hub SSO integration (OIDC)

BC Inventory can authenticate users through the DWS Hub using OpenID Connect (authorization code
+ PKCE, strict mode). This note covers how it works, what to configure, and the Hub-side
registration it depends on.

## How it works

The Hub proves **identity**; BC Inventory keeps deciding **authorization**.

1. The sign-in page shows **Sign in with DWS Hub** when SSO is enabled and the Hub is reachable.
2. The browser generates a PKCE verifier + `state` + `nonce` and redirects to the Hub's
   `authorize` endpoint (`response_type=code`, `code_challenge_method=S256`).
3. The Hub authenticates the user and redirects back to `…/auth/sso/callback?code=…&state=…`.
4. The SPA checks `state`, then posts the `code` + verifier to **our** backend.
5. The backend exchanges the code at the Hub's `token` endpoint, verifies the returned `id_token`
   against the Hub's JWKS (RS256; `iss`, `aud`, `exp`, `sub`, and `nonce` all checked), maps the
   Hub user to a **pre-existing** local account (by stored `sub`, then email), and issues an
   ordinary BC Inventory session.

Because a normal session is issued, roles, entity scope, the audit trail and the per-request
session revalidation (AR-01) behave identically to a password login. SSO only replaces the
password step.

**No auto-provisioning.** A customs account needs an admin-assigned role and entity scope, so a
Hub user with no matching **active** BC Inventory account is refused with a clear message — the
administrator must create the account first (User Management). On first SSO login the Hub `sub` is
stored on the account, so the link stays stable even if the email later changes.

## Configuration (`deploy/backend/.env`)

```
SSO_ENABLED=true
SSO_ISSUER=http://test-dwshub.kpndomain.com
SSO_CLIENT_ID=it-inventory-test
SSO_REDIRECT_URI=http://test-it-inventory.kpndomain.com/auth/sso/callback
SSO_SCOPE=openid profile email
```

The issuer must exactly match the `issuer` in the Hub's discovery document; the backend verifies
this on startup fetch and refuses a mismatched discovery host. If the Hub is unreachable, the
button is hidden and password login still works.

## Two ways in

| Flow | Trigger | How the code + verifier reach the callback |
|---|---|---|
| **SP-initiated** | User opens the app, clicks **Sign in with DWS Hub** | The SPA generates the PKCE pair, keeps the verifier in `sessionStorage`, and the callback reads it back there. The URL carries only `code` + `state`. |
| **Portal launch** (IdP-initiated) | User clicks the **IT Inventory tile inside the Hub** | The **Hub** generates the PKCE pair, runs `authorize` with the user's existing Hub session, and redirects to `/auth/sso/callback?code=…&state=…&code_verifier=…`. The callback uses the URL-supplied `code_verifier`. |

The callback (`SsoCallback.tsx`) handles both: if the URL carries a `code_verifier`, it is a Hub
launch and that verifier is used; otherwise it is our own button and the verifier comes from
`sessionStorage`. Either way the backend exchange and id_token verification are identical. In the
IdP-initiated case there is no locally stored `state` to compare (that is inherent to a launch the
app did not start) — trust rests on the single-use code and the id_token signature/iss/aud/exp
checks the backend performs. After sign-in the app replaces the callback URL with the landing
page, so the `code_verifier` does not linger in browser history.

> The `/auth/sso/start` route also exists for an SP-initiated auto-start, but **this Hub does not
> need it** — its tile drives the IdP-initiated flow straight to the callback.

## Hub-side registration (required)

In Hub Admin, register BC Inventory as a target app (per the Hub integration contract):

- `sso_mode` = `oidc`
- `oauth_client_id` = `it-inventory-test` (the assigned staging client id) — matches `SSO_CLIENT_ID`
- `oidc_redirect_uris` (OIDC Redirect URIs) includes **exactly**
  `http://test-it-inventory.kpndomain.com/auth/sso/callback` (and, for production, the production
  callback). This is where the Hub sends the user after authenticating, with `code` + `code_verifier`.
- **Target URL** = `http://test-it-inventory.kpndomain.com/` (the app home shown on the tile).

Until this is done, the Hub returns an enforcement error and the flow cannot start.

> **Staging registration verified.** A pre-flight of `GET /api/sso/authorize` with
> `client_id=it-inventory-test` and the callback above returned a 302 to the Hub login (carrying
> the request in `returnTo`), which is the correct response for a registered client — not an error.

## Enabling on staging

On the backend host (172.28.92.57):

```bash
cd /opt/bc-inventory && git pull --ff-only
nano deploy/backend/.env          # set the SSO_* values above
```

The `sso_sub` column is a schema change; apply it with the owner account, then restart:

```bash
cd /opt/bc-inventory/deploy/backend \
  && DB_USER=<owner> DB_PASSWORD='<owner pw>' DB_AUTO_MIGRATE=true docker compose run --rm api --migrate
cd /opt/bc-inventory/deploy && ./update.sh backend
```

Redeploy the frontend so it has the SSO button and callback route:

```bash
cd /opt/bc-inventory/deploy && ./update.sh frontend
```

## Verify

```bash
curl -s http://test-it-inventory.kpndomain.com/api/v1/auth/sso/info    # enabled:true + Hub endpoints
```

> **Live on staging (confirmed).** `/auth/sso/info` returns `enabled:true` with the Hub
> `authorize` endpoint, `client_id=it-inventory-test`, and the callback URI. The backend can only
> build that payload after a successful discovery + JWKS fetch, so the Hub connection is verified.
> The final human sign-in needs a Hub account whose email matches an **active** BC Inventory user.

Then, with a Hub account whose email matches an active BC Inventory user, open the login page,
click **Sign in with DWS Hub**, authenticate at the Hub, and confirm you land in the app. The
Audit Log records the event as `auth.sso_login`.

## Failure references

| Code | Meaning |
|---|---|
| button hidden | SSO disabled, or the Hub is unreachable from the backend host |
| SSO-003 | code exchange rejected (wrong redirect_uri / client_id / verifier, or a reused/expired code) |
| SSO-004 | id_token failed verification (signature, `iss`/`aud`, expiry, or nonce) |
| SSO-005 | verified Hub user has no active BC Inventory account — create it first |
