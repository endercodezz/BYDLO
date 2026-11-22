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

Обновление выполняет пакетный менеджер. Если репозиторий BYDLO подключён (см. следующий раздел),
достаточно `sudo apt update && sudo apt upgrade`. Без репозитория скачайте новый `.deb` из релиза
и установите его той же командой `sudo apt install ./bydlo_<версия>_amd64.deb`: `apt` заменит файлы
в `/opt/bydlo`, но `apt upgrade` сам новые версии не найдёт — отдельно скачанный `.deb`
не является настроенным репозиторием.
Встроенное автообновление в `.deb`-установке отключено и не пишет в файлы, которыми управляет
`dpkg`; флажок «Автоматически обновлять BYDLO» в настройках не показывается, вместо него
приложение объясняет, как обновляется эта установка: через `apt upgrade`, если файл источника apt
для BYDLO существует на машине, и вручную, если его нет. Настройки, профили и подписки лежат
в `~/.local/share/bydlo` и переживают замену пакета.

## Репозиторий apt: обновление через `apt upgrade`

Релизный workflow умеет собирать из проверенных `.deb` (amd64 и arm64) подписанный репозиторий
и публиковать его на GitHub Pages: `https://endercodezz.github.io/BYDLO/`.
Индексы `dists/stable/Release` и `dists/stable/InRelease` подписаны отдельным ключом репозитория;
это не ключ каталога обновлений из #411 и не ключ подписи Windows/macOS.

Состояние публикации: задача CI **Publish apt repository** выполняется только на теге `v*`
и только когда в секретах репозитория задан `APT_SIGNING_KEY`, а GitHub Pages включён
с источником «GitHub Actions». Пока ключ не добавлен, задача пропускается, репозиторий
не публикуется, и обновляться нужно вручную через `.deb` из релиза.

### Одноразовое подключение

Ключ кладётся в `/etc/apt/keyrings/bydlo.gpg`, источник — в `/etc/apt/sources.list.d/bydlo.sources`
с `Signed-By`. `apt-key` не используется: он добавляет ключ в общее доверенное хранилище для всех
репозиториев, а `Signed-By` разрешает ключу подписывать только этот репозиторий.

```bash
sudo install -d -m 0755 /etc/apt/keyrings
curl -fsSL -o /tmp/bydlo.gpg https://endercodezz.github.io/BYDLO/bydlo.gpg
gpg --show-keys --with-fingerprint /tmp/bydlo.gpg
sudo install -m 0644 /tmp/bydlo.gpg /etc/apt/keyrings/bydlo.gpg
```

`gpg --show-keys` печатает отпечаток ключа. Сверьте его с отпечатком из описания релиза
и из журнала задачи CI **Publish apt repository** (строка `apt repository signing key: ...`).
Скачивание ключа в файл — не `curl | sudo bash`: команда только сохраняет файл, а установка
выполняется отдельным `install`.

Файл источника:

```bash
sudo tee /etc/apt/sources.list.d/bydlo.sources > /dev/null <<'EOF'
Types: deb
URIs: https://endercodezz.github.io/BYDLO/
Suites: stable
Components: main
Architectures: amd64 arm64
Signed-By: /etc/apt/keyrings/bydlo.gpg
EOF
```

Затем:

```bash
sudo apt update
sudo apt install bydlo     # если пакет ещё не установлен
sudo apt upgrade
```

Проверка: `apt policy bydlo` показывает источник `https://endercodezz.github.io/BYDLO/ stable`
и установленную версию. Дальше обновление — обычные `sudo apt update && sudo apt upgrade`.

Не добавляйте `[trusted=yes]` и не отключайте проверку подписи: без `Signed-By` apt примет
любой ключ, которым подписан индекс.

### Отключение

```bash
sudo rm /etc/apt/sources.list.d/bydlo.sources /etc/apt/keyrings/bydlo.gpg
sudo apt update
```

BYDLO остаётся установленным, но снова обновляется вручную через `.deb` из релиза.

### Ротация ключа репозитория

Закрытая часть ключа живёт только в секрете CI `APT_SIGNING_KEY`; в репозитории и в пакетах
её нет. Ротация не должна ломать `apt update` у тех, кто уже подключил репозиторий, поэтому
старый ключ остаётся в опубликованном `bydlo.gpg` на один переходный релиз.

1. Создайте новый ключ без парольной фразы (CI не может её ввести) и выгрузите закрытую часть:
   `gpg --quick-generate-key 'BYDLO apt repository <адрес>' rsa4096 sign 5y`,
   затем `gpg --armor --export-secret-keys <новый отпечаток>`.
2. Замените секрет `APT_SIGNING_KEY` новым ключом.
3. Задайте переменную репозитория `APT_EXTRA_KEY` со старым отпечатком: задача CI передаст её
   в `scripts/build-apt-repository.sh` как `--extra-key`. Новый ключ подписывает индекс, старый
   остаётся в `bydlo.gpg` и `bydlo.gpg.fingerprint`.
4. Опубликуйте релиз. Пользователи, которые заменят файл ключа, получат оба ключа.
5. Через релиз удалите `APT_EXTRA_KEY`: старый ключ перестанет публиковаться. Отзывать его нужно
   только при подозрении на утечку (см. ниже).

Пользователю при ротации нужно один раз заменить файл ключа:

```bash
curl -fsSL -o /tmp/bydlo.gpg https://endercodezz.github.io/BYDLO/bydlo.gpg
gpg --show-keys --with-fingerprint /tmp/bydlo.gpg
sudo install -m 0644 /tmp/bydlo.gpg /etc/apt/keyrings/bydlo.gpg
sudo apt update
```

Пока файл ключа не заменён, `apt update` для этого источника завершается ошибкой подписи
(`NO_PUBKEY` или `BADSIG`). Это ожидаемое безопасное поведение: apt не принимает индекс,
подписанный ключом, которого нет в `Signed-By`.

### Отзыв ключа

Если закрытая часть ключа могла утечь, ротация с сохранением старого ключа не подходит:
скомпрометированный ключ нельзя оставлять доверенным.

1. Отзовите ключ сертификатом отзыва (`gpg --gen-revoke <отпечаток>`) и опубликуйте сертификат
   в описании релиза: так видно, что ключ больше не действителен.
2. Удалите секрет `APT_SIGNING_KEY` со старым ключом и задайте новый ключ, созданный по процедуре
   ротации. `APT_EXTRA_KEY` не задавайте: отозванный ключ в новый `bydlo.gpg` не попадает.
3. Выпустите релиз. В описании релиза укажите новый отпечаток и требование сразу заменить файл ключа.
4. Пользователь заменяет `/etc/apt/keyrings/bydlo.gpg` (команды выше) и выполняет `sudo apt update`.
   До замены `apt update` для источника BYDLO завершается ошибкой подписи: индекс подписан новым
   ключом, которого нет в `Signed-By`. Установленная версия продолжает работать.

Отзыв не удаляет установленный пакет и не меняет данные пользователя.

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
