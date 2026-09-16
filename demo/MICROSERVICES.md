# Verifying the stack

The README says what the system is. This says how to check that it is, and what
each answer proves. Every command here was run against the running stack.

## Start

```bash
cp .env.example .env       # generate a key pair into it
docker compose up --build
docker compose ps          # all four: healthy
```

Before the stable packages are public, point the build at the staged local
candidate (the path is inside the Docker build context):

```bash
NOELIA_SOURCE=/src/.local-feed/5.0.0 \
  docker compose --env-file .env up --build --wait
```

`docker compose ps` showing `healthy` proves less than it looks: a container is
healthy when its own probe answers, which says nothing about whether the
services agree with each other. That is what the rest of this file is for.

## The browser gets its files

```bash
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8080/
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8080/login
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8080/css/main.css
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8080/js/pages/dashboard.js
```

Four times `200`. A `404` on the JS means the frontend image was built before
the file existed — the image copies, it does not mount.

## Registration reaches the service through both hops

```bash
SESSION=$(curl -s -X POST http://localhost:8080/api/auth/register \
  -H 'Content-Type: application/json' \
  -d '{"displayName":"Demo Person","email":"demo@example.com","password":"a-long-enough-password"}')
TOKEN=$(echo "$SESSION" | python3 -c "import sys,json; print(json.load(sys.stdin)['token'])")
```

That path is browser → nginx → gateway → user-service. A failure at any hop
looks the same from here, so read `docker compose logs gateway` before guessing.

## A token minted by one service is accepted by the other

```bash
curl -s -o /dev/null -w "ohne Token: %{http_code}\n" http://localhost:8080/api/todos
curl -s -o /dev/null -w "mit Token:  %{http_code}\n" \
  http://localhost:8080/api/todos -H "Authorization: Bearer $TOKEN"
```

`401` then `200`. todo-service verified an ES256 signature it could not have
produced — it holds only the public half:

```bash
docker compose exec todo-service printenv | grep -c Jwt__PrivateKey   # 0
docker compose exec user-service printenv | grep -c Jwt__PrivateKey   # 1
```

This is also the check that fails first when the two services were given keys
from different pairs: user-service signs happily and todo-service refuses
everything.

## One person's todos are one person's

```bash
curl -s -X POST http://localhost:8080/api/todos -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"title":"Etwas erledigen"}'

OTHER=$(curl -s -X POST http://localhost:8080/api/auth/register \
  -H 'Content-Type: application/json' \
  -d '{"displayName":"Andere","email":"andere@example.com","password":"a-long-enough-password"}' \
  | python3 -c "import sys,json; print(json.load(sys.stdin)['token'])")

curl -s http://localhost:8080/api/todos -H "Authorization: Bearer $OTHER"
```

`[]`. The second person sees nothing, because the owner comes from the token
and never from a parameter a caller could change.

## Signing out ends the session

```bash
curl -s -c jar -X POST http://localhost:8080/api/auth/register \
  -H 'Content-Type: application/json' \
  -d '{"displayName":"Demo","email":"demo@example.com","password":"a-long-enough-password"}'

curl -s -b jar -o /dev/null -w "refresh:  %{http_code}\n" -X POST http://localhost:8080/api/auth/refresh
curl -s -b jar -c jar -o /dev/null -X POST http://localhost:8080/api/auth/sign-out -H "Authorization: Bearer $TOKEN"
curl -s -b jar -o /dev/null -w "danach:   %{http_code}\n" -X POST http://localhost:8080/api/auth/refresh
```

`200` then `401`. The session ended in user-service's own SQLite file — no other
server had to be running for that to take effect. This is the check the demo
used to need an extra container for.

**The access token it was carrying keeps working until it expires.** That is the
trade, stated rather than hidden:

```bash
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8080/api/users/me -H "Authorization: Bearer $TOKEN"
```

Still `200`. The window is `JwtSettings:ExpireMinutes` — 15 minutes here — and
it is a number you set, not infrastructure you deploy. Closing it to zero is
what a revocation store is for, and this demo deliberately runs without one.

The refresh token never appears in a response body:

```bash
curl -s -D - -o /dev/null -X POST http://localhost:8080/api/auth/register \
  -H 'Content-Type: application/json' -d '{...}' | grep -i set-cookie
# noelia.rt=...; path=/api/auth; samesite=strict; httponly
```

`httponly` is the point: a cross-site scripting bug cannot read it. The access
token lives in the page's memory and is gone on reload — the cookie is what
brings a session back.

## The logs say the right amount

```bash
docker compose logs user-service --no-log-prefix | grep -c "Security headers score"
```

`1`, however many requests you made. A security warning that repeats on every
response is one that gets switched off, and then it protects nothing. Each
distinct finding is stated once per process.

```bash
docker compose logs user-service --no-log-prefix | grep "Blocked revoked token"
```

One line per refused request, naming the `jti`, the subject and the path.

## Test suite

```bash
dotnet test                       # microservice and monolith suites, no container runtime needed
npm --prefix src/frontend test    # 3 tests, against a real DOM
```

The services are hosted in memory against a temporary SQLite file. Nothing to
start, nothing to skip.

`MicroserviceEndToEndTests` uses two separately hosted service factories: User
issues the ES256 access token, Todo verifies it, one owner creates an item and a
second owner receives an empty list plus 404 when attempting to mutate it. This
is the automated equivalent of the manual checks above and fails if either
cross-service key agreement or ownership filtering is removed.

The release-only container pass is:

```bash
./eng/test-docker-health.sh 5.0.0 /absolute/path/to/local-feed
```

It waits for the frontend, gateway, User and Todo healthchecks, verifies live
and ready directly on every API, and requires `/noelia` to be an empty 404 in
Production before starting the separate gatewayless Monolith gate.

The frontend tests exist because of one bug. `FormController` disabled the form
before reading it, and a disabled control is **left out of `FormData`
entirely** — so every field arrived at the server as null and registration
answered 400. Every other test here posts JSON and never touches a form, so
nothing caught it. These do: reverse the two lines and the first one goes red.

## Two startup warnings that are not defects

```
Storing keys in a directory '/root/.aspnet/DataProtection-Keys' ...
No XML encryptor configured. Key ... may be persisted to storage in unencrypted form.
```

ASP.NET Core's Data Protection key ring, created by the framework. **Nothing in
this demo is encrypted with it** — the services set no cookie but the refresh
cookie, which carries the token itself and does not go through Data Protection.
Verified with `curl -D -`: no other `Set-Cookie` on any endpoint.

They become real warnings the moment something *does* use it — cookie
authentication, antiforgery, `IDataProtector`. Then the keys have to be shared
between replicas and protected at rest, or every replica rejects the others'
cookies and anyone with filesystem access can forge them.
