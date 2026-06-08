table 50160 "Auction Invoice PDF Buffer"
{
    Caption = 'Auction Invoice PDF Buffer';
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "Entry No."; Integer)
        {
            Caption = 'Entry No.';
            AutoIncrement = true;
        }
        field(2; "Document No."; Code[20])
        {
            Caption = 'Document No.';
        }
        field(3; "Document Type"; Option)
        {
            Caption = 'Document Type';
            OptionMembers = "Sales Invoice","Credit Memo";
        }
        field(10; "Success"; Boolean)
        {
            Caption = 'Success';
        }
        field(11; "Error Message"; Text[2048])
        {
            Caption = 'Error Message';
        }
        field(12; "Report ID Used"; Integer)
        {
            Caption = 'Report ID Used';
        }
        field(13; "Attachment Created"; Boolean)
        {
            Caption = 'Attachment Created';
        }
    }

    keys
    {
        key(PK; "Entry No.")
        {
            Clustered = true;
        }
    }
}
