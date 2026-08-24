page 50202 "KPHG KSeF Number Entry"
{
    PageType = StandardDialog;
    Caption = 'Enter KSeF Number';

    layout
    {
        area(Content)
        {
            field(DocNo; DocNo)
            {
                ApplicationArea = All;
                Caption = 'Document No.';
                Editable = false;
            }
            field(KSeFNumber; KSeFNumber)
            {
                ApplicationArea = All;
                Caption = 'KSeF Number';
                ToolTip = 'The KSeF registration number as verified in the KSeF portal.';
            }
        }
    }

    var
        DocNo: Code[20];
        KSeFNumber: Code[100];

    procedure SetDocumentNo(No: Code[20])
    begin
        DocNo := No;
    end;

    procedure GetKSeFNumber(): Code[100]
    begin
        exit(KSeFNumber);
    end;
}
