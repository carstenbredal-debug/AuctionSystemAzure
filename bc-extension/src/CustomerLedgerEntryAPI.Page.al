page 50100 "Customer Ledger Entries API"
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
                field(entryNo; "Entry No.") { }
                field(postingDate; "Posting Date") { }
                field(documentType; "Document Type") { }
                field(documentNo; "Document No.") { }
                field(customerNo; "Customer No.") { }
                field(customerName; "Customer Name") { }
                field(description; Description) { }
                field(currencyCode; "Currency Code") { }
                field(amount; Amount) { }
                field(remainingAmount; "Remaining Amount") { }
                field(originalAmount; "Original Amount") { }
                field(open; Open) { }
                field(dueDate; "Due Date") { }
                field(closedByEntryNo; "Closed by Entry No.") { }
                field(closedAtDate; "Closed at Date") { }
                field(externalDocumentNo; "External Document No.") { }
            }
        }
    }
}
