codeunit 50200 "KPHG Posting Subscribers"
{
    Permissions =
        tabledata "Sales Invoice Header" = RIMD,
        tabledata "Sales Invoice Line" = RIMD,
        tabledata "Sales Cr.Memo Header" = RIMD,
        tabledata "Sales Cr.Memo Line" = RIMD,
        tabledata "KPHG KSeF Setup" = R;
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnAfterSalesInvHeaderInsert', '', false, false)]
    local procedure CopyHeaderFields(var SalesInvHeader: Record "Sales Invoice Header"; SalesHeader: Record "Sales Header")
    var
        Setup: Record "KPHG KSeF Setup";
    begin
        SalesInvHeader."KPHG KSeF Required" := SalesHeader."KPHG KSeF Required";
        SalesInvHeader."KPHG KSeF Status" := SalesHeader."KPHG KSeF Status";
        SalesInvHeader."KPHG KSeF Error Message" := SalesHeader."KPHG KSeF Error Message";

        // Auto-set KSeF Required from setup if not already set
        if not SalesInvHeader."KPHG KSeF Required" then begin
            Setup.GetSetup();
            if Setup."Default KSeF Required" then
                SalesInvHeader."KPHG KSeF Required" := true;
        end;

        if SalesInvHeader."KPHG KSeF Required" and
           (SalesInvHeader."KPHG KSeF Status" = SalesInvHeader."KPHG KSeF Status"::"Not Required")
        then
            SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Ready;

        SalesInvHeader.Modify();
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnAfterSalesInvLineInsert', '', false, false)]
    local procedure CopyLineFields(var SalesInvLine: Record "Sales Invoice Line"; SalesLine: Record "Sales Line")
    begin
        SalesInvLine."KPHG Lot No." := SalesLine."KPHG Lot No.";
        SalesInvLine."KPHG Line Component" := SalesLine."KPHG Line Component";
        SalesInvLine."KPHG Settlement Reference" := SalesLine."KPHG Settlement Reference";
        SalesInvLine."KPHG GTU Code" := SalesLine."KPHG GTU Code";
        SalesInvLine."KPHG PKWiU" := SalesLine."KPHG PKWiU";
        SalesInvLine.Modify();
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnAfterSalesCrMemoHeaderInsert', '', false, false)]
    local procedure CopyCrMemoHeaderFields(var SalesCrMemoHeader: Record "Sales Cr.Memo Header"; SalesHeader: Record "Sales Header")
    var
        Setup: Record "KPHG KSeF Setup";
        OrigInvHeader: Record "Sales Invoice Header";
    begin
        SalesCrMemoHeader."KPHG KSeF Required" := SalesHeader."KPHG KSeF Required";

        // Auto-set KSeF Required from setup if not already set
        if not SalesCrMemoHeader."KPHG KSeF Required" then begin
            Setup.GetSetup();
            if Setup."Default KSeF Required" then
                SalesCrMemoHeader."KPHG KSeF Required" := true;
        end;

        if SalesCrMemoHeader."KPHG KSeF Required" then
            SalesCrMemoHeader."KPHG KSeF Status" := SalesCrMemoHeader."KPHG KSeF Status"::Ready;

        // Copy original invoice KSeF number if applies-to is set
        if SalesHeader."Applies-to Doc. No." <> '' then
            if OrigInvHeader.Get(SalesHeader."Applies-to Doc. No.") then
                SalesCrMemoHeader."KPHG Original Invoice KSeF No." := OrigInvHeader."KPHG KSeF Number";

        SalesCrMemoHeader.Modify();
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnAfterSalesCrMemoLineInsert', '', false, false)]
    local procedure CopyCrMemoLineFields(var SalesCrMemoLine: Record "Sales Cr.Memo Line"; SalesLine: Record "Sales Line")
    begin
        SalesCrMemoLine."KPHG Lot No." := SalesLine."KPHG Lot No.";
        SalesCrMemoLine."KPHG Line Component" := SalesLine."KPHG Line Component";
        SalesCrMemoLine."KPHG Settlement Reference" := SalesLine."KPHG Settlement Reference";
        SalesCrMemoLine."KPHG GTU Code" := SalesLine."KPHG GTU Code";
        SalesCrMemoLine."KPHG PKWiU" := SalesLine."KPHG PKWiU";
        SalesCrMemoLine.Modify();
    end;

    // NOTE: KSeF submission is intentionally NOT done inline on posting anymore. Submitting synchronously
    // inside OnAfterPostSalesDoc made high-volume posting (auction robots) flood KSeF Test — rate limits /
    // session-token exhaustion / timeouts — and blocked BC posting on each call. Posting now only marks the
    // document KSeF "Ready" (CopyHeaderFields above); the paced "KPHG KSeF Dispatch" codeunit (run by a
    // recurring Job Queue Entry) submits Ready documents in bounded batches at a KSeF-tolerable rate.
}
