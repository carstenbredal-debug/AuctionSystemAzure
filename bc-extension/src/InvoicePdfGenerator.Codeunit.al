codeunit 50151 "Auction Invoice PDF Generator"
{
    procedure GenerateAndAttachInvoicePdf(var Buffer: Record "Auction Invoice PDF Buffer")
    var
        SalesInvoiceHeader: Record "Sales Invoice Header";
        ReportId: Integer;
    begin
        if not SalesInvoiceHeader.Get(Buffer."Document No.") then begin
            Buffer."Error Message" := 'Posted Sales Invoice not found: ' + Buffer."Document No.";
            Buffer.Success := false;
            exit;
        end;

        SalesInvoiceHeader.SetRecFilter();

        // Strategy 1: Try report from Report Selections (Polish report in test)
        ReportId := GetInvoiceReportId();
        Buffer."Report ID Used" := ReportId;

        if TryGenerateAndAttach(SalesInvoiceHeader, ReportId, 'Invoice_' + SalesInvoiceHeader."No.", Database::"Sales Invoice Header", Buffer) then
            exit;

        // Strategy 2: Fall back to Standard Sales Invoice report (1306)
        Buffer."Error Message" := 'Report ' + Format(ReportId) + ' failed: ' + GetLastErrorText() + ' | Trying standard report 1306';
        ReportId := 1306;
        Buffer."Report ID Used" := ReportId;

        if TryGenerateAndAttach(SalesInvoiceHeader, ReportId, 'Invoice_' + SalesInvoiceHeader."No.", Database::"Sales Invoice Header", Buffer) then begin
            Buffer."Error Message" := 'Used fallback standard report 1306 (Polish report failed)';
            exit;
        end;

        Buffer."Error Message" := 'Both reports failed. Last error: ' + GetLastErrorText();
        Buffer.Success := false;
    end;

    procedure GenerateAndAttachCreditMemoPdf(var Buffer: Record "Auction Invoice PDF Buffer")
    var
        SalesCrMemoHeader: Record "Sales Cr.Memo Header";
        ReportId: Integer;
    begin
        if not SalesCrMemoHeader.Get(Buffer."Document No.") then begin
            Buffer."Error Message" := 'Posted Sales Credit Memo not found: ' + Buffer."Document No.";
            Buffer.Success := false;
            exit;
        end;

        SalesCrMemoHeader.SetRecFilter();

        ReportId := GetCreditMemoReportId();
        Buffer."Report ID Used" := ReportId;

        if TryGenerateCrMemoAndAttach(SalesCrMemoHeader, ReportId, 'CreditMemo_' + SalesCrMemoHeader."No.", Database::"Sales Cr.Memo Header", Buffer) then
            exit;

        Buffer."Error Message" := 'Report ' + Format(ReportId) + ' failed: ' + GetLastErrorText() + ' | Trying standard report 1307';
        ReportId := 1307;
        Buffer."Report ID Used" := ReportId;

        if TryGenerateCrMemoAndAttach(SalesCrMemoHeader, ReportId, 'CreditMemo_' + SalesCrMemoHeader."No.", Database::"Sales Cr.Memo Header", Buffer) then begin
            Buffer."Error Message" := 'Used fallback standard report 1307 (Polish report failed)';
            exit;
        end;

        Buffer."Error Message" := 'Both reports failed. Last error: ' + GetLastErrorText();
        Buffer.Success := false;
    end;

    [TryFunction]
    local procedure TryGenerateAndAttach(var SalesInvoiceHeader: Record "Sales Invoice Header"; ReportId: Integer; FileName: Text; TableId: Integer; var Buffer: Record "Auction Invoice PDF Buffer")
    var
        TempBlob: Codeunit "Temp Blob";
        DocumentAttachment: Record "Document Attachment";
        RecRef: RecordRef;
        OutStr: OutStream;
        InStr: InStream;
    begin
        RecRef.GetTable(SalesInvoiceHeader);
        TempBlob.CreateOutStream(OutStr);
        Report.SaveAs(ReportId, '', ReportFormat::Pdf, OutStr, RecRef);

        TempBlob.CreateInStream(InStr);

        // Remove existing attachment
        DocumentAttachment.SetRange("Table ID", TableId);
        DocumentAttachment.SetRange("No.", SalesInvoiceHeader."No.");
        DocumentAttachment.SetRange("File Extension", 'pdf');
        DocumentAttachment.SetRange("File Name", FileName);
        if DocumentAttachment.FindFirst() then
            DocumentAttachment.Delete(true);

        Clear(DocumentAttachment);
        DocumentAttachment.Init();
        DocumentAttachment.Validate("Table ID", TableId);
        DocumentAttachment.Validate("No.", SalesInvoiceHeader."No.");
        DocumentAttachment.Validate("File Name", FileName);
        DocumentAttachment.Validate("File Extension", 'pdf');
        DocumentAttachment."Document Reference ID".ImportStream(InStr, FileName + '.pdf');
        DocumentAttachment.Insert(true);

        Buffer.Success := true;
        Buffer."Attachment Created" := true;
    end;

    [TryFunction]
    local procedure TryGenerateCrMemoAndAttach(var SalesCrMemoHeader: Record "Sales Cr.Memo Header"; ReportId: Integer; FileName: Text; TableId: Integer; var Buffer: Record "Auction Invoice PDF Buffer")
    var
        TempBlob: Codeunit "Temp Blob";
        DocumentAttachment: Record "Document Attachment";
        RecRef: RecordRef;
        OutStr: OutStream;
        InStr: InStream;
    begin
        RecRef.GetTable(SalesCrMemoHeader);
        TempBlob.CreateOutStream(OutStr);
        Report.SaveAs(ReportId, '', ReportFormat::Pdf, OutStr, RecRef);

        TempBlob.CreateInStream(InStr);

        DocumentAttachment.SetRange("Table ID", TableId);
        DocumentAttachment.SetRange("No.", SalesCrMemoHeader."No.");
        DocumentAttachment.SetRange("File Extension", 'pdf');
        DocumentAttachment.SetRange("File Name", FileName);
        if DocumentAttachment.FindFirst() then
            DocumentAttachment.Delete(true);

        Clear(DocumentAttachment);
        DocumentAttachment.Init();
        DocumentAttachment.Validate("Table ID", TableId);
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
