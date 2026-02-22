# GitHub Copilot CLI Skills - RealEstateAggregator

This directory contains skill definitions for GitHub Copilot CLI to make common development tasks easier.

## Available Skills

### 1. **add-api-endpoint.md**
Add a new Minimal API endpoint with full implementation (DTO, endpoint, service method).

**Usage:**
```bash
gh copilot suggest "add api endpoint for getting top N listings by price"
```

---

### 2. **create-scraper.md**
Create a new real estate scraper for Python backend.

**Usage:**
```bash
gh copilot suggest "create scraper for sreality.cz"
```

---

### 3. **add-database-migration.md**
Create and apply EF Core database migration.

**Usage:**
```bash
gh copilot suggest "add database migration for user favorites"
```

---

### 4. **add-blazor-page.md**
Create a new Blazor Server page with MudBlazor components.

**Usage:**
```bash
gh copilot suggest "add blazor page for user favorites"
```

---

### 5. **deploy-services.md**
Deploy RealEstateAggregator services using Docker Compose.

**Usage:**
```bash
gh copilot suggest "deploy all services to production"
```

---

## How to Use

### Install GitHub Copilot CLI
```bash
# Install extension
gh extension install github/gh-copilot

# Authenticate
gh auth login
```

### Run Skills
```bash
# Interactive mode
gh copilot suggest

# Direct query
gh copilot suggest "add new scraper for bezrealitky.cz"

# Execute suggested command
gh copilot suggest -t shell "start all docker services"
gh copilot suggest -t git "commit changes with descriptive message"
```

### Customize Skills
Edit markdown files in `.github/copilot/skills/` to:
- Add project-specific context
- Update code examples
- Add new troubleshooting tips
- Include recent learnings

## Skill Template

Create new skills following this structure:

```markdown
# {Skill Name} Skill

## Description
Brief description of what this skill does.

## Usage
```bash
gh copilot suggest "{example query}"
```

## Steps
1. Step one with code example
2. Step two...

## Checklist
- [ ] Task 1
- [ ] Task 2

## Related Files
- Path/to/relevant/file.cs

## Troubleshooting
**Error:** Description
- Solution: Fix
```

## Tips

- **Be specific:** "add api endpoint for searching listings by location" is better than "add endpoint"
- **Use natural language:** Copilot understands context from project files
- **Iterate:** Run `gh copilot suggest` multiple times to refine commands
- **Combine with git:** `gh copilot suggest -t git "commit message for feature xyz"`

## Contributing

When you discover a useful pattern or workflow:

1. Create a new skill markdown file
2. Follow the template structure
3. Include real examples from this project
4. Add to this README's "Available Skills" section

---

**Last Updated:** 22. února 2026  
**Project:** RealEstateAggregator  
**Stack:** .NET 10 + Blazor + PostgreSQL + Python
