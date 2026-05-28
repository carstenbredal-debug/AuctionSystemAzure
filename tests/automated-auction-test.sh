#!/bin/bash
# Automated Auction Test Script
# Usage: ./automated-auction-test.sh [phase]
#   phase1 - Submit auction results (lots → brokers) 
#   phase2 - Sell lots to buyers
#   phase3 - Take back 100 lots
#   phase4 - Resell taken-back lots
#   phase5 - Verify results
#   all    - Run all phases sequentially (default)

set -o pipefail

API_BASE="https://auctionsystemazure-hjdgbugnfvfsaka7.westeurope-01.azurewebsites.net/api"
PHASE="${1:-all}"

BROKER_IDS=(6 25 26 27 28 29 30 31 32 33)

declare -A BROKER_BUYERS
BROKER_BUYERS[6]="22 28 41 48"
BROKER_BUYERS[25]="8 32 36 42 43"
BROKER_BUYERS[26]="29 37 50"
BROKER_BUYERS[27]="12 14 24 30 49"
BROKER_BUYERS[28]="11 17 18 19 31 40 52 53 54 55"
BROKER_BUYERS[29]="15 21 34 45"
BROKER_BUYERS[30]="26"
BROKER_BUYERS[31]="10 11 27 33 35 38 39 44 47"
BROKER_BUYERS[32]="6 20 23 25 56"
BROKER_BUYERS[33]="7 9 13 16 46 51"

ERRORS=0
log() { echo "[$(date +%H:%M:%S)] $*"; }
log_error() { echo "[$(date +%H:%M:%S)] ERROR: $*" >&2; ERRORS=$((ERRORS + 1)); }

random_price() { echo $(( RANDOM % 21 + 20 )); }
random_element() { local arr=("$@"); echo "${arr[RANDOM % ${#arr[@]}]}"; }
random_broker() { random_element "${BROKER_IDS[@]}"; }

#############################################
# PHASE 1: Submit auction results (parallel)
#############################################
phase1_submit_results() {
    log "===== PHASE 1: Submitting auction results for pending lots ====="

    local lots_json
    lots_json=$(curl -s "$API_BASE/auctions/2/lots")
    
    # Get pending lot numbers (status=0 only, skip already sold/broker)
    local pending_file="/tmp/auction_pending_lots.txt"
    echo "$lots_json" | python3 -c "
import json, sys
lots = json.load(sys.stdin)
pending = [l['lotNumber'] for l in lots if l.get('status', 0) == 0]
for ln in pending:
    print(ln)
" > "$pending_file"

    local lot_count
    lot_count=$(wc -l < "$pending_file")
    log "Found $lot_count pending lots to process (already sold lots are untouched)"

    # Generate curl commands file for parallel execution
    local cmds_file="/tmp/auction_curl_cmds.txt"
    local results_dir="/tmp/auction_results"
    rm -rf "$results_dir" && mkdir -p "$results_dir"
    > "$cmds_file"

    local idx=0
    while IFS= read -r lot_number; do
        [ -z "$lot_number" ] && continue
        local broker_id
        broker_id=$(random_broker)
        local price
        price=$(random_price)
        echo "curl -s -o $results_dir/result_${idx}.json -w '%{http_code}' -X POST '$API_BASE/auction-results' -H 'Content-Type: application/json' -d '{\"lotNumber\": $lot_number, \"brokerId\": $broker_id, \"priceEur\": $price}'" >> "$cmds_file"
        idx=$((idx + 1))
    done < "$pending_file"

    log "Submitting $lot_count lots in parallel batches of 20..."
    
    local submitted=0
    local total_lines
    total_lines=$(wc -l < "$cmds_file")
    local batch_num=0

    while IFS= read -r cmd; do
        eval "$cmd" > "/tmp/auction_http_code_${submitted}.txt" 2>/dev/null &
        submitted=$((submitted + 1))
        
        # Run in batches of 20
        if (( submitted % 20 == 0 )); then
            wait
            batch_num=$((batch_num + 1))
            if (( submitted % 100 == 0 )); then
                log "  Submitted $submitted / $total_lines lots..."
            fi
        fi
    done < "$cmds_file"
    wait

    # Count successes
    local success=0
    local fail=0
    for f in "$results_dir"/result_*.json; do
        [ -f "$f" ] || continue
        if grep -q '"resultId"' "$f" 2>/dev/null; then
            success=$((success + 1))
        else
            fail=$((fail + 1))
        fi
    done

    log "Phase 1 complete: $success lots submitted successfully, $fail failures"
}

#############################################
# PHASE 2: Sell lots to buyers
#############################################
phase2_sell_to_buyers() {
    log "===== PHASE 2: Selling lots to buyers ====="

    # Get all unsold auction results (SoldToBuyerId is null)
    curl -s "$API_BASE/auction-results" > /tmp/auction_all_results.json

    # Group unsold results by broker
    local broker_groups_file="/tmp/auction_broker_groups.json"
    python3 << 'PYEOF'
import json
with open('/tmp/auction_all_results.json') as f:
    results = json.load(f)
unsold = {}
for r in results:
    if r.get('soldToBuyerId') is None:
        bid = r['brokerId']
        if bid not in unsold:
            unsold[bid] = []
        unsold[bid].append(r['id'])
with open('/tmp/auction_broker_groups.json', 'w') as f:
    json.dump(unsold, f)
PYEOF

    local total_sold=0
    local total_invoices=0

    for broker_id in "${BROKER_IDS[@]}"; do
        local buyers_str="${BROKER_BUYERS[$broker_id]}"
        local buyers_arr=($buyers_str)
        [ ${#buyers_arr[@]} -eq 0 ] && continue

        # Get result IDs for this broker
        local result_ids
        result_ids=$(python3 -c "
import json
with open('$broker_groups_file') as f:
    groups = json.load(f)
ids = groups.get('$broker_id', [])
print(' '.join(str(i) for i in ids))
")
        [ -z "$result_ids" ] && continue
        local result_arr=($result_ids)
        local total=${#result_arr[@]}
        [ "$total" -eq 0 ] && continue

        local idx=0
        while (( idx < total )); do
            local batch_size=$(( RANDOM % 11 + 5 ))
            (( idx + batch_size > total )) && batch_size=$(( total - idx ))

            local batch=()
            for (( i=0; i<batch_size; i++ )); do
                batch+=("${result_arr[$((idx + i))]}")
            done
            idx=$(( idx + batch_size ))

            local buyer_id
            buyer_id=$(random_element "${buyers_arr[@]}")

            local comm_type comm_value
            if (( RANDOM % 2 == 0 )); then
                comm_type="percentage"
                comm_value=$(( RANDOM % 7 + 2 ))
            else
                comm_type="amount"
                comm_value=$(( RANDOM % 151 + 50 ))
            fi

            local ids_json
            ids_json=$(printf '%s\n' "${batch[@]}" | python3 -c "import sys,json; print(json.dumps([int(x.strip()) for x in sys.stdin if x.strip()]))")

            local response
            response=$(curl -s -w "\n%{http_code}" -X POST "$API_BASE/auction-results/sell-to-buyer" \
                -H "Content-Type: application/json" \
                -d "{\"auctionResultIds\": $ids_json, \"buyerId\": $buyer_id, \"commissionType\": \"$comm_type\", \"commissionValue\": $comm_value}" 2>/dev/null)

            local http_code
            http_code=$(echo "$response" | tail -1)

            if [ "$http_code" = "200" ]; then
                total_sold=$(( total_sold + batch_size ))
                local body
                body=$(echo "$response" | sed '$d')
                local inv_id
                inv_id=$(echo "$body" | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('invoiceId','none'))" 2>/dev/null)
                if [ "$inv_id" != "none" ] && [ "$inv_id" != "null" ] && [ -n "$inv_id" ]; then
                    total_invoices=$((total_invoices + 1))
                fi
            else
                log_error "Failed to sell batch to buyer $buyer_id from broker $broker_id (HTTP $http_code)"
            fi
        done

        log "  Broker $broker_id: sold $total lots"
    done

    log "Phase 2 complete: $total_sold lots sold, $total_invoices invoices created"
}

#############################################
# PHASE 3: Take back 100 lots
#############################################
phase3_takeback() {
    log "===== PHASE 3: Taking back 100 lots ====="

    curl -s "$API_BASE/auction-results" > /tmp/auction_all_results.json

    # Pick 100 random sold lots
    local sold_ids
    sold_ids=$(python3 << 'PYEOF'
import json, random
with open('/tmp/auction_all_results.json') as f:
    results = json.load(f)
sold = [r['id'] for r in results if r.get('soldToBuyerId') is not None]
random.shuffle(sold)
for rid in sold[:100]:
    print(rid)
PYEOF
)

    local all_ids=()
    while IFS= read -r rid; do
        [ -z "$rid" ] && continue
        all_ids+=("$rid")
    done <<< "$sold_ids"

    local total=${#all_ids[@]}
    log "Taking back $total lots in batches of 10..."

    local total_taken=0
    local idx=0
    while (( idx < total )); do
        local batch_size=10
        (( idx + batch_size > total )) && batch_size=$(( total - idx ))

        local batch=()
        for (( i=0; i<batch_size; i++ )); do
            batch+=("${all_ids[$((idx + i))]}")
        done
        idx=$(( idx + batch_size ))

        local ids_json
        ids_json=$(printf '%s\n' "${batch[@]}" | python3 -c "import sys,json; print(json.dumps([int(x.strip()) for x in sys.stdin if x.strip()]))")

        local response
        response=$(curl -s -w "\n%{http_code}" -X POST "$API_BASE/takeback-requests" \
            -H "Content-Type: application/json" \
            -d "{\"auctionResultIds\": $ids_json}" 2>/dev/null)

        local http_code
        http_code=$(echo "$response" | tail -1)

        if [ "$http_code" = "200" ]; then
            total_taken=$(( total_taken + batch_size ))
        else
            log_error "Failed to take back batch (HTTP $http_code)"
        fi
    done

    log "Phase 3 complete: $total_taken lots taken back (credit notes generated)"
}

#############################################
# PHASE 4: Resell taken-back lots
#############################################
phase4_resell() {
    log "===== PHASE 4: Reselling taken-back lots ====="

    curl -s "$API_BASE/auction-results" > /tmp/auction_all_results.json

    local broker_groups_file="/tmp/auction_resell_groups.json"
    python3 << 'PYEOF'
import json
with open('/tmp/auction_all_results.json') as f:
    results = json.load(f)
unsold = {}
for r in results:
    if r.get('soldToBuyerId') is None:
        bid = r['brokerId']
        if bid not in unsold:
            unsold[bid] = []
        unsold[bid].append(r['id'])
with open('/tmp/auction_resell_groups.json', 'w') as f:
    json.dump(unsold, f)
PYEOF

    local total_resold=0

    for broker_id in "${BROKER_IDS[@]}"; do
        local buyers_str="${BROKER_BUYERS[$broker_id]}"
        local buyers_arr=($buyers_str)
        [ ${#buyers_arr[@]} -eq 0 ] && continue

        local result_ids
        result_ids=$(python3 -c "
import json
with open('$broker_groups_file') as f:
    groups = json.load(f)
ids = groups.get('$broker_id', [])
print(' '.join(str(i) for i in ids))
")
        [ -z "$result_ids" ] && continue
        local result_arr=($result_ids)
        local total=${#result_arr[@]}
        [ "$total" -eq 0 ] && continue

        local buyer_id
        buyer_id=$(random_element "${buyers_arr[@]}")
        local comm_value=$(( RANDOM % 5 + 3 ))

        local ids_json
        ids_json=$(printf '%s\n' "${result_arr[@]}" | python3 -c "import sys,json; print(json.dumps([int(x.strip()) for x in sys.stdin if x.strip()]))")

        local response
        response=$(curl -s -w "\n%{http_code}" -X POST "$API_BASE/auction-results/sell-to-buyer" \
            -H "Content-Type: application/json" \
            -d "{\"auctionResultIds\": $ids_json, \"buyerId\": $buyer_id, \"commissionType\": \"percentage\", \"commissionValue\": $comm_value}" 2>/dev/null)

        local http_code
        http_code=$(echo "$response" | tail -1)

        if [ "$http_code" = "200" ]; then
            total_resold=$(( total_resold + total ))
            log "  Broker $broker_id: resold $total lots to buyer $buyer_id"
        else
            log_error "Failed to resell from broker $broker_id (HTTP $http_code)"
        fi
    done

    log "Phase 4 complete: $total_resold lots resold"
}

#############################################
# PHASE 5: Verify results
#############################################
phase5_verify() {
    log "===== PHASE 5: Verification ====="

    curl -s "$API_BASE/auction-results" > /tmp/verify_results.json
    curl -s "$API_BASE/auctions/2/lots" > /tmp/verify_lots.json
    curl -s "$API_BASE/settlements/invoices" > /tmp/verify_invoices.json

    python3 << 'PYEOF'
import json

with open('/tmp/verify_results.json') as f:
    results = json.load(f)
with open('/tmp/verify_lots.json') as f:
    lots = json.load(f)
with open('/tmp/verify_invoices.json') as f:
    invoices = json.load(f)

total_results = len(results)
sold = len([r for r in results if r.get('soldToBuyerId') is not None])
unsold = total_results - sold

pending_lots = len([l for l in lots if l.get('status', 0) == 0])
broker_lots = len([l for l in lots if l.get('status', 0) == 5])
sold_lots = len([l for l in lots if l.get('status', 0) == 2])

inv_count = len([i for i in invoices if not i.get('invoiceNumber','').startswith('CN-')])
cn_count = len([i for i in invoices if i.get('invoiceNumber','').startswith('CN-')])

print(f'  Auction Results: {total_results} total ({sold} sold, {unsold} unsold)')
print(f'  Lot Statuses:    {pending_lots} pending, {broker_lots} broker, {sold_lots} sold')
print(f'  Documents:       {inv_count} invoices, {cn_count} credit notes')
print()
print('  Verification complete.')
PYEOF
}

#############################################
# MAIN
#############################################
log "Automated Auction Test — Phase: $PHASE"
log "API: $API_BASE"
log ""

case "$PHASE" in
    phase1) phase1_submit_results ;;
    phase2) phase2_sell_to_buyers ;;
    phase3) phase3_takeback ;;
    phase4) phase4_resell ;;
    phase5) phase5_verify ;;
    all)
        phase1_submit_results
        echo ""
        log "Phase 1 done. Proceeding to Phase 2..."
        echo ""
        phase2_sell_to_buyers
        echo ""
        log "Phase 2 done. Proceeding to Phase 3..."
        echo ""
        phase3_takeback
        echo ""
        log "Phase 3 done. Proceeding to Phase 4..."
        echo ""
        phase4_resell
        echo ""
        phase5_verify
        ;;
    *) echo "Usage: $0 [phase1|phase2|phase3|phase4|phase5|all]"; exit 1 ;;
esac
