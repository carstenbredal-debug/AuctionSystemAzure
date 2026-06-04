// Apply Payment API - applies customer payments to invoices in BC (v1.0.0.8)
page 50151 "Apply Payment API"
{
    APIPublisher = 'auctionSystem';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'paymentApplication';
    EntitySetName = 'paymentApplications';
    PageType = API;
    SourceTable = "Payment Application Buffer";
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
                field(customerNo; Rec.CustomerNo) { }
                field(paymentEntryNo; Rec.PaymentEntryNo) { }
                field(invoiceDocumentNo; Rec.InvoiceDocumentNo) { }
                field(amountToApply; Rec.AmountToApply) { }
                field(resultStatus; Rec.ResultStatus) { }
                field(resultMessage; Rec.ResultMessage) { }
                field(sourceDocumentType; Rec.SourceDocumentType) { }
            }
        }
    }

    trigger OnInsertRecord(BelowxRec: Boolean): Boolean
    var
        PaymentEntry: Record "Cust. Ledger Entry";
        InvoiceEntry: Record "Cust. Ledger Entry";
        CustEntryApplyPostedEntries: Codeunit "CustEntry-Apply Posted Entries";
        ApplyUnapplyParameters: Record "Apply Unapply Parameters";
        ApplyingAmount: Decimal;
        PostingDateToUse: Date;
    begin
        // Find the source entry (payment or credit memo)
        if Rec.PaymentEntryNo <> 0 then begin
            PaymentEntry.Get(Rec.PaymentEntryNo);
        end else begin
            // Find by customer number - get the first open entry of the specified type
            PaymentEntry.SetRange("Customer No.", Rec.CustomerNo);
            if (Rec.SourceDocumentType = 'CreditMemo') or (Rec.SourceDocumentType = 'Credit Memo') then
                PaymentEntry.SetRange("Document Type", PaymentEntry."Document Type"::"Credit Memo")
            else
                PaymentEntry.SetRange("Document Type", PaymentEntry."Document Type"::Payment);
            PaymentEntry.SetRange(Open, true);
            if not PaymentEntry.FindFirst() then begin
                Rec.ResultStatus := 'Error';
                Rec.ResultMessage := 'No open entry found for customer ' + Rec.CustomerNo;
                exit(true);
            end;
        end;

        PaymentEntry.CalcFields("Remaining Amount");
        if not PaymentEntry.Open then begin
            Rec.ResultStatus := 'Error';
            Rec.ResultMessage := 'Payment entry is not open.';
            exit(true);
        end;

        // Find the invoice entry by document number
        InvoiceEntry.SetRange("Customer No.", PaymentEntry."Customer No.");
        InvoiceEntry.SetRange("Document Type", InvoiceEntry."Document Type"::Invoice);
        InvoiceEntry.SetRange("Document No.", Rec.InvoiceDocumentNo);
        InvoiceEntry.SetRange(Open, true);
        if not InvoiceEntry.FindFirst() then begin
            Rec.ResultStatus := 'Error';
            Rec.ResultMessage := 'No open invoice found with document no. ' + Rec.InvoiceDocumentNo;
            exit(true);
        end;

        InvoiceEntry.CalcFields("Remaining Amount");

        // Determine amount to apply (always positive for calculation)
        ApplyingAmount := Rec.AmountToApply;
        if ApplyingAmount = 0 then
            ApplyingAmount := Abs(InvoiceEntry."Remaining Amount");

        // Cap at available source remaining
        if ApplyingAmount > Abs(PaymentEntry."Remaining Amount") then
            ApplyingAmount := Abs(PaymentEntry."Remaining Amount");

        // Set application on invoice entry
        InvoiceEntry."Applies-to ID" := CopyStr(UserId(), 1, 50);
        if InvoiceEntry."Remaining Amount" > 0 then
            InvoiceEntry."Amount to Apply" := ApplyingAmount
        else
            InvoiceEntry."Amount to Apply" := -ApplyingAmount;
        InvoiceEntry.Modify(true);

        // Set application on payment/credit memo entry
        PaymentEntry."Applies-to ID" := CopyStr(UserId(), 1, 50);
        if PaymentEntry."Remaining Amount" < 0 then
            PaymentEntry."Amount to Apply" := -ApplyingAmount
        else
            PaymentEntry."Amount to Apply" := ApplyingAmount;
        PaymentEntry.Modify(true);

        // Determine posting date: use the latest date among Today, payment, and invoice
        PostingDateToUse := Today;
        if PaymentEntry."Posting Date" > PostingDateToUse then
            PostingDateToUse := PaymentEntry."Posting Date";
        if InvoiceEntry."Posting Date" > PostingDateToUse then
            PostingDateToUse := InvoiceEntry."Posting Date";

        // Post the application
        ApplyUnapplyParameters."Document No." := PaymentEntry."Document No.";
        ApplyUnapplyParameters."Posting Date" := PostingDateToUse;
        CustEntryApplyPostedEntries.Apply(PaymentEntry, ApplyUnapplyParameters);

        Rec.ResultStatus := 'Success';
        Rec.ResultMessage := 'Applied ' + Format(ApplyingAmount) + ' to invoice ' + Rec.InvoiceDocumentNo;
        Rec.AmountToApply := ApplyingAmount;
        exit(true);
    end;
}
