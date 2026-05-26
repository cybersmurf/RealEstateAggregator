#!/bin/bash
# Syncs user inspection photos from Google Drive back to the server for all
# listings that have drive_inspection_folder_id set.
# Usage: ./sync_inspection_photos_from_drive.sh [API_BASE_URL]
#   API_BASE_URL defaults to http://localhost:5001

API_BASE="${1:-http://localhost:5001}"

LISTING_IDS=(
  0de2b4e6-5e83-4128-8eb4-51c74a4776c8
  14fe1165-c84f-4dcd-b5aa-ca01ae563f22
  1d34c18b-99c3-4cda-8da8-7d34d57bb88b
  1f4d213f-df7a-4963-8639-d7e19f125851
  30a96021-a016-4717-97a1-f776706233cb
  30d23958-c7f2-4e21-9d90-1017ad7c57ec
  344daecc-8b1a-4057-93a7-9fe2b14c24cc
  383c843d-3b15-4113-b75f-1185b933d5d2
  40a93696-ecbe-4523-91a1-272189670dbc
  44931ef0-561e-455e-970d-d3dffd56f0c2
  45cd728f-13e5-4bab-9397-fcdc22cfd318
  55d306d5-6473-4af1-b7d8-c39666d55566
  56acfea4-0c04-44c3-8ea8-b0e5c8e1d250
  57a4033e-5eb3-404a-9898-a2737de990b7
  67f10048-b07c-4c59-a20d-40ff24c6631f
  6a63ad84-1ed4-4e3d-bb8b-92520898cb1c
  6cb00624-e930-42c6-8159-7f18f9afade9
  74b6f591-78ff-4222-9450-9535d5e2639e
  7b5b50ac-6a7d-4e58-bf4b-d3846ea5e6cf
  967a2865-0eb6-4642-bca9-87fc8e0ff4b8
  9e267b6a-b367-4fcd-9841-ae5c0713881a
  9f35f480-09f6-40b3-ace0-15ece1d67c29
  abdd67fc-8cb4-4bce-b86b-0a9a92de6370
  ac332897-a5aa-4ed8-875b-485928ff2f0a
  ae5745e9-b580-43a7-b262-420de5eac3f7
  af685a81-dbb5-44b5-9516-568973398769
  b08f88b7-a317-420c-a0d3-f0c3132ee813
  b3333bb8-a129-4f42-a889-ed004e68a635
  b410bb3f-a2f8-41d4-a47b-692045a9bba0
  bc8316be-cb1a-4b6e-8b2c-4ab3433a2c14
  bd35b548-4afa-43f1-a156-5b3450885490
  c57de711-5d69-46a2-ae65-5eded933fa91
  c67f2863-2caa-4415-88a2-1d8734c0cbfe
  c8c22aba-7011-4390-b4d8-57bbaf1adaee
  cebdbe62-ae82-4fb4-b870-49b7e9af56d3
  dd2caf59-9bb5-458f-bbc2-5143e97f7a19
  e3bf6de8-e217-4579-8dfe-0d4633e6d323
  e4328a6c-268c-4289-8dba-381157b876fa
  e9ea13a5-77f4-42b7-a540-8a4aee968371
  ea0fce1d-0592-4923-ac1b-67c79cb84340
  f1b8d7cf-1ff1-441e-915d-07698b8c5e78
  f3c9595d-f892-4fcc-aba1-84e599ca8a3c
)

echo "Starting inspection photo sync from Google Drive..."
echo "API: $API_BASE"
echo "Listings: ${#LISTING_IDS[@]}"
echo "---"

imported_total=0
skipped_total=0
errors=0

for id in "${LISTING_IDS[@]}"; do
  response=$(curl -s -w "\n%{http_code}" -X POST "$API_BASE/api/listings/$id/scan-drive-inspection" \
    -H "Content-Type: application/json" --max-time 120)
  http_code=$(echo "$response" | tail -1)
  body=$(echo "$response" | head -1)

  if [[ "$http_code" == "200" ]]; then
    imported=$(echo "$body" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('imported',0))" 2>/dev/null)
    skipped=$(echo "$body" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('skipped',0))" 2>/dev/null)
    imported_total=$((imported_total + imported))
    skipped_total=$((skipped_total + skipped))
    echo "✓ $id  imported=$imported skipped=$skipped"
  else
    errors=$((errors + 1))
    msg=$(echo "$body" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('detail', d))" 2>/dev/null || echo "$body")
    echo "✗ $id  HTTP $http_code: $msg"
  fi
done

echo "---"
echo "Done. Imported: $imported_total | Skipped (already in DB): $skipped_total | Errors: $errors"
