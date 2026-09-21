# Stop hook: whitespace-format the C# files edited since the last run, ONCE per turn.
#
# Replaces format-cs.ps1, a PostToolUse hook that ran `dotnet format` after every Edit/Write of a .cs
# file. Measured 2026-09-21: 5-7 s per edit for src projects, 14-15 s for test projects (the test
# projects pull in most of the solution). A task with 20-50 edits spent minutes just waiting.
#
# Design notes (all measured 2026-09-21):
#  - `--folder` mode formats from .editorconfig alone and never loads MSBuild: ~3.4 s for a 29-file
#    batch across four projects, versus 12.6 s in solution mode and 5-15 s per single edit in project
#    mode. Its output was byte-identical to project/solution mode on every file compared.
#  - `--include` takes repo-relative paths, resolved against the process cwd, so cwd is forced to the
#    repo root and the folder argument is `.`.
#  - "Edited since last run" = dirty in git (modified or untracked) AND mtime newer than a stamp file
#    in %TEMP%. Without the stamp every turn would re-format every dirty file.
#  - Migrations / obj / bin are never touched.
$ErrorActionPreference = 'SilentlyContinue'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location -LiteralPath $repo

$stamp = Join-Path ([System.IO.Path]::GetTempPath()) 'vault-extract-format.stamp'
$last  = if (Test-Path -LiteralPath $stamp) { (Get-Item -LiteralPath $stamp).LastWriteTimeUtc } else { [datetime]::MinValue }

$files = @(
    git ls-files -m -o --exclude-standard -- '*.cs' |
        Where-Object { $_ -notmatch '(^|/)(obj|bin|Migrations)/' } |
        Where-Object { (Test-Path -LiteralPath $_) -and ((Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $last) }
)
if ($files.Count -eq 0) { exit 0 }

# Chunk to stay well under the Windows command-line length limit.
for ($i = 0; $i -lt $files.Count; $i += 60) {
    $chunk = $files[$i..([Math]::Min($i + 59, $files.Count - 1))]
    dotnet format whitespace . --folder --include $chunk --verbosity quiet 2>$null
}

(New-Item -ItemType File -Path $stamp -Force).LastWriteTimeUtc = [datetime]::UtcNow
exit 0
