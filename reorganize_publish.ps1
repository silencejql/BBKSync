param(
    [string]$PublishDir = "C:\Users\silencejql\Desktop\新建文件夹\WPF\publish"
)

$ErrorActionPreference = "Stop"

function Move-RuntimeSection($depsJson, $pkgId, $ver) {
    $targets = $depsJson.targets.'.NETCoreApp,Version=v10.0/win-x64'
    $key = "$pkgId/$ver"
    $entry = $targets.PSObject.Properties[$key]
    if ($null -eq $entry -or $null -eq $entry.Value) {
        Write-Host "未找到 package: $key" -ForegroundColor Yellow
        return @()
    }
    $pkg = $entry.Value
    $runtime = $pkg.runtime
    if ($null -eq $runtime) {
        Write-Host "无 runtime 段: $key" -ForegroundColor Yellow
        return @()
    }

    $destRoot = Join-Path $PublishDir "deps\$pkgId\$ver"
    New-Item -ItemType Directory -Force -Path $destRoot | Out-Null

    $moved = @()
    foreach ($prop in $runtime.PSObject.Properties) {
        $file = $prop.Name
        $srcPath = Join-Path $PublishDir ($file -replace '/', '\')
        if (Test-Path -LiteralPath $srcPath) {
            $destPath = Join-Path $destRoot ($file -replace '/', '\')
            New-Item -ItemType Directory -Force -Path (Split-Path $destPath -Parent) | Out-Null
            Move-Item -LiteralPath $srcPath -Destination $destPath -Force
            $moved += $file
        }
    }
    return $moved
}

$depsPath = Join-Path $PublishDir "BBKSync.deps.json"
$deps = Get-Content -LiteralPath $depsPath -Raw -Encoding UTF8 | ConvertFrom-Json

$m1 = Move-RuntimeSection $deps 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64' '10.0.3'
$m2 = Move-RuntimeSection $deps 'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64' '10.0.3'

Write-Host "NETCore runtime 移入 deps: $($m1.Count) 个"
Write-Host "WindowsDesktop runtime 移入 deps: $($m2.Count) 个"

Remove-Item -LiteralPath (Join-Path $PublishDir 'BBKSync.pdb') -ErrorAction SilentlyContinue

$rootFiles = Get-ChildItem -LiteralPath $PublishDir -File | Select-Object -ExpandProperty Name
Write-Host "根目录文件数: $($rootFiles.Count)"