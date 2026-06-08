page 50160 "Auction Invoice PDF API"
{
    PageType = API;
    APIPublisher = 'auctionSystem';
    APIGroup = 'pdf';
    APIVersion = 'v1.0';
    EntityName = 'invoicePdfRequest';
    EntitySetName = 'invoicePdfRequests';
    SourceTable = "Auction Invoice PDF Buffer";
    DelayedInsert = true;

    layout
    {
        area(Content)
        {
            repeater(Records)
            {
                field(entryNo; Rec."Entry No.")
                {
                    Editable = false;
                }
                field(documentNo; Rec."Document No.")
                {
                }
                field(documentType; Rec."Document Type")
                {
                }
                field(success; Rec."Success")
                {
                    Editable = false;
                }
                field(errorMessage; Rec."Error Message")
                {
                    Editable = false;
                }
                field(reportIdUsed; Rec."Report ID Used")
                {
                    Editable = false;
                }
                field(attachmentCreated; Rec."Attachment Created")
                {
                    Editable = false;
                }
            }
        }
    }

    trigger OnInsertRecord(BelowxRec: Boolean): Boolean
    var
        PdfGen: Codeunit "Auction Invoice PDF Generator";
    begin
        case Rec."Document Type" of
            Rec."Document Type"::"Sales Invoice":
                PdfGen.GenerateAndAttachInvoicePdf(Rec);
            Rec."Document Type"::"Credit Memo":
                PdfGen.GenerateAndAttachCreditMemoPdf(Rec);
        end;
        Rec.Insert(true);
        exit(false);
    end;
}
