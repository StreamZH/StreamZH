# Users and roles

Settings → **Users** manages who can sign in. The first account, created at setup, is the **owner**.

## Roles

| Role | May |
|---|---|
| **Owner** | Everything, including other owners and the [owner-only settings](#owner-only-settings) |
| **Admin** | Everything except owners and the owner-only settings. Folders only inside the owner's storage locations |
| **Friend** | Library, downloads and requests; no settings. Requests are approved automatically by default |
| **Requester** | Only Discover, their own requests and their profile |

A disabled user cannot sign in, and their open sessions end at once. Changing someone's role or password also ends
their sessions.

## Users tab

**Add user**: user name (used to sign in), password (at least 8 characters, share it privately), display name,
role, enabled, and for Friends and Requesters:

- **Approve requests automatically**: the default of Settings → Requests, or on/off for this user.
- **Request limits**: movies and series per number of days; empty means no limit. Settings → Requests sets the
  defaults.

The list shows the role, last sign-in and a linked Discord account. Per user: edit, **set a new password** (they are
signed out everywhere), **Sign out everywhere**, unlink Discord, remove. Only an owner can change an owner; nobody
can change their own role.

## Invites

**Create invite** makes a link that lets someone create their own account with a set role (Friend or Requester).
Choose how long it is valid and how many accounts may be created with it, and add a note for whom it is. The link is
shown only once; anyone with it can create an account. **Withdraw** stops it at once; accounts already created with it
stay.

Invite links need the sign-in page: the authentication method must be **Forms** (Settings → General → Security).

## Your profile

The menu behind your name in the top bar → **Profile** (Requesters: also in the sidebar): display name, password change, and **Discord**: link your
Discord account to request with the StreamZH bot as yourself (Create code, then `/link CODE` in Discord; the code
works once, for 10 minutes).

## Owner-only settings

Some settings decide who gets in, which programs run and where secrets go. Only an owner can change them; Admins see
them read-only:

- the API key (see it, create a new one),
- security: authentication method, "Authentication required", certificate validation, trusted proxies, client IP
  header,
- host: bind address, port, URL base, application URL, HTTPS and its certificate (Admins may rename the instance),
- the outgoing proxy,
- backups: make, download, restore, delete, and the backup folder, interval and retention,
- tool paths (par2, 7-Zip, ffprobe) and extra par2/7-Zip parameters,
- storage locations.

## API key and sign-in-free access

- The **API key** (Settings → General) acts as an **Admin**: scripts can do everything an Admin can, but not the
  owner-only things.
- **"Disabled for local addresses"** and **External** authentication act as the owner: everyone on those networks
  or behind that proxy has full access. Use them only where that is true. See [docs/SECURITY.md](../SECURITY.md).

## Forgot your password?

An owner or admin can set a new password for another user (Users → the user → new password); only an owner can do
it for another owner. There is no reset by e-mail and no reset from the command line, so if you are the only owner,
keep your password in a password manager or create a second owner account.
