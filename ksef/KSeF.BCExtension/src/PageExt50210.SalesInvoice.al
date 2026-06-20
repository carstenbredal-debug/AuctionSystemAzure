pageextension 50210 "KPHG Sales Invoice Ext" extends "Sales Invoice"
{
    layout
    {
        addlast(General)
        {
            group("KPHG KSeF")
            {
                Caption = 'KSeF';
                field("KPHG KSeF Required"; Rec."KPHG KSeF Required") { ApplicationArea = All; }
                field("KPHG KSeF Status"; Rec."KPHG KSeF Status") { ApplicationArea = All; }
                field("KPHG KSeF Error Message"; Rec."KPHG KSeF Error Message") { ApplicationArea = All; }
            }
        }
    }
}
