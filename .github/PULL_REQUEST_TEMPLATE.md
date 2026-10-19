## What and why

<!-- What does this change, and why? Link the issue: "Fixes #123". -->

## How it was tested

<!-- Tests added or changed, and what you tried by hand (platform, browser, light/dark, phone width for UI changes). -->

## Checklist

- [ ] `dotnet build StreamZH.slnx` without warnings and `dotnet test tests/StreamZH.Tests` pass
- [ ] Frontend changes: `npx tsc -b`, `npx oxlint src` and `npm run build` pass in `frontend/`
- [ ] Tests for the new behaviour
- [ ] English everywhere; help texts short and plain; expert settings behind the Advanced switch
- [ ] New C# files have the GPL header; ported code is marked and listed in `NOTICE.md`
- [ ] Docs updated where users see a change (`docs/guide`, `docs/INSTALL.md`, `CHANGELOG.md` under the unreleased version)
- [ ] Security-relevant change (sign-in, folders, secrets, tools, outgoing requests): `docs/SECURITY.md` read and updated
- [ ] No personal data, real keys, real host names or copyrighted material in code, tests or screenshots
