pageextension 50214 "KPHG Posted Sales CrMemo Ext" extends "Posted Sales Credit Memo"
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
                field("KPHG Original Invoice KSeF No."; Rec."KPHG Original Invoice KSeF No.") { ApplicationArea = All; }
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
                    SalesCrMemoHeader: Record "Sales Cr.Memo Header";
                begin
                    SalesCrMemoHeader.Get(Rec."No.");
                    KSeFManagement.SendCrMemoToKSeF(SalesCrMemoHeader);
                    CurrPage.Update(false);
                end;
            }
            action("KPHG Reset KSeF Status")
            {
                ApplicationArea = All;
                Caption = 'Reset KSeF Status';
                Image = Restore;
                ToolTip = 'Re-queues this credit memo for KSeF submission. Only documents without a KSeF number can be reset.';

                trigger OnAction()
                var
                    KSeFManagement: Codeunit "KPHG KSeF Management";
                    SalesCrMemoHeader: Record "Sales Cr.Memo Header";
                begin
                    if not Confirm('Reset credit memo %1 to Ready for re-submission to KSeF?', false, Rec."No.") then
                        exit;
                    SalesCrMemoHeader.Get(Rec."No.");
                    KSeFManagement.ResetCrMemoKSeFStatus(SalesCrMemoHeader);
                    CurrPage.Update(false);
                end;
            }
            action("KPHG Fetch from KSeF")
            {
                ApplicationArea = All;
                Caption = 'Fetch from KSeF';
                Image = Refresh;
                ToolTip = 'Looks this credit memo up directly in KSeF by document number and fills in the KSeF number, QR reference and acceptance date. No submission is performed.';

                trigger OnAction()
                var
                    KSeFManagement: Codeunit "KPHG KSeF Management";
                    SalesCrMemoHeader: Record "Sales Cr.Memo Header";
                begin
                    SalesCrMemoHeader.Get(Rec."No.");
                    if KSeFManagement.FetchCrMemoFromKSeF(SalesCrMemoHeader) then
                        Message('Credit memo %1 found in KSeF. Number: %2', SalesCrMemoHeader."No.", SalesCrMemoHeader."KPHG KSeF Number")
                    else
                        Message('Credit memo %1 was not found in KSeF.', SalesCrMemoHeader."No.");
                    CurrPage.Update(false);
                end;
            }
            action("KPHG Mark as Accepted")
            {
                ApplicationArea = All;
                Caption = 'Mark as Accepted (enter KSeF number)';
                Image = Approve;
                ToolTip = 'Records a KSeF number obtained outside the normal flow (verified in the KSeF portal) and marks the credit memo Accepted. No submission is performed.';

                trigger OnAction()
                var
                    KSeFManagement: Codeunit "KPHG KSeF Management";
                    SalesCrMemoHeader: Record "Sales Cr.Memo Header";
                    NumberEntry: Page "KPHG KSeF Number Entry";
                begin
                    NumberEntry.SetDocumentNo(Rec."No.");
                    if NumberEntry.RunModal() <> Action::OK then
                        exit;
                    if NumberEntry.GetKSeFNumber() = '' then
                        exit;
                    SalesCrMemoHeader.Get(Rec."No.");
                    KSeFManagement.MarkCrMemoAccepted(SalesCrMemoHeader, NumberEntry.GetKSeFNumber());
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
                    SalesCrMemoHeader: Record "Sales Cr.Memo Header";
                begin
                    SalesCrMemoHeader.Get(Rec."No.");
                    KSeFManagement.CheckCrMemoStatus(SalesCrMemoHeader);
                    CurrPage.Update(false);
                end;
            }
        }
    }
}
