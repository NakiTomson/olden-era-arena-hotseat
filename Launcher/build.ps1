$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll "/win32manifest:$PSScriptRoot\src\app.manifest" "/out:$PSScriptRoot\OldenEraLauncher.exe" "$PSScriptRoot\src\Launcher.cs" "$PSScriptRoot\src\IntegrationSmoke.cs"
if ($LASTEXITCODE -ne 0) { throw 'Launcher compilation failed' }
Write-Output 'Launcher built successfully.'
