table 50200 "KPHG KSeF Setup"
{
    Caption = 'KSeF Setup';
    DataClassification = CustomerContent;
    // Posting subscribers read (and auto-create) this setup on every sales post, running as the
    // posting/integration user. Grant the access inherently so posting never depends on the user
    // having the KSeF Admin permission set assigned (which breaks when the company is recreated).
    InherentPermissions = RIMD;
    InherentEntitlements = RIMD;

    fields
    {
        field(1; "Primary Key"; Code[10])
        {
            Caption = 'Primary Key';
        }
        field(10; "Azure Function URL"; Text[250])
        {
            Caption = 'Azure Function URL';
        }
        field(11; "Azure Function Key"; Text[250])
        {
            Caption = 'Azure Function Key';
            ExtendedDatatype = Masked;
        }
        field(20; "KSeF Environment"; Option)
        {
            Caption = 'KSeF Environment';
            OptionMembers = Demo,Test,Production;
            OptionCaption = 'Demo,Test,Production';
        }
        field(30; "Company NIP"; Code[20])
        {
            Caption = 'Company NIP';
        }
        field(50; "Default KSeF Required"; Boolean)
        {
            Caption = 'Default KSeF Required';
            // Default to automatic so a reset that recreates this record (via GetSetup's Init) comes up
            // KSeF-required, instead of reverting to manual. The Install codeunit seeds the same on install.
            InitValue = true;
        }

        // ---- KSeF transfer overview (FlowField counts for the Setup cue tiles) ----
        field(100; "Inv In KSeF"; Integer)
        {
            Caption = 'Invoices in KSeF';
            FieldClass = FlowField;
            Editable = false;
            CalcFormula = count("Sales Invoice Header" where("KPHG KSeF Required" = const(true), "KPHG KSeF Status" = const(Accepted)));
        }
        field(101; "Inv Pending"; Integer)
        {
            Caption = 'Invoices pending';
            FieldClass = FlowField;
            Editable = false;
            CalcFormula = count("Sales Invoice Header" where("KPHG KSeF Required" = const(true), "KPHG KSeF Status" = filter(Ready | Processing | Sent)));
        }
        field(102; "Inv Failed"; Integer)
        {
            Caption = 'Invoices failed';
            FieldClass = FlowField;
            Editable = false;
            CalcFormula = count("Sales Invoice Header" where("KPHG KSeF Required" = const(true), "KPHG KSeF Status" = filter(Rejected | Error)));
        }
        field(110; "CrMemo In KSeF"; Integer)
        {
            Caption = 'Credit memos in KSeF';
            FieldClass = FlowField;
            Editable = false;
            CalcFormula = count("Sales Cr.Memo Header" where("KPHG KSeF Required" = const(true), "KPHG KSeF Status" = const(Accepted)));
        }
        field(111; "CrMemo Pending"; Integer)
        {
            Caption = 'Credit memos pending';
            FieldClass = FlowField;
            Editable = false;
            CalcFormula = count("Sales Cr.Memo Header" where("KPHG KSeF Required" = const(true), "KPHG KSeF Status" = filter(Ready | Processing | Sent)));
        }
        field(112; "CrMemo Failed"; Integer)
        {
            Caption = 'Credit memos failed';
            FieldClass = FlowField;
            Editable = false;
            CalcFormula = count("Sales Cr.Memo Header" where("KPHG KSeF Required" = const(true), "KPHG KSeF Status" = filter(Rejected | Error)));
        }
    }

    keys
    {
        key(PK; "Primary Key")
        {
            Clustered = true;
        }
    }

    procedure GetSetup()
    begin
        if not Get('SETUP') then begin
            Init();
            "Primary Key" := 'SETUP';
            Insert();
        end;
    end;
}
