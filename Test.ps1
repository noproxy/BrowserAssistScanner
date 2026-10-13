$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$testBin = Join-Path $projectDir 'test-artifacts\bin'
New-Item -ItemType Directory -Path $testBin -Force | Out-Null
$testExe = Join-Path $testBin 'ScannerTests.exe'
$sources = @('src\Scanner.cs', 'src\PackageAnalysis.cs', 'src\SelectionState.cs', 'src\DeleteEngine.cs', 'src\ShellRecycler.cs', 'src\RedundantDirectories.cs', 'tests\ScannerTests.cs', 'tests\DeletionTests.cs', 'tests\SmartSelectionTests.cs') | ForEach-Object { Join-Path $projectDir $_ }
& $compiler /nologo /target:exe /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$testExe" @sources
if ($LASTEXITCODE -ne 0) { throw '测试编译失败。' }
$fixtureRoot = Join-Path $projectDir ('test-artifacts\fixtures\' + [Guid]::NewGuid().ToString('N'))
$packages = Join-Path $fixtureRoot '包目录 中文 空格'
$vam = Join-Path $fixtureRoot 'VAM'
New-Item -ItemType Directory -Path $packages,$vam -Force | Out-Null
$links = @((Join-Path $vam 'AddonPackages'), (Join-Path $packages '循环链接'))
try {
    foreach ($link in $links) { New-Item -ItemType Junction -Path $link -Target $packages | Out-Null }
    & $testExe $fixtureRoot
    if ($LASTEXITCODE -ne 0) { throw '扫描测试失败。' }
    $formTestExe = Join-Path $testBin 'FormTests.exe'
    $formSources = @(Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' -File | ForEach-Object FullName) + (Join-Path $projectDir 'tests\FormTests.cs')
    & $compiler /nologo /target:exe /main:FormTests /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$formTestExe" @formSources
    if ($LASTEXITCODE -ne 0) { throw '界面测试编译失败。' }
    & $formTestExe $fixtureRoot
    if ($LASTEXITCODE -ne 0) { throw '界面删除测试失败。' }
    $hoverTestExe = Join-Path $testBin 'HoverTests.exe'
    $hoverSources = @(Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' -File | ForEach-Object FullName) + (Join-Path $projectDir 'tests\HoverTests.cs')
    & $compiler /nologo /target:exe /main:HoverTests /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$hoverTestExe" @hoverSources
    if ($LASTEXITCODE -ne 0) { throw '悬停测试编译失败。' }
    & $hoverTestExe $fixtureRoot
    if ($LASTEXITCODE -ne 0) { throw '悬停测试失败。' }
    $filterTestExe = Join-Path $testBin 'FilterTests.exe'
    $filterSources = @(Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' -File | ForEach-Object FullName) + (Join-Path $projectDir 'tests\FilterTests.cs')
    & $compiler /nologo /target:exe /main:FilterTests /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$filterTestExe" @filterSources
    if ($LASTEXITCODE -ne 0) { throw '前端过滤测试编译失败。' }
    & $filterTestExe $fixtureRoot
    if ($LASTEXITCODE -ne 0) { throw '前端过滤测试失败。' }
    $sharedTestExe = Join-Path $testBin 'SharedSelectionTests.exe'
    $sharedSources = @(Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' -File | ForEach-Object FullName) + (Join-Path $projectDir 'tests\SharedSelectionTests.cs')
    & $compiler /nologo /target:exe /main:SharedSelectionTests /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$sharedTestExe" @sharedSources
    if ($LASTEXITCODE -ne 0) { throw '共享勾选测试编译失败。' }
    & $sharedTestExe $fixtureRoot
    if ($LASTEXITCODE -ne 0) { throw '共享勾选测试失败。' }
    $hideTestExe = Join-Path $testBin 'HideTests.exe'
    $hideSources = @(Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' -File | ForEach-Object FullName) + (Join-Path $projectDir 'tests\HideTests.cs')
    & $compiler /nologo /target:exe /main:HideTests /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$hideTestExe" @hideSources
    if ($LASTEXITCODE -ne 0) { throw '隐藏测试编译失败。' }
    & $hideTestExe $fixtureRoot
    if ($LASTEXITCODE -ne 0) { throw '隐藏测试失败。' }
} finally {
    # Remove only these known junction entries. Preserve the fixture files.
    foreach ($linkPath in $links) {
        if (Test-Path -LiteralPath $linkPath) {
            $link = Get-Item -LiteralPath $linkPath -Force
            if (($link.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $link.Delete() }
        }
    }
}
