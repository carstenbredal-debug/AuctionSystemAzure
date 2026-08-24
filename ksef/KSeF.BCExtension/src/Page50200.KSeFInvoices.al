page 50200 "KPHG KSeF Invoices"
{
    PageType = List;
    SourceTable = "Sales Invoice Header";
    ApplicationArea = All;
    UsageCategory = Lists;
    Caption = 'KSeF Invoices';
    Editable = false;

    layout
    {
        area(Content)
        {
            repeater(General)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
                field("Sell-to Customer No."; Rec."Sell-to Customer No.") { ApplicationArea = All; }
                field("Sell-to Customer Name"; Rec."Sell-to Customer Name") { ApplicationArea = All; }
                field("Posting Date"; Rec."Posting Date") { ApplicationArea = All; }
                field("Amount Including VAT"; Rec."Amount Including VAT") { ApplicationArea = All; }
                field("KPHG KSeF Required"; Rec."KPHG KSeF Required") { ApplicationArea = All; }
                field("KPHG KSeF Status"; Rec."KPHG KSeF Status") { ApplicationArea = All; }
                field("KPHG KSeF Number"; Rec."KPHG KSeF Number") { ApplicationArea = All; }
                field("KPHG KSeF Element Ref."; Rec."KPHG KSeF Element Ref.") { ApplicationArea = All; }
                field("KPHG KSeF Submission DT"; Rec."KPHG KSeF Submission DT") { ApplicationArea = All; }
                field("KPHG KSeF Acceptance DT"; Rec."KPHG KSeF Acceptance DT") { ApplicationArea = All; }
                field("KPHG KSeF Error Message"; Rec."KPHG KSeF Error Message") { ApplicationArea = All; }
            }
        }
    }

    actions
    {
        area(Processing)
        {
            action("Send to KSeF")
            {
                ApplicationArea = All;
                Caption = 'Send to KSeF';
                Image = ElectronicDocument;

                trigger OnAction()
                var
                    KSeFManagement: Codeunit "KPHG KSeF Management";
                    SalesInvHeader: Record "Sales Invoice Header";
                begin
                    CurrPage.SetSelectionFilter(SalesInvHeader);
                    if SalesInvHeader.FindSet() then
                        repeat
                            KSeFManagement.SendToKSeF(SalesInvHeader);
                        until SalesInvHeader.Next() = 0;
                end;
            }
            action("Reset KSeF Status")
            {
                ApplicationArea = All;
                Caption = 'Reset KSeF Status';
                Image = Restore;
                ToolTip = 'Re-queues the selected invoices for KSeF submission. Only invoices without a KSeF number can be reset.';

                trigger OnAction()
                var
                    KSeFManagement: Codeunit "KPHG KSeF Management";
                    SalesInvHeader: Record "Sales Invoice Header";
                begin
                    CurrPage.SetSelectionFilter(SalesInvHeader);
                    if SalesInvHeader.IsEmpty() then
                        exit;
                    if not Confirm('Reset %1 selected invoice(s) to Ready for re-submission to KSeF?', false, SalesInvHeader.Count()) then
                        exit;
                    if SalesInvHeader.FindSet() then
                        repeat
                            KSeFManagement.ResetKSeFStatus(SalesInvHeader);
                        until SalesInvHeader.Next() = 0;
                    Message('Selected invoices were reset to Ready. The dispatch job will re-send them within a minute.');
                end;
            }
            action("Fetch from KSeF")
            {
                ApplicationArea = All;
                Caption = 'Fetch from KSeF';
                Image = Refresh;
                ToolTip = 'Looks the selected invoices up directly in KSeF by invoice number and fills in the KSeF number, QR reference and acceptance date. Use for invoices whose submit response was lost. No submission is performed.';

                trigger OnAction()
                var
                    KSeFManagement: Codeunit "KPHG KSeF Management";
                    SalesInvHeader: Record "Sales Invoice Header";
                    FoundCount: Integer;
                    NotFoundCount: Integer;
                    SkippedCount: Integer;
                begin
                    CurrPage.SetSelectionFilter(SalesInvHeader);
                    if SalesInvHeader.FindSet() then
                        repeat
                            if SalesInvHeader."KPHG KSeF Number" <> '' then
                                SkippedCount += 1
                            else
                                if KSeFManagement.FetchFromKSeF(SalesInvHeader) then begin
                                    FoundCount += 1;
                                    Commit();
                                end else
                                    NotFoundCount += 1;
                        until SalesInvHeader.Next() = 0;
                    Message('%1 invoice(s) updated from KSeF, %2 not found in KSeF, %3 skipped (already have a KSeF number).',
                        FoundCount, NotFoundCount, SkippedCount);
                end;
            }
            action("Mark as Accepted")
            {
                ApplicationArea = All;
                Caption = 'Mark as Accepted (enter KSeF number)';
                Image = Approve;
                ToolTip = 'Records a KSeF number obtained outside the normal flow (verified in the KSeF portal) and marks the invoice Accepted. No submission is performed.';

                trigger OnAction()
                var
                    KSeFManagement: Codeunit "KPHG KSeF Management";
                    SalesInvHeader: Record "Sales Invoice Header";
                    NumberEntry: Page "KPHG KSeF Number Entry";
                begin
                    NumberEntry.SetDocumentNo(Rec."No.");
                    if NumberEntry.RunModal() <> Action::OK then
                        exit;
                    if NumberEntry.GetKSeFNumber() = '' then
                        exit;
                    SalesInvHeader.Get(Rec."No.");
                    KSeFManagement.MarkAccepted(SalesInvHeader, NumberEntry.GetKSeFNumber());
                    CurrPage.Update(false);
                end;
            }
            action("Check Status")
            {
                ApplicationArea = All;
                Caption = 'Check KSeF Status';
                Image = Status;

                trigger OnAction()
                var
                    KSeFManagement: Codeunit "KPHG KSeF Management";
                    SalesInvHeader: Record "Sales Invoice Header";
                begin
                    CurrPage.SetSelectionFilter(SalesInvHeader);
                    if SalesInvHeader.FindSet() then
                        repeat
                            KSeFManagement.CheckStatus(SalesInvHeader);
                        until SalesInvHeader.Next() = 0;
                end;
            }
        }
    }
}
