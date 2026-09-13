param([Parameter(Mandatory=$true)][string]$DotNetSdkRoot)
$ErrorActionPreference = 'Stop'
$modRoot = $PSScriptRoot
$runtime = Join-Path (Split-Path $modRoot) 'Launcher\runtime'
$sdk = Get-ChildItem -LiteralPath (Join-Path $DotNetSdkRoot 'sdk') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$compiler = Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll'
$output = Join-Path $modRoot 'payload\BepInEx\plugins\ArenaHotseat\ArenaHotseat.dll'
New-Item -ItemType Directory -Path (Split-Path $output) -Force | Out-Null
$references = @(Get-ChildItem -LiteralPath (Join-Path $runtime 'dotnet'),(Join-Path $runtime 'BepInEx\core') -Filter '*.dll' -File | ForEach-Object {
    try { [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null; '/reference:"' + $_.FullName + '"' } catch [System.BadImageFormatException] { }
})
$arguments = @('/nologo','/target:library','/langversion:latest','/nullable:enable','/nostdlib+','/optimize+',('/out:"' + $output + '"')) + $references + @(('"' + (Join-Path $modRoot 'src\ArenaHotseat.cs') + '"'),('"' + (Join-Path $modRoot 'src\AssemblyInfo.cs') + '"'))
$response = Join-Path $modRoot 'compiler.rsp'
[System.IO.File]::WriteAllLines($response, $arguments, [System.Text.UTF8Encoding]::new($false))
& (Join-Path $DotNetSdkRoot 'dotnet.exe') exec $compiler "@$response"
if ($LASTEXITCODE -ne 0) { throw 'Mod compilation failed' }
if (Test-Path -LiteralPath (Join-Path $modRoot 'mod.json')) {
    $manifest = Get-Content -LiteralPath (Join-Path $modRoot 'mod.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $manifest.files[0].sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText((Join-Path $modRoot 'mod.json'), ($manifest | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
}
Write-Output "Built: $output"
