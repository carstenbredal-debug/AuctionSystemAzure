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
