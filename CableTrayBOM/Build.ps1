# Build.ps1 - CableTrayBOM Revit Add-in Builder (2024 + 2025)
#
# USAGE:
#   powershell -ExecutionPolicy Bypass -File .\Build.ps1                    # Builds ALL detected versions
#   powershell -ExecutionPolicy Bypass -File .\Build.ps1 -RevitYear 2025   # Build only 2025
#   powershell -ExecutionPolicy Bypass -File .\Build.ps1 -RevitYear 2024   # Build only 2024
#   powershell -ExecutionPolicy Bypass -File .\Build.ps1 -RevitYear Both   # Explicit both
#   powershell -ExecutionPolicy Bypass -File .\Build.ps1 -RevitPath2024 "D:\Revit 2024" -RevitPath2025 "C:\Revit 2025"

param(
    [ValidateSet("2024", "2025", "Both")]
    [string]$RevitYear = "Both",
    [string]$RevitPath2024 = "",
    [string]$RevitPath2025 = "",
    [string]$RevitPath = "",       # Legacy: sets both if year-specific not given
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$OutputDir = "",
    [switch]$NoDeploy,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Vertiv BOM - Cable Tray & Fixtures - Revit Add-in Build"   -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# -------------------------------------------------------------------
# .NET SDK check
# -------------------------------------------------------------------
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Host "ERROR: .NET SDK not found. Install from https://dotnet.microsoft.com/download" -ForegroundColor Red
    exit 1
}
Write-Host "[OK] .NET SDK: $(& dotnet --version 2>&1)" -ForegroundColor Green

# -------------------------------------------------------------------
# Auto-detect Revit installations or API DLLs
# Search order:
#   1. Revit installation (all drives)
#   2. Documents\RevitAPI\{year}\ (for building without Revit)
#   3. Project lib\{year}\ folder
#   4. Registry
# -------------------------------------------------------------------
function Find-Revit([string]$year) {
    # 1. Build search paths from ALL available fixed drives
    $paths = @()
    Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | 
        Where-Object { $_.Root -match '^[A-Z]:\\$' } | 
        ForEach-Object {
            $drive = $_.Root
            $paths += Join-Path $drive "Program Files\Autodesk\Revit $year"
            $paths += Join-Path $drive "Autodesk\Revit $year"
        }
    if ($env:ProgramFiles) { $paths += Join-Path $env:ProgramFiles "Autodesk\Revit $year" }
    if (${env:ProgramFiles(x86)}) { $paths += Join-Path ${env:ProgramFiles(x86)} "Autodesk\Revit $year" }

    # 2. Documents\RevitAPI\{year} (build without Revit installed)
    $myDocs = [Environment]::GetFolderPath("MyDocuments")
    if ($myDocs) {
        $paths += Join-Path $myDocs "RevitAPI\$year"
        $paths += Join-Path $myDocs "RevitAPI\Revit $year"
    }

    # 3. Project lib folder
    $paths += Join-Path $ProjectDir "lib\$year"
    $paths += Join-Path $ProjectDir "lib\Revit$year"

    # Deduplicate
    $paths = $paths | Select-Object -Unique

    # Check each path for RevitAPI.dll
    foreach ($p in $paths) {
        try {
            $testDll = Join-Path $p "RevitAPI.dll"
            if (Test-Path $testDll) { return $p }
        } catch { continue }
    }

    # 4. Registry fallback
    $regKeys = @(
        "HKLM:\SOFTWARE\Autodesk\Revit\$year",
        "HKLM:\SOFTWARE\Autodesk\Revit $year",
        "HKLM:\SOFTWARE\WOW6432Node\Autodesk\Revit\$year"
    )
    foreach ($regPath in $regKeys) {
        try {
            if (Test-Path $regPath) {
                $props = Get-ItemProperty $regPath -ErrorAction SilentlyContinue
                $ip = $props.InstallationLocation
                if (-not $ip) { $ip = $props.InstallPath }
                if ($ip) {
                    $testDll = Join-Path $ip "RevitAPI.dll"
                    if (Test-Path $testDll) { return $ip }
                }
            }
        } catch { continue }
    }

    return ""
}

# If Revit 2024 not found but 2025 is, offer to use 2025 API DLLs
# (Revit 2024 API is forward-compatible for compilation with 2025 DLLs)
function Find-RevitFallback([string]$year, [string]$otherPath) {
    if ($year -eq "2024" -and $otherPath) {
        Write-Host "  [INFO] Revit 2024 not found. Using Revit 2025 API DLLs for compilation." -ForegroundColor Yellow
        Write-Host "         (The compiled DLL will work with Revit 2024)" -ForegroundColor Gray
        return $otherPath
    }
    if ($year -eq "2025" -and $otherPath) {
        Write-Host "  [INFO] Revit 2025 not found. Using Revit 2024 API DLLs for compilation." -ForegroundColor Yellow
        return $otherPath
    }
    return ""
}

# Resolve paths
if ($RevitPath -and -not $RevitPath2024) { $RevitPath2024 = $RevitPath }
if ($RevitPath -and -not $RevitPath2025) { $RevitPath2025 = $RevitPath }
if (-not $RevitPath2024) { $RevitPath2024 = Find-Revit "2024" }
if (-not $RevitPath2025) { $RevitPath2025 = Find-Revit "2025" }

# Determine which to build
$builds = @()
if ($RevitYear -eq "2024" -or $RevitYear -eq "Both") {
    if ($RevitPath2024) {
        $builds += @{ Year = "2024"; Framework = "net48"; Path = $RevitPath2024 }
    } elseif ($RevitYear -eq "2024") {
        $docsPath = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "RevitAPI\2024"
        Write-Host "ERROR: Revit 2024 not found." -ForegroundColor Red
        Write-Host "  Option 1: Use -RevitPath2024 `"C:\Path\To\Revit 2024`"" -ForegroundColor Yellow
        Write-Host "  Option 2: Copy RevitAPI.dll + RevitAPIUI.dll to:" -ForegroundColor Yellow
        Write-Host "            $docsPath" -ForegroundColor Yellow
        exit 1
    } else {
        Write-Host "[SKIP] Revit 2024 not found" -ForegroundColor Yellow
    }
}
if ($RevitYear -eq "2025" -or $RevitYear -eq "Both") {
    if ($RevitPath2025) {
        $builds += @{ Year = "2025"; Framework = "net8.0-windows"; Path = $RevitPath2025 }
    } elseif ($RevitYear -eq "2025") {
        $docsPath = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "RevitAPI\2025"
        Write-Host "ERROR: Revit 2025 not found." -ForegroundColor Red
        Write-Host "  Option 1: Use -RevitPath2025 `"C:\Path\To\Revit 2025`"" -ForegroundColor Yellow
        Write-Host "  Option 2: Copy RevitAPI.dll + RevitAPIUI.dll to:" -ForegroundColor Yellow
        Write-Host "            $docsPath" -ForegroundColor Yellow
        exit 1
    } else {
        Write-Host "[SKIP] Revit 2025 not found" -ForegroundColor Yellow
    }
}

if ($builds.Count -eq 0) {
    $docsPath = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "RevitAPI"
    Write-Host "ERROR: No Revit installations or API DLLs found." -ForegroundColor Red
    Write-Host ""
    Write-Host "  To build without Revit installed, copy RevitAPI.dll + RevitAPIUI.dll to:" -ForegroundColor Yellow
    Write-Host "    $docsPath\2024\   (for Revit 2024)" -ForegroundColor Yellow
    Write-Host "    $docsPath\2025\   (for Revit 2025)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  These DLLs can be found in any Revit installation folder." -ForegroundColor Gray
    exit 1
}

Write-Host ""
Write-Host "Building for: $(($builds | ForEach-Object { "Revit $($_.Year) ($($_.Framework))" }) -join ', ')" -ForegroundColor Cyan
foreach ($b in $builds) {
    $src = if ($b.Path -match 'RevitAPI|lib\\') { "API DLLs" } else { "Installation" }
    Write-Host "  Revit $($b.Year): $($b.Path) [$src]" -ForegroundColor Gray
}
Write-Host ""

# -------------------------------------------------------------------
# Clean (optional)
# -------------------------------------------------------------------
if ($Clean) {
    Write-Host "[CLEAN] Removing bin/ and obj/..." -ForegroundColor Yellow
    Remove-Item (Join-Path $ProjectDir "bin") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $ProjectDir "obj") -Recurse -Force -ErrorAction SilentlyContinue
}

# -------------------------------------------------------------------
# Output directory
# Default deploy target: C:\ZZ_Revit_Addin\Addin_Build (overridable via -OutputDir).
# Per-year subfolders (\2024, \2025) are created beneath it.
# -------------------------------------------------------------------
if (-not $OutputDir) { $OutputDir = "C:\ZZ_Revit_Addin\Addin_Build" }
if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }
Write-Host "Output: $OutputDir" -ForegroundColor Gray
Write-Host ""

# -------------------------------------------------------------------
# Note: NuGet restore is done per-target (each framework needs its own Revit API path)
# -------------------------------------------------------------------

# -------------------------------------------------------------------
# Build each version
# -------------------------------------------------------------------
$step = 1
$successCount = 0

foreach ($build in $builds) {
    $year = $build.Year
    $fw = $build.Framework
    $rPath = $build.Path
    $outDir = Join-Path $OutputDir $year

    Write-Host ""
    Write-Host "[$step/$($builds.Count)] Building for Revit $year ($fw)..." -ForegroundColor Yellow

    Push-Location $ProjectDir
    try {
        $buildArgs = @(
            "publish"; "CableTrayBOM.csproj"
            "-c"; $Configuration
            "-f"; $fw
            "/p:RevitApiPath=$rPath"
            # each build restores with its own API path
            "-o"; $outDir
        )
        $ErrorActionPreference = 'Continue'
        $buildOutput = & dotnet $buildArgs 2>&1
        $ErrorActionPreference = 'Stop'
        $exitCode = $LASTEXITCODE

        # Show errors/warnings
        foreach ($line in $buildOutput) {
            $s = "$line"
            if ($s -match "error") { Write-Host "  $s" -ForegroundColor Red }
            elseif ($s -match "warning") { Write-Host "  $s" -ForegroundColor Yellow }
        }

        if ($exitCode -ne 0) {
            Write-Host "  *** BUILD FAILED for Revit $year ***" -ForegroundColor Red
            foreach ($line in $buildOutput) { Write-Host "    $line" }
            continue
        }

        $dll = Join-Path $outDir "CableTrayBOM.dll"
        if (-not (Test-Path $dll)) {
            Write-Host "  ERROR: CableTrayBOM.dll not found in $outDir" -ForegroundColor Red
            continue
        }

        $sz = [math]::Round((Get-Item $dll).Length / 1KB, 1)
        Write-Host "  Built: CableTrayBOM.dll ($sz KB)" -ForegroundColor Green

        # Deploy
        if (-not $NoDeploy) {
            $addinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"

            # Only deploy to Addins folder if it exists (Revit is installed)
            if (Test-Path (Split-Path $addinsRoot)) {
                $pluginDir = Join-Path $addinsRoot "CableTrayBOM"
                if (-not (Test-Path $addinsRoot)) { New-Item -ItemType Directory -Path $addinsRoot -Force | Out-Null }
                if (-not (Test-Path $pluginDir)) { New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null }

                Write-Host "  Deploying to: $pluginDir" -ForegroundColor Gray
                Get-ChildItem $outDir -Filter "*.dll" | ForEach-Object { Copy-Item $_.FullName $pluginDir -Force }
                Get-ChildItem $outDir -Filter "*.json" -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item $_.FullName $pluginDir -Force }

                $addinFile = Join-Path $ProjectDir "CableTrayBOM.addin"
                if (Test-Path $addinFile) {
                    $addinContent = Get-Content $addinFile -Raw
                    $addinContent = $addinContent -replace "<Assembly>CableTrayBOM.dll</Assembly>", "<Assembly>CableTrayBOM\CableTrayBOM.dll</Assembly>"
                    Set-Content -Path (Join-Path $addinsRoot "CableTrayBOM.addin") -Value $addinContent -Encoding UTF8
                }
                Write-Host "  Deployed to Revit $year" -ForegroundColor Green
            } else {
                Write-Host "  Revit $year not installed - output at: $outDir" -ForegroundColor Yellow
            }
        }

        $successCount++
    } finally { Pop-Location }
    $step++
}

# -------------------------------------------------------------------
# Summary
# -------------------------------------------------------------------
Write-Host ""
Write-Host "============================================================" -ForegroundColor $(if ($successCount -eq $builds.Count) { "Green" } else { "Yellow" })
if ($successCount -eq $builds.Count) {
    Write-Host "  BUILD SUCCESSFUL ($successCount/$($builds.Count))" -ForegroundColor Green
} else {
    Write-Host "  BUILD PARTIAL ($successCount/$($builds.Count) succeeded)" -ForegroundColor Yellow
}
foreach ($b in $builds) {
    Write-Host "  Revit $($b.Year) ($($b.Framework)): $OutputDir\$($b.Year)\" -ForegroundColor Gray
}
if (-not $NoDeploy) {
    Write-Host "  Restart Revit to load the add-in" -ForegroundColor Green
}
Write-Host "============================================================" -ForegroundColor $(if ($successCount -eq $builds.Count) { "Green" } else { "Yellow" })
Write-Host ""
