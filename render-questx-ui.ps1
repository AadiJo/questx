param(
    [string]$OutputPath = "questx-in-headset-ui-preview.png"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$projectDir = Join-Path $repoRoot "unity"
$projectVersionPath = Join-Path $projectDir "ProjectSettings\ProjectVersion.txt"

$versionMatch = Select-String `
    -LiteralPath $projectVersionPath `
    -Pattern '^m_EditorVersion:\s*(.+)$'
if (-not $versionMatch) {
    throw "Could not read the Unity version from $projectVersionPath"
}

$unityVersion = $versionMatch.Matches[0].Groups[1].Value.Trim()
$unityExe = "C:\Program Files\Unity\Hub\Editor\$unityVersion\Editor\Unity.exe"
if (-not (Test-Path -LiteralPath $unityExe)) {
    throw "Unity $unityVersion was not found at $unityExe"
}

$resolvedOutput = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    [System.IO.Path]::GetFullPath($OutputPath)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputPath))
}

$outputDirectory = Split-Path -Parent $resolvedOutput
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$unityArgs = @(
    "-batchmode",
    "-quit",
    "-projectPath", $projectDir,
    "-executeMethod", "QuestX.EditorTools.InHeadsetUiRenderer.Render",
    "-questXUiOutput", $resolvedOutput,
    "-logFile", (Join-Path $projectDir "Library\questx-ui-render.log")
)

Write-Host "Rendering QuestX UI with Unity $unityVersion..."
$renderProcess = Start-Process `
    -FilePath $unityExe `
    -ArgumentList $unityArgs `
    -PassThru `
    -Wait `
    -WindowStyle Hidden

$renderLog = Join-Path $projectDir "Library\questx-ui-render.log"
if ($renderProcess.ExitCode -ne 0) {
    if (Test-Path -LiteralPath $renderLog) {
        Get-Content -LiteralPath $renderLog -Tail 80
    }
    throw "Unity UI rendering failed with exit code $($renderProcess.ExitCode)"
}

if (-not (Test-Path -LiteralPath $resolvedOutput)) {
    throw "Unity exited successfully but did not create $resolvedOutput"
}

if (-not (Select-String -LiteralPath $renderLog -Pattern 'QUESTX_UI_RENDER_SUCCESS' -Quiet)) {
    Get-Content -LiteralPath $renderLog -Tail 80
    throw "Unity exited without confirming a successful UI render"
}

$result = Get-Item -LiteralPath $resolvedOutput
Write-Host "Rendered $($result.FullName) ($($result.Length) bytes)"
