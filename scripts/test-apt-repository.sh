#!/usr/bin/env bash
# Synthetic regression for scripts/build-apt-repository.sh (issue #488).
# Builds a repository from fake .deb files with a throwaway key generated inside this script, then
# checks the structure, the checksums and the signature. Never installs a package, never runs apt,
# never touches /etc and never uses the network.
set -euo pipefail

# shellcheck source=scripts/env.sh
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
        echo "apt repository regression skipped: needs dpkg-dev and gnupg (Linux only)"
        exit 0
        ;;
esac

for tool in dpkg-deb dpkg-scanpackages gpg gpgv sha256sum; do
    command -v "$tool" > /dev/null ||
        { echo "apt repository regression: $tool not found (install dpkg-dev and gnupg)" >&2; exit 1; }
done

fail() { echo "apt repository regression: $*" >&2; exit 1; }

fixture=$(mktemp -d "$BYDLO_ROOT/.tmp/apt-repository-XXXXXXXX")
trap 'rm -rf "$fixture"' EXIT

# A throwaway key: nothing here is trusted by any client and it never leaves the fixture.
export GNUPGHOME="$fixture/gnupg"
install -d -m 0700 "$GNUPGHOME"
gpg --batch --quiet --pinentry-mode loopback --passphrase '' \
    --quick-generate-key 'BYDLO apt regression <apt-regression@bydlo.invalid>' rsa3072 sign 1d

make_deb() {
    local arch=$1 version=$2 root="$fixture/deb-$1"
    install -d "$root/DEBIAN" "$root/opt/bydlo"
    printf 'synthetic payload for %s\n' "$arch" > "$root/opt/bydlo/BYDLO"
    cat > "$root/DEBIAN/control" <<EOF
Package: bydlo
Version: $version
Architecture: $arch
Maintainer: BYDLO regression <apt-regression@bydlo.invalid>
Section: net
Priority: optional
Description: synthetic package for the apt repository regression
EOF
    dpkg-deb --root-owner-group -Zxz --build "$root" "$fixture/debs/bydlo_${version}_${arch}.deb" > /dev/null
}

install -d "$fixture/debs"
make_deb amd64 0.4.3
make_deb arm64 0.4.3
# Anything that is not a .deb must be ignored instead of indexed.
printf 'not a package\n' > "$fixture/debs/README.txt"

bash "$BYDLO_ROOT/scripts/build-apt-repository.sh" "$fixture/debs" "$fixture/site"

release_dir="$fixture/site/dists/stable"
for file in "$release_dir/Release" "$release_dir/Release.gpg" "$release_dir/InRelease" \
    "$fixture/site/bydlo.gpg" "$fixture/site/bydlo.gpg.asc" "$fixture/site/bydlo.gpg.fingerprint"; do
    [[ -s $file ]] || fail "missing $(basename "$file")"
done

# Every index listed in Release must match its size and SHA-256.
release_checksums_ok() {
    local dir=$1
    ( cd "$dir" && awk '/^SHA256:/ {flag=1; next} /^[A-Za-z]/ {flag=0} flag && NF==3 {print $1"  "$3}' Release ) |
        ( cd "$dir" && sha256sum -c --strict --quiet - ) > /dev/null 2>&1
}
release_checksums_ok "$release_dir" || fail "Release checksums do not match the index files"

# apt verifies with gpgv against the installed keyring, so check exactly that path first.
gpgv --keyring "$fixture/site/bydlo.gpg" "$release_dir/InRelease" > /dev/null 2>&1 ||
    fail "InRelease does not verify with the published keyring"
gpgv --keyring "$fixture/site/bydlo.gpg" "$release_dir/Release.gpg" "$release_dir/Release" > /dev/null 2>&1 ||
    fail "Release.gpg does not verify with the published keyring"

# The armored copy must carry the same key, so a user can compare the fingerprint by hand.
export GNUPGHOME="$fixture/verify"
install -d -m 0700 "$GNUPGHOME"
gpg --batch --quiet --import "$fixture/site/bydlo.gpg.asc"
fingerprint=$(gpg --batch --with-colons --fingerprint | awk -F: '/^fpr:/ {print $10; exit}')
[[ $(cat "$fixture/site/bydlo.gpg.fingerprint") == "$fingerprint" ]] ||
    fail "bydlo.gpg.fingerprint does not match the published key"

for arch in amd64 arm64; do
    packages="$release_dir/main/binary-$arch/Packages"
    [[ -s $packages ]] || fail "missing Packages for $arch"
    [[ -s "${packages}.gz" ]] || fail "missing Packages.gz for $arch"
    grep -qx 'Package: bydlo' "$packages" || fail "Packages for $arch does not list bydlo"
    grep -qx "Architecture: $arch" "$packages" || fail "Packages for $arch has the wrong Architecture"
    grep -qx 'Version: 0.4.3' "$packages" || fail "Packages for $arch has the wrong Version"
    filename=$(awk '/^Filename:/ {print $2}' "$packages")
    [[ $filename == "pool/main/b/bydlo/bydlo_0.4.3_$arch.deb" ]] ||
        fail "Packages for $arch has an unexpected Filename: $filename"
    [[ -f "$fixture/site/$filename" ]] || fail "Packages for $arch points at a missing file: $filename"
    expected=$(awk '/^SHA256:/ {print $2}' "$packages")
    actual=$(sha256sum "$fixture/site/$filename" | cut -d' ' -f1)
    [[ $expected == "$actual" ]] || fail "Packages for $arch has a wrong SHA256 for $filename"
done

# The checks must be able to fail: a modified index and a modified signature are both rejected.
cp "$release_dir/main/binary-amd64/Packages" "$fixture/Packages.orig"
printf '\n' >> "$release_dir/main/binary-amd64/Packages"
if release_checksums_ok "$release_dir"; then fail "a modified Packages file was accepted"; fi
cp "$fixture/Packages.orig" "$release_dir/main/binary-amd64/Packages"
release_checksums_ok "$release_dir" || fail "restoring Packages did not restore the checksums"

sed 's/^Date: .*/Date: Thu, 01 Jan 1970 00:00:00 UTC/' "$release_dir/InRelease" > "$fixture/InRelease.tampered"
if gpg --batch --verify "$fixture/InRelease.tampered" > /dev/null 2>&1; then
    fail "a modified InRelease was accepted"
fi

# Rotation: the new key signs while the old key stays published for one transition release.
export GNUPGHOME="$fixture/gnupg"
gpg --batch --quiet --pinentry-mode loopback --passphrase '' \
    --quick-generate-key 'BYDLO apt regression next <apt-next@bydlo.invalid>' rsa3072 sign 1d
next=$(gpg --batch --with-colons --list-secret-keys 'BYDLO apt regression next' | awk -F: '/^fpr:/ {print $10; exit}')
previous=$(gpg --batch --with-colons --list-secret-keys 'BYDLO apt regression <' | awk -F: '/^fpr:/ {print $10; exit}')
bash "$BYDLO_ROOT/scripts/build-apt-repository.sh" "$fixture/debs" "$fixture/rotated" \
    --key "$next" --extra-key "$previous"
gpgv --keyring "$fixture/rotated/bydlo.gpg" "$fixture/rotated/dists/stable/InRelease" > /dev/null 2>&1 ||
    fail "the rotated keyring does not verify InRelease"
grep -qx "$previous" "$fixture/rotated/bydlo.gpg.fingerprint" || fail "the old key is missing from the rotated keyring"
grep -qx "$next" "$fixture/rotated/bydlo.gpg.fingerprint" || fail "the new key is missing from the rotated keyring"

# Two files for one version and architecture would make Packages ambiguous.
cp "$fixture/debs/bydlo_0.4.3_amd64.deb" "$fixture/debs/bydlo_0.4.3_amd64-copy.deb"
if bash "$BYDLO_ROOT/scripts/build-apt-repository.sh" "$fixture/debs" "$fixture/duplicate" > /dev/null 2>&1; then
    fail "a duplicate package version was accepted"
fi
rm "$fixture/debs/bydlo_0.4.3_amd64-copy.deb"

install -d "$fixture/empty"
if bash "$BYDLO_ROOT/scripts/build-apt-repository.sh" "$fixture/empty" "$fixture/empty-site" > /dev/null 2>&1; then
    fail "an input directory without .deb files was accepted"
fi

echo "apt repository regression passed: amd64 + arm64, signed Release/InRelease verified"
