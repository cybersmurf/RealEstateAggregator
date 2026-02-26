---
applyTo: '*'
description: "Comprehensive secure coding instructions for all languages and frameworks, based on OWASP Top 10 and industry best practices."
---
# Secure Coding and OWASP Guidelines

## Instructions

Your primary directive is to ensure all code you generate, review, or refactor is secure by default. You must operate with a security-first mindset. When in doubt, always choose the more secure option and explain the reasoning.

### 1. A01: Broken Access Control & A10: SSRF
- **Enforce Principle of Least Privilege:** Default to the most restrictive permissions.
- **Deny by Default:** Access should only be granted if there is an explicit rule allowing it.
- **Validate All Incoming URLs for SSRF:** Use strict allow-list-based validation.
- **Prevent Path Traversal:** Sanitize file path inputs using secure path APIs.

### 2. A02: Cryptographic Failures
- **Use Strong Algorithms:** For hashing passwords use Argon2 or bcrypt. Never MD5/SHA-1.
- **Protect Data in Transit:** Always default to HTTPS.
- **Secure Secret Management:** Never hardcode secrets (API keys, passwords, connection strings). Use environment variables or secrets management.
  ```csharp
  // GOOD: Read from environment
  var apiKey = Environment.GetEnvironmentVariable("API_KEY");
  // BAD: Hardcoded
  var apiKey = "sk_hardcoded_bad";
  ```

### 3. A03: Injection
- **No Raw SQL Queries:** Use parameterized queries (EF Core handles this). Never string-concatenate SQL from user input.
- **Prevent XSS:** Use context-aware output encoding. Prefer methods that treat data as text.

### 4. A05: Security Misconfiguration
- **Disable verbose error messages** in production environments.
- **Set Security Headers:** Add `Content-Security-Policy`, `Strict-Transport-Security`, `X-Content-Type-Options`.
- **Use Up-to-Date Dependencies:** Run `dotnet list package --vulnerable` regularly.

### 5. A07: Identification & Authentication Failures
- **Secure Session Management:** Use `HttpOnly`, `Secure`, and `SameSite=Strict` cookie attributes.
- **Protect Against Brute Force:** Implement rate limiting on authentication endpoints.

## General Guidelines
- **Be Explicit About Security:** When suggesting code that mitigates a security risk, state what you are protecting against.
- **Educate During Code Reviews:** When identifying a vulnerability, explain the risk AND provide the corrected code.
