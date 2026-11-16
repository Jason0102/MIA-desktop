param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$office = $null
$deck = $null
$owned = $false
try {
    # Connect to an existing instance without closing it or its user's documents.
    try { $office = [Runtime.InteropServices.Marshal]::GetActiveObject('PowerPoint.Application') }
    catch { $office = New-Object -ComObject PowerPoint.Application; $owned = $true }
    $previousSecurity = $office.AutomationSecurity
    try {
        $office.AutomationSecurity = 3 # Disable macros while opening task data.
        $deck = $office.Presentations.Open($Source, -1, 0, 0)
    } finally { $office.AutomationSecurity = $previousSecurity }
    $images = @()
    for ($index = 1; $index -le $deck.Slides.Count; $index++) {
        $slide = $deck.Slides.Item($index)
        try {
            $path = Join-Path $OutputDirectory ('slide-{0:D3}.png' -f $index)
            $height = [int][Math]::Round(1600 * $deck.PageSetup.SlideHeight / $deck.PageSetup.SlideWidth)
            $slide.Export($path, 'PNG', 1600, $height)
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Slide $index did not render." }
            $images += $path
        } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($slide) }
    }
    $result = @{ status='completed'; application='PowerPoint'; slides=$images.Count; images=$images; visual_review_required=$true }
} catch {
    $result = @{ status='failed'; error=$_.Exception.Message; hresult=$_.Exception.HResult }
} finally {
    if ($deck) { $deck.Close(); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($deck) }
    if ($office) {
        if ($owned -and $office.Presentations.Count -eq 0) { $office.Quit() }
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($office)
    }
}
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json') -Encoding UTF8
