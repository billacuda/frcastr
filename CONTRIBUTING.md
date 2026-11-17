# Contributing to frcastr

Thanks for helping. Fixes, features, and documentation are all welcome. This page explains how to
send them and the terms they're accepted under.

frcastr is maintained by one person, so please be patient with reviews. For anything bigger than a
small fix, open an issue first to talk it over, so nobody spends time on something that can't be
merged.

## Licensing of contributions

frcastr is licensed under the [MIT License](LICENSE). By sending a contribution, you agree that:

- It's licensed under the MIT License, the same as the rest of the project. This applies however
  you send it: a pull request, an issue, an email, or files shared some other way.
- You wrote it, or otherwise have the right to contribute it under that license.
- You keep the copyright in your own contribution. The project, and everyone who uses it, can use,
  change, and redistribute it under the license.

**Pull requests** (not accepted at this time; see [Pull requests](#pull-requests)): sign off each
commit (`git commit -s`). This adds a `Signed-off-by:` line, which certifies the
[Developer Certificate of Origin](https://developercertificate.org/).

**Contributions sent outside GitHub** (by email or as files): please include one line in writing,
such as "I contribute these files to frcastr under the MIT License."

Contributors are credited by name in the [changelog](CHANGELOG.md), with a link to their GitHub
profile if they have one. If you'd rather not be credited, please say so.

## Reporting bugs and security problems

- **Bugs and feature ideas:** open an issue using one of the templates.
- **Security problems:** don't open a public issue. Follow [SECURITY.md](SECURITY.md) instead.

## Code

### Building and testing

See [Quick start](README.md#quick-start) in the README. You need a .NET 10 SDK and SQL Server.

```powershell
dotnet build frcastr.slnx
```

There's no automated test suite yet, so describe how you checked the change: which pages,
widgets, or data sources you tried.

### Pull requests

> **Pull requests aren't accepted at this time.** frcastr doesn't take contributions through GitHub
> right now. If it opens up to official contributors later, pull requests will be accepted under the
> terms on this page, including [Licensing of contributions](#licensing-of-contributions) and the
> sign-off. Until then, open an issue to suggest a change.

When pull requests are open:

- Keep each pull request to one change, with a description of what it does and why.
- Add tests for fixes and new behavior where practical.
- Database changes need an EF Core migration: `dotnet ef migrations add <Name> --project src\frcastr.Infrastructure --startup-project src\frcastr.Web`.
- Firmware changes under `esp32/` should say which board and sensors they were tested on.
- Add a line to `CHANGELOG.md` under **[Unreleased]**, in the matching section. Follow the existing
  style: a short bold title, then one or two plain sentences about what changed for the person
  using it.
- User-facing text (interface, README, and changelog) uses the serial comma: "one, two, and three."
- Code scanning runs on every push. Fix what it finds, or, if a finding is a false positive, mark
  the line as reviewed with a short reason, the way the existing code does.

### AI-assisted contributions

AI tools are fine to use, but you must say so. When you send a contribution, state whether AI tools
were used and, if they were, which ones (for example, the assistant or model and its version) and
what they were used for: writing code, reviewing, or something else. A contribution that doesn't
say isn't accepted.

As with any contribution, you're responsible for what you send: review it, test it, and make sure
you have the right to contribute it.
