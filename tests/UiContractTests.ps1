$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$main = (($([IO.Directory]::GetFiles((Join-Path $project 'src'), 'MainForm*.cs') | ForEach-Object { [IO.File]::ReadAllText($_) })) -join "`n")
$shell = [IO.File]::ReadAllText((Join-Path $project 'src\MainForm.Shell.cs'))
$dialogFiles = @('Dialogs.cs', 'NodeDialogs.cs', 'CredentialsForm.cs', 'RepoDialog.cs', 'SettingsForm.cs')
$dialogs = (($dialogFiles | ForEach-Object { [IO.File]::ReadAllText((Join-Path $project ('src\' + $_))) }) -join "`n")
$theme = [IO.File]::ReadAllText((Join-Path $project 'src\Theme.cs'))
$controls = [IO.File]::ReadAllText((Join-Path $project 'src\ModernControls.cs'))
$icons = [IO.File]::ReadAllText((Join-Path $project 'src\AppIcons.cs'))
$chrome = [IO.File]::ReadAllText((Join-Path $project 'src\WindowChrome.cs'))
$vulnerabilityHtml = [IO.File]::ReadAllText((Join-Path $project 'src\VulnerabilityHtmlReport.cs'))
$program = [IO.File]::ReadAllText((Join-Path $project 'src\Program.cs'))
$profiles = [IO.File]::ReadAllText((Join-Path $project 'src\Profiles.cs'))
$sshRunner = [IO.File]::ReadAllText((Join-Path $project 'src\SshCommandRunner.cs'))
$kernelSecurity = [IO.File]::ReadAllText((Join-Path $project 'profiles\redos_kernel_security.sh'))
$kernelOnly = [IO.File]::ReadAllText((Join-Path $project 'profiles\redos_kernel_only.sh'))

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
$optionalSeed = $main -match 'Profiles\.TryRead\("seed_config\.json"\)' -and $profiles -match 'public static string TryRead'
Assert-Ui $optionalSeed 'empty build tolerates missing optional seed'
$transportFailsClosed = $sshRunner -match 'throw new OperationCanceledException' -and $sshRunner -match 'throw new TimeoutException' -and $sshRunner -notmatch 'TIMEOUT_OR_ERROR'
Assert-Ui $transportFailsClosed 'SSH cancellation and timeout fail closed'
$kernelProfiles = $kernelSecurity + "`n" + $kernelOnly
$safeBoot = $kernelProfiles -notmatch 'mv\s+"\$f"\s+"\$backup_dir"' -and $kernelProfiles -notmatch 'grub2-mkconfig' -and
    $kernelSecurity -match 'default_ok' -and $kernelOnly -match 'default_ok'
Assert-Ui $safeBoot 'kernel profiles diagnose BLS problems without rewriting bootloader'
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
$adaptiveHeaders = $shell -match 'actionButton\.Left - 28' -and $shell -match 'subtitleLabel\.Width = available'
Assert-Ui $adaptiveHeaders 'page headers adapt around contextual action'
$fstecProgressOnPage = $shell -match '_fstecProgress = new ModernProgressBar \{ Dock = DockStyle\.Bottom' -and $main -notmatch 'top\.Controls\.Add\(_fstecProgress\)'
Assert-Ui $fstecProgressOnPage 'FSTEC database progress is visible on its own page'
$hasDiscovery = $main -match '_treeSearch' -and $main -match '_summarySearch' -and $main -match 'SortMode = DataGridViewColumnSortMode.Automatic'
Assert-Ui $hasDiscovery 'server/result search and sortable result columns exist'
$hasKeyboard = $main -match 'KeyPreview = true' -and $main -match 'MainFormKeyDown' -and $main -match 'Keys\.D1' -and $main -match 'Keys\.Escape'
Assert-Ui $hasKeyboard 'keyboard navigation and search shortcuts exist'
$menusDisposeAfterDispatch = $shell -match 'BeginInvoke\(new Action\(menu\.Dispose\)\)' -and
    $main -match 'BeginInvoke\(new Action\(m\.Dispose\)\)'
Assert-Ui $menusDisposeAfterDispatch 'context menus survive keyboard item dispatch'
$menuDensity = $theme -match 'item\.Height = 32' -and $theme -match 'item\.Width = Math\.Max\(220'
Assert-Ui $menuDensity 'context menus use readable commercial-density rows'
$zeroSelectionLocked = $shell -match '_btnRun\.Enabled = selected > 0' -and
    $shell -match '_btnPreview\.Enabled = selected > 0' -and
    $shell -match '_btnVulnerabilityScan\.Enabled = selected > 0'
Assert-Ui $zeroSelectionLocked 'actions requiring targets are disabled for an empty selection'
$detailsLocked = $shell -match '_btnEditSelection\.Enabled = selected != null' -and
    $shell -match '_btnSystemServices\.Enabled = selected != null'
Assert-Ui $detailsLocked 'selection-specific server actions require a selected object'
$roundedButtonsEraseCorners = $controls -match 'e\.Graphics\.Clear\(Parent != null \? Parent\.BackColor : BackColor\)'
Assert-Ui $roundedButtonsEraseCorners 'rounded buttons erase transparent corner artifacts'
$groupedVulnerabilityReport = ($vulnerabilityHtml -match 'remediationGroups') -and ($vulnerabilityHtml -match '<details><summary>') -and
    ($vulnerabilityHtml -match "shown\.textContent='")
Assert-Ui $groupedVulnerabilityReport 'HTML vulnerability report groups findings by remediation action'
$preservesEncryptedCredential = $dialogs -match 'EncPassword = original\.EncPassword' -and
    $dialogs -match 'ToolTipText = '
Assert-Ui $preservesEncryptedCredential 'credential editor preserves undecryptable DPAPI values'
$unifiedIcons = $controls -match 'AppIcons\.Draw' -and $shell -match 'new AppIconView' -and
    $icons -match 'DrawMark' -and $controls -notmatch 'DrawNavigationIcon'
Assert-Ui $unifiedIcons 'navigation and brand use one unified icon system'
$modernChrome = $main -match 'EnableModernWindowChrome' -and $chrome -match 'class ModernTitleBar' -and
    $chrome -match 'HtBottomRight' -and $chrome -match 'TrackPopupMenu'
Assert-Ui $modernChrome 'custom title bar preserves resize and system menu contracts'
$singleSelectionToggle = $main -match '_btnToggleAll' -and $main -notmatch 'var markNone'
Assert-Ui $singleSelectionToggle 'server selection uses one contextual toggle button'
$noTopStripe = $chrome -notmatch 'FillRectangle\(accent,\s*0,\s*0,\s*Width,\s*2\)'
Assert-Ui $noTopStripe 'title bar has no decorative top stripe'
$textBoxesKeepCompleteBorders = $controls -match 'class ModernTextBox : UserControl' -and
    $controls -match 'BorderStyle = BorderStyle\.None' -and
    $controls -match '\(Height - editorHeight\) / 2'
Assert-Ui $textBoxesKeepCompleteBorders 'native text boxes keep complete corner borders'
$compactServerActions = $main -notmatch 'var bulkNodes' -and
    $main -match 'AddCompactBtn\(leftButtons, [^,]+, 72'
Assert-Ui $compactServerActions 'server action bar fits its minimum width'
$comboHasCompleteChrome = $controls -match 'class ModernComboBox[\s\S]*?DrawChrome' -and
    $controls -match 'Graphics\.FromHwnd\(Handle\)'
Assert-Ui $comboHasCompleteChrome 'combo boxes draw one complete border and dropdown button'
$uniformPageHeaders = $shell -match 'const int headerHeight = 78' -and
    $shell -match 'const int actionWidth = 146' -and $shell -match 'const int actionHeight = 40' -and
    $shell -notmatch 'head\.Height = 72'
Assert-Ui $uniformPageHeaders 'all pages use one header and action size'

$buildInfo = [IO.File]::ReadAllText((Join-Path $project 'src\BuildInfo.cs'))
$versionMatch = [regex]::Match($buildInfo, 'Version = "([0-9]+\.[0-9]+\.[0-9]+)"')
$manifest = Get-Content (Join-Path $project 'update.json') -Raw | ConvertFrom-Json
$versionsMatch = $versionMatch.Success -and $manifest.version -eq $versionMatch.Groups[1].Value
Assert-Ui $versionsMatch 'EXE and update.json versions match'

exit $(if ($failed -eq 0) { 0 } else { 1 })
