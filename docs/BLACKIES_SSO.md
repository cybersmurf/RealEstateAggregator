# Přihlášení vlastníků účtem Blackies pošta (Stalwart OIDC)

Stav: nasazeno 2026-09-30. Petr a Lenka se do RealEstate hlásí stejným
účtem jako do pošty, trezoru, receptů a poznámek; zákazníci dál heslem.

## Analýza

- App (Blazor Server) drží přihlášení v cookie `realestate.auth`, ve které je
  bearer token API jako claim (`api_token`); `ApiAuthHandler` ho přikládá ke
  všem voláním API. Token vydává API v `/api/auth/login` po ověření hesla.
- API rozlišuje `IsAdmin` na uživateli; admin obchází tarify (`PlanAccess.HasPlan`)
  a vidí původní texty inzerátů. Výchozí admin je `ADMIN_EMAIL` (Gmail, heslo z env).
- Stalwart na mail.blackies.cz je OIDC provider (authorization code + PKCE,
  discovery `/.well-known/openid-configuration`, claims `email`, `name`,
  `preferred_username`). Ostatní domácí služby (Grafana, Mealie, Memos, RDP)
  ho už používají; klient musí být registrovaný (`requireClientRegistration`).
- Cíl: přidat druhý způsob přihlášení bez zásahu do tokenů API a bez změny
  zákaznického toku. Žádné nové tabulky.

## Návrh

```text
/login → „Přihlásit se účtem Blackies pošta“
  → GET /account/login-blackies (Challenge schématu "Blackies")
  → Stalwart /login (uživatel zadá heslo od pošty)
  → /signin-blackies (code + PKCE → access token; App ho neukládá)
  → OnTokenValidated: POST API /api/auth/oidc {accessToken}
      API: GET Stalwart /auth/userinfo s tím tokenem → email, name
           → jen adresy z OIDC_ADMIN_EMAILS → účet vznikne / IsAdmin=true
           → vydá běžný bearer token
  → App sestaví stejnou cookie jako u hesla (AccountEndpoints.BuildPrincipal)
```

Proč takhle:
- **Ověření tokenu dělá API voláním userinfo**, ne App parsováním id_tokenu:
  API je jediné místo, které účty zakládá a vydává tokeny; App zůstává tenká.
  Token cizího vydavatele na userinfo Stalwartu neprojde.
- **Allowlist adres v API** (`OIDC_ADMIN_EMAILS`): ostatní účty od pošty (kdyby
  přibyly) do aplikace nemají přístup, dokud se nepřidají. Kdo je v seznamu,
  je automaticky admin — to je smysl tohohle přihlášení.
- **Bez `Oidc__ClientId` se nic neregistruje** ani neukazuje: vývoj a testy
  bez Stalwartu fungují jako dřív.
- `UseForwardedHeaders` (X-Forwarded-Proto od Traefiku), jinak by
  `redirect_uri` a Secure cookie vycházely z `http://`.

## Nastavení

| Kde | Co |
|---|---|
| Stalwart `OAuthClient` | `clientId: realestate`, redirect `https://realestate.sudata.eu/signin-blackies`, secret |
| `/srv/realestate/.env` | `OIDC_CLIENT_ID=realestate`, `OIDC_CLIENT_SECRET`, `OIDC_ADMIN_EMAILS=petr@blackies.cz,lenka@blackies.cz` |
| API env | `OIDC_ADMIN_EMAILS`, `OIDC_USERINFO_URL` (výchozí `https://mail.blackies.cz/auth/userinfo`) |
| App env | `Oidc__Authority`, `Oidc__ClientId`, `Oidc__ClientSecret` |

Nový vlastník = adresa do `OIDC_ADMIN_EMAILS` + `docker compose up -d api`.
Odebrání = adresa pryč (účet zůstane, ale přes Blackies se nepřihlásí; heslo
nemá, `PasswordHash` je null → přihlášení heslem taky neprojde).

## Ověření

Playwright: `/login` → tlačítko → Stalwart (`input[name=username]`,
`input[name=password]`, `button[type=submit]`) → zpět na `/` s cookie,
`/account` ukazuje admina; API `/api/auth/oidc` s náhodným tokenem vrací 401,
cizí adresa 403. Z LAN resolve `realestate.sudata.eu` na 192.168.11.2, App
si pro discovery a userinfo sahá na mail.blackies.cz přes Hetzner (hairpin,
stejně jako Grafana).

## Co by šlo dál

- Odhlášení i ze Stalwartu (`end_session_endpoint` Stalwart nemá).
- Skupina místo seznamu adres, až bude v Stalwartu víc lidí.
