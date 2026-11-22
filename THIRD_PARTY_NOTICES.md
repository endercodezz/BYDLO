# Сторонние компоненты

BYDLO распространяется по лицензии GPL-3.0-or-later и опирается на работу многих проектов.
sing-box, Xray, Zapret, AmneziaWG и OpenVPN работают как **отдельные фоновые программы**;
BYDLO не линкуется с ними. Исключение — перенесённое на C# ядро TgWsProxy, встроенное в BYDLO.
Полный релиз содержит готовые движки либо сборки из закреплённых исходников.
Готовые архивы закреплены в [`engines/engines.lock.json`](engines/engines.lock.json),
входы сборки — в [`engines/bundle-inputs.lock.json`](engines/bundle-inputs.lock.json);
упаковщики проверяют SHA-256 до извлечения. Исходники и лицензии включаются в комплект, как описано ниже.

## Сетевые движки и инструменты (отдельные процессы; бинарники добавляются при упаковке)

| Компонент | Авторы | Лицензия | Исходники | Как использует BYDLO |
|---|---|---|---|---|
| zapret-discord-youtube | Flowseal; на основе zapret от bol-van | MIT | https://github.com/Flowseal/zapret-discord-youtube | Стратегии (`general*.bat`) и набор `bin/` используются без изменений. При упаковке BYDLO дополняет `lists/list-general.txt` из собственного `engines/bydlo-zapret-list.txt`. BYDLO разбирает стратегии и запускает `winws.exe` напрямую. |
| zapret (winws, nfqws, tpws) | bol-van | MIT | https://github.com/bol-van/zapret | Windows: внутри Flowseal; Linux/macOS: закреплённый upstream v72.13, nfqws/tpws как отдельные процессы. |
| WinDivert | basil00 | LGPL-3.0 или GPL-2.0 | https://github.com/basil00/WinDivert | Драйвер перехвата пакетов, используемый `winws.exe` (входит в релиз Flowseal). |
| Cygwin 3.4.10 / Newlib | Cygwin contributors / Red Hat и другие | LGPL-3.0-or-later с Cygwin Linking Exception; дополнительные permissive notices Newlib | https://cygwin.com/licensing.html | Неизменённый cygwin1.dll из Flowseal; динамическая библиотека winws. |
| sing-box 1.14.4 | SagerNet / nekohasekai | GPL-3.0-or-later (с дополнительными условиями об использовании названия) | https://github.com/SagerNet/sing-box | Основной движок для VLESS/VMess/Trojan/Shadowsocks/Hysteria 2/TUIC/SOCKS/HTTP/WireGuard; конфигурацию генерирует BYDLO. |
| Xray-core 26.10.31 | Project X (XTLS) | MPL-2.0 | https://github.com/XTLS/Xray-core | Движок для транспорта XHTTP и альтернативный движок для протоколов семейства V2Ray. |
| Cronet / cronet-go | The Chromium Authors, SagerNet и участники | BSD-3-Clause (Chromium), GPL-3.0-or-later (Go-обвязка) | https://github.com/SagerNet/cronet-go/tree/0d28acc44093df24b2526dea3d6ffefd6b0a54f0 | Неизменённая libcronet из архива sing-box. Текст Chromium BSD включён в `engines/licenses/Chromium.txt`; закреплённый Chromium/NaiveProxy: https://github.com/SagerNet/naiveproxy/tree/72a06c9fca0e2d228588c7f3074bf7efff3ff686. |
| Xray GeoIP / GeoSite | Project X, V2Ray и авторы исходных списков | GPL-3.0; MIT для GeoSite и унаследованных частей | https://github.com/XTLS/Xray-rules-dat | Неизменённые geoip.dat/geosite.dat внутри закреплённого архива Xray; дополнительные тексты лицензий находятся в `engines/licenses/Xray-rules-*.txt`. |
| WireGuard for Windows / wireguard-tools | Jason A. Donenfeld, WireGuard LLC | MIT / GPL-2.0 | https://www.wireguard.com | Официальный клиент для нативных туннелей WireGuard (устанавливает пользователь). |
| AmneziaWG (amneziawg-go 3.1.20260828) | WireGuard LLC, Amnezia VPN и участники | MIT; хост BYDLO — GPL-3.0-or-later | https://github.com/amnezia-vpn/amneziawg-go/tree/v3.1.20260828 | Компилируется вместе с минимальным процесс-хостом, без клиента Amnezia. |
| Wintun 0.14.1 | WireGuard LLC | Prebuilt Binaries License | https://www.wintun.net/ | Неизменённая подписанная DLL для AmneziaWG на Windows; собственная копия также входит в upstream-архив Xray. Текст лицензии: `engines/licenses/Wintun.txt` и `LICENSE-Wintun` рядом с Xray; используется только через разрешённый Wintun API. |
| OpenVPN 2.7.9 | OpenVPN Inc. и участники | GPL-2.0 (с исключениями для OpenSSL/Apache-2.0) | https://github.com/OpenVPN/openvpn/tree/v2.7.9 | Встроенный консольный движок без OpenVPN GUI и служб. |
| OpenVPN DCO 2.8.14 | OpenVPN Inc. и участники | GPL-2.0 | https://github.com/OpenVPN/ovpn-dco-win/tree/2.8.14 | Неизменённые подписанные драйверы Windows 10/11; установка только с явным согласием. |
| OpenSSL 3.6.5 | OpenSSL Project Authors | Apache-2.0 | https://github.com/openssl/openssl/tree/openssl-3.6.5 | Windows: DLL из официального MSI; Unix: статическая библиотека OpenVPN. |
| libcap-ng 0.9.6 | Steve Grubb и участники | LGPL-2.1-or-later | https://github.com/stevegrubb/libcap-ng/tree/v0.9.6 | Статическая библиотека Linux OpenVPN; соответствующие исходники и скрипт пересборки включены. |
| pkcs11-helper / Microsoft Visual C++ runtime | Alon Bar-Lev / Microsoft | BSD (выбранная альтернатива для pkcs11-helper) / условия распространения Microsoft runtime | https://github.com/OpenSC/pkcs11-helper | Неизменённые runtime DLL из официального Windows MSI; тексты OpenVPN bundle сохранены в `engines/licenses/OpenVPN-bundle.txt`. |

Лицензия sing-box содержит дополнительные условия, ограничивающие использование названия «sing-box» для производных
работ. BYDLO упоминает sing-box только для указания авторства и не переименовывает и не распространяет изменённые бинарники sing-box.

В `engines/sources/` релиза включаются закреплённые исходники sing-box, Xray, OpenVPN, OpenSSL,
DCO (Windows), libcap-ng (Linux), а для AmneziaWG — процесс-хост и полные vendored Go-зависимости с их лицензиями.
Входы сборок и SHA-256 находятся в `engines/bundle-inputs.lock.json`; команды — в `docs/BUILD.md`.
Windows MSI используется только как источник файлов: установщик и его custom actions никогда не запускаются сборкой.

## Перенесённый код: tg-ws-proxy

Ядро [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) v1.11.4 (MIT) перенесено на C# в
`src/Bydlo.Engines/TgWsProxy` и работает внутри BYDLO: протокол MTProto obfuscated2, Fake TLS, пул WebSocket,
резервные пути через Cloudflare и прямой TCP, закодированный список доменов Cloudflare. Python-код в репозиторий не
копируется. Дополнительно перенесена защита Fake TLS от повторного ClientHello из
[upstream-коммита dbc8b781](https://github.com/Flowseal/tg-ws-proxy/commit/dbc8b7811a1b39f5fd99f3994a8cdfc58e899916)
после v1.11.4; базовая версия upstream остаётся 1.11.4. Лицензия оригинала:

```text
MIT License

Copyright (c) 2026 Flowseal

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Референс протокола: регистрация WARP (код не копируется)

Встроенная регистрация Cloudflare WARP (`src/Bydlo.Core/Warp`) повторяет endpoints
неофициального клиентского API, которые использует [ViRb3/wgcf](https://github.com/ViRb3/wgcf)
(MIT, © ViRb3). Код wgcf в BYDLO не копируется: HTTP-клиент и фабрика WireGuard-профиля
написаны заново, арифметика X25519 реализована самостоятельно по RFC 7748
(сверена с тестовыми векторами RFC). BYDLO не аффилирован ни с Cloudflare, ни с wgcf.

```text
MIT License

Copyright (c) 2020 ViRb3

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Библиотеки, входящие в BYDLO (NuGet)

| Компонент | Лицензия | Исходники |
|---|---|---|
| Avalonia UI 11.3.22 (Avalonia, Desktop, Themes.Fluent, Fonts.Inter, Skia, FreeDesktop, Native, Win32, X11, Remote.Protocol) | MIT для кода; Inter отдельно под OFL-1.1 | https://github.com/AvaloniaUI/Avalonia/tree/627ae9ef921621e27e7aa58df2796fbadb50af88 |
| SkiaSharp 2.88.9 / HarfBuzzSharp 8.3.1.1 и NativeAssets (через Avalonia) | MIT для обвязок; оригинальные лицензии встроенных компонентов в [notices](licenses/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt) | https://github.com/mono/SkiaSharp |
| ANGLE (Avalonia.Angle.Windows.Natives 2.1.25547.20250602) | BSD-3-Clause | https://github.com/google/angle |
| MicroCom.Runtime 0.11.0 | MIT | https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol 0.21.3 | MIT | https://github.com/tmds/Tmds.DBus/tree/8cb04f66c330b64244e996ff68f685f27f7381a0 |
| CommunityToolkit.Mvvm 8.4.2 | MIT и оригинальные [дополнительные уведомления](licenses/CommunityToolkit-THIRD-PARTY-NOTICES.txt) | https://github.com/CommunityToolkit/dotnet |
| YamlDotNet 18.1.0 | MIT | https://github.com/aaubry/YamlDotNet/tree/748334a8fa7c227740018b284b71ad95cc6b7fc7 |
| ZXing.Net 0.16.11 | Apache-2.0 | https://github.com/micjahn/ZXing.Net/tree/v0.16.11.0 |
| Grpc.Net.Client / Grpc.Net.Common / Grpc.Core.Api 2.85.0 | Apache-2.0 | https://github.com/grpc/grpc-dotnet |
| Google.Protobuf 3.36.2 | BSD-3-Clause | https://github.com/protocolbuffers/protobuf/tree/2c74169b34066ceb8ddb6b882fcb3fb32d737a55 |
| .NET 10.0.14, System.Security.Cryptography.ProtectedData / System.ServiceProcess.ServiceController 10.0.14 | MIT и оригинальные [сторонние уведомления](licenses/DotNet-THIRD-PARTY-NOTICES.txt) | https://github.com/dotnet/runtime/tree/v10.0.14 |
| Microsoft.Extensions.Logging.Abstractions / DependencyInjection.Abstractions 8.0.1 (через Velopack) | MIT и оригинальные [сторонние уведомления](licenses/DotNet-Extensions-THIRD-PARTY-NOTICES.txt) | https://github.com/dotnet/runtime |

Тексты включены в [`licenses/`](licenses/README.md); точные источники и SHA-256 находятся
в [`licenses/manifest.json`](licenses/manifest.json). `dotnet publish` копирует этот каталог,
`LICENSE` и `THIRD_PARTY_NOTICES.md` рядом с приложением для всех RID, в том числе при single-file publish.
Проверка релизного комплекта отклоняет отсутствующие или изменённые уведомления до упаковки
и после извлечения update-пакета. Оригинальные notices сохранены целиком: они могут перечислять
компоненты для других платформ или удалённые trimming, что не означает использование всех этих компонентов BYDLO.

Только разработка и тесты: Avalonia.BuildServices 11.3.2, Avalonia.Diagnostics/Headless 11.3.22,
Grpc.Tools 2.85.0, xUnit 2.9.3 / xunit.runner.visualstudio 4.0.1 (Apache-2.0),
Microsoft.NET.Test.Sdk 18.11.0 и coverlet.collector 10.1.0 (MIT). Они не поставляются как библиотеки
Release-приложения. Лицензии инструментов сохраняются в соответствующих NuGet-пакетах.

## Ресурсы

| Ресурс | Лицензия | Исходники |
|---|---|---|
| Шрифт Inter 3.019 (через Avalonia.Fonts.Inter 11.3.22) | SIL Open Font License 1.1, оригинал в [licenses/Inter.txt](licenses/Inter.txt) | https://github.com/rsms/inter/tree/v3.19 |
| Material Design Icons (геометрия иконок в `src/Bydlo.App/Themes/Icons.axaml`) | Apache-2.0 | https://github.com/Templarian/MaterialDesign |
| Тестовые файлы `tests/Bydlo.Tests/Fixtures/general*.bat.txt` (стратегии Flowseal без изменений) | MIT, © 2016-2026 bol-van, © 2024-2026 Flowseal | https://github.com/Flowseal/zapret-discord-youtube |
| Списки подписок [igareck/vpn-configs-for-russia](https://github.com/igareck/vpn-configs-for-russia) (скачиваются приложением с зеркал автора, в проект не копируются) | GPL-3.0 | https://github.com/igareck/vpn-configs-for-russia |
| Проверенный набор подписок [0xRadikal/Free-v2ray-Configs](https://github.com/0xRadikal/Free-v2ray-Configs) `verified/` (каждый сервер ответил через прокси в независимых проверках; скачивается приложением с зеркал, в проект не копируется) | MIT | https://github.com/0xRadikal/Free-v2ray-Configs |
| VLESS-список [MatinGhanbari/v2ray-configs](https://github.com/MatinGhanbari/v2ray-configs) `subscriptions/filtered/subs/vless.txt` (скачивается приложением с зеркал, в проект не копируется) | MIT | https://github.com/MatinGhanbari/v2ray-configs |

## Тексты лицензий

- MIT: https://opensource.org/license/mit
- Apache-2.0: https://www.apache.org/licenses/LICENSE-2.0
- MPL-2.0: https://www.mozilla.org/MPL/2.0/
- GPL-2.0: https://www.gnu.org/licenses/old-licenses/gpl-2.0.html
- GPL-3.0: см. [LICENSE](LICENSE)
- LGPL-3.0: https://www.gnu.org/licenses/lgpl-3.0.html
- OFL-1.1: https://openfontlicense.org

Полный комплект содержит этот файл, `LICENSE`, `licenses/`, `engines/licenses/`, `engines/sources/`, lock-файлы
и `ENGINES-SHA256SUMS.txt`. Сохраняйте их при переупаковке и распространении; не ограничивайтесь
исполняемым файлом BYDLO. Команды сборки, состав исходников и порядок обновления описаны
в [docs/BUILD.md](docs/BUILD.md#обновление-встроенных-компонентов). CI на GitHub создаёт комплект
для всех пяти платформ и публикует черновики релизов; зеркало на Gitea собирает только Linux x64.
macOS поддерживается только на Apple Silicon (`osx-arm64`). Универсальный upstream-бинарник
tpws сохраняется без изменений; наличие в нём x86_64 не означает поддержку Intel Mac в BYDLO.

## Встроенный Zapret

Дополнения BYDLO к списку доменов Flowseal основаны на списке сопровождающего BYDLO.

- Flowseal 1.10.3: MIT, текст из закреплённого [LICENSE.txt](https://github.com/Flowseal/zapret-discord-youtube/blob/1.10.3/LICENSE.txt).
- bol-van/zapret 72.13: MIT, подтверждён по [docs/LICENSE.txt](https://github.com/bol-van/zapret/blob/v72.13/docs/LICENSE.txt), включая copyright bol-van. Универсальный `binaries/mac64/tpws` содержит x86_64 и arm64.
- WinDivert 2.2: выбираем LGPL-3.0; комплект содержит тексты LGPLv3/GPLv3/GPLv2 и закреплённые исходники v2.2.2. Исходный код: https://github.com/basil00/WinDivert/tree/v2.2.2.
- Cygwin DLL 3.4.10: LGPLv3+ и Linking Exception, тексты `CYGWIN_LICENSE`, LGPLv3 и дополнительные notices Newlib включены. Архив `cygwin-3.4.10-1-src.tar.xz` содержит соответствующие исходники и cygport recipe; источник — зеркало дистрибутива Cygwin в ČVUT.

Релизные архивы включают `engines/licenses/` и (для Windows x64) `engines/sources/` с исходниками WinDivert и Cygwin. Их URL и SHA-256 закреплены отдельно в `engines/zapret-sources.lock.json`; упаковка прекращается при несовпадении. GPLv3 находится в корневом `LICENSE`. Бинарники не изменяются; библиотеку Cygwin/WinDivert можно заменить совместимой версией. Нативные пресеты написаны для BYDLO, код/стратегии Sergeydigl3 и других проектов без лицензии не копируются.

## Перенос стандартных проверок Test Zapret

`src/Bydlo.Engines/Zapret/TestZapretProbe.cs` переносит стандартную HTTP/TLS-часть
[Flowseal Test Zapret 1.10.3](https://github.com/Flowseal/zapret-discord-youtube/blob/1.10.3/utils/test%20zapret.ps1)
в управляемый жизненный цикл BYDLO. Изменения: структурированные результаты curl, отдельные
таймауты/неизвестные результаты, проверка HTTP-кода, реальное время успешных ответов,
отмена и запуск только собственных дочерних процессов. Интерактивный координатор, ping
и DPI-suite не переносятся. © 2016–2026 bol-van; © 2024–2026 Flowseal. Лицензия MIT:

```text
MIT License

Copyright (c) 2016-2026 bol-van
Copyright (c) 2024-2026 Flowseal

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## API групп Xray

`src/Bydlo.Engines/Groups/Protos/` содержит сокращённые wire-совместимые схемы, сверенные с Xray-core v26.10.31,
адаптированные для клиента BYDLO: StatsService, RoutingService и ObservatoryService. Исходники Project X (XTLS), MPL-2.0:
[stats/command/command.proto](https://github.com/XTLS/Xray-core/blob/v26.10.31/app/stats/command/command.proto),
[router/command/command.proto](https://github.com/XTLS/Xray-core/blob/v26.10.31/app/router/command/command.proto),
[observatory/command/command.proto](https://github.com/XTLS/Xray-core/blob/v26.10.31/app/observatory/command/command.proto),
[observatory/config.proto](https://github.com/XTLS/Xray-core/blob/v26.10.31/app/observatory/config.proto).
Схемы остаются под MPL-2.0; текст лицензии: [Xray-core LICENSE](https://github.com/XTLS/Xray-core/blob/v26.10.31/LICENSE).
Для клиента используются [gRPC for .NET](https://github.com/grpc/grpc-dotnet) (The gRPC Authors, Apache-2.0),
[Protocol Buffers](https://github.com/protocolbuffers/protobuf) (Google LLC, BSD-3-Clause);
Grpc.Tools (The gRPC Authors, Apache-2.0) используется только при сборке.

## Velopack

Установщики, AppImage и обновления используют [Velopack 1.2.161](https://github.com/velopack/velopack/tree/92d6a1c91716729d449034df5c50307dcce39493).
Авторские права: Caelan Sayler (2021), Velopack Ltd. (2024). Лицензия MIT включена
в [licenses/Velopack.txt](licenses/Velopack.txt) и распространяется как `LICENSE-Velopack.txt` рядом с приложением.
