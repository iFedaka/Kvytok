# Keeps the C4 diagrams in step with docs/architecture/workspace.dsl (Structurizr DSL).
# Exports each view to C4-PlantUML (docs/architecture/c4/*.puml), renders it to docs/images/c4-*.png,
# and points the README at the PNGs between <!-- c4:<name>:start --> and <!-- c4:<name>:end --> markers.
#   ./scripts/sync-architecture.ps1          export, render, update README.md
#   ./scripts/sync-architecture.ps1 -Check   change nothing; exit 1 if the .puml sources or the README
#                                            are out of date with the DSL (used by CI)
# The PNGs are re-rendered only when a .puml changes or a PNG is missing, so -Check compares the
# sources, not image bytes. Requires Docker.

param([switch]$Check)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$architectureDir = Join-Path $root "docs/architecture"
$pumlDir = Join-Path $architectureDir "c4"
$imagesDir = Join-Path $root "docs/images"
$readmeFilePath = Join-Path $root "README.md"
# Pinned, so a new release cannot change the drawings (or the check) unannounced
$structurizr = "structurizr/structurizr@sha256:8aebde645a7cc8d3b9e1e774cd6d75a84ce45d74ae70eb783876182fe7db31a2"
$plantuml    = "plantuml/plantuml@sha256:2cf2f097652701319e29b9e6a809e68d4ddb06fbeeb696705c097da8fa6d0507"
# view key in workspace.dsl -> marker name, PNG name and alt text
$views = [ordered]@{
    SystemContext = @{ Marker = "context";    Png = "c4-system-context.png"; Alt = "C4 system context: the buyer, the organiser and the payment provider around Kvytok" }
    Containers    = @{ Marker = "containers"; Png = "c4-containers.png";     Alt = "C4 containers: the Api, the Worker and the Migrator with the database, the queue and Application Insights" }
}

function Read-Text($path) { [IO.File]::ReadAllText($path) }

$tempDir = Join-Path ([IO.Path]::GetTempPath()) "kvytok-c4-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $tempDir | Out-Null
$exportDir = Join-Path $tempDir "export"
New-Item -ItemType Directory -Force $exportDir | Out-Null

try {
    docker run --rm -v "${architectureDir}:/usr/local/structurizr:ro" -v "${exportDir}:/out" $structurizr `
        export -workspace /usr/local/structurizr/workspace.dsl -format plantuml/c4plantuml -output /out | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "structurizr export failed with exit code $LASTEXITCODE" }

    $readmeText = Read-Text $readmeFilePath
    $nl = $readmeText.Contains("`r`n") ? "`r`n" : "`n"
    $stale = @()
    $rerender = @()

    foreach ($view in $views.Keys) {
        $v = $views[$view]
        $name = "structurizr-$view.puml"
        $src  = Join-Path $exportDir $name
        if (-not (Test-Path $src)) { throw "The export produced no $name; is the view key '$view' still in workspace.dsl?" }

        # The exporter ignores the DSL's "External" tag and has no option for it, so mark the
        # external system here, and drop the legend, which needs glyphs the image's font lacks
        $puml = (Read-Text $src) -replace "`r`n", "`n"
        if ($puml -match 'System\(Paymentprovider,') {
            $puml = $puml -replace 'System\(Paymentprovider,', 'System_Ext(Paymentprovider,'
        }
        $puml = $puml -replace 'SHOW_LEGEND\([^)]*\)\n?', ''
        [IO.File]::WriteAllText($src, $puml, [Text.UTF8Encoding]::new($false))

        $committed = Join-Path $pumlDir $name
        $png       = Join-Path $imagesDir $v.Png
        if (-not (Test-Path $committed) -or (Read-Text $committed) -ne $puml) {
            $stale += "docs/architecture/c4/$name"
            $rerender += $view
        } elseif (-not (Test-Path $png)) {
            $stale += "docs/images/$($v.Png)"
            $rerender += $view
        }

        $pattern = "(?s)(<!-- c4:$($v.Marker):start -->\r?\n).*?(<!-- c4:$($v.Marker):end -->)"
        if ($readmeText -notmatch $pattern) { throw "README.md has no c4:$($v.Marker) markers" }
        $block = "![$($v.Alt)](docs/images/$($v.Png))$nl"
        $readmeText = [regex]::Replace($readmeText, $pattern, { param($m) $m.Groups[1].Value + $block + $m.Groups[2].Value })
    }
    if ((Read-Text $readmeFilePath) -ne $readmeText) { $stale += "README.md" }

    if ($Check) {
        if ($stale) {
            Write-Error "Out of date with workspace.dsl: $($stale -join ', '). Run ./scripts/sync-architecture.ps1 and commit the result."
            exit 1
        }
        Write-Host "Architecture diagrams are up to date."
        return
    }

    New-Item -ItemType Directory -Force $pumlDir, $imagesDir | Out-Null
    foreach ($view in $views.Keys) {
        Copy-Item (Join-Path $exportDir "structurizr-$view.puml") $pumlDir -Force
    }
    if ($rerender) {
        $renderDir = Join-Path $tempDir "render"
        New-Item -ItemType Directory -Force $renderDir | Out-Null
        foreach ($view in $rerender) { Copy-Item (Join-Path $pumlDir "structurizr-$view.puml") $renderDir }
        docker run --rm -v "${renderDir}:/data" $plantuml -tpng -DPLANTUML_LIMIT_SIZE=8192 /data | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "plantuml failed with exit code $LASTEXITCODE" }
        foreach ($view in $rerender) { Copy-Item (Join-Path $renderDir "structurizr-$view.png") (Join-Path $imagesDir $views[$view].Png) -Force }
    }
    [IO.File]::WriteAllText($readmeFilePath, $readmeText, [Text.UTF8Encoding]::new($false))
    Write-Host ($stale ? "Updated: $($stale -join ', ')" : "Already up to date.")
}
finally {
    Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
