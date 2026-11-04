<#
.SYNOPSIS
    BBKSync MinVer 一键发版脚本（Windows PowerShell 5.1+）。

.DESCRIPTION
    流程：校验环境与版本 -> 工作区干净检查 -> 打带注释的 git tag（v 前缀）
         -> dotnet publish 发布 -> 校验 exe 版本号与 tag 一致
         -> 可选运行 reorganize_publish.ps1 -> 可选推送分支与 tag 到 origin
         -> 指定 -Push 时调用 gh 在 GitHub 上创建同名 Release 并附带 BBKSync.exe。

    版本号由 MinVer 依据 git tag 自动计算（csproj 中 MinVerTagPrefix=v），
    因此必须【先打 tag，再发布】，产物才会带正式版本号。
    任何步骤失败都会自动删除本次新建的本地 tag（仅当 tag 由本脚本创建），
    修正问题后可直接重跑。

.PARAMETER Version
    必填，不带 v 前缀的语义化版本号，如 1.4.0 或 1.4.0-beta.1。

.PARAMETER Push
    发布成功后把当前分支与新 tag 推送到 origin，并在 GitHub 上创建同名 Release
    （需要 gh CLI 已登录，或 git 凭据中存在 github.com 令牌）。默认只在本地操作。

.PARAMETER Reorganize
    发布成功后执行仓库根目录下的 reorganize_publish.ps1（整理运行时到 deps 子目录）。

.PARAMETER CleanPublish
    发布前清空发布目录。默认不清空（目录内可能有 settings.json / history.json
    等运行期数据）；dotnet publish 本身会覆盖同名文件。

.PARAMETER AllowDirty
    允许工作区存在未提交改动（默认不允许，避免把未提交代码发版）。

.PARAMETER SkipPublish
    只打 tag 不执行发布（一般不需要，仅用于补打 tag 等特殊场景）。

.EXAMPLE
    .\release.ps1 -Version 1.4.0
    本地发版：打 v1.4.0 并发布到 .\publish，不推送。

.EXAMPLE
    .\release.ps1 -Version 1.4.0 -Push
    本地发版并推送到 GitHub（推送 master 分支与 v1.4.0 tag）。

.EXAMPLE
    .\release.ps1 -Version 1.4.0-beta.1 -Push
    发布预发布版本，MinVer 产品版本为 1.4.0-beta.1+commit。

.EXAMPLE
    .\release.ps1 -Version 1.4.0 -Reorganize -CleanPublish
    清空 publish 目录后发布，并运行目录整理脚本。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [switch]$Push,
    [switch]$Reorganize,
    [switch]$CleanPublish,
    [switch]$AllowDirty,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$ErrorView = 'NormalView'

# ---------------------------------------------------------------------------
# 路径与常量
# ---------------------------------------------------------------------------
$RepoRoot   = $PSScriptRoot
$Project    = Join-Path $RepoRoot 'LanFileSync\LanFileSync.csproj'
$PublishDir = Join-Path $RepoRoot 'publish'
$ExeName    = 'BBKSync.exe'
$ReorgScript = Join-Path $RepoRoot 'reorganize_publish.ps1'
$Tag        = "v$Version"
$CoreVersion = ($Version -split '-')[0]
$ExpectedFileVersion = ('{0}.0' -f $CoreVersion)   # MinVer FileVersion: major.minor.patch.0

# ---------------------------------------------------------------------------
# 工具函数
# ---------------------------------------------------------------------------
function Write-Step([string]$msg) {
    Write-Host ''
    Write-Host "==> $msg" -ForegroundColor Cyan
}

function Write-Ok([string]$msg) {
    Write-Host "    [OK] $msg" -ForegroundColor Green
}

function Write-Warn([string]$msg) {
    Write-Host "    [!]  $msg" -ForegroundColor Yellow
}

function Fail([string]$msg) {
    throw $msg
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$File,
        [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments,
        [string]$ErrorMessage
    )
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        $detail = if ($ErrorMessage) { $ErrorMessage } else { "$File 执行失败，退出码 $LASTEXITCODE" }
        Fail $detail
    }
}

# 从 git 凭据助手读取 github.com 令牌（不落盘、不打印），供 gh 创建 Release 使用
function Get-GitHubStoredToken {
    $env:GCM_INTERACTIVE = 'never'
    try {
        $credential = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        foreach ($line in $credential) {
            if ($line -match '^password=(.+)$') { return $Matches[1] }
        }
        return $null
    }
    catch {
        return $null
    }
    finally {
        Remove-Item Env:GCM_INTERACTIVE -ErrorAction SilentlyContinue
    }
}

# 从 origin 地址解析 owner/repo（支持 SSH 与 HTTPS 两种写法）
function Get-GitHubRepoSlug {
    $url = git remote get-url origin 2>$null
    if ($url -and ($url -match 'github\.com[:/](?<slug>[^/]+/[^/]+?)(?:\.git)?$')) {
        return $Matches['slug']
    }
    return $null
}

# 返回可连通的 GitHub 域名 IP：优先 DNS 结果，api.github.com 另备已知可用 IP
# （部分网络会屏蔽 DNS 解析到的个别 GitHub IP，导致 gh/curl 默认连接超时）
function Find-GitHubEndpointIp {
    param([string]$HostName)
    $candidates = @()
    try {
        $candidates += Resolve-DnsName $HostName -Type A -ErrorAction Stop |
            Select-Object -ExpandProperty IPAddress
    }
    catch { }
    if ($HostName -eq 'api.github.com') {
        $candidates += @('140.82.112.5', '140.82.113.5', '140.82.114.5', '140.82.116.5')
    }
    foreach ($ip in ($candidates | Select-Object -Unique)) {
        if (-not $ip) { continue }
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $task = $client.ConnectAsync($ip, 443)
            if ($task.Wait(5000) -and $client.Connected) { return $ip }
        }
        catch { }
        finally { $client.Dispose() }
    }
    return $null
}

# gh 不可用时的兜底：直接调用 REST API 创建 Release 并上传 exe
function New-GitHubReleaseByRest {
    param(
        [string]$Tag,
        [string]$Version,
        [string]$Token,
        [string]$AssetPath
    )
    $slug = Get-GitHubRepoSlug
    if (-not $slug) { Fail '无法从 origin 解析 GitHub 仓库标识（owner/repo）。' }
    $apiIp = Find-GitHubEndpointIp 'api.github.com'
    if (-not $apiIp) { Fail 'api.github.com:443 不可达，无法创建 GitHub Release。请检查网络或代理后重试。' }

    $bodyFile = Join-Path $env:TEMP "bbksync-release-$Tag.json"
    $respFile = Join-Path $env:TEMP "bbksync-release-$Tag-response.json"
    $bodyObj = @{
        tag_name   = $Tag
        name       = $Tag
        body       = "自动发布 $Tag。产物：BBKSync.exe，可在远端电脑使用「程序升级」批量分发。"
        draft      = $false
        prerelease = [bool]($Version -match '-')
    }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($bodyFile, ($bodyObj | ConvertTo-Json -Compress), $utf8NoBom)

    $code = curl.exe -sS --max-time 60 --resolve "api.github.com:443:$apiIp" `
        -X POST "https://api.github.com/repos/$slug/releases" `
        -H "Authorization: Bearer $Token" `
        -H "Accept: application/vnd.github+json" `
        -H "Content-Type: application/json; charset=utf-8" `
        --data-binary "@$bodyFile" -o $respFile -w '%{http_code}'
    if ($LASTEXITCODE -ne 0 -or "$code" -notmatch '^20') {
        $detail = if (Test-Path $respFile) { Get-Content $respFile -Raw } else { '' }
        Remove-Item $bodyFile, $respFile -ErrorAction SilentlyContinue
        Fail "创建 GitHub Release 失败（HTTP $code）。$detail"
    }
    $release = Get-Content $respFile -Raw | ConvertFrom-Json
    Write-Ok "Release 已创建（id=$($release.id)）"

    $assetUploaded = $false
    if ($AssetPath -and (Test-Path $AssetPath)) {
        $uploadIp = Find-GitHubEndpointIp 'uploads.github.com'
        if (-not $uploadIp) {
            Write-Warn 'uploads.github.com 不可达，跳过上传 BBKSync.exe（Release 已创建）。'
        }
        else {
            $assetName = [System.Uri]::EscapeDataString((Split-Path $AssetPath -Leaf))
            $uploadUrl = "https://uploads.github.com/repos/$slug/releases/$($release.id)/assets?name=$assetName"
            $upCode = curl.exe -sS --max-time 300 --resolve "uploads.github.com:443:$uploadIp" `
                -X POST $uploadUrl `
                -H "Authorization: Bearer $Token" `
                -H "Content-Type: application/octet-stream" `
                --data-binary "@$AssetPath" -o $respFile -w '%{http_code}'
            if ($LASTEXITCODE -ne 0 -or "$upCode" -notmatch '^20') {
                Write-Warn "上传 BBKSync.exe 失败（HTTP $upCode），Release 已创建但未附产物。"
            }
            else {
                Write-Ok 'BBKSync.exe 已上传到 Release'
                $assetUploaded = $true
            }
        }
    }
    Remove-Item $bodyFile, $respFile -ErrorAction SilentlyContinue
    return $assetUploaded
}

# ---------------------------------------------------------------------------
# 前置检查
# ---------------------------------------------------------------------------
Write-Step "准备发版 $Tag（仓库：$RepoRoot）"

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Fail '未找到 git 命令，请先安装 Git 并加入 PATH。'
}
if (-not (Test-Path (Join-Path $RepoRoot '.git'))) {
    Fail "脚本必须放在 git 仓库根目录运行：$RepoRoot"
}
if (-not (Test-Path $Project)) {
    Fail "找不到项目文件：$Project"
}

# 1) 工作区必须干净
Push-Location $RepoRoot
try {
    $dirty = git status --porcelain
    if ($dirty -and -not $AllowDirty) {
        Write-Host ($dirty -join "`n") -ForegroundColor DarkYellow
        Fail '工作区存在未提交改动，请先提交或暂存（确认无风险可加 -AllowDirty）。'
    }

    $branch = git rev-parse --abbrev-ref HEAD
    Write-Ok "当前分支：$branch"

    # 2) tag 不能已存在（本地）
    $localTag = git tag --list $Tag
    if ($localTag) {
        Fail "本地已存在 tag：$Tag"
    }

    # 3) 若配置了 origin，检查远端是否已有同名 tag（网络失败只警告不阻断）
    $remote = (git remote) -contains 'origin'
    if ($remote) {
        try {
            $remoteRef = git ls-remote --tags origin "refs/tags/$Tag" 2>$null
            if ($remoteRef) { Fail "远端 origin 已存在 tag：$Tag，请更换版本号或先删除远端 tag。" }
            Write-Ok '远端不存在同名 tag'
        }
        catch {
            Write-Warn "检查远端 tag 失败（可能无网络/SSH 不可用），继续本地发版：$($_.Exception.Message)"
        }
    }
    else {
        Write-Warn '未配置 origin 远端，将只做本地发版。'
    }

    # 4) 发布前检查 .NET 10 SDK
    if (-not $SkipPublish) {
        $sdks = dotnet --list-sdks
        $net10 = $sdks | Where-Object { $_ -match '^10\.' }
        if (-not $net10) {
            Write-Host ($sdks -join "`n") -ForegroundColor DarkYellow
            Fail '未安装 .NET 10 SDK，无法发布 net10.0 项目。下载：https://dotnet.microsoft.com/download'
        }
        Write-Ok ".NET SDK：$(($net10 | Select-Object -First 1).Split(' ')[0])"
    }

    # -----------------------------------------------------------------------
    # 打 tag（必须先于 publish，MinVer 才能算出正式版本）
    # -----------------------------------------------------------------------
    Write-Step "创建本地带注释 tag：$Tag"
    $tagCreated = $false
    try {
        Invoke-Native -File git -Arguments @('tag', '-a', $Tag, '-m', "Release $Tag") -ErrorMessage "创建 tag $Tag 失败"
        $tagCreated = $true
        Write-Ok "tag $Tag 已指向 $(git rev-parse --short HEAD)"

        # 可选：推送前 fast-forward 拉取，避免推送被拒
        if ($Push -and $remote) {
            $upstream = git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null
            if ($upstream) {
                Write-Step "快进拉取 $upstream"
                Invoke-Native -File git -Arguments @('pull', '--ff-only', '--quiet') -ErrorMessage 'git pull --ff-only 失败，请先同步远端后重试。'
                Write-Ok '已是最新'
            }
        }

        # -------------------------------------------------------------------
        # 发布
        # -------------------------------------------------------------------
        if (-not $SkipPublish) {
            if ($CleanPublish -and (Test-Path $PublishDir)) {
                Write-Step "清空发布目录：$PublishDir"
                Get-ChildItem -LiteralPath $PublishDir -Force | Remove-Item -Recurse -Force
                Write-Ok '发布目录已清空'
            }

            Write-Step "dotnet publish -> $PublishDir"
            Invoke-Native -File dotnet -Arguments @('publish', $Project, '-c', 'Release', '-o', $PublishDir, '--nologo') -ErrorMessage 'dotnet publish 失败，请查看上方编译输出。'

            # -----------------------------------------------------------------
            # 校验产物版本
            # -----------------------------------------------------------------
            Write-Step '校验发布产物版本'
            $exe = Join-Path $PublishDir $ExeName
            if (-not (Test-Path $exe)) {
                Fail "发布目录中找不到 $ExeName：$exe"
            }
            $vi = (Get-Item $exe).VersionInfo
            Write-Host "    文件路径     : $exe"
            Write-Host "    FileVersion  : $($vi.FileVersion)"
            Write-Host "    ProductVersion(Informational): $($vi.ProductVersion)"

            if ($vi.FileVersion -ne $ExpectedFileVersion) {
                Fail "FileVersion 不匹配：期望 $ExpectedFileVersion，实际 $($vi.FileVersion)"
            }
            if (-not $vi.ProductVersion.StartsWith($Version)) {
                Fail "ProductVersion 不以 $Version 开头：实际 $($vi.ProductVersion)"
            }
            # 单文件发布的 BBKSync.exe 是原生 apphost（不含程序集清单，无法用 GetAssemblyName），
            # AssemblyVersion 改为从中间输出目录里最新的托管 BBKSync.dll 读取
            $buildRoot = Join-Path (Split-Path $Project -Parent) 'bin\Release'
            $asmDll = Get-ChildItem -Path $buildRoot -Filter 'BBKSync.dll' -Recurse -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $asmDll) { Fail "未找到中间产物 BBKSync.dll（$buildRoot），无法校验 AssemblyVersion。" }
            $asmVersion = [System.Reflection.AssemblyName]::GetAssemblyName($asmDll.FullName).Version
            if ("$asmVersion" -ne $ExpectedFileVersion) {
                Fail "AssemblyVersion 不匹配：期望 $ExpectedFileVersion，实际 $asmVersion（$($asmDll.FullName)）"
            }
            Write-Ok "版本校验通过：$Version（FileVersion/AssemblyVersion=$ExpectedFileVersion）"

            if ($Reorganize) {
                Write-Step "运行目录整理脚本：$ReorgScript"
                if (-not (Test-Path $ReorgScript)) { Fail "找不到 $ReorgScript" }
                # 在当前进程内执行并显式传入发布目录（不依赖该脚本里的中文默认路径）
                try {
                    & $ReorgScript -PublishDir $PublishDir
                }
                catch {
                    Fail "reorganize_publish.ps1 执行失败：$($_.Exception.Message)"
                }
                Write-Ok '目录整理完成'
            }
        }
        else {
            Write-Warn '已指定 -SkipPublish，跳过发布与版本校验。'
        }

        # -------------------------------------------------------------------
        # 推送
        # -------------------------------------------------------------------
        if ($Push) {
            if (-not $remote) { Fail '指定了 -Push 但未配置 origin 远端。' }
            Write-Step "推送分支 $branch 与 tag $Tag 到 origin"
            Invoke-Native -File git -Arguments @('push', 'origin', "HEAD:$branch") -ErrorMessage '推送分支失败'
            Invoke-Native -File git -Arguments @('push', 'origin', "refs/tags/$Tag") -ErrorMessage '推送 tag 失败'
            Write-Ok '分支与 tag 已推送'
            # 推送已成功，后续失败不能再回滚本地 tag（否则与远端不一致）
            $tagCreated = $false

            Write-Step "在 GitHub 上创建 Release：$Tag"
            # 优先用 gh（有登录态时最省事）；网络屏蔽 api.github.com 时自动改用 REST 兜底
            $releaseToken = $env:GH_TOKEN
            if (-not $releaseToken) { $releaseToken = $env:GITHUB_TOKEN }
            if (-not $releaseToken) {
                $releaseToken = Get-GitHubStoredToken
                if ($releaseToken) {
                    $env:GH_TOKEN = $releaseToken
                    Write-Ok '已从 git 凭据读取令牌（仅本次进程使用）'
                }
            }

            $asset = if (-not $SkipPublish) { Join-Path $PublishDir $ExeName } else { $null }
            $notes = "自动发布 $Tag。产物：$ExeName，可在远端电脑使用「程序升级」批量分发。"
            $releaseDone = $false
            $releaseAssetUploaded = $false
            if (Get-Command gh -ErrorAction SilentlyContinue) {
                $releaseArgs = @('release', 'create', $Tag, '--title', $Tag, '--notes', $notes)
                if ($Version -match '-') { $releaseArgs += '--prerelease' }
                if ($asset -and (Test-Path $asset)) { $releaseArgs += $asset }
                gh @releaseArgs
                if ($LASTEXITCODE -eq 0) {
                    $releaseDone = $true
                    $releaseAssetUploaded = [bool]($asset -and (Test-Path $asset))
                    Write-Ok "GitHub Release 已发布：$Tag"
                }
                else {
                    Write-Warn 'gh 创建 Release 失败，改用 REST API 兜底。'
                }
            }
            if (-not $releaseDone) {
                if (-not $releaseToken) {
                    Fail '缺少 GitHub 令牌：请先执行 gh auth login，或设置 GH_TOKEN 环境变量后重跑。'
                }
                $releaseAssetUploaded = New-GitHubReleaseByRest -Tag $Tag -Version $Version -Token $releaseToken -AssetPath $asset
                Write-Ok "GitHub Release 已发布：$Tag"
            }
        }
        else {
            Write-Warn '未指定 -Push，跳过推送与 GitHub Release 创建（本地发版不产生远程 Release）。'
        }
    }
    catch {
        # 发版失败：回滚本次创建的本地 tag，保证脚本可直接重跑
        if ($tagCreated) {
            Write-Warn "发版失败，删除本次创建的本地 tag $Tag 以便重试。"
            git tag -d $Tag | Out-Null
        }
        throw
    }
    finally {
        Pop-Location
    }
}
catch {
    Pop-Location -ErrorAction SilentlyContinue
    Write-Host ''
    Write-Host "发版失败：$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
# 汇总
# ---------------------------------------------------------------------------
Write-Step "发版完成：$Tag"
Write-Host "    git 状态   : tag $Tag 已创建$(if ($Push) { ' 并推送至 origin' } else { '（仅本地，确认无误后执行：git push origin ' + $Tag + '）' })"
if ($Push) {
    $assetNote = if ($releaseAssetUploaded) { "已创建并附带 $ExeName" } else { "已创建（未附 $ExeName）" }
    Write-Host "    GitHub     : Release $Tag $assetNote"
}
if (-not $SkipPublish) {
    Write-Host "    发布目录   : $PublishDir"
    Write-Host "    可分发文件 : $(Join-Path $PublishDir $ExeName)"
}
Write-Host "    后续在远端电脑上使用「程序升级」功能即可批量分发该 exe。" -ForegroundColor DarkGray
