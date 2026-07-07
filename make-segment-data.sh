#!/bin/bash

OUTPUT_FILE="unique_ids.csv"

echo "Generating 1,000,000 unique rows (single column)..."

# Generates a single column of unique prefixed IDs (e.g., ID_0000001 to ID_1000000)
awk -v rows=1000000 '
BEGIN {
    for (i = 1; i <= rows; i++) {
        printf "ID_%07d\n", i
    }
}' > "$OUTPUT_FILE"

echo "Done! Created $OUTPUT_FILE"
wc -l "$OUTPUT_FILE"
