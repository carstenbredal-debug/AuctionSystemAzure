tableextension 50200 "KPHG Sales Header Ext" extends "Sales Header"
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
    }
}
