#!/bin/bash
set -e

# Get OAuth token
TOKEN=$(curl -s -X POST "https://login.microsoftonline.com/${BC_TENANT_ID}/oauth2/v2.0/token" \
  -d "client_id=${BC_CLIENT_ID}" \
  -d "client_secret=${BC_CLIENT_SECRET}" \
  -d "scope=https://api.businesscentral.dynamics.com/.default" \
  -d "grant_type=client_credentials" | python3 -c "import sys,json; print(json.load(sys.stdin)['access_token'])")

echo "Token obtained: ${#TOKEN} chars"

# Get company ID from automation API
echo "Getting companies..."
COMPANIES=$(curl -s -H "Authorization: Bearer $TOKEN" \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/api/microsoft/automation/v2.0/companies")
echo "$COMPANIES" | python3 -c "import sys,json; data=json.load(sys.stdin); [print(f'  {c[\"name\"]} - {c[\"id\"]}') for c in data.get('value',[])]"

# Get first company ID
COMPANY_ID=$(echo "$COMPANIES" | python3 -c "import sys,json; data=json.load(sys.stdin); print(data['value'][0]['id'])")
echo "Using company: $COMPANY_ID"

# Step 1: Create extensionUpload entry
echo "Creating extension upload..."
UPLOAD_RESPONSE=$(curl -s -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/api/microsoft/automation/v2.0/companies(${COMPANY_ID})/extensionUpload" \
  -d '{"schedule":"Current version","schemaSyncMode":"Add"}')
echo "Upload response: $UPLOAD_RESPONSE"

# Get the upload ID
UPLOAD_ID=$(echo "$UPLOAD_RESPONSE" | python3 -c "import sys,json; data=json.load(sys.stdin); print(data.get('systemId',''))")
echo "Upload ID: $UPLOAD_ID"

if [ -z "$UPLOAD_ID" ]; then
  echo "Failed to create upload entry"
  exit 1
fi

# Step 2: PATCH the extensionContent with base64 encoded .app file
echo "Encoding and uploading .app file..."
APP_CONTENT=$(base64 -w 0 AuctionSystemIntegration.app)

PATCH_RESPONSE=$(curl -s -X PATCH \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "If-Match: *" \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/api/microsoft/automation/v2.0/companies(${COMPANY_ID})/extensionUpload(${UPLOAD_ID})" \
  -d "{\"extensionContent\":\"${APP_CONTENT}\"}")
echo "Patch response: $PATCH_RESPONSE"

# Step 3: Trigger the upload action
echo "Triggering upload..."
UPLOAD_ACTION=$(curl -s -X POST \
  -H "Authorization: Bearer $TOKEN" \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/api/microsoft/automation/v2.0/companies(${COMPANY_ID})/extensionUpload(${UPLOAD_ID})/Microsoft.NAV.upload")
echo "Upload action response: $UPLOAD_ACTION"
echo "Done!"
