# Markdown link/anchor guard for this repository (CI, invoked by .github/workflows/jobsystem-ci.yml).
#
# What it protects: every relative link and every intra-document anchor in the tracked .md files must
# resolve. Editing a README is otherwise the cheapest way to break navigation silently -- renaming a
# heading invalidates "#anchor" links and moving a file invalidates relative paths, and nothing else
# in the gate matrix looks at markdown.
#
# Scope: `git ls-files '*.md'` minus docs/archive/ (history, deliberately frozen), docs/research/
# (scraped external pages, not repo-authored) and third-party trees.
#
# Checks per link:
#   [1] http(s):/mailto:            -> skipped (no network access here)
#   [2] #anchor                     -> must match a heading slug or an <a id="..."> in the same file
#   [3] relative/path               -> must exist on disk
#   [4] relative/path#anchor (.md)  -> path must exist and the target must contain that anchor
#
# Exit code: 0 = all links resolve; 1 = at least one broken link.
# ASCII only on purpose: Windows PowerShell 5.1 reads non-BOM UTF-8 as ANSI and mangles it.
param(
    [string[]]$Paths = @()
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $repo

# ---- heading slugs (GitHub-ish: lowercase, drop punctuation, spaces -> '-', keep CJK) ----
function Get-Slug([string]$text) {
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $text.Trim().ToLowerInvariant().ToCharArray()) {
        if ([char]::IsLetterOrDigit($ch) -or $ch -eq '-' -or $ch -eq '_') { [void]$sb.Append($ch) }
        elseif ([int]$ch -gt 0x2FFF -and -not [char]::IsWhiteSpace($ch)) { [void]$sb.Append($ch) }
        elseif ($ch -eq ' ' -or $ch -eq "`t") { [void]$sb.Append('-') }
    }
    return $sb.ToString()
}

function Get-Anchors([string]$path) {
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    $inFence = $false
    foreach ($line in [IO.File]::ReadAllLines($path, [Text.Encoding]::UTF8)) {
        if ($line -match '^\s*(```|~~~)') { $inFence = -not $inFence; continue }
        if ($inFence) { continue }
        if ($line -match '^#{1,6}\s+(.*)$') { [void]$set.Add((Get-Slug $Matches[1])) }
        foreach ($m in [regex]::Matches($line, '<a\s+id="([^"]+)"')) { [void]$set.Add($m.Groups[1].Value.ToLowerInvariant()) }
    }
    return $set
}

# ---- file list: tracked markdown, minus frozen/historical and third-party trees ----
$files = $Paths
if ($files.Count -eq 0) {
    $files = @(git -c core.quotepath=false ls-files '*.md')
    $files = @($files | Where-Object {
            $_ -notmatch '^docs/archive/' -and $_ -notmatch '^docs/research/' -and
            $_ -notmatch 'thirdParty' -and $_ -notmatch 'third_party'
        })
}

$linkTotal = 0
$broken = New-Object System.Collections.Generic.List[string]

foreach ($f in $files) {
    if (-not (Test-Path -LiteralPath $f)) { $broken.Add("$f : file missing"); continue }
    $dir = Split-Path -Parent (Resolve-Path -LiteralPath $f).Path
    if ([string]::IsNullOrEmpty($dir)) { $dir = $repo }
    $anchorsHere = Get-Anchors $f
    $inFence = $false
    $lineno = 0
    foreach ($line in [IO.File]::ReadAllLines($f, [Text.Encoding]::UTF8)) {
        $lineno++
        if ($line -match '^\s*(```|~~~)') { $inFence = -not $inFence; continue }
        if ($inFence) { continue }
        foreach ($m in [regex]::Matches($line, '\[[^\]]*\]\(([^)\s]+)\)')) {
            $target = $m.Groups[1].Value
            $linkTotal++
            if ($target -match '^(https?://|mailto:)') { continue }
            if ($target.StartsWith('#')) {
                if (-not $anchorsHere.Contains($target.Substring(1).ToLowerInvariant())) {
                    $broken.Add("${f}:${lineno} : $target  <- anchor missing in this file")
                }
                continue
            }
            $path, $anchor = $target, ''
            $hash = $target.IndexOf('#')
            if ($hash -ge 0) { $path = $target.Substring(0, $hash); $anchor = $target.Substring($hash + 1) }
            $full = [IO.Path]::GetFullPath((Join-Path $dir $path.Replace('/', [IO.Path]::DirectorySeparatorChar)))
            if (-not (Test-Path -LiteralPath $full)) {
                $broken.Add("${f}:${lineno} : $target  <- path missing")
            }
            elseif ($anchor -and $full.ToLowerInvariant().EndsWith('.md')) {
                if (-not (Get-Anchors $full).Contains($anchor.ToLowerInvariant())) {
                    $broken.Add("${f}:${lineno} : $target  <- anchor missing in target")
                }
            }
        }
    }
}

if ($broken.Count -gt 0) {
    Write-Host "FAIL: $($broken.Count) broken link(s) in $($files.Count) markdown file(s) ($linkTotal links checked)"
    $broken | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "doc-links: files=$($files.Count) links=$linkTotal broken=0"
Write-Host 'PASS: doc links hold'
exit 0
