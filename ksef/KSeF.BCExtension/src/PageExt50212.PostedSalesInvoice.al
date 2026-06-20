pageextension 50212 "KPHG Posted Sales Inv Ext" extends "Posted Sales Invoice"
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
                field("KPHG KSeF Number"; Rec."KPHG KSeF Number") { ApplicationArea = All; }
                field("KPHG KSeF Element Ref."; Rec."KPHG KSeF Element Ref.") { ApplicationArea = All; }
                field("KPHG KSeF Submission DT"; Rec."KPHG KSeF Submission DT") { ApplicationArea = All; }
                field("KPHG KSeF Acceptance DT"; Rec."KPHG KSeF Acceptance DT") { ApplicationArea = All; }
                field("KPHG KSeF QR Reference"; Rec."KPHG KSeF QR Reference") { ApplicationArea = All; }
                field("KPHG KSeF Session Ref."; Rec."KPHG KSeF Session Ref.") { ApplicationArea = All; }
                field("KPHG KSeF Error Message"; Rec."KPHG KSeF Error Message") { ApplicationArea = All; }
            }
        }
    }

    actions
    {
        addlast(Processing)
        {
            action("KPHG Send to KSeF")
            {
                ApplicationArea = All;
                Caption = 'Send to KSeF';
                Image = ElectronicDocument;

                trigger OnAction()
                var
                    KSeFManagement: Codeunit "KPHG KSeF Management";
                    SalesInvHeader: Record "Sales Invoice Header";
                begin
                    SalesInvHeader.Get(Rec."No.");
                    KSeFManagement.SendToKSeF(SalesInvHeader);
                    CurrPage.Update(false);
                end;
            }
            action("KPHG Check KSeF Status")
            {
                ApplicationArea = All;
                Caption = 'Check KSeF Status';
                Image = Status;

                trigger OnAction()
                var
                    KSeFManagement: Codeunit "KPHG KSeF Management";
                    SalesInvHeader: Record "Sales Invoice Header";
                begin
                    SalesInvHeader.Get(Rec."No.");
                    KSeFManagement.CheckStatus(SalesInvHeader);
                    CurrPage.Update(false);
                end;
            }
        }
    }
}
