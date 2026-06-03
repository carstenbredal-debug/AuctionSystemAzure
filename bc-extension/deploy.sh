#!/bin/bash
set -e

APP_FILE="${1:-AuctionSystemLedgerAPI.app}"

if [ ! -f "$APP_FILE" ]; then
  echo "Error: $APP_FILE not found"
  exit 1
fi

echo "Deploying $APP_FILE ($(stat -c%s "$APP_FILE") bytes)..."

# Get OAuth token
TOKEN=$(curl -s -X POST "https://login.microsoftonline.com/${BC_TENANT_ID}/oauth2/v2.0/token" \
  -d "client_id=${BC_CLIENT_ID}" \
  -d "client_secret=${BC_CLIENT_SECRET}" \
  -d "scope=https://api.businesscentral.dynamics.com/.default" \
  -d "grant_type=client_credentials" | python3 -c "import sys,json; print(json.load(sys.stdin)['access_token'])")

echo "Token obtained: ${#TOKEN} chars"

# Get first company ID
COMPANIES=$(curl -s -H "Authorization: Bearer $TOKEN" \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/api/microsoft/automation/v2.0/companies")
COMPANY_ID=$(echo "$COMPANIES" | python3 -c "import sys,json; data=json.load(sys.stdin); print(data['value'][0]['id'])")
echo "Company: $COMPANY_ID"

# Check for existing upload entries and try to reuse or create new
echo "Checking for existing upload entries..."
EXISTING=$(curl -s -H "Authorization: Bearer $TOKEN" \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/api/microsoft/automation/v2.0/companies(${COMPANY_ID})/extensionUpload")
EXISTING_ID=$(echo "$EXISTING" | python3 -c "
import sys,json
data=json.load(sys.stdin)
entries = data.get('value',[])
if entries:
    print(entries[0].get('systemId',''))
else:
    print('')
" 2>/dev/null || echo "")

if [ -n "$EXISTING_ID" ]; then
  echo "Reusing existing upload entry: $EXISTING_ID"
  UPLOAD_ID="$EXISTING_ID"
else
  echo "Creating new upload entry..."
  UPLOAD_RESPONSE=$(curl -s -X POST \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" \
    "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/api/microsoft/automation/v2.0/companies(${COMPANY_ID})/extensionUpload" \
    -d '{"schedule":"Current version","schemaSyncMode":"Add"}')
  UPLOAD_ID=$(echo "$UPLOAD_RESPONSE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('systemId',''))")
fi

if [ -z "$UPLOAD_ID" ]; then
  echo "Failed to get upload ID"
  exit 1
fi
echo "Upload ID: $UPLOAD_ID"

# Upload .app file via media endpoint
echo "Uploading .app content..."
MEDIA_HTTP=$(curl -s -o /dev/null -w "%{http_code}" -X PATCH \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/octet-stream" \
  -H "If-Match: *" \
  --data-binary "@${APP_FILE}" \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/api/microsoft/automation/v2.0/companies(${COMPANY_ID})/extensionUpload(${UPLOAD_ID})/extensionContent")
echo "Media upload HTTP: $MEDIA_HTTP"

if [ "$MEDIA_HTTP" -lt 200 ] || [ "$MEDIA_HTTP" -ge 300 ]; then
  echo "Media upload failed"
  exit 1
fi

# Trigger the install
echo "Triggering install..."
INSTALL_RESPONSE=$(curl -s -w "\n%{http_code}" -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Length: 0" \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/api/microsoft/automation/v2.0/companies(${COMPANY_ID})/extensionUpload(${UPLOAD_ID})/Microsoft.NAV.upload")
INSTALL_CODE=$(echo "$INSTALL_RESPONSE" | tail -1)
INSTALL_BODY=$(echo "$INSTALL_RESPONSE" | head -n -1)
echo "Install HTTP: $INSTALL_CODE"

if [ "$INSTALL_CODE" -ge 200 ] && [ "$INSTALL_CODE" -lt 300 ]; then
  echo "Extension deployment triggered successfully!"
else
  echo "Install failed: $INSTALL_BODY"
  exit 1
fi
