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

## Hub-side registration (required)

In Hub Admin, register BC Inventory as a target app (per the Hub integration contract):

- `sso_mode` = `oidc`
- `oauth_client_id` = `it-inventory-test` (the assigned staging client id) — matches `SSO_CLIENT_ID`
- `oidc_redirect_uris` includes **exactly** `http://test-it-inventory.kpndomain.com/auth/sso/callback`
  (and, for production, the production callback)

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
