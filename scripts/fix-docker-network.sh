#!/usr/bin/env bash
# Install persistent Docker outbound networking (DOCKER-USER iptables via systemd).
# Run once on prod server after git pull; survives reboot automatically.
#
# Problem: docker-iptables.service flushed DOCKER-USER on boot but only whitelisted
# 172.17/172.22/172.23 – realestate compose network (172.18) was blocked.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UNIT_SRC="${SCRIPT_DIR}/systemd/docker-iptables.service"
UNIT_DST="/etc/systemd/system/docker-iptables.service"
LEGACY_FIX_UNIT="/etc/systemd/system/docker-iptables-fix.service"

if [[ ! -f "$UNIT_SRC" ]]; then
  echo "Missing ${UNIT_SRC}" >&2
  exit 1
fi

echo "Installing ${UNIT_DST} ..."
sudo cp "$UNIT_SRC" "$UNIT_DST"

# Legacy unit inserted ACCEPT after DROP – never worked, disable it.
if [[ -f "$LEGACY_FIX_UNIT" ]]; then
  echo "Disabling broken docker-iptables-fix.service ..."
  sudo systemctl disable --now docker-iptables-fix.service 2>/dev/null || true
fi

sudo systemctl daemon-reload
sudo systemctl enable docker-iptables.service
sudo systemctl restart docker-iptables.service

echo ""
echo "DOCKER-USER chain:"
sudo iptables -L DOCKER-USER -n -v

echo ""
echo "Quick connectivity test from realestate-scraper (if running):"
if sudo docker ps --format '{{.Names}}' | grep -qx 'realestate-scraper'; then
  sudo docker exec realestate-scraper python3 -c \
    "import httpx; r=httpx.get('https://reality.bazos.cz/', timeout=15); print('bazos', r.status_code)"
else
  echo "  (realestate-scraper not running – skip)"
fi

echo ""
echo "Done. Rules persist across reboot via docker-iptables.service."
