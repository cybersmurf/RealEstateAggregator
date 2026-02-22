#!/bin/bash

# RealEstate Export - Manual CLI Demo
# Simple export utan EF Core dependency issues

set -e

LISTING_ID="$1"
FORMAT="${2:-markdown}"
OUTPUT="${3:-./exports}"

if [ -z "$LISTING_ID" ]; then
  echo "❌ Usage: ./export.sh <listing-id> [format] [output-dir]"
  exit 1
fi

# Create output directory
mkdir -p "$OUTPUT"

# Query database directly with psql
LISTING=$(docker exec realestate-db psql -U postgres -d realestate_dev -t -A -F'|' -c \
  "SELECT id, source_code, title, description, price, area_built_up, rooms, location_text, region FROM re_realestate.listings WHERE id = '$LISTING_ID';")

if [ -z "$LISTING" ]; then
  echo "❌ Inzerát s ID $LISTING_ID nenalezen"
  exit 1
fi

# Parse data
IFS='|' read -r ID SOURCE TITLE DESC PRICE AREA ROOMS LOCATION REGION <<< "$LISTING"

# Create filename
FILENAME=$(echo "$TITLE" | tr ' ' '_' | cut -c1-40)

case "$FORMAT" in
  markdown)
    FILEPATH="$OUTPUT/${FILENAME}.md"
    cat > "$FILEPATH" << MDEOF
# $TITLE

## 📋 Metadata

| Parametr | Hodnota |
|----------|---------|
| **ID** | \`$ID\` |
| **Zdroj** | $SOURCE |
| **Region** | $REGION |
| **Lokalita** | $LOCATION |

## 💰 Cena a Plocha

| Parametr | Hodnota |
|----------|---------|
| **Cena** | $PRICE Kč |
| **Plocha** | $AREA m² |
| **Pokoje** | $ROOMS |

## 📝 Popis

$DESC

## 🔗 Zdrojový odkaz

Zdroj: **$SOURCE**

---

*Export vytvořeno: $(date)*
MDEOF
    ;;
  *)
    echo "❌ Neznámý formát: $FORMAT"
    exit 1
    ;;
esac

echo "✅ Exportováno: $FILEPATH"
