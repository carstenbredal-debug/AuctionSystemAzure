page 50152 "Unapply Entries API"
{
    APIPublisher = 'auctionSystem';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'unapplyEntry';
    EntitySetName = 'unapplyEntries';
    PageType = API;
    SourceTable = "Unapply Buffer";
    SourceTableTemporary = true;
    InsertAllowed = true;
    ModifyAllowed = false;
    DeleteAllowed = false;
    ODataKeyFields = Id;
    DelayedInsert = true;

    layout
    {
        area(Content)
        {
            repeater(Group)
            {
                field(id; Rec.Id) { }
                field(entryNo; Rec.EntryNo) { }
                field(resultStatus; Rec.ResultStatus) { }
                field(resultMessage; Rec.ResultMessage) { }
            }
        }
    }

    trigger OnInsertRecord(BelowxRec: Boolean): Boolean
    var
        CustLedgerEntry: Record "Cust. Ledger Entry";
        DetailedCustLedgEntry: Record "Detailed Cust. Ledg. Entry";
        CustEntryApplyPostedEntries: Codeunit "CustEntry-Apply Posted Entries";
        ApplyUnapplyParameters: Record "Apply Unapply Parameters";
    begin
        // Find the customer ledger entry
        if not CustLedgerEntry.Get(Rec.EntryNo) then begin
            Rec.ResultStatus := 'Error';
            Rec.ResultMessage := 'Ledger entry not found: ' + Format(Rec.EntryNo);
            exit(true);
        end;

        // Find the most recent application detailed entry for this ledger entry
        DetailedCustLedgEntry.SetRange("Cust. Ledger Entry No.", Rec.EntryNo);
        DetailedCustLedgEntry.SetRange("Entry Type", DetailedCustLedgEntry."Entry Type"::Application);
        DetailedCustLedgEntry.SetRange(Unapplied, false);
        if not DetailedCustLedgEntry.FindLast() then begin
            Rec.ResultStatus := 'Error';
            Rec.ResultMessage := 'No application entry found for entry ' + Format(Rec.EntryNo);
            exit(true);
        end;

        // Unapply the entry
        ApplyUnapplyParameters."Document No." := DetailedCustLedgEntry."Document No.";
        ApplyUnapplyParameters."Posting Date" := DetailedCustLedgEntry."Posting Date";
        CustEntryApplyPostedEntries.PostUnApplyCustomer(DetailedCustLedgEntry, ApplyUnapplyParameters);

        Rec.ResultStatus := 'Success';
        Rec.ResultMessage := 'Unapplied entry ' + Format(Rec.EntryNo);
        exit(true);
    end;
}
