# Сборка и проверка

Сохраняйте `Launcher` и `Mod1` рядом. Сборка предназначена для Windows x64. Команды ниже разрешают выполнение проверенного локального скрипта только в запускаемом процессе PowerShell; постоянная политика системы не меняется.

## Лаунчер

Нужен .NET Framework 4.8 с компилятором в стандартном каталоге Windows. Из папки `Launcher`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

Результат — `Launcher/OldenEraLauncher.exe`. Конфигурация `OldenEraLauncher.exe.config` должна лежать рядом.

## Мод

Нужен SDK .NET 8. Сначала скачайте готовый комплект **той же версии**, распакуйте его и скопируйте из него `Launcher/runtime` в свою копию репозитория. В Git хранится только манифест runtime; сами зависимости находятся в релизе. Скрипт компиляции использует .NET 6 и библиотеки BepInEx из этого каталога.

Из папки `Mod1` выполните команду, заменив пример пути на каталог SDK с `dotnet.exe` и подпапкой `sdk`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -DotNetSdkRoot 'C:\Program Files\dotnet'
```

Результат — `Mod1/payload/BepInEx/plugins/ArenaHotseat/ArenaHotseat.dll`. Скрипт обновит SHA256 DLL в `Mod1/mod.json`. Он не меняет поддерживаемый хеш игры и не проверяет совместимость новой сборки игры.

## Проверка лаунчера без игры

Из папки `Launcher`:

```powershell
$smokePath = Join-Path $env:TEMP ('olden-era-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $smokePath | Out-Null
$process = Start-Process -FilePath '.\OldenEraLauncher.exe' -ArgumentList @('--smoke', ('"{0}"' -f $smokePath)) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw 'Проверка лаунчера завершилась ошибкой; см. last-error.txt.' }
Get-Content -LiteralPath (Join-Path $smokePath 'smoke-results.json')
```

Проверяются установка, повторное применение, отключение, отказ для другой сборки, повреждённый payload, сохранение внешних изменений, ограничения путей и восстановление прерванной операции. Проверка создаёт собственные тестовые файлы и не обращается к установленной игре.

## Проверка мода и выпуск

В `Mod1/src/ArenaHotseat.cs` находятся нативные адреса и смещения для конкретного `GameAssembly.dll`. После обновления игры недостаточно поменять хеш: нужно перепроверить обращения к игре, собрать мод и пройти [игровой список проверок](Launcher/README.md#игровой-список-проверок).

Перед выпуском проверьте каждый файл из `Launcher/runtime/runtime.json` и `Mod1/mod.json` по его SHA256. В готовый комплект включайте `Launcher/OldenEraLauncher.exe`, конфигурацию, весь проверенный runtime, `Mod1/mod.json`, payload и документацию с лицензиями. Личные настройки, состояние установленного мода, резервные копии и журналы в архив не входят.

Проверьте распакованный ZIP и приложите `SHA256SUMS.txt` к релизу. Исходники распространяемых LGPL-библиотек приложены отдельным архивом; описание находится в [THIRD-PARTY.md](THIRD-PARTY.md).
