// Apply Payment API - applies customer payments to invoices in BC (v1.0.0.7)
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
        StaleEntry: Record "Cust. Ledger Entry";
        CustEntryApplyPostedEntries: Codeunit "CustEntry-Apply Posted Entries";
        ApplyUnapplyParameters: Record "Apply Unapply Parameters";
        ApplyingAmount: Decimal;
        PostingDateToUse: Date;
        AppliesToId: Code[50];
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

        // Clear stale Applies-to ID from ALL open entries for this customer
        // This prevents previous failed attempts from polluting the application
        AppliesToId := CopyStr(UserId(), 1, 50);
        StaleEntry.SetRange("Customer No.", PaymentEntry."Customer No.");
        StaleEntry.SetRange(Open, true);
        StaleEntry.SetFilter("Applies-to ID", '<>%1', '');
        if StaleEntry.FindSet(true) then
            repeat
                StaleEntry."Applies-to ID" := '';
                StaleEntry."Amount to Apply" := 0;
                StaleEntry.Modify(true);
            until StaleEntry.Next() = 0;

        // Re-fetch entries after clearing (they may have been modified)
        PaymentEntry.Get(PaymentEntry."Entry No.");
        PaymentEntry.CalcFields("Remaining Amount");
        InvoiceEntry.Get(InvoiceEntry."Entry No.");
        InvoiceEntry.CalcFields("Remaining Amount");

        // Set application on invoice entry
        InvoiceEntry."Applies-to ID" := AppliesToId;
        if InvoiceEntry."Remaining Amount" > 0 then
            InvoiceEntry."Amount to Apply" := ApplyingAmount
        else
            InvoiceEntry."Amount to Apply" := -ApplyingAmount;
        InvoiceEntry.Modify(true);

        // Set application on payment/credit memo entry
        PaymentEntry."Applies-to ID" := AppliesToId;
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

        // Post the application with error handling
        ApplyUnapplyParameters."Document No." := PaymentEntry."Document No.";
        ApplyUnapplyParameters."Posting Date" := PostingDateToUse;

        ClearLastError();
        if not TryApplyPayment(CustEntryApplyPostedEntries, PaymentEntry, ApplyUnapplyParameters) then begin
            Rec.ResultStatus := 'Error';
            Rec.ResultMessage := 'v1.0.0.7 Apply failed: ' + GetLastErrorText() +
                ' (postingDate=' + Format(PostingDateToUse) +
                ', payDate=' + Format(PaymentEntry."Posting Date") +
                ', invDate=' + Format(InvoiceEntry."Posting Date") +
                ', today=' + Format(Today) + ')';
            exit(true);
        end;

        Rec.ResultStatus := 'Success';
        Rec.ResultMessage := 'v1.0.0.7 Applied ' + Format(ApplyingAmount) + ' to invoice ' + Rec.InvoiceDocumentNo + ' (date=' + Format(PostingDateToUse) + ')';
        Rec.AmountToApply := ApplyingAmount;
        exit(true);
    end;

    [TryFunction]
    local procedure TryApplyPayment(var CustEntryApplyPostedEntries: Codeunit "CustEntry-Apply Posted Entries"; var PaymentEntry: Record "Cust. Ledger Entry"; var ApplyUnapplyParameters: Record "Apply Unapply Parameters")
    begin
        CustEntryApplyPostedEntries.Apply(PaymentEntry, ApplyUnapplyParameters);
    end;
}
