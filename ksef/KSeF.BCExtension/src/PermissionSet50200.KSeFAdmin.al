permissionset 50200 "KPHG KSeF Admin"
{
    Assignable = true;
    Caption = 'KSeF Administration';

    Permissions =
        table "KPHG KSeF Setup" = X,
        tabledata "KPHG KSeF Setup" = RIMD,
        table "KPHG JPK Export" = X,
        tabledata "KPHG JPK Export" = RIMD,
        codeunit "KPHG Posting Subscribers" = X,
        codeunit "KPHG KSeF Management" = X,
        codeunit "KPHG JPK Management" = X,
        page "KPHG KSeF Invoices" = X,
        page "KPHG KSeF Setup" = X,
        page "KPHG JPK Exports" = X,
        page "KPHG JPK Export Card" = X;
}
