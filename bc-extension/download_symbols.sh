#!/bin/bash
# Get OAuth token for BC
TOKEN=$(curl -s -X POST "https://login.microsoftonline.com/${BC_TENANT_ID}/oauth2/v2.0/token" \
  -d "client_id=${BC_CLIENT_ID}" \
  -d "client_secret=${BC_CLIENT_SECRET}" \
  -d "scope=https://api.businesscentral.dynamics.com/.default" \
  -d "grant_type=client_credentials" | python3 -c "import sys,json; print(json.load(sys.stdin)['access_token'])")

echo "Token obtained: ${#TOKEN} chars"

# Download System symbols
echo "Downloading System.app..."
curl -s -o .alpackages/System.app \
  -H "Authorization: Bearer $TOKEN" \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/dev/packages?publisher=Microsoft&appName=System&versionText=23.0.0.0"

echo "Downloading Application.app..."
curl -s -o .alpackages/Application.app \
  -H "Authorization: Bearer $TOKEN" \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/dev/packages?publisher=Microsoft&appName=Application&versionText=23.0.0.0"

echo "Downloading Base Application.app..."
curl -s -o .alpackages/BaseApplication.app \
  -H "Authorization: Bearer $TOKEN" \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/dev/packages?publisher=Microsoft&appName=Base%%20Application&versionText=23.0.0.0"

echo "Downloading System Application.app..."
curl -s -o .alpackages/SystemApplication.app \
  -H "Authorization: Bearer $TOKEN" \
  "https://api.businesscentral.dynamics.com/v2.0/${BC_TENANT_ID}/${BC_ENVIRONMENT}/dev/packages?publisher=Microsoft&appName=System%%20Application&versionText=23.0.0.0"

ls -la .alpackages/
