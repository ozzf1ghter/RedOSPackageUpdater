$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$main = [IO.File]::ReadAllText((Join-Path $project 'src\MainForm.cs'))
$shell = [IO.File]::ReadAllText((Join-Path $project 'src\MainForm.Shell.cs'))
$dialogs = [IO.File]::ReadAllText((Join-Path $project 'src\Dialogs.cs'))
$theme = [IO.File]::ReadAllText((Join-Path $project 'src\Theme.cs'))
$controls = [IO.File]::ReadAllText((Join-Path $project 'src\ModernControls.cs'))
$program = [IO.File]::ReadAllText((Join-Path $project 'src\Program.cs'))

$failed = 0
function Assert-Ui([bool]$condition, [string]$name) {
    if ($condition) { Write-Host "OK   $name" }
    else { Write-Host "FAIL $name"; $script:failed++ }
}

$interactive = $main + "`n" + $dialogs
$legacyTypes = @('Button', 'CheckBox', 'ComboBox', 'ProgressBar',
    'DataGridView', 'ListView', 'NumericUpDown', 'MenuStrip')
foreach ($type in $legacyTypes) {
    $pattern = '\bnew\s+' + [regex]::Escape($type) + '\s*[\{\(]'
    Assert-Ui (-not [regex]::IsMatch($interactive, $pattern)) "no legacy constructor new $type"
}

$hasThemes = $theme -match 'public static void Configure\(bool dark\)' -and $theme -match 'if \(dark\)' -and $theme -match 'else'
Assert-Ui $hasThemes 'light and dark palettes exist'
$hasDpi = $program -match 'DpiAwareness\.Enable' -and $program -match 'SetProcessDpiAwarenessContext'
Assert-Ui $hasDpi 'Per-Monitor DPI awareness enabled'
$hasControls = $controls -match 'class ModernButton' -and $controls -match 'class ModernCheckBox' -and
    $controls -match 'class ModernComboBox' -and $controls -match 'class ModernDataGridView' -and
    $controls -match 'class ModernProgressBar' -and $controls -match 'class ModernToast' -and
    $controls -match 'class ModernTextBox' -and $controls -match 'class ModernListView' -and
    $controls -match 'class ModernNumericUpDown'
Assert-Ui $hasControls 'core custom controls exist'
$legacyRemoved = $shell -notmatch 'legacyMenu' -and $main -notmatch 'new MenuStrip'
Assert-Ui $legacyRemoved 'hidden legacy menu removed'
$hasLocks = $shell -match 'LockConfiguration' -and $main -match '_configurationControls'
Assert-Ui $hasLocks 'mutating actions lock during operations'
$hasLayouts = $main -match 'UiLayoutRules\.CommandBar' -and $main -match 'UiLayoutRules\.ServerWorkspace'
Assert-Ui $hasLayouts 'responsive layout uses testable rules'
$hasDiscovery = $main -match '_treeSearch' -and $main -match '_summarySearch' -and $main -match 'SortMode = DataGridViewColumnSortMode.Automatic'
Assert-Ui $hasDiscovery 'server/result search and sortable result columns exist'
$hasKeyboard = $main -match 'KeyPreview = true' -and $main -match 'MainFormKeyDown' -and $main -match 'Keys\.D1' -and $main -match 'Keys\.Escape'
Assert-Ui $hasKeyboard 'keyboard navigation and search shortcuts exist'
$menusDisposeAfterDispatch = $shell -match 'BeginInvoke\(new Action\(menu\.Dispose\)\)' -and
    $main -match 'BeginInvoke\(new Action\(m\.Dispose\)\)'
Assert-Ui $menusDisposeAfterDispatch 'context menus survive keyboard item dispatch'
$roundedButtonsEraseCorners = $controls -match 'e\.Graphics\.Clear\(Parent != null \? Parent\.BackColor : BackColor\)'
Assert-Ui $roundedButtonsEraseCorners 'rounded buttons erase transparent corner artifacts'
$groupedVulnerabilityReport = ($main -match 'remediationGroups') -and ($main -match '<details><summary>') -and
    ($main -match "shown\.textContent='")
Assert-Ui $groupedVulnerabilityReport 'HTML vulnerability report groups findings by remediation action'

$buildInfo = [IO.File]::ReadAllText((Join-Path $project 'src\BuildInfo.cs'))
$versionMatch = [regex]::Match($buildInfo, 'Version = "([0-9]+\.[0-9]+\.[0-9]+)"')
$manifest = Get-Content (Join-Path $project 'update.json') -Raw | ConvertFrom-Json
$versionsMatch = $versionMatch.Success -and $manifest.version -eq $versionMatch.Groups[1].Value
Assert-Ui $versionsMatch 'EXE and update.json versions match'

exit $(if ($failed -eq 0) { 0 } else { 1 })
