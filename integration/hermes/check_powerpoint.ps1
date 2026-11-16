$ErrorActionPreference = 'Stop'
$check = Join-Path $PSScriptRoot ('../../.mia-runtime/office-check-' + [guid]::NewGuid().ToString('N'))
$check = [IO.Path]::GetFullPath($check)
New-Item -ItemType Directory -Path $check | Out-Null
$source = Join-Path $check 'fixture.pptx'
$office = $null; $deck = $null; $owned = $false
try {
    try { $office = [Runtime.InteropServices.Marshal]::GetActiveObject('PowerPoint.Application') }
    catch { $office = New-Object -ComObject PowerPoint.Application; $owned = $true }
    $deck = $office.Presentations.Add(0)
    $deck.PageSetup.SlideWidth = 960
    $deck.PageSetup.SlideHeight = 540
    $slide = $deck.Slides.Add(1, 1)
    $slide.Shapes.Title.TextFrame.TextRange.Text = 'MIA PowerPoint verification'
    $slide.Shapes.Item(2).TextFrame.TextRange.Text = 'Host Office rendering works.'
    $deck.SaveAs($source)
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($slide)
} finally {
    if ($deck) { $deck.Close(); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($deck) }
    if ($office) {
        if ($owned -and $office.Presentations.Count -eq 0) { $office.Quit() }
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($office)
    }
}
$before = (Get-FileHash -LiteralPath $source).Hash
& (Join-Path $PSScriptRoot 'mia-codex/powerpoint.ps1') -Source $source -OutputDirectory $check
$result = Get-Content -LiteralPath (Join-Path $check 'result.json') -Raw | ConvertFrom-Json
if ($result.status -ne 'completed' -or $result.slides -ne 1) { throw ($result | ConvertTo-Json -Compress) }
if ((Get-FileHash -LiteralPath $source).Hash -ne $before) { throw 'Source presentation was modified.' }
Add-Type -AssemblyName System.Drawing
$picture = [Drawing.Image]::FromFile($result.images[0])
try { if ($picture.Width -ne 1600 -or $picture.Height -ne 900) { throw 'Unexpected rendering size.' } }
finally { $picture.Dispose() }
$result | ConvertTo-Json -Depth 4
