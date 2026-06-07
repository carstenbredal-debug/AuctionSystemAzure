#!/bin/bash
set -e

# Get OAuth token
TOKEN=$(curl -s -X POST "https://login.microsoftonline.com/${BC_TENANT_ID}/oauth2/v2.0/token" \
  -d "client_id=${BC_CLIENT_ID}" \
  -d "client_secret=${BC_CLIENT_SECRET}" \
  -d "scope=https://api.businesscentral.dynamics.com/.default" \
  -d "grant_type=client_credentials" | python3 -c "import sys,json; print(json.load(sys.stdin)['access_token'])")

echo "Token obtained: ${#TOKEN} chars"

# Get company ID
COMPANIES=$(curl -s -H "Authorization: Bearer $TOKEN" \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/api/microsoft/automation/v2.0/companies")
COMPANY_ID=$(echo "$COMPANIES" | python3 -c "import sys,json; data=json.load(sys.stdin); print(data['value'][0]['id'])")
echo "Company: $COMPANY_ID"

# Create extensionUpload entry
echo "Creating extension upload entry..."
UPLOAD_RESPONSE=$(curl -s -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/api/microsoft/automation/v2.0/companies(${COMPANY_ID})/extensionUpload" \
  -d '{"schedule":"Current version","schemaSyncMode":"Add"}')

UPLOAD_ID=$(echo "$UPLOAD_RESPONSE" | python3 -c "import sys,json; data=json.load(sys.stdin); print(data.get('systemId',''))")
ETAG=$(echo "$UPLOAD_RESPONSE" | python3 -c "import sys,json; data=json.load(sys.stdin); print(data.get('@odata.etag',''))")
echo "Upload ID: $UPLOAD_ID, ETag: $ETAG"

# Upload the .app file content via media endpoint
echo "Uploading .app content via media endpoint..."
MEDIA_RESPONSE=$(curl -s -w "\n%{http_code}" -X PATCH \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/octet-stream" \
  -H "If-Match: $ETAG" \
  --data-binary @AuctionSystemIntegration.app \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/api/microsoft/automation/v2.0/companies(${COMPANY_ID})/extensionUpload(${UPLOAD_ID})/extensionContent")
HTTP_CODE=$(echo "$MEDIA_RESPONSE" | tail -1)
BODY=$(echo "$MEDIA_RESPONSE" | head -n -1)
echo "Media upload HTTP: $HTTP_CODE"
echo "Body: $BODY"

if [ "$HTTP_CODE" -ge 200 ] && [ "$HTTP_CODE" -lt 300 ]; then
  # Trigger the upload action
  echo "Triggering install..."
  INSTALL_RESPONSE=$(curl -s -w "\n%{http_code}" -X POST \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Length: 0" \
    "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/api/microsoft/automation/v2.0/companies(${COMPANY_ID})/extensionUpload(${UPLOAD_ID})/Microsoft.NAV.upload")
  INSTALL_CODE=$(echo "$INSTALL_RESPONSE" | tail -1)
  INSTALL_BODY=$(echo "$INSTALL_RESPONSE" | head -n -1)
  echo "Install HTTP: $INSTALL_CODE"
  echo "Install body: $INSTALL_BODY"
else
  echo "Media upload failed"
fi
