# Exhume

Dig up what your machine remembers.

A local-first Windows command-line application for exploring forgotten files,
browser history, application traces and repository activity. Evidence stays
read-only; narration uses the configured local Ollama service.

## Stage 1

The foundation includes Core contracts, a SQLite store, configuration loading,
timeline grouping, prompt budgeting, narration interfaces and a CLI skeleton.
`doctor` is implemented. The other seven commands currently print
`not implemented`; adapters and narration arrive in later stages.

## Development

Requires Windows and the .NET 10 SDK. Keep the checkout on D:.
From this directory, use PowerShell:

```powershell
. .\eng\dev.ps1
dotnet restore Exhume.slnx --configfile NuGet.Config --disable-build-servers -m:1
dotnet build Exhume.slnx --no-restore --disable-build-servers -m:1
dotnet test Exhume.slnx --no-restore --disable-build-servers -m:1
dotnet run --project src/Exhume.Cli --no-build -- doctor
```

The development script puts temporary files, NuGet caches and SDK user state in
ignored `.local/`. It disables telemetry, certificate creation and global tool
PATH changes. Package restore requires NuGet access; the application itself
contacts only its configured Ollama endpoint. Libraries are pinned in individual
project files. Nullable references and warnings-as-errors apply everywhere.

Tests use synthetic data and temporary directories. They need neither a model
nor administrator rights. Empty adapter test projects are placeholders for later
stages; passing the foundation suite does not verify real-machine scanning.

## Configuration

Precedence: command-line options, environment variables, `exhume.json` beside
the executable, then defaults. `doctor` shows effective values and their origins.

| CLI option | Environment variable | Default |
|---|---|---|
| `--home` | `EXHUME_HOME` | `.exhume` under the current directory |
| `--ollama-url` | `EXHUME_OLLAMA_URL` | `http://localhost:11434` |
| `--model` | `EXHUME_MODEL` | `gemma4:e4b` |
| `--timeout-seconds` | `EXHUME_TIMEOUT_SECONDS` | `120` |
| `--max-attempts` | `EXHUME_MAX_ATTEMPTS` | `3` |

```json
{
  "OllamaUrl": "http://localhost:11434",
  "ModelTag": "gemma4:e4b",
  "DataDirectory": "D:/ExhumeData",
  "RequestTimeoutSeconds": 120,
  "MaximumAttempts": 3
}
```

Keep the data directory off C:. Application temporary files belong under its
`temp/` subdirectory. Never commit `.exhume/` or private evidence. Doctor checks
the model list without generating text or downloading a model. An unreachable
model is reported without failing the application health check.

## Layout and handoff

- `src/Exhume.Core/`: shared records, adapter/store contracts and deterministic logic.
- `src/Exhume.Narration/`: narration shapes and service contract.
- `src/Exhume.Adapters.*/`: one project per evidence source.
- `src/Exhume.Cli/Commands/`: one file per command.
- `tests/Exhume.*.Tests/`, `tests/Fixtures/`: tests and shared fixtures.
- `prompts/`: future versioned `.txt` prompts, copied to build/publish output.
- `docs/spec.md`: design and acceptance criteria.
- `docs/CHANGES.md`: recorded compatibility decisions and proposed deviations.

Builders leave changes uncommitted. The owner commits Stage 1 and prepares the
builder worktrees before parallel implementation begins.
