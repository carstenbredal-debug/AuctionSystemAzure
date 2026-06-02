page 50150 "Customer Ledger Entries API"
{
    APIPublisher = 'auctionSystem';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'customerLedgerEntry';
    EntitySetName = 'customerLedgerEntries';
    PageType = API;
    SourceTable = "Cust. Ledger Entry";
    Editable = false;
    DelayedInsert = true;
    ODataKeyFields = "Entry No.";

    layout
    {
        area(Content)
        {
            repeater(Group)
            {
                field(entryNo; Rec."Entry No.") { }
                field(postingDate; Rec."Posting Date") { }
                field(documentType; Rec."Document Type") { }
                field(documentNo; Rec."Document No.") { }
                field(customerNo; Rec."Customer No.") { }
                field(customerName; Rec."Customer Name") { }
                field(description; Rec.Description) { }
                field(currencyCode; Rec."Currency Code") { }
                field(amount; Rec.Amount) { }
                field(remainingAmount; Rec."Remaining Amount") { }
                field(originalAmount; Rec."Original Amount") { }
                field(open; Rec.Open) { }
                field(dueDate; Rec."Due Date") { }
                field(closedByEntryNo; Rec."Closed by Entry No.") { }
                field(closedAtDate; Rec."Closed at Date") { }
                field(externalDocumentNo; Rec."External Document No.") { }
            }
        }
    }
}
