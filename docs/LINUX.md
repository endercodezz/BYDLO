# Установка на Linux: NixOS, Debian/Ubuntu, AppImage, Arch

Все варианты ставят одно и то же приложение. Пакеты релиза (`.deb`, AppImage, `bydlo-bin`)
собираются из проверенного publish-каталога и содержат встроенные движки.
`bydlo-bin` извлекает опубликованный `.deb` без установки через dpkg.
Пакет Nix собирается из исходников и берёт часть движков из nixpkgs (см. ниже).
Реальная работа туннелей во всех вариантах проверяется сопровождающим вручную;
права root для TUN, Zapret и AmneziaWG нужны так же, как в [матрице совместимости](COMPATIBILITY.md).

Если Zapret сообщает об отсутствии прав, нажмите «Установить службу и запустить Zapret»
рядом с ошибкой. Установка остановит подключения BYDLO и запросит пароль администратора
через PolKit; после успешной установки Zapret запустится через службу, а интерфейс останется
без прав root. Нужны systemd и работающий агент аутентификации PolKit в графической сессии.
При отмене запроса Zapret не запускается, действие можно повторить.
В AppImage комплект службы перед запросом прав копируется во временный каталог:
это позволяет установщику читать файлы вне пользовательского FUSE-монтирования.

## NixOS и Nix (flake)

Запуск без установки:

```bash
nix run github:endercodezz/BYDLO
```

Установка в профиль пользователя:

```bash
nix profile install github:endercodezz/BYDLO
```

Системная установка через модуль NixOS (`flake.nix` конфигурации):

```nix
{
  inputs.bydlo.url = "github:endercodezz/BYDLO";

  outputs = { nixpkgs, bydlo, ... }: {
    nixosConfigurations.<имя-хоста> = nixpkgs.lib.nixosSystem {
      modules = [
        bydlo.nixosModules.default
        { programs.bydlo.enable = true; }
      ];
    };
  };
}
```

Также доступны `overlays.default` (добавляет `pkgs.bydlo`) и пакет `bydlo-minimal` без движков из nixpkgs.

Как устроен пакет:

- Приложение собирается `buildDotnetModule` из исходников, зависимости NuGet закреплены в `nix/deps.json`.
  Это framework-dependent сборка на .NET 10 из nixpkgs, без trimming и ReadyToRun.
- Обёртка `BYDLO` (и ссылка `bydlo`) добавляет в `LD_LIBRARY_PATH` библиотеки, которые Avalonia, SkiaSharp
  и libsecret загружают во время работы: X11, fontconfig, freetype, libGL, libsecret, glib.
  Без этого на NixOS приложение падает при старте (`libfontconfig.so.1: cannot open shared object file`).
- AmneziaWG: хост `bydlo-amneziawg` собирается из `tools/amneziawg` с теми же `go.mod`/`go.sum`, что и релиз,
  и кладётся в `engines/amneziawg/<версия>/` как встроенный движок.
- sing-box, Xray, OpenVPN и WireGuard (`wg-quick`) берутся из nixpkgs через `PATH`. Они используются, только
  если в настройках не указан свой путь и не установлена закреплённая версия через «Инструменты → Движки».
  Версии nixpkgs могут отличаться от `engines/engines.lock.json` (например, OpenVPN 2.6 вместо 2.7).
- Zapret (nfqws) ставится из интерфейса: закреплённый архив bol-van со статическими бинарниками и проверкой SHA-256.
- Автозапуск и ассоциации импорта регистрируют путь, через который запущена обёртка (обычно
  `/run/current-system/sw/bin/BYDLO`), а не путь в `/nix/store`: после обновления и сборки мусора он не ломается.
- Привилегированная служба (`Bydlo.Helper`) в пакет Nix не входит: она устанавливается в `/usr/local/lib`
  и `/etc/systemd/system`, что не подходит для NixOS. Кнопка установки службы сообщит, что её нет в этой сборке.
  Для TUN и Zapret запускайте BYDLO с правами root.
- Секреты профилей хранятся в Secret Service (libsecret): нужен запущенный GNOME Keyring, KWallet или аналог.
- Обновляет установку Nix, а не BYDLO: `nix profile upgrade` для профиля пользователя или пересборка
  конфигурации NixOS. Встроенное автообновление в такой установке отключено, флажок в настройках
  не показывается. Каталог данных `~/.local/share/bydlo` при обновлении не меняется.

### Разработка на NixOS

```bash
nix develop
```

Оболочка содержит .NET SDK 10, Go, PowerShell, `dpkg` и `squashfs-tools`, а также задаёт `LD_LIBRARY_PATH`
для `dotnet run` и пути к `protoc`/`grpc_csharp_plugin` из nixpkgs (бинарники из NuGet-пакета `Grpc.Tools`
без nix-ld не запускаются). Дальше — обычные команды из [BUILD.md](BUILD.md).

После изменения версий пакетов в `Directory.Packages.props` обновите `nix/deps.json`:

```bash
bash scripts/update-nix-deps.sh
```

Задача CI **Nix flake** проверяет, что flake собирается.

## Debian, Ubuntu и производные (.deb)

Скачайте `bydlo_<версия>_amd64.deb` (или `_arm64.deb`) из релиза и установите:

```bash
sudo apt install ./bydlo_<версия>_amd64.deb
```

Приложение ставится в `/opt/bydlo`, команда — `bydlo`, ярлык появляется в меню. Пакет объявляет зависимости
X11, fontconfig, ICU и OpenSSL; libsecret и libnotify рекомендуются. Удаление: `sudo apt remove bydlo`.
Данные пользователя (`~/.local/share/bydlo`) при удалении не трогаются.

Обновление выполняет пакетный менеджер. Скачайте новый `.deb` из релиза и установите его той же
командой `sudo apt install ./bydlo_<версия>_amd64.deb`: `apt` заменит файлы в `/opt/bydlo`.
Репозиторий `apt` для BYDLO не публикуется, поэтому `apt upgrade` и `apt update` новые версии
не находят — отдельно скачанный `.deb` не является настроенным репозиторием.
Встроенное автообновление в `.deb`-установке отключено и не пишет в файлы, которыми управляет
`dpkg`; флажок «Автоматически обновлять BYDLO» в настройках не показывается, вместо него
приложение объясняет ручную установку. Настройки, профили и подписки лежат в `~/.local/share/bydlo`
и переживают замену пакета.

## AppImage

```bash
chmod +x BydloDesktop-linux-x64.AppImage
./BydloDesktop-linux-x64.AppImage
```

Для ARM64 выберите `BydloDesktop-linux-arm64.AppImage`. Нужны доступный `/dev/fuse`
и подходящий `fusermount` в `PATH`; конкретная настройка FUSE зависит от дистрибутива.
На NixOS включите `programs.appimage = { enable = true; binfmt = true; };`
или запускайте через `appimage-run`; для NixOS всё же удобнее flake. Если в системе нет ICU,
AppImage запускается в инвариантном режиме глобализации (форматы дат и чисел как в invariant culture,
строки интерфейса не меняются). Автозапуск регистрирует сам файл `.AppImage`, а не временную точку монтирования:
не перемещайте файл после включения автозапуска.

В AppImage Velopack доступно автообновление с явным включением в настройках
(см. [BUILD.md](BUILD.md#нативные-пакеты-и-подпись-215)). Начиная с v0.4.2 это
единственная публикуемая семья AppImage. Прежние
`BYDLO-<версия>-x86_64.AppImage` / `-aarch64.AppImage` остаются в старых релизах.

Различия проверены по исходникам Velopack 1.2.161 и чтением контейнеров, без запуска:

- Прежний упаковщик использовал type2-runtime `20251108` и SquashFS zstd;
  Velopack использует свои закреплённые vendor runtimes и по умолчанию SquashFS gzip.
  Это разные runtime, поэтому одинаковое поведение FUSE по расширению не предполагается.
  В [vendor runtime x64](https://github.com/velopack/velopack/blob/1.2.161/vendor/appimagekit-runtime-x86_64)
  присутствует встроенная libfuse и поиск `fusermount`; зависимость от одной лишь
  системной `libfuse.so.2` не заявляется. Фактический запуск x64/ARM64 проверяется вручную в #402.
- Приложение находится в `usr/bin`, вместо прежнего `usr/lib/bydlo`; сохраняется наш
  `AppRun` с ICU fallback. Desktop entry запускает `BYDLO`, использует иконку
  `BydloDesktop` и `StartupWMClass=BYDLO`. Автозапуск использует `APPIMAGE` — путь
  к самому файлу. AppImage не устанавливает ярлык в системное меню автоматически.
- CI извлекает SquashFS через `unsquashfs`, проверяет launcher, `.desktop`, иконку,
  права исполнения AppRun, приложения, helper и UpdateNix, а также комплект движков.
  AppImage, установщики и движки при проверке не исполняются. X11, fontconfig,
  OpenSSL и остальные внешние зависимости приложения по-прежнему нужны.

Построение AppImage описано в исходниках
[Velopack AppImageTool](https://github.com/velopack/velopack/blob/1.2.161/src/vpk/Velopack.Packaging.Unix/AppImageTool.cs)
и [LinuxPackCommandRunner](https://github.com/velopack/velopack/blob/1.2.161/src/vpk/Velopack.Packaging.Unix/Commands/LinuxPackCommandRunner.cs).

## Arch Linux (AUR)

В `packaging/arch/` три пакета, готовых для AUR (у каждого есть `.SRCINFO`):

| Пакет | Источник |
|---|---|
| `bydlo-bin` | `.deb` из релиза GitHub (amd64 для x86_64, arm64 для aarch64), только чтение архива |
| `bydlo` | Исходники тега `v<pkgver>` (архив с SHA-256), сборка как в CI |
| `bydlo-git` | Последний коммит ветки по умолчанию, версия из `git describe` |

`bydlo` и `bydlo-git` собирают самодостаточное приложение и однофайловую привилегированную службу,
скачивают закреплённые sing-box/Xray/Zapret через `tools/Bydlo.BundleEngines` (только загрузка и проверка SHA-256)
и собирают хост AmneziaWG из `tools/amneziawg`. OpenVPN берётся из пакета Arch (`optdepends`).
Нужны `dotnet-sdk`, `go` и `jq`; при сборке требуется сеть (NuGet, модули Go, архивы движков).
Пакеты конфликтуют друг с другом и ставят одинаковые файлы: `/opt/bydlo`, `/usr/bin/bydlo`, ярлык и иконку.

Установка из checkout:

```bash
cd packaging/arch/bydlo-bin   # или bydlo, bydlo-git
makepkg -si
```

Бинарный комплект .NET нельзя обрабатывать `strip`, поэтому в PKGBUILD указано `options=('!strip' '!debug')`.

Обновление выполняет `pacman`: пересоберите пакет из AUR (`yay -Syu bydlo-bin` или
`makepkg -si` в обновлённом checkout). Встроенное автообновление в установке из `/opt/bydlo`
отключено, флажок в настройках не показывается, вместо него приложение объясняет обновление
через пакетный менеджер. Каталог данных `~/.local/share/bydlo` при обновлении пакета не меняется.

### Публикация в AUR

Нужна учётная запись AUR с SSH-ключом. Для каждого пакета:

```bash
git clone ssh://aur@aur.archlinux.org/bydlo-bin.git aur-bydlo-bin
cp packaging/arch/bydlo-bin/{PKGBUILD,.SRCINFO} aur-bydlo-bin/
cd aur-bydlo-bin
git add PKGBUILD .SRCINFO
git commit -m "Update to 0.4.0"
git push
```

При выпуске новой версии в `bydlo` и `bydlo-bin` обновите `pkgver`, сбросьте `pkgrel=1`,
выполните `updpkgsums` и `makepkg --printsrcinfo > .SRCINFO`. `bydlo-git` обновлять не нужно,
кроме изменений самого процесса сборки. В строке `# Maintainer:` укажите сопровождающего пакета в AUR.

## Сборка пакетов

`.deb` и AppImage создаются из проверенного каталога публикации (см. [BUILD.md](BUILD.md#публикация)):

```bash
source scripts/env.sh
bash scripts/package-linux.sh linux-x64 artifacts/linux-x64 artifacts/linux-packages        # оба формата
bash scripts/package-linux.sh linux-arm64 artifacts/linux-arm64 artifacts/linux-packages deb
```

Нужны `dpkg-deb` и `mksquashfs` (`squashfs-tools`). Скрипт не запускает ни приложение, ни движки:
AppImage собирается как закреплённый type2-runtime `20251108` (SHA-256 в скрипте, подпись проверена ключом
`570C77ACEA40C0F1B758902CBF96CCA56490F695`) и образ SquashFS. Для упаковки готового релиза другой версии
задайте `BYDLO_VERSION`. В CI задача `package` вызывает этот скрипт **только с `deb`**
для x64 и ARM64. AppImage для релиза создаёт только Velopack; локальный старый упаковщик
остаётся доступен для воспроизведения прежних пакетов. Суммы и attestations покрывают
окончательные плоские assets релиза, а не вложенные CI-каталоги.
