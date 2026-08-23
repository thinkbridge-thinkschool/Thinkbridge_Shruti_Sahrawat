# Entra ID: what is verified, and what is not

Companion to [`DAY3_README.md`](../DAY3_README.md), which covers the architecture.
This file covers only the honest question: **which parts of it have actually been
proven to work, and which parts are code I believe in but have not seen run.**

The exercise asks for `curl` output showing an Entra-issued access token
authenticating against the API. I cannot produce that, and this is the account
of why rather than a quiet omission.

---

## Verified

| Claim | How it was proven |
|---|---|
| The internal JWT path validates a self-issued token end to end | `OrderControllerTests` — anonymous 401, wrong policy 403, correct policy 201, expired 401, malformed 401 |
| Refresh rotation and reuse detection work against a real HTTP pipeline | `RefreshTokenTests` — replay a spent token, whole family dies |
| The policy scheme routes on the issuer claim | `IssuerSchemeSelectorTests` — 20 cases covering no header, non-Bearer, unreadable token, our issuer, an Entra issuer, an unknown issuer, and a case-insensitive `Bearer` prefix |
| A token claiming an Entra issuer but signed with the internal key is routed to Entra, not to the internal validator | Same file. This is the attack the router must not fall for: a forged issuer claim must not buy validation under the weaker scheme |
| Renaming `Jwt:Issuer` does not break routing | Same file. The old inline lambda compared against a hardcoded `"OrderRefactorIssuer"` while the validator it routed to read `ValidIssuer` from configuration — the two would have silently diverged the moment config changed, and every internally-issued token would have been routed to Entra and rejected |

The second row is the part worth stating plainly. The policy scheme reads the
issuer claim *before* anything has validated the signature, which sounds alarming
until you see what it is used for: it only chooses **which validator runs**, and
both validators then do full signature and lifetime validation. Claiming to be
Entra does not skip validation, it opts you into stricter validation against
Microsoft's published keys, which an attacker cannot sign for. The test above is
what turns that from an argument into evidence.

---

## Not verified

**A real Entra-issued access token has never reached this API.**

The app registration exists in the tenant and the configuration in
`appsettings.json` (`TenantId`, `ClientId`, `Audience`) points at it. Those are
public identifiers, not secrets — an API that only *validates* tokens needs no
client secret, because it verifies signatures against Microsoft's published
JWKS endpoint at
`https://login.microsoftonline.com/{tenant}/v2.0/.well-known/openid-configuration`.

What fails is the step before that: obtaining a token to send.

```
AADSTS65005: The application asked for permissions to access a resource
that is not available, or the scope 'access_as_user' has not been granted.
```

The tenant is an institutional one where granting an API scope requires an
administrator, and I do not hold that role in it. `az account get-access-token
--resource api://{client-id}` and the SPA consent flow both stop at the same
place. This is an access-control fact about the tenant, not a defect in the code
under test — but the distinction does not matter for the claim being made. The
Entra branch is unexercised, and I will not report it as working.

---

## What would close it

In rough order of how quickly each could be done:

1. **A tenant where I am the admin.** A personal Azure AD tenant on a free
   subscription takes about ten minutes: register the API, expose an
   `access_as_user` scope, grant admin consent to a test client, then
   `az account get-access-token --resource api://{client-id}` and curl the
   result at `POST /api/orders`. The tenant IDs in config would change; nothing
   in `Program.cs` would.
2. **Admin consent in the existing tenant.** One approval on the existing
   registration, then the same two commands.
3. **A signing-key stub against the real handler.** Stand up a local JWKS
   endpoint, point `Authority` at it, and issue tokens signed with a key it
   publishes. This proves that `JwtBearerHandler` fetches the key set, validates
   the signature asymmetrically, and enforces audience and lifetime — everything
   in the Entra path except Microsoft itself. It is the most thorough option and
   the most work, and it is what I would build if this were shipping.

Option 3 is the honest answer to "what if you never get the tenant." I did not
build it because a scope-grant approval is a smaller ask than a JWKS harness,
and because inventing an elaborate substitute risks looking like proof when it
is still not the real thing.

---

## What I would not do

Assert that the Entra path works because the code looks right. It is the
authentication boundary of the application; "looks right" is the standard that
produces the security incidents. The two schemes are independently registered
and independently validated, so the unverified branch cannot weaken the verified
one — but until a Microsoft-signed token has been accepted by this API, the
correct status is *unverified*, and that is what this repository claims.
