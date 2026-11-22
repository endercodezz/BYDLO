#!/usr/bin/env bash
# Builds a signed apt repository from already verified .deb packages (issue #488).
# Usage: scripts/build-apt-repository.sh <deb-dir> <output-dir> [--suite <name>] [--component <name>]
#        [--origin <name>] [--label <name>] [--key <fingerprint>] [--extra-key <fingerprint>]...
# --extra-key publishes an additional public key in bydlo.gpg, so a key rotation can ship both keys
# for one transition release while only the new key signs.
# The output directory is a complete static site: pool/, dists/<suite>/ with Packages, Release,
# Release.gpg and InRelease, the exported public key bydlo.gpg and a small index.html.
# Signing is mandatory: an unsigned repository would make apt refuse the source.
# Needs dpkg-deb, dpkg-scanpackages (dpkg-dev) and gpg. Never installs, runs or unpacks a package.
set -euo pipefail

# shellcheck source=scripts/env.sh
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"

die() { echo "build-apt-repository: $*" >&2; exit 1; }

[[ $# -ge 2 ]] || die "usage: $0 <deb-dir> <output-dir> [--suite <name>] [--component <name>] [--origin <name>] [--label <name>] [--key <fingerprint>] [--extra-key <fingerprint>]..."
input=$(cd "$1" && pwd) || die "input directory not found: $1"
mkdir -p "$2"
output=$(cd "$2" && pwd)
[[ $output != / ]] || die "refusing to use the filesystem root as the output directory"
shift 2

suite=stable
component=main
origin=BYDLO
label=BYDLO
key=
extra_keys=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --suite) suite=${2:?--suite needs a value}; shift 2 ;;
        --component) component=${2:?--component needs a value}; shift 2 ;;
        --origin) origin=${2:?--origin needs a value}; shift 2 ;;
        --label) label=${2:?--label needs a value}; shift 2 ;;
        --key) key=${2:?--key needs a value}; shift 2 ;;
        --extra-key) extra_keys+=("${2:?--extra-key needs a value}"); shift 2 ;;
        *) die "unknown argument: $1" ;;
    esac
done

# Suite and component become directory names and appear in Release, so keep them to a safe alphabet.
[[ $suite =~ ^[a-z0-9][a-z0-9._-]*$ ]] || die "invalid suite name: $suite"
[[ $component =~ ^[a-z0-9][a-z0-9._-]*$ ]] || die "invalid component name: $component"

for tool in dpkg-deb dpkg-scanpackages gpg gpgv; do
    command -v "$tool" > /dev/null || die "$tool not found (install dpkg-dev and gnupg)"
done

shopt -s nullglob
debs=("$input"/*.deb)
shopt -u nullglob
[[ ${#debs[@]} -gt 0 ]] || die "no .deb files in $input"

# A stale index would keep advertising packages that are no longer in the pool.
rm -rf "$output/dists" "$output/pool"
install -d "$output/dists/$suite" "$output/pool/$component"

declare -A seen=()
declare -A overrides=()
architectures=()
for deb in "${debs[@]}"; do
    package=$(dpkg-deb -f "$deb" Package)
    version=$(dpkg-deb -f "$deb" Version)
    arch=$(dpkg-deb -f "$deb" Architecture)
    [[ $package =~ ^[a-z0-9][a-z0-9+.-]+$ ]] || die "invalid Package field in $(basename "$deb"): '$package'"
    [[ -n $version ]] || die "missing Version field in $(basename "$deb")"
    [[ $arch =~ ^[a-z0-9][a-z0-9-]*$ ]] || die "invalid Architecture field in $(basename "$deb"): '$arch'"
    # Two files for one version and architecture would make Packages ambiguous for apt.
    [[ -z ${seen["$package $version $arch"]:-} ]] ||
        die "duplicate package $package $version $arch: $(basename "$deb") and ${seen["$package $version $arch"]}"
    seen["$package $version $arch"]=$(basename "$deb")
    if [[ ! " ${architectures[*]-} " == *" $arch "* ]]; then
        architectures+=("$arch")
    fi
    # An explicit override keeps dpkg-scanpackages quiet and the Section/Priority stable.
    # The override format is "package priority section", not the control field order.
    section=$(dpkg-deb -f "$deb" Section 2> /dev/null || true)
    priority=$(dpkg-deb -f "$deb" Priority 2> /dev/null || true)
    overrides["$package"]="$package ${priority:-optional} ${section:-misc}"
    pool="$output/pool/$component/${package:0:1}/$package"
    install -d "$pool"
    install -m 0644 "$deb" "$pool/"
done

override_file=$(mktemp)
trap 'rm -f "$override_file"' EXIT
printf '%s\n' "${overrides[@]}" | LC_ALL=C sort > "$override_file"

# dpkg-scanpackages writes Filename relative to the directory it scans, so scan from the output root.
for arch in "${architectures[@]}"; do
    index="$output/dists/$suite/$component/binary-$arch"
    install -d "$index"
    ( cd "$output" && dpkg-scanpackages --multiversion --arch "$arch" "pool/$component" "$override_file" ) > "$index/Packages"
    [[ -s $index/Packages ]] || die "no packages indexed for architecture $arch"
    gzip -9 -n -c "$index/Packages" > "$index/Packages.gz"
    cat > "$index/Release" <<EOF
Archive: $suite
Origin: $origin
Label: $label
Component: $component
Architecture: $arch
EOF
done

# Release lists every index with its size and checksum; apt refuses a file that does not match.
release_dir="$output/dists/$suite"
checksums() {
    local algorithm=$1 file
    ( cd "$release_dir" && find . -type f ! -name Release ! -name Release.gpg ! -name InRelease | LC_ALL=C sort ) |
        while read -r file; do
            printf ' %s %s %s\n' "$("$algorithm" "$release_dir/$file" | cut -d' ' -f1)" \
                "$(stat -c %s "$release_dir/$file")" "${file#./}"
        done
}
{
    echo "Origin: $origin"
    echo "Label: $label"
    echo "Suite: $suite"
    echo "Codename: $suite"
    echo "Architectures: ${architectures[*]}"
    echo "Components: $component"
    echo "Description: $origin apt repository"
    echo "Date: $(LC_ALL=C date -u '+%a, %d %b %Y %H:%M:%S UTC')"
    echo "MD5Sum:"
    checksums md5sum
    echo "SHA256:"
    checksums sha256sum
} > "$release_dir/Release"

if [[ -z $key ]]; then
    mapfile -t keys < <(gpg --batch --with-colons --list-secret-keys | awk -F: '/^fpr:/ {print $10}')
    [[ ${#keys[@]} -eq 1 ]] ||
        die "expected exactly one secret key in GNUPGHOME, found ${#keys[@]}; pass --key <fingerprint>"
    key=${keys[0]}
fi

# Clearsigned InRelease is what modern apt reads; Release.gpg stays for older clients.
gpg --batch --yes --local-user "$key" --digest-algo SHA256 \
    --clearsign --output "$release_dir/InRelease" "$release_dir/Release"
gpg --batch --yes --local-user "$key" --digest-algo SHA256 \
    --armor --detach-sign --output "$release_dir/Release.gpg" "$release_dir/Release"
gpg --batch --verify "$release_dir/InRelease" > /dev/null 2>&1 || die "the new InRelease does not verify"

# Users install this file as /etc/apt/keyrings/bydlo.gpg and pin it with Signed-By.
# apt verifies with gpgv, which reads only a binary keyring; the armored copy is for humans.
# Extra keys are published but never sign, so a rotation can keep the old key trusted for a while.
gpg --batch --yes --export "$key" ${extra_keys[@]+"${extra_keys[@]}"} > "$output/bydlo.gpg"
gpg --batch --yes --armor --export "$key" ${extra_keys[@]+"${extra_keys[@]}"} > "$output/bydlo.gpg.asc"
for published in "$key" ${extra_keys[@]+"${extra_keys[@]}"}; do
    gpg --batch --with-colons --fingerprint "$published" | awk -F: '/^fpr:/ {print $10; exit}'
done > "$output/bydlo.gpg.fingerprint"
gpgv --keyring "$output/bydlo.gpg" "$release_dir/InRelease" > /dev/null 2>&1 ||
    die "the exported keyring does not verify InRelease"

cat > "$output/index.html" <<EOF
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>BYDLO apt repository</title>
</head>
<body>
<h1>BYDLO apt repository</h1>
<p>Подписанный репозиторий apt для Debian, Ubuntu и производных. Пакеты: amd64 и arm64.</p>
<ul>
<li><a href="bydlo.gpg">bydlo.gpg</a> — открытый ключ подписи репозитория (бинарный, для /etc/apt/keyrings).</li>
<li><a href="bydlo.gpg.asc">bydlo.gpg.asc</a> — тот же ключ в ASCII-броне для проверки отпечатка.</li>
<li><a href="bydlo.gpg.fingerprint">bydlo.gpg.fingerprint</a> — отпечаток ключа.</li>
<li><a href="dists/$suite/InRelease">dists/$suite/InRelease</a> — подписанный индекс.</li>
</ul>
<p>Инструкция подключения: <a href="https://github.com/endercodezz/BYDLO/blob/main/docs/LINUX.md">docs/LINUX.md</a>.</p>
</body>
</html>
EOF

echo "apt repository: $output"
echo "suite: $suite, component: $component, architectures: ${architectures[*]}"
echo "signing key: $key"
