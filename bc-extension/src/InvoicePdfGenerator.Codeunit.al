codeunit 50151 "Auction Invoice PDF Generator"
{
    procedure GenerateAndAttachInvoicePdf(var Buffer: Record "Auction Invoice PDF Buffer")
    var
        SalesInvoiceHeader: Record "Sales Invoice Header";
        TempBlob: Codeunit "Temp Blob";
        DocumentAttachment: Record "Document Attachment";
        RecRef: RecordRef;
        OutStr: OutStream;
        InStr: InStream;
        ReportId: Integer;
        FileName: Text;
    begin
        if not SalesInvoiceHeader.Get(Buffer."Document No.") then begin
            Buffer."Error Message" := 'Posted Sales Invoice not found: ' + Buffer."Document No.";
            Buffer.Success := false;
            exit;
        end;

        SalesInvoiceHeader.SetRecFilter();
        ReportId := GetInvoiceReportId();
        Buffer."Report ID Used" := ReportId;

        RecRef.GetTable(SalesInvoiceHeader);
        TempBlob.CreateOutStream(OutStr);
        if not Report.SaveAs(ReportId, '', ReportFormat::Pdf, OutStr, RecRef) then begin
            Buffer."Error Message" := 'Report.SaveAs failed for Report ID ' + Format(ReportId);
            Buffer.Success := false;
            exit;
        end;

        TempBlob.CreateInStream(InStr);
        FileName := 'Invoice_' + SalesInvoiceHeader."No.";

        // Remove any existing PDF attachment with the same name
        DocumentAttachment.SetRange("Table ID", Database::"Sales Invoice Header");
        DocumentAttachment.SetRange("No.", SalesInvoiceHeader."No.");
        DocumentAttachment.SetRange("File Extension", 'pdf');
        DocumentAttachment.SetRange("File Name", FileName);
        if DocumentAttachment.FindFirst() then
            DocumentAttachment.Delete(true);

        // Create new attachment
        Clear(DocumentAttachment);
        DocumentAttachment.Init();
        DocumentAttachment.Validate("Table ID", Database::"Sales Invoice Header");
        DocumentAttachment.Validate("No.", SalesInvoiceHeader."No.");
        DocumentAttachment.Validate("File Name", FileName);
        DocumentAttachment.Validate("File Extension", 'pdf');
        DocumentAttachment."Document Reference ID".ImportStream(InStr, FileName + '.pdf');
        DocumentAttachment.Insert(true);

        Buffer.Success := true;
        Buffer."Attachment Created" := true;
    end;

    procedure GenerateAndAttachCreditMemoPdf(var Buffer: Record "Auction Invoice PDF Buffer")
    var
        SalesCrMemoHeader: Record "Sales Cr.Memo Header";
        TempBlob: Codeunit "Temp Blob";
        DocumentAttachment: Record "Document Attachment";
        RecRef: RecordRef;
        OutStr: OutStream;
        InStr: InStream;
        ReportId: Integer;
        FileName: Text;
    begin
        if not SalesCrMemoHeader.Get(Buffer."Document No.") then begin
            Buffer."Error Message" := 'Posted Sales Credit Memo not found: ' + Buffer."Document No.";
            Buffer.Success := false;
            exit;
        end;

        SalesCrMemoHeader.SetRecFilter();
        ReportId := GetCreditMemoReportId();
        Buffer."Report ID Used" := ReportId;

        RecRef.GetTable(SalesCrMemoHeader);
        TempBlob.CreateOutStream(OutStr);
        if not Report.SaveAs(ReportId, '', ReportFormat::Pdf, OutStr, RecRef) then begin
            Buffer."Error Message" := 'Report.SaveAs failed for Report ID ' + Format(ReportId);
            Buffer.Success := false;
            exit;
        end;

        TempBlob.CreateInStream(InStr);
        FileName := 'CreditMemo_' + SalesCrMemoHeader."No.";

        DocumentAttachment.SetRange("Table ID", Database::"Sales Cr.Memo Header");
        DocumentAttachment.SetRange("No.", SalesCrMemoHeader."No.");
        DocumentAttachment.SetRange("File Extension", 'pdf');
        DocumentAttachment.SetRange("File Name", FileName);
        if DocumentAttachment.FindFirst() then
            DocumentAttachment.Delete(true);

        Clear(DocumentAttachment);
        DocumentAttachment.Init();
        DocumentAttachment.Validate("Table ID", Database::"Sales Cr.Memo Header");
        DocumentAttachment.Validate("No.", SalesCrMemoHeader."No.");
        DocumentAttachment.Validate("File Name", FileName);
        DocumentAttachment.Validate("File Extension", 'pdf');
        DocumentAttachment."Document Reference ID".ImportStream(InStr, FileName + '.pdf');
        DocumentAttachment.Insert(true);

        Buffer.Success := true;
        Buffer."Attachment Created" := true;
    end;

    local procedure GetInvoiceReportId(): Integer
    var
        ReportSelections: Record "Report Selections";
    begin
        ReportSelections.SetRange(Usage, ReportSelections.Usage::"S.Invoice");
        if ReportSelections.FindFirst() then
            exit(ReportSelections."Report ID");
        exit(Report::"Standard Sales - Invoice");
    end;

    local procedure GetCreditMemoReportId(): Integer
    var
        ReportSelections: Record "Report Selections";
    begin
        ReportSelections.SetRange(Usage, ReportSelections.Usage::"S.Cr.Memo");
        if ReportSelections.FindFirst() then
            exit(ReportSelections."Report ID");
        exit(Report::"Standard Sales - Credit Memo");
    end;
}
