# Keeps the C4 diagrams in README.md in step with docs/architecture/workspace.dsl (Structurizr DSL).
# Exports each view to Mermaid with the structurizr/structurizr Docker image, then rewrites the README
# between <!-- c4:<view>:start --> and <!-- c4:<view>:end --> markers.
#   ./scripts/sync-architecture.ps1          export, update docs/architecture/mermaid and README.md
#   ./scripts/sync-architecture.ps1 -Check   change nothing; exit 1 if either is out of date (used by CI)
# Requires Docker.

param([switch]$Check)

$ErrorActionPreference = "Stop"

$root      = Split-Path $PSScriptRoot -Parent
$archDir   = Join-Path $root "docs/architecture"
$mermaid   = Join-Path $archDir "mermaid"
$readmeFilePath    = Join-Path $root "README.md"
$dockerImage     = "structurizr/structurizr"
# view key in workspace.dsl -> marker name in the README
$views     = [ordered]@{ SystemContext = "context"; Containers = "containers" }

try {
    # Write into an empty dir so a view removed from the DSL does not leave a stale file behind
    $export = Join-Path ([IO.Path]::GetTempPath()) "kvytok-c4-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force $export | Out-Null
    docker run --rm -v "${archDir}:/usr/local/structurizr:ro" -v "${export}:/out" $dockerImage `
        export -workspace /usr/local/structurizr/workspace.dsl -format mermaid -output /out | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "structurizr export failed with exit code $LASTEXITCODE" }

    $readmeText = [IO.File]::ReadAllText($readmeFilePath)
    $nl = $readmeText.Contains("`r`n") ? "`r`n" : "`n"
    $stale = @()

    foreach ($view in $views.Keys) {
        $name = "structurizr-$view.mmd"
        $src  = Join-Path $export $name
        if (-not (Test-Path $src)) { throw "The export produced no $name; is the view key '$view' still in workspace.dsl?" }
        $diagram = ([IO.File]::ReadAllText($src).Trim() -replace "`r?`n", $nl)

        $current = Join-Path $mermaid $name
        if (-not (Test-Path $current) -or [IO.File]::ReadAllText($current) -ne [IO.File]::ReadAllText($src)) {
            $stale += "docs/architecture/mermaid/$name"
        }

        $marker  = $views[$view]
        $pattern = "(?s)(<!-- c4:$marker`:start -->\r?\n).*?(<!-- c4:$marker`:end -->)"
        if ($readmeText -notmatch $pattern) { throw "README.md has no c4:$marker markers" }
        $block = "``````mermaid$nl$diagram$nl``````$nl"
        $readmeText = [regex]::Replace($readmeText, $pattern, { param($m) $m.Groups[1].Value + $block + $m.Groups[2].Value })
    }

    if ([IO.File]::ReadAllText($readmeFilePath) -ne $readmeText) { $stale += "README.md" }

    if ($Check) {
        if ($stale) {
            Write-Error "Out of date with workspace.dsl: $($stale -join ', '). Run ./scripts/sync-architecture.ps1 and commit the result."
            exit 1
        }
        Write-Host "Architecture diagrams are up to date."
    } else {
        New-Item -ItemType Directory -Force $mermaid | Out-Null
        foreach ($view in $views.Keys) { Copy-Item (Join-Path $export "structurizr-$view.mmd") $mermaid -Force }
        [IO.File]::WriteAllText($readmeFilePath, $readmeText, [Text.UTF8Encoding]::new($false))
        Write-Host ($stale ? "Updated: $($stale -join ', ')" : "Already up to date.")
    }
}
finally {
    Remove-Item $export -Recurse -Force -ErrorAction SilentlyContinue
}
