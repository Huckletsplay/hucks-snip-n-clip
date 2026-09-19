#!/usr/bin/env bash
# Creates a local code signing identity, once per Mac, so macOS stops treating every rebuild of
# Huck's Snip 'n' Clip as a brand-new app.
#
# WHY THIS EXISTS
# macOS ties a permission like Screen Recording to an app's identity. With no signing identity that
# identity is a fingerprint of the exact binary, so any rebuild looks like different software and
# the permission has to be granted all over again. A signing certificate gives the app a stable
# identity instead, and the permission then survives every rebuild.
#
# The certificate this makes is self-made. Only this Mac trusts it, which is all that is needed
# here: it is for development comfort, not for handing the app to other people. Giving the app to
# someone else needs a certificate rented from Apple through the paid Developer Program.
#
# Run once:  bash setup-signing.sh
# Undo:      bash setup-signing.sh --remove
set -euo pipefail

IDENTITY_NAME="${HUCKS_SNIP_N_CLIP_SIGN_IDENTITY:-Huck's Snip 'n' Clip Local Signing}"
KEYCHAIN="$HOME/Library/Keychains/login.keychain-db"

if [ "${1:-}" = "--remove" ]; then
    echo "Removing \"$IDENTITY_NAME\" from your login keychain..."
    security delete-identity -c "$IDENTITY_NAME" "$KEYCHAIN" 2>/dev/null || true
    echo "Done. Builds fall back to the unsigned form, and macOS will ask for permission again"
    echo "after each rebuild."
    exit 0
fi

if security find-identity -v -p codesigning 2>/dev/null | grep -qF "$IDENTITY_NAME"; then
    echo "\"$IDENTITY_NAME\" already exists. Nothing to do."
    echo "Rebuild with: bash build.sh"
    exit 0
fi

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

# The private key is generated here, handed to the keychain, and deleted with this folder when the
# script exits. It is never written into the project and never reaches git.
cat > "$WORK_DIR/openssl.cnf" <<'CONFIG'
[ req ]
distinguished_name = dn
prompt = no
x509_extensions = codesign_ext

[ dn ]
CN = REPLACED_AT_RUNTIME

[ codesign_ext ]
basicConstraints = critical,CA:false
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
CONFIG

python3 - "$WORK_DIR/openssl.cnf" "$IDENTITY_NAME" <<'PY'
import sys
path, name = sys.argv[1], sys.argv[2]
text = open(path, encoding='utf-8').read().replace('REPLACED_AT_RUNTIME', name)
open(path, 'w', encoding='utf-8').write(text)
PY

echo "Creating the certificate..."
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
    -keyout "$WORK_DIR/key.pem" -out "$WORK_DIR/cert.pem" \
    -config "$WORK_DIR/openssl.cnf" 2>/dev/null

# The bundle is given a real password rather than an empty one. OpenSSL and Apple's keychain
# importer disagree about how an empty password is represented, and the disagreement surfaces as
# "MAC verification failed during PKCS12 import (wrong password?)" - which reads like a wrong
# password and is not one. This bit us on 2026-09-05. The password exists only inside this script
# and dies with the temporary folder.
#
# -legacy keeps the contents in the older ciphers Apple accepts, and already produces the SHA-1
# checksum it wants; -macalg sha1 states that explicitly rather than relying on the default.
BUNDLE_PASSWORD="$(openssl rand -hex 16)"
BUNDLE_READY=no

if openssl pkcs12 -export -legacy -macalg sha1 \
        -inkey "$WORK_DIR/key.pem" -in "$WORK_DIR/cert.pem" \
        -name "$IDENTITY_NAME" -out "$WORK_DIR/identity.p12" \
        -passout "pass:$BUNDLE_PASSWORD" 2>/dev/null; then
    BUNDLE_READY=yes
fi

echo
echo "macOS will now ask for your Mac password, possibly more than once."
echo "That is macOS confirming you meant to add something to your keychain. It is expected."
echo "You will not see the characters as you type. That is normal."
echo

IMPORTED=no

if [ "$BUNDLE_READY" = yes ]; then
    if security import "$WORK_DIR/identity.p12" -k "$KEYCHAIN" -P "$BUNDLE_PASSWORD" \
            -T /usr/bin/codesign -T /usr/bin/security 2>"$WORK_DIR/import.log"; then
        IMPORTED=yes
    else
        echo "The bundled import did not take. Falling back to importing the two pieces separately."
        sed 's/^/  /' "$WORK_DIR/import.log" >&2 2>/dev/null || true
        echo
    fi
fi

if [ "$IMPORTED" = no ]; then
    # No bundle, no checksum to disagree about: hand over the key and the certificate as two plain
    # files and let the keychain pair them up itself.
    security import "$WORK_DIR/key.pem" -k "$KEYCHAIN" -t priv -f openssl \
        -T /usr/bin/codesign -T /usr/bin/security
    security import "$WORK_DIR/cert.pem" -k "$KEYCHAIN" -t cert -f openssl \
        -T /usr/bin/codesign -T /usr/bin/security
fi

# Trust it for signing code, and only for signing code.
security add-trusted-cert -r trustRoot -p codeSign -k "$KEYCHAIN" "$WORK_DIR/cert.pem"

# Let the signing tool use the key without a prompt on every single build.
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "" "$KEYCHAIN" >/dev/null 2>&1 || true

echo
if security find-identity -v -p codesigning 2>/dev/null | grep -qF "$IDENTITY_NAME"; then
    echo "Done. \"$IDENTITY_NAME\" is ready."
    echo
    echo "Next:"
    echo "  1. bash build.sh"
    echo "  2. bash install.sh"
    echo "  3. Turn on Screen Recording one last time."
    echo
    echo "After that, rebuilds keep the permission."
else
    echo "The certificate went in, but macOS does not report it as usable for signing yet." >&2
    echo >&2
    echo "Open Keychain Access, pick \"login\" on the left, find" >&2
    echo "\"$IDENTITY_NAME\", double-click it, expand Trust, and set" >&2
    echo "Code Signing to \"Always Trust\". Then run this script again to check." >&2
    exit 1
fi
