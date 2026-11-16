SIGame Direct IP 1.0.8 — сборка из патчей

Для игры сборка не нужна: скачайте готовый мод из Releases.

Требования: Windows x64, Git, .NET 10 SDK, Node.js 22.12+ / npm,
Rust MSVC >=1.90, C++ Build Tools и Windows SDK. Нужен Интернет.

Сборка из корня репозитория:
powershell -NoProfile -File .\build-from-source.ps1

Скрипт скачает SI и SIOnline на точных коммитах из patches/upstream.json,
проверит SHA-256 патчей, применит их и проверит итоговые деревья Git.
Исходники и зависимости появятся в .build; эта папка не публикуется в Git.
Из SI используются только нужные проекты движка. Неиспользуемые шрифты
FuturaC и готовые Steam DLL исключены из рабочего дерева SIOnline.
Версии пакетов клиента закреплены lock-файлами; npm, Cargo и NuGet
восстанавливают зависимости при сборке.

Результат в publish-exe: SIGame.exe, DirectHost, README-RU.txt,
DirectIP-Docs и DirectIP-Tools. Скрипт ничего не устанавливает в игру.
Steam DLL берутся из установленной игры; в мод они не включаются.

Только подготовить исходники:
powershell -NoProfile -File .\build-from-source.ps1 -PrepareOnly

Существующие каталоги не перезаписываются. Для повторной сборки укажите
новые -WorkDirectory .build-2 -OutputDirectory publish-2 либо удалите
свои предыдущие каталоги сборки. Другой SDK: -Dotnet "C:\путь\dotnet.exe".
После -PrepareOnly исходники можно редактировать и собирать в .build:
SIOnline — npm ci, npm run build-steam, npm run build-direct-web;
SIOnline/tauri — npm ci, npm run tauri build -- --features steam_client --no-bundle;
DirectHost — dotnet publish -c Release -r win-x64 --self-contained true.

Для проверки клиента в .build/SIOnline:
npm run build-dev
node --no-experimental-global-navigator node_modules/jest/bin/jest.js --runInBand
npm run lint
В исходном проекте имеются прежние ошибки lint; чистый прогон не заявляется.

Полный снимок исходников 1.0.8 с отдельными проверками и техническим аудитом:
SIGame-Direct-IP-source-v1.0.8.zip в
https://github.com/toolewdpersona/SIGame-Direct-IP/releases/tag/v1.0.8
Этот архив содержит развёрнутые SI/SIOnline и собственный скрипт сборки.
Там же доступны дополнительные MPL-исходники и контрольные суммы.

Авторство и условия: DirectIP-Docs/LICENSES.txt и DirectIP-Docs/NOTICE.txt.
Полнота соответствующих исходников всех сторонних компонентов не подтверждена;
сохранившиеся вопросы перечислены в NOTICE.txt.
