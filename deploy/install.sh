#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# PCL-CMU admin backend — install/upgrade helper for Debian 12.
#
# Run as root, from the deployment directory that contains:
#     publish/   (the self-contained linux-x64 publish output)
#     deploy/    (this script + pcl-cmu.service)
#     data/      (seed instances.json, optional)
#     wwwroot/   (seed static resources, optional)
#
# Layout created on the server:
#     /opt/pcl-cmu/app      published app + appsettings.json
#     /opt/pcl-cmu/data     instances.json
#     /opt/pcl-cmu/wwwroot  manifest/announcements/friends/update/packs
#     /opt/pcl-cmu/certs    cert.pem + key.pem (self-signed, generated here)
#     /opt/pcl-cmu/keys     manifest-signing-key.pem (operator must supply)
#     /opt/pcl-cmu/admin.env  PCL_CMU_ADMIN_PASSWORD_HASH
# ---------------------------------------------------------------------------
set -euo pipefail

APP_DIR=/opt/pcl-cmu/app
DATA_DIR=/opt/pcl-cmu/data
WWW_DIR=/opt/pcl-cmu/wwwroot
CERTS_DIR=/opt/pcl-cmu/certs
KEYS_DIR=/opt/pcl-cmu/keys
ENV_FILE=/opt/pcl-cmu/admin.env
SERVICE_FILE=/etc/systemd/system/pcl-cmu.service

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"

echo "==> Creating directories"
mkdir -p "$APP_DIR" "$DATA_DIR" "$WWW_DIR" "$CERTS_DIR" "$KEYS_DIR"

echo "==> Installing published app"
if [ ! -d "$ROOT_DIR/publish" ]; then
  echo "ERROR: $ROOT_DIR/publish not found." >&2
  echo "       Build it first:" >&2
  echo "         dotnet publish -c Release -r linux-x64 --self-contained true -o publish" >&2
  exit 1
fi
cp -r "$ROOT_DIR/publish/." "$APP_DIR/"

echo "==> Seeding data (existing files are preserved)"
if [ -f "$ROOT_DIR/data/instances.json" ] && [ ! -f "$DATA_DIR/instances.json" ]; then
  cp "$ROOT_DIR/data/instances.json" "$DATA_DIR/instances.json"
fi
if [ -d "$ROOT_DIR/wwwroot" ]; then
  cp -rn "$ROOT_DIR/wwwroot/." "$WWW_DIR/"
fi

echo "==> Generating self-signed certificate (if missing)"
if [ ! -f "$CERTS_DIR/cert.pem" ] || [ ! -f "$CERTS_DIR/key.pem" ]; then
  openssl req -x509 -newkey rsa:2048 \
    -keyout "$CERTS_DIR/key.pem" \
    -out "$CERTS_DIR/cert.pem" \
    -days 3650 -nodes \
    -subj "/CN=example.com" \
    -addext "subjectAltName=DNS:example.com"
  chmod 600 "$CERTS_DIR/key.pem"
  chmod 644 "$CERTS_DIR/cert.pem"
fi

echo "==> Requiring manifest signing key"
if [ ! -f "$KEYS_DIR/manifest-signing-key.pem" ]; then
  echo "ERROR: missing $KEYS_DIR/manifest-signing-key.pem" >&2
  echo "       Copy the manifest signing private key there and chmod 600 it." >&2
  echo "       Do NOT commit this key to source control or the release zip." >&2
  exit 1
fi
chmod 600 "$KEYS_DIR/manifest-signing-key.pem"

echo "==> Preparing admin.env (edit the password hash!)"
if [ ! -f "$ENV_FILE" ]; then
  {
    echo "# Set this to the output of:"
    echo "#   /opt/dotnet/dotnet $APP_DIR/PclCmu.Admin.dll --hash-password 'your-strong-password'"
    echo "#PCL_CMU_ADMIN_PASSWORD_HASH=pbkdf2\$100000\$REPLACE_ME"
  } > "$ENV_FILE"
  chmod 600 "$ENV_FILE"
  echo "WARNING: $ENV_FILE was created without a real password hash."
  echo "         Generate one and set PCL_CMU_ADMIN_PASSWORD_HASH before first login."
fi

echo "==> Installing and enabling systemd service"
install -m 644 "$SCRIPT_DIR/pcl-cmu.service" "$SERVICE_FILE"
systemctl daemon-reload
systemctl enable pcl-cmu.service
systemctl restart pcl-cmu.service

echo
echo "Done."
echo "  Admin UI : http://127.0.0.1:9443/admin  (loopback only)"
echo "  Public    : https://example.com/manifest.json"
echo "  Service   : systemctl status pcl-cmu"
echo
echo "Remember to set PCL_CMU_ADMIN_PASSWORD_HASH in $ENV_FILE and restart:"
echo "  systemctl restart pcl-cmu"
