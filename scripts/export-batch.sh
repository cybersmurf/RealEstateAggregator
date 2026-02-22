#!/bin/bash

# RealEstate Export Batch - Manual CLI Demo
# Export multiple listings based on filters

set -e

REGION="${1:-}"
LIMIT="${2:-10}"
FORMAT="${3:-markdown}"
OUTPUT="${4:-./exports}"

# Create output directory
mkdir -p "$OUTPUT"

# Query database directly via CSV to avoid formatting issues
if [ -z "$REGION" ]; then
  QUERY="COPY (SELECT id, title, source_code, price, area_built_up FROM re_realestate.listings ORDER BY first_seen_at DESC LIMIT $LIMIT) TO STDOUT WITH (FORMAT CSV, HEADER FALSE);"
else
  QUERY="COPY (SELECT id, title, source_code, price, area_built_up FROM re_realestate.listings WHERE region ILIKE '%$REGION%' ORDER BY first_seen_at DESC LIMIT $LIMIT) TO STDOUT WITH (FORMAT CSV, HEADER FALSE);"
fi

LISTINGS=$(docker exec realestate-db psql -U postgres -d realestate_dev -c "$QUERY")

if [ -z "$LISTINGS" ]; then
  echo "⚠️  Žádné inzeráty nenalezeny"
  exit 0
fi

# Count lines
LISTING_COUNT=$(echo "$LISTINGS" | grep -v '^$' | wc -l)

# Create batch filename
TIMESTAMP=$(date +%Y-%m-%d_%H%M%S)
FILEPATH="$OUTPUT/batch_export_${TIMESTAMP}.md"

case "$FORMAT" in
  markdown)
    cat > "$FILEPATH" << 'BATCH_HEADER'
# Real Estate Export - Batch Report

Vyexportováno z Real Estate Aggregator systému.

---

BATCH_HEADER

    COUNTER=1
    # Process CSV output
    echo "$LISTINGS" | while IFS=',' read -r ID TITLE SOURCE PRICE AREA; do
      [ -z "$ID" ] && continue
      # Remove quotes from CSV fields
      ID=$(echo "$ID" | tr -d '"')
      TITLE=$(echo "$TITLE" | tr -d '"')
      SOURCE=$(echo "$SOURCE" | tr -d '"')
      PRICE=$(echo "$PRICE" | tr -d '"')
      AREA=$(echo "$AREA" | tr -d '"')
      
      cat >> "$FILEPATH" << LISTING_MD

## $COUNTER. $TITLE

| Parametr | Hodnota |
|----------|---------|
| **ID** | \`$ID\` |
| **Zdroj** | $SOURCE |
| **Cena** | ${PRICE} Kč |
| **Plocha** | ${AREA} m² |

---

LISTING_MD
      COUNTER=$((COUNTER + 1))
    done
    ;;
  *)
    echo "❌ Neznámý formát: $FORMAT"
    exit 1
    ;;
esac

echo "✅ Exportováno $LISTING_COUNT inzerátů: $FILEPATH"
