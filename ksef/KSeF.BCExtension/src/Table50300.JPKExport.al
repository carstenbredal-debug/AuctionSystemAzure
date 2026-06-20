table 50300 "KPHG JPK Export"
{
    Caption = 'JPK Export';
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer)
        {
            Caption = 'Entry No.';
            AutoIncrement = true;
        }
        field(10; "Report Type"; Option)
        {
            Caption = 'Report Type';
            OptionMembers = "JPK_V7M";
            OptionCaption = 'JPK_V7M';
        }
        field(20; Year; Integer)
        {
            Caption = 'Year';
            MinValue = 2020;
            MaxValue = 2050;
        }
        field(21; Month; Integer)
        {
            Caption = 'Month';
            MinValue = 1;
            MaxValue = 12;
        }
        field(30; Status; Option)
        {
            Caption = 'Status';
            OptionMembers = New,Processing,Generated,Error;
            OptionCaption = 'New,Processing,Generated,Error';
        }
        field(40; "Created DateTime"; DateTime)
        {
            Caption = 'Created Date/Time';
        }
        field(41; "Generated DateTime"; DateTime)
        {
            Caption = 'Generated Date/Time';
        }
        field(50; "Created By"; Code[50])
        {
            Caption = 'Created By';
        }
        field(60; "File Name"; Text[250])
        {
            Caption = 'File Name';
        }
        field(70; "Error Message"; Text[250])
        {
            Caption = 'Error Message';
        }
        field(80; "No. of Sales Records"; Integer)
        {
            Caption = 'No. of Sales Records';
        }
        field(81; "No. of Purchase Records"; Integer)
        {
            Caption = 'No. of Purchase Records';
        }
        field(90; "Tax Due (Output)"; Decimal)
        {
            Caption = 'Tax Due (Output VAT)';
        }
        field(91; "Tax Deductible (Input)"; Decimal)
        {
            Caption = 'Tax Deductible (Input VAT)';
        }
        field(100; "Purpose"; Option)
        {
            Caption = 'Purpose';
            OptionMembers = "Original","Correction";
            OptionCaption = 'Original,Correction';
        }
        field(110; "Correction No."; Integer)
        {
            Caption = 'Correction No.';
        }
        field(120; "XML Content"; Blob)
        {
            Caption = 'XML Content';
        }
        field(130; "Schema Valid"; Boolean)
        {
            Caption = 'Schema Valid';
        }
        field(140; "Validation Errors"; Text[2048])
        {
            Caption = 'Validation Errors';
        }
    }

    keys
    {
        key(PK; "Entry No.")
        {
            Clustered = true;
        }
        key(Period; Year, Month)
        {
        }
    }

    trigger OnInsert()
    begin
        "Created DateTime" := CurrentDateTime();
        "Created By" := CopyStr(UserId(), 1, 50);
    end;
}
