# Entra ID: what is verified, and in which tenant

Companion to [`DAY3_README.md`](../DAY3_README.md), which covers the architecture.
This file answers the only question that matters about an authentication
boundary: **what has actually been observed to work, and under what conditions.**

An earlier version of this file said no Microsoft-signed token had ever reached
this API. That is no longer true, and the way it stopped being true is itself
worth recording.

---

## Verified end to end

A real Entra-issued access token authenticates against this API. Three calls to
`GET /api/orders/whoami`, which requires authentication and no policy:

```
no token    -> 401
junk token  -> 401
Entra token -> 200
```

and the body of the third:

```json
{
  "authenticated": true,
  "issuer": "https://login.microsoftonline.com/03490f7a-f873-47af-9963-ae925b4871b8/v2.0",
  "validated_by": "EntraJwt",
  "name": "Shruti Sahrawat",
  "claims": [
    { "type": "aud", "value": "62fe0e7f-3b56-443e-82d0-baa577371525" },
    { "type": "azp", "value": "04b07795-8ddb-461a-bbee-02f9e1bf7b46" },
    { "type": "http://schemas.microsoft.com/identity/claims/scope", "value": "access_as_user" },
    { "type": "preferred_username", "value": "shruti9shruti4@gmail.com" },
    { "type": "ver", "value": "2.0" }
  ]
}
```

Every link in the chain is exercised: Microsoft issued the token, the
`JwtBearerHandler` fetched the signing keys from the tenant's published JWKS
endpoint and verified the signature asymmetrically, the audience and lifetime
checks passed, and the policy scheme routed the request to `EntraJwt` on the
strength of the issuer claim. `azp` shows the Azure CLI as the calling client,
which is the pre-authorisation working.

The two 401s matter as much as the 200. A malformed token is rejected rather
than waved through, and the no-token case never reaches the handler at all.

---

## The tenant this was proven in, and the one it was not

**Proven in:** `03490f7a-f873-47af-9963-ae925b4871b8` — a personal Entra
directory where I am Global Administrator, so I could grant the scope to myself.

**Not proven in:** the institutional thinkbridge tenant,
`b69d82df-4ebe-474d-9ac7-00efbf13427e`. The original blocker there is
unchanged:

```
AADSTS65005: The application asked for permissions to access a resource
that is not available, or the scope 'access_as_user' has not been granted.
```

Granting an API scope in that tenant requires an administrator and I do not hold
that role. **I routed around the blocker rather than through it**, and the
distinction should not be blurred: what is proven is that the *code* validates
Entra tokens correctly, not that the thinkbridge registration is configured.

That is also the honest thing to do when waiting on someone else's approval at
work. Prove the code against a tenant you control, keep moving, and swap three
configuration values when the grant lands. The three values are `TenantId`,
`ClientId` and `Audience` in `appsettings.json`. No code changes.

---

## Two things that would have silently broken this

Recorded because both produce a 401 that looks like a signing failure and is
not.

**Token version.** `Program.cs` sets
`Authority = https://login.microsoftonline.com/{tenant}/v2.0`, which expects a
v2 issuer. A default app registration issues **v1** tokens, whose issuer is
`https://sts.windows.net/{tenant}/`. Those fail issuer validation — and worse,
`IssuerSchemeSelector` matches on the prefix `https://login.microsoftonline.com/`,
so a v1 token would not even reach the Entra validator. It would fall through to
the internal scheme and be rejected there, for a completely unrelated reason.
Setting `requestedAccessTokenVersion = 2` on the registration is what makes the
two halves agree.

**Audience shape.** With `requestedAccessTokenVersion = 2`, the `aud` claim is
the bare client-id GUID, *not* `api://{client-id}`. Configuring the latter is a
401 with no useful diagnostic. The value in `appsettings.json` was set by reading
the decoded token rather than by assuming.

---

## Reproducing it

```powershell
az login --tenant 03490f7a-f873-47af-9963-ae925b4871b8 --allow-no-subscriptions
$appId = "62fe0e7f-3b56-443e-82d0-baa577371525"
$token = az account get-access-token --scope "api://$appId/access_as_user" --query accessToken -o tsv
curl.exe -s -H "Authorization: Bearer $token" http://localhost:5021/api/orders/whoami
```

The registration was created entirely from the CLI — app registration, exposed
scope, and pre-authorisation of the Azure CLI's own client id
(`04b07795-8ddb-461a-bbee-02f9e1bf7b46`) against that scope. That last step is
the one most walkthroughs omit: the CLI is just another client application, and
the API has to trust it before Entra will issue a token for the scope. Without
it you get a consent prompt that cannot be completed non-interactively, which
looks exactly like the `AADSTS65005` this started with.

Note also that the two scopes cannot be created and pre-authorised in a single
Graph `PATCH` — the permission id does not exist yet when the request is
validated, and the whole call is rejected atomically. It takes two.

---

## What I would still not claim

That the thinkbridge registration works. It has never been exercised, and the
only way to know is to obtain the grant and run the same three curls against it.
The code path is proven; that particular configuration is not.
