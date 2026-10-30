# Exhume

Dig up what your machine remembers.

Exhume is a Windows command-line application for exploring forgotten files,
browser history, application traces, and the owner's repository activity.
It collects evidence into a local SQLite store, groups activity by month, and
can narrate the results through a configured Ollama service.

| Mode | Evidence | Output |
|---|---|---|
| Residue | Windows run traces and leftover application folders | Program obituaries with evidence and confidence |
| Obsessions | Chrome, Edge, and Brave history | Monthly domain activity and era summaries |
| Attic | A selected folder or external drive | Monthly categories, counts, sizes, and file samples |
| Past Self | The owner's commits and current repository diff | Historical persona and comments tied to current lines |

Evidence readers do not modify their sources. Browser databases are copied
before being queried; repository reading does not create commits or refs.
Scans never invoke the model. Narration is generated on first view and cached.

## Build and run

Requires Windows 11 and the .NET 10 SDK. Keep the checkout and application data
on D: or another local drive outside C:. From this directory, use PowerShell and
dot-source the development setup before any build or test:

```powershell
. .\eng\dev.ps1
dotnet restore Exhume.slnx --configfile NuGet.Config --disable-build-servers -m:1
dotnet build Exhume.slnx --no-restore --disable-build-servers -m:1
dotnet test Exhume.slnx --no-restore --disable-build-servers -m:1
dotnet run --project src/Exhume.Cli --no-build -- doctor
```

Run these commands serially. `--disable-build-servers -m:1` also handles
environments where MSBuild worker nodes cannot start. The setup script keeps
temporary files, NuGet caches, and SDK user state in ignored `.local/`. It
disables telemetry, certificate creation, and global tool PATH changes. Package
restoration uses NuGet during development; it is not application traffic.
Dependencies are pinned in each project. Nullable references and
warnings-as-errors are enabled throughout the solution.

For the examples below, define a session-local PowerShell helper:

```powershell
function exhume { dotnet run --project .\src\Exhume.Cli --no-build -- @args }
```

To prepare a self-contained Windows x64 build, after the same development setup:

```powershell
dotnet publish src/Exhume.Cli/Exhume.Cli.csproj -c Release -r win-x64 --self-contained true --disable-build-servers -m:1 -o .local/publish
.\.local\publish\Exhume.Cli.exe doctor
```

Keep the published `prompts/` folder beside the executable. These are build
instructions; live-machine acceptance and model quality are separate checks.

## First scan without a model

Choose a data directory separate from the evidence you want to inspect:

```powershell
$env:EXHUME_HOME = 'D:\ExhumeData'
exhume dig --source folder --path 'D:\OldFiles'
exhume eras --source folder --no-model
exhume timeline --no-model
```

Folder scans aggregate files by UTC month and category, skipping hidden/system
entries and directories such as `.git`, `node_modules`, `bin`, and `obj`. EXIF
capture dates take precedence over filesystem dates. An EXIF timestamp without
an offset is treated as a UTC clock, not proof of its original timezone. Files
with implausible dates remain undated and are excluded from the timeline.

## Commands

All eight commands accept the shared configuration options listed below.
`timeline`, `obituaries`, `eras`, `haunt`, and `export` accept `--no-model`.

| Command | Purpose | Main options |
|---|---|---|
| `doctor` | Configuration, store counts, source availability, model status | Shared configuration options |
| `dig` | Scan and store evidence | `--source`, `--path`, `--author`, `--profile`, `--cutoff`, `--scan-repos`, `--elevated` |
| `timeline` | Monthly artifact counts; optionally open a month | `--source`, `--year`, `--month YYYY-MM`, `--no-model` |
| `obituaries` | Program evidence and obituaries | `--limit` (default 20), `--no-model` |
| `eras` | Monthly browser, folder, or repository summaries | `--source`, `--year`, `--no-model` |
| `haunt` | Persona-voiced comments on the current diff | `--path` / `--repo`, `--author`, `--cutoff`, `--report`, `--no-model` |
| `export` | Write a new Markdown report | `--path` / `--output`, `--redact`, `--no-model` |
| `forget` | Remove stored evidence, narration, and application-owned temporary/report files | `--yes` |

Sources are `folder`, `browser`, `windows`, and `git`. Repeat `--source` on `dig`
or give comma-separated values. With no source selection, `dig` attempts every
available source; folder and repository sources still require a path. Unavailable
sources are listed and skipped. Each source reports progress and its saved count.

```powershell
exhume dig --source browser --profile 'edge:Default'
exhume eras --source browser --year 2020 --no-model
exhume dig --source windows
exhume obituaries --limit 10 --no-model
exhume dig --source git --path 'D:\Repositories\Archive' --author 'owner@example.invalid'
exhume eras --source git --no-model
exhume timeline --month 2020-06 --no-model
```

Browser profiles can be qualified as `chrome:Default`, `edge:Profile 1`, or
`brave:Default`. An unqualified profile name can match multiple browsers. History
copies live under the configured data directory's `temp/` and are removed after
reading, including failure paths.

Git author filtering uses an email address, compared case-insensitively. If
`--author` is omitted, the repository's configured `user.email` is used;
Exhume does not change that configuration.

### Haunt the current diff

```powershell
exhume haunt --path 'D:\Repositories\Archive' --author 'owner@example.invalid' --no-model
exhume haunt --path 'D:\Repositories\Archive' --author 'owner@example.invalid' --cutoff '2023-01-01' --report review.md
```

The default persona cutoff is UTC midnight eighteen months before today's date. Persona
evidence comes only from matching commits strictly before that cutoff. The diff
includes staged and working-directory changes against HEAD, with file and line
caps. Binary, linked, or oversized content is omitted. A clean tree produces a
neutral message.

Accepted comments must match the supplied file and a real new-file line visible
in the bounded prompt. There are at most five comments per file, each at most
two sentences. A chunk with no valid comments has nothing to say. The optional
`--report review.md` saves under `<data directory>/reports/exhume-review.md`;
pass a filename, not a destination path. Comments are never inserted into source
files or Git metadata.

### Optional sources

```powershell
exhume dig --source git --path 'D:\Repositories' --scan-repos --author 'owner@example.invalid'
exhume dig --source windows --elevated
```

Repository discovery searches the supplied root and two directory levels below
it. `--elevated` opts into attempting Prefetch reads; it does not request or grant
administrator rights. An unreadable Prefetch directory is reported and skipped
while standard Windows sources remain usable. Prefetch reads only filenames and
filesystem timestamps, not compressed contents.

## Narration and configuration

Precedence: command-line options, environment variables, `exhume.json` beside
the executable, then defaults. `doctor` shows effective values and their origins.

| CLI option | Environment variable | Default |
|---|---|---|
| `--home` | `EXHUME_HOME` | `.exhume` under the current working directory |
| `--ollama-url` | `EXHUME_OLLAMA_URL` | `http://localhost:11434` |
| `--model` | `EXHUME_MODEL` | `gemma4:e4b` |
| `--timeout-seconds` | `EXHUME_TIMEOUT_SECONDS` | `120` per request |
| `--max-attempts` | `EXHUME_MAX_ATTEMPTS` | `3` |

Example `exhume.json`:

```json
{
  "OllamaUrl": "http://localhost:11434",
  "ModelTag": "gemma4:e4b",
  "DataDirectory": "D:/ExhumeData",
  "RequestTimeoutSeconds": 120,
  "MaximumAttempts": 3
}
```

Use an already installed model tag on the configured Ollama service. The
`gemma4:e4b` default is configuration, not confirmation that the tag is installed,
fits the available hardware, or has passed a live quality check. Exhume does not
download models. `doctor` reads the model listing without generating text.

The configured Ollama URL is the application's only HTTP destination. Keep it
local to keep narration evidence on the machine. Redirects and proxies are
disabled for model calls. Transport failures, timeouts, malformed JSON, and
invalid response shapes are retried with bounded attempts; exhausted narration
falls back to raw evidence and a visible warning.

Narration views report their model-call count. Repeating a cached view makes zero
generation calls. Cache keys include narration kind, artifact or source/month
identity, prompt version, and model tag. Persona identity uses repository, author,
and cutoff; haunt identity also includes the persona and current diff. Re-scanning
an existing artifact does not invalidate its narration by itself. Change the
prompt version or model tag to produce a new narration, or use `forget` to clear
the stored evidence and cache together.

## Export and privacy

```powershell
exhume export --path 'D:\Reports\exhume-private.md' --no-model
exhume export --path 'D:\Reports\exhume-share.md' --redact
```

Exports contain the timeline, obituaries, eras, and any undated evidence. Choose
a new destination outside C:; existing files are not overwritten. Unredacted
reports can contain private paths, titles, commit subjects, and sample names.

Redacted exports replace browser titles, reduce file samples to extensions, and
keep domains. They also omit evidence paths, executable paths, commit subjects,
installed-sibling names, and generated narration so cached prose cannot restore
removed details. Redaction affects the report only; the local store is unchanged.
Counts, dates, domains, and program names can still be identifying, so review the
report before sharing it.

Application data defaults to `.exhume/`, with evidence and narration in
`exhume.db`. Writable data directories on C:, drive roots, network/device paths,
and links that resolve to prohibited locations are rejected. Do not commit
private evidence, reports, or the data directory.

```powershell
exhume forget --yes
exhume doctor
```

`forget` asks for confirmation in an interactive terminal. In a noninteractive
session, it does nothing unless `--yes` is supplied. It clears the store and
application-owned files under `temp/` and `reports/`, preserves unrelated files
and linked directories, and removes the data directory only if empty. Exports
written elsewhere are not removed. A subsequent `doctor` opens an empty store.

## Verification and limits

Automated tests use synthetic browser databases, temporary repository and folder
fixtures, fake registry/disk readers, and fake HTTP handlers. They run without
administrator rights or a running model and clean up their temporary evidence.
These checks do not establish real-machine acceptance.

The owner still needs to verify browser copying with a real running browser,
high-confidence residue on the actual machine, representative old folders,
useful line-bound comments on a real repository, and the configured model's
availability and output quality. See the [acceptance checklist](docs/spec.md#18-acceptance-criteria)
and [implementation decisions and proposals](docs/CHANGES.md).

Exit codes are `0` for success, including empty results and narration fallback;
`1` for usage/configuration errors; `2` for a partial scan failure or cancellation;
and `3` for unwritable application storage. Previously saved scan batches remain
after cancellation; the pending batch is discarded.

The solution separates Core, Narration, the four source adapters, and the CLI.
Prompt files are versioned and copied into build and publish output. Repository
changes are left uncommitted; the owner handles source control.
