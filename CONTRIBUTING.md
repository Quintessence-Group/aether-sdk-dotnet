# Contributing to AetherDb.Sdk

Thanks for your interest in improving the .NET SDK for
[Aether](https://aetherdb.ai)! Bug reports, fixes, docs, and features are all welcome.

The package id is `AetherDb.Sdk`; the assembly and namespace are `Aether.Sdk` (you install
`AetherDb.Sdk` but write `using Aether.Sdk;`).

## Getting started

```bash
git clone https://github.com/quintessence-group/aether-sdk-dotnet.git
cd aether-sdk-dotnet
dotnet build
```

## Development workflow

1. Fork the repo and create a topic branch off `main`.
2. Make a focused change, covered by tests.
3. Run the checks below — everything should pass.
4. Open a pull request describing the change and its motivation.

### Build, test & format

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes
```

## Guidelines

- Run `dotnet format` before committing.
- Add or update tests for any behavior change.
- Update `README.md` for any user-facing change.
- The SDK multi-targets `netstandard2.0` and `net8.0`; keep changes compatible with both.
- Keep public API changes backward-compatible where possible; call out breaking changes
  clearly in the PR.

## Reporting issues

- **Bugs / features:** open a GitHub issue.
- **Security vulnerabilities:** follow [SECURITY.md](SECURITY.md) — please do not file a
  public issue.

## License

By contributing, you agree that your contributions will be licensed under the project's
[MIT License](LICENSE).
