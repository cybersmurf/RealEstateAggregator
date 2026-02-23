#!/bin/bash
# Trigger all scrapers
curl -s -X POST http://localhost:8001/scrape/trigger \
  -H "Content-Type: application/json" \
  -d '{"source_codes":["REMAX","NEMZNOJMO","HVREALITY","PREMIAREALITY","DELUXREALITY","LEXAMO","CENTURY21"],"full_rescan":false}' \
  | python3 -m json.tool
