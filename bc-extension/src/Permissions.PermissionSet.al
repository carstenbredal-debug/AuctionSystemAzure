permissionset 50150 "Auction System API"
{
    Caption = 'Auction System API';
    Assignable = true;

    Permissions =
        table "Auction Invoice PDF Buffer" = X,
        tabledata "Auction Invoice PDF Buffer" = RIMD,
        table "Payment Application Buffer" = X,
        tabledata "Payment Application Buffer" = RIMD,
        table "Unapply Buffer" = X,
        tabledata "Unapply Buffer" = RIMD,
        codeunit "Auto Fill Delivery Date" = X,
        codeunit "Auction Invoice PDF Generator" = X,
        page "Auction Invoice PDF API" = X,
        page "Apply Payment API" = X,
        page "Auction Customer API" = X,
        page "Customer Ledger Entries API" = X,
        page "Unapply Entries API" = X,
        page "Auction Vendor API" = X,
        page "Vendor Posting Groups API" = X;
}
