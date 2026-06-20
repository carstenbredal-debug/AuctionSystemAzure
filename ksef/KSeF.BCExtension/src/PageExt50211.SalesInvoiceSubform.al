pageextension 50211 "KPHG Sales Invoice Sub Ext" extends "Sales Invoice Subform"
{
    layout
    {
        addafter(Description)
        {
            field("KPHG Lot No."; Rec."KPHG Lot No.") { ApplicationArea = All; }
            field("KPHG Line Component"; Rec."KPHG Line Component") { ApplicationArea = All; }
            field("KPHG Settlement Reference"; Rec."KPHG Settlement Reference") { ApplicationArea = All; }
            field("KPHG GTU Code"; Rec."KPHG GTU Code") { ApplicationArea = All; }
            field("KPHG PKWiU"; Rec."KPHG PKWiU") { ApplicationArea = All; }
        }
    }
}
