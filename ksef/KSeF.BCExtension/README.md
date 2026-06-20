# KPHG Auction Compliance AL Extension v2

Business Central extension for KSeF (Polish e-Invoice) integration.

## Includes
- KSeF status enum (Not Required, Required, Ready, Sent, Accepted, Rejected, Error)
- Line component enum (Lot Sale, Broker Fee, Auction Fee, Storage, Finance, Other)
- GTU code enum (GTU_01 through GTU_13)
- Custom fields on Sales Header, Sales Line, Posted Sales Invoice Header/Line
- Credit memo support (Sales Cr. Memo Header/Line extensions)
- GTU Code and PKWiU fields on all invoice/credit memo lines
- KSeF Element Reference Number for status tracking
- Page extensions for Sales Invoice, Posted Sales Invoice, Posted Credit Memo
- KSeF Setup page (Azure Function URL, Function Key, NIP, environment)
- KSeF Invoices list page with batch Send/Check Status actions
- Posting subscribers to copy custom fields and auto-send on post
- HttpClient integration with Azure Functions middleware
- Test Connection action to verify Azure Function connectivity
- Check Status action to poll KSeF for acceptance/rejection

## Architecture

```
BC (this extension)  →  Azure Functions  →  KSeF API
     HttpClient           /api/invoice/submit
                          /api/invoice/status
                          /api/health
```

## Setup
1. Deploy the Azure Functions project
2. Install this extension in BC
3. Go to KSeF Setup page
4. Enter Company NIP, Azure Function URL, and Function Key
5. Select KSeF Environment (Demo/Test/Production)
6. Optionally enable "Auto Send on Post" and "Default KSeF Required"

## Object IDs

| Range | Type | Objects |
|-------|------|---------|
| 50100 | Tables | KSeF Setup |
| 50100-50102 | Enums | KSeF Status, Line Component, GTU Code |
| 50100-50105 | Table Extensions | Sales Header, Sales Line, Sales Invoice Header/Line, Sales Cr. Memo Header/Line |
| 50100-50101 | Pages | KSeF Invoices, KSeF Setup |
| 50110-50115 | Page Extensions | Sales Invoice, Sales Invoice Subform, Posted Sales Invoice, Posted Sales Invoice Subform, Posted Sales Cr. Memo, Posted Sales Cr. Memo Subform |
| 50100-50101 | Codeunits | Posting Subscribers, KSeF Management |
