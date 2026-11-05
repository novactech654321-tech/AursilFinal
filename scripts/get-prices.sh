#!/bin/bash
# Fetch live gold, silver and platinum prices from the Nebula Metals API.
#
# Usage:
#   export NEBULA_API_KEY="nbk_xxxxxxxx"
#   ./scripts/get-prices.sh
#
# The API key is read from the environment so it is never saved in the repo.
#
# Saved files (in scripts/output/): gold.json, silver.json, platinum.json and the combined prices.json.
# A file is only replaced when the new download succeeded, so a failed run (no credits, network down...)
# never destroys the last good prices.

BASE_URL="${NEBULA_BASE_URL:-https://workspace.nebulaapi.com}"
CITY="${NEBULA_CITY:-Dubai}"
OUT_DIR="${OUT_DIR:-$(dirname "$0")/output}"

if [ -z "$NEBULA_API_KEY" ]; then
    echo "Set NEBULA_API_KEY first:  export NEBULA_API_KEY=\"nbk_...\"" >&2
    exit 1
fi
mkdir -p "$OUT_DIR"

FAILED=0

fetch_metal_data() {
    local METAL_NAME=$1 ENDPOINT=$2 FINAL_FILE="$OUT_DIR/$3"
    local TMP_FILE
    TMP_FILE=$(mktemp)

    echo "Fetching data for $METAL_NAME..."
    local HTTP_STATUS
    HTTP_STATUS=$(curl -s -o "$TMP_FILE" -w "%{http_code}" \
        -H "x-api-key: $NEBULA_API_KEY" \
        -H "Accept: application/json" \
        "${BASE_URL}${ENDPOINT}")

    # Success = HTTP 200 and Nebula says success:true
    if [ "$HTTP_STATUS" -eq 200 ] && grep -q '"success":true' "$TMP_FILE"; then
        mv "$TMP_FILE" "$FINAL_FILE"
        echo "OK - saved to $FINAL_FILE"
        if command -v jq >/dev/null 2>&1; then jq . "$FINAL_FILE"; else cat "$FINAL_FILE"; echo; fi
    else
        FAILED=1
        if [ "$HTTP_STATUS" -eq 401 ]; then
            echo "FAILED: HTTP 401, unauthorized for $METAL_NAME. Check your API key."
        elif [ "$HTTP_STATUS" -eq 403 ]; then
            echo "FAILED: HTTP 403 for $METAL_NAME. The key is out of credits or not allowed."
        else
            echo "FAILED: $METAL_NAME returned HTTP $HTTP_STATUS."
        fi
        cat "$TMP_FILE"; echo
        echo "(the previous $FINAL_FILE was kept unchanged)"
        rm -f "$TMP_FILE"
    fi
    echo "----------------------------------------"
}

fetch_metal_data "Gold ($CITY)"   "/gold_price?city=$CITY"   "gold.json"
fetch_metal_data "Silver ($CITY)" "/silver_price?city=$CITY" "silver.json"
fetch_metal_data "Platinum"       "/platinum_price"          "platinum.json"

# Combined file prices.json, only when all three downloads in THIS run succeeded
if [ "$FAILED" -eq 0 ]; then
    {
        printf '{"fetchedAt":"%s","gold":' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        cat "$OUT_DIR/gold.json"
        printf ',"silver":'
        cat "$OUT_DIR/silver.json"
        printf ',"platinum":'
        cat "$OUT_DIR/platinum.json"
        printf '}\n'
    } > "$OUT_DIR/prices.json"
    echo "Combined file saved to $OUT_DIR/prices.json"
else
    echo "Some requests failed, so prices.json was NOT updated." >&2
    exit 1
fi
