# Зависимости и их исходники

Готовый комплект содержит BepInEx Unity IL2CPP Windows x64 **6.0.0-be.788** и его зависимости. Список устанавливаемых файлов и SHA256 каждого файла находится в `Launcher/runtime/runtime.json`.

Файлы лицензий сохранены в [licenses](licenses/). [sources.json](licenses/sources.json) связывает каждую библиотеку с исходным репозиторием, точной ревизией и файлами уведомлений.

## Библиотеки LGPL

- **BepInEx 6.0.0-be.788**, commit `5b766a3b7f6c164d4798924a93f3acf4db769d06`: [исходники](https://github.com/BepInEx/BepInEx/tree/5b766a3b7f6c164d4798924a93f3acf4db769d06), [LGPL 2.1](licenses/BepInEx-LICENSE).
- **Il2CppInterop 1.5.3**, commit `dbda1cb353b0f4253345dc45136d170b9e50a5a0`: [исходники](https://github.com/BepInEx/Il2CppInterop/tree/dbda1cb353b0f4253345dc45136d170b9e50a5a0), [лицензия LGPL 3 и включённый текст GPL](licenses/Il2CppInterop-LICENSE).
- **Unity Doorstop 4.5.0**: [исходники](https://github.com/NeighTools/UnityDoorstop/tree/v4.5.0), [лицензия](licenses/UnityDoorstop-LICENSE).

Архивы исходников этих ревизий, включая их скрипты сборки, приложены к [релизу 0.2.1](https://github.com/NakiTomson/olden-era-arena-hotseat/releases/tag/v0.2.1) как `ThirdParty-Sources-0.2.1.zip`. Их можно скачать отдельно от готового комплекта. Лицензии библиотек сохраняют силу независимо от мода и лаунчера.

Библиотеки загружаются из отдельных DLL. Для собственного комплекта их можно пересобрать из опубликованных исходников и заменить; хеши заменённых файлов нужно обновить в `Launcher/runtime/runtime.json`. Исходники мода и его скрипт сборки опубликованы в этом репозитории.

## Остальные библиотеки

Комплект включает HarmonyX 2.10.2, AsmResolver 6.0.0-beta.5, AssetRipper.CIL 1.2.2, AssetRipper.Primitives 3.2.0, Cpp2IL development.1452 (включая LibCpp2IL, StableNameDotNet и WasmDisassembler), Disarm master.99, Iced 1.21.0, Mono.Cecil 0.11.4, MonoMod 22.07.31.01, MonoMod.Backports 1.1.2, SemanticVersioning 2.0.2, Gee.External.Capstone 2.3.2 и Dobby 1.0.5. Их уведомления и лицензии находятся в `licenses`; уведомление встроенного Capstone сохранено отдельно от лицензии .NET-обёртки.

**.NET Runtime 6** соответствует ревизии `0ec02c8c96e2eda06dc5b5edfdbdba0f36415082`: [исходники](https://github.com/dotnet/runtime/tree/0ec02c8c96e2eda06dc5b5edfdbdba0f36415082), [лицензия](licenses/DotNet-LICENSE.TXT), [уведомления о сторонних компонентах](licenses/DotNet-THIRD-PARTY-NOTICES.TXT).

Для генерации interop включён архив базовых библиотек **Unity 6000.0.66** из [сервиса BepInEx Unity libraries](https://unity.bepinex.dev/libraries/6000.0.66.zip). Это отдельные библиотеки Unity, а не DLL с игровой логикой Olden Era. Права на компоненты Unity принадлежат их правообладателям; лицензии других библиотек в этом проекте на них не распространяются.

## Инструменты разработки

Для анализа личной установленной копии использовался [Il2CppDumper 6.7.46](https://github.com/Perfare/Il2CppDumper/releases/tag/v6.7.46), а для сборки мода — [SDK .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0). Эти инструменты не входят в готовый комплект и не нужны игроку.

Проект не связан с Unfrozen, Ubisoft или Hooded Horse. Исходные игровые DLL, игровые ресурсы и дампы в публикацию не включены.
