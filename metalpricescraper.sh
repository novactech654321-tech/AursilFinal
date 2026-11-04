#!/bin/bash

# Define the base URL and your API key
BASE_URL="https://workspace.nebulaapi.com"
API_KEY="nbk_L3SwuWWcDsBwtb3O0TCENL3oro3jfX34"

# Define a function that takes 3 arguments: Name, Endpoint, and Output File
fetch_metal_data() {
    local METAL_NAME=$1
    local ENDPOINT=$2
    local OUTPUT_FILE=$3

    echo "Fetching data for $METAL_NAME..."

    # Execute curl using the arguments passed to the function
    local HTTP_STATUS=$(curl -s -o "$OUTPUT_FILE" -w "%{http_code}" \
      -H "x-api-key: $API_KEY" \
      -H "Accept: application/json" \
      "${BASE_URL}${ENDPOINT}")

    if [ "$HTTP_STATUS" -eq 200 ]; then
        echo "✓ Request successful! Saved to $OUTPUT_FILE"
        
        # Read the file and parse with jq
        # Note: You may need to change '.data.price' if your API uses '.data.gold', '.data.silver', etc.
        echo "Extracted $METAL_NAME Price:"
        jq -r '.data.price' "$OUTPUT_FILE" 
        
    elif [ "$HTTP_STATUS" -eq 401 ]; then
        echo "✗ Error 401: Unauthorized for $METAL_NAME. Please check your API key."
        cat "$OUTPUT_FILE"
        
    else
        echo "✗ Request failed for $METAL_NAME with HTTP Status: $HTTP_STATUS"
        echo "Check $OUTPUT_FILE for server error details."
    fi
    
    echo "----------------------------------------"
}

# Call the function for each of your three requests
fetch_metal_data "Gold (Chennai)" "/gold_price?city=Chennai" "gold_chennai.json"
fetch_metal_data "Silver (Chennai)" "/silver_price?city=Chennai" "silver_chennai.json"
fetch_metal_data "Platinum" "/platinum_price" "platinum.json"