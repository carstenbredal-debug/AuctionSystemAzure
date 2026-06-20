tableextension 50204 "KPHG Sales Cr Memo Hdr Ext" extends "Sales Cr.Memo Header"
{
    fields
    {
        field(50200; "KPHG KSeF Required"; Boolean)
        {
            Caption = 'KSeF Required';
            DataClassification = CustomerContent;
        }
        field(50201; "KPHG KSeF Status"; Enum "KPHG KSeF Status")
        {
            Caption = 'KSeF Status';
            DataClassification = CustomerContent;
        }
        field(50202; "KPHG KSeF Error Message"; Text[250])
        {
            Caption = 'KSeF Error Message';
            DataClassification = CustomerContent;
        }
        field(50203; "KPHG KSeF Number"; Code[100])
        {
            Caption = 'KSeF Number';
            DataClassification = CustomerContent;
        }
        field(50204; "KPHG KSeF Submission DT"; DateTime)
        {
            Caption = 'KSeF Submission DateTime';
            DataClassification = CustomerContent;
        }
        field(50205; "KPHG KSeF Acceptance DT"; DateTime)
        {
            Caption = 'KSeF Acceptance DateTime';
            DataClassification = CustomerContent;
        }
        field(50209; "KPHG KSeF Element Ref."; Code[100])
        {
            Caption = 'KSeF Element Reference No.';
            DataClassification = CustomerContent;
        }
        field(50210; "KPHG Original Invoice KSeF No."; Code[100])
        {
            Caption = 'Original Invoice KSeF Number';
            DataClassification = CustomerContent;
        }
        field(50211; "KPHG KSeF Session Ref."; Code[100])
        {
            Caption = 'KSeF Session Reference No.';
            DataClassification = CustomerContent;
        }
        field(50212; "KPHG KSeF QR Reference"; Text[250])
        {
            Caption = 'KSeF QR Reference';
            DataClassification = CustomerContent;
        }
    }
}
