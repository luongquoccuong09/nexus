---
id: P0-D03
title: "Username and password first; Google linking later"
summary: "Launch with username and password authentication; add optional Google OIDC account linking later using reauthentication and (issuer, sub)."
date: 2026-11-23
decided-by: owner
status: accepted
---

# Username and password first; Google linking later

Start authentication with ASP.NET Core Identity username/password and BFF session cookies (`M1a`). Add Google OIDC linking in `M1c` once password auth is stable, requiring an active session plus password reauthentication and storing unique `(issuer, sub)` as the external identity key.

**Why:** Removes external IdP setup as a blocker for trial launch while keeping a secure, collision-proof path to Google/M365 identity linking.

**How to apply:** Never auto-link Google accounts by matching email (`SEC-22`). Require password reauthentication before linking (`SEC-23`) and prevent removing the user's last sign-in method (`M1.13`).
