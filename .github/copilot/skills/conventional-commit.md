# Conventional Commits Guidelines

## Format
```
type(scope): short description

[optional body]

[optional footer(s)]
```

## Commit Types
| Type | When to use |
|---|---|
| `feat` | New feature (correlates with MINOR in SemVer) |
| `fix` | Bug fix (correlates with PATCH in SemVer) |
| `docs` | Documentation only changes |
| `style` | Formatting, missing semicolons – no logic change |
| `refactor` | Code change that is neither a fix nor a feature |
| `perf` | Performance improvement |
| `test` | Adding or fixing tests |
| `build` | Changes to build system or dependencies |
| `ci` | CI/CD pipeline changes |
| `chore` | Maintenance tasks (version bumps, config) |
| `revert` | Reverts a previous commit |

## Scopes (this project)
`api`, `app`, `db`, `scraper`, `docker`, `tests`, `docs`

## Examples
```
feat(scraper): add REAS jihomoravsky-kraj/cena-do-10-milionu URL filter
fix(api): correct OfferType HasConversion to use switch expression
perf(db): add GIN index on search_tsv for full-text search
test(api): add 30 CadastreTests covering RUIAN URL format
docs: update copilot-instructions after Session 9
chore(docker): set restart: unless-stopped on all 4 services
```

## Breaking Changes
Add `!` after type or `BREAKING CHANGE:` footer:
```
feat(api)!: rename /api/listings/search to /api/listings/query
BREAKING CHANGE: old endpoint removed, update all clients
```
