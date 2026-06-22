codeunit 50201 "KPHG KSeF Management"
{
    Permissions =
        tabledata "Sales Invoice Header" = RIMD,
        tabledata "Sales Cr.Memo Header" = RIMD,
        tabledata "KPHG KSeF Setup" = R;

    procedure MarkReady(var SalesInvHeader: Record "Sales Invoice Header")
    begin
        if not SalesInvHeader."KPHG KSeF Required" then
            Error('KSeF is not required for invoice %1.', SalesInvHeader."No.");

        SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Ready;
        SalesInvHeader."KPHG KSeF Error Message" := '';
        SalesInvHeader.Modify(true);
    end;

    procedure SendToKSeF(var SalesInvHeader: Record "Sales Invoice Header")
    var
        Setup: Record "KPHG KSeF Setup";
        Client: HttpClient;
        Content: HttpContent;
        Headers: HttpHeaders;
        ResponseMessage: HttpResponseMessage;
        RequestBody: Text;
        ResponseText: Text;
        TextValue: Text;
        JsonResponse: JsonObject;
        JsonToken: JsonToken;
        Success: Boolean;
    begin
        if not SalesInvHeader."KPHG KSeF Required" then
            Error('KSeF is not required for invoice %1.', SalesInvHeader."No.");

        if SalesInvHeader."KPHG KSeF Status" = SalesInvHeader."KPHG KSeF Status"::Accepted then
            Error('Invoice %1 has already been accepted by KSeF.', SalesInvHeader."No.");

        Setup.GetSetup();
        if Setup."Azure Function URL" = '' then
            Error('Azure Function URL is not configured. Please set up KSeF integration in the KSeF Setup page.');

        RequestBody := BuildInvoiceJson(SalesInvHeader);

        Content.WriteFrom(RequestBody);
        Content.GetHeaders(Headers);
        Headers.Remove('Content-Type');
        Headers.Add('Content-Type', 'application/json');

        Client.DefaultRequestHeaders().Add('x-functions-key', Setup."Azure Function Key");

        // Mark as Processing before the HTTP call
        SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Processing;
        SalesInvHeader."KPHG KSeF Send Attempts" := SalesInvHeader."KPHG KSeF Send Attempts" + 1;
        SalesInvHeader."KPHG KSeF Error Message" := '';
        SalesInvHeader.Modify(true);
        Commit();

        Success := Client.Post(Setup."Azure Function URL" + '/invoice/submit', Content, ResponseMessage);

        if not Success then begin
            SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Error;
            SalesInvHeader."KPHG KSeF Error Message" := 'HTTP request failed.';
            SalesInvHeader.Modify(true);
            Error('Failed to connect to Azure Function.');
        end;

        ResponseMessage.Content().ReadAs(ResponseText);
        JsonResponse.ReadFrom(ResponseText);

        if not TryGetJsonText(JsonResponse, 'success', TextValue) then
            TextValue := '';

        if TextValue = 'true' then begin
            SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Sent;
            SalesInvHeader."KPHG KSeF Submission DT" := CurrentDateTime();
            SalesInvHeader."KPHG KSeF Error Message" := '';

            if TryGetJsonText(JsonResponse, 'elementReferenceNumber', TextValue) then
                SalesInvHeader."KPHG KSeF Element Ref." := CopyStr(TextValue, 1, 100);

            if TryGetJsonText(JsonResponse, 'sessionReferenceNumber', TextValue) then
                SalesInvHeader."KPHG KSeF Session Ref." := CopyStr(TextValue, 1, 100);

            if TryGetJsonText(JsonResponse, 'kSeFReferenceNumber', TextValue) then begin
                SalesInvHeader."KPHG KSeF Number" := CopyStr(TextValue, 1, 100);
                SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Accepted;
                SalesInvHeader."KPHG KSeF Acceptance DT" := CurrentDateTime();
            end;

            if TryGetJsonText(JsonResponse, 'qrVerificationUrl', TextValue) then
                SalesInvHeader."KPHG KSeF QR Reference" := CopyStr(TextValue, 1, 250);

            SalesInvHeader.Modify(true);
            Message('Invoice %1 submitted to KSeF successfully.', SalesInvHeader."No.");
        end else begin
            SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Error;
            if TryGetJsonText(JsonResponse, 'error', TextValue) then
                SalesInvHeader."KPHG KSeF Error Message" := CopyStr(FormatErrorMessage(TextValue), 1, 250)
            else
                SalesInvHeader."KPHG KSeF Error Message" := 'Unknown error from Azure Function.';
            SalesInvHeader.Modify(true);
            Error('KSeF submission failed: %1', SalesInvHeader."KPHG KSeF Error Message");
        end;
    end;

    procedure AutoSendInvoiceToKSeF(var SalesInvHeader: Record "Sales Invoice Header")
    var
        Setup: Record "KPHG KSeF Setup";
        Client: HttpClient;
        Content: HttpContent;
        Headers: HttpHeaders;
        ResponseMessage: HttpResponseMessage;
        RequestBody: Text;
        ResponseText: Text;
        TextValue: Text;
        JsonResponse: JsonObject;
        Success: Boolean;
        NewStatus: Enum "KPHG KSeF Status";
        ErrText: Text;
        DupNumber: Text;
    begin
        if not SalesInvHeader."KPHG KSeF Required" then
            exit;

        if SalesInvHeader."KPHG KSeF Status" = SalesInvHeader."KPHG KSeF Status"::Accepted then
            exit;

        Setup.GetSetup();
        if Setup."Azure Function URL" = '' then
            exit;

        RequestBody := BuildInvoiceJson(SalesInvHeader);

        Content.WriteFrom(RequestBody);
        Content.GetHeaders(Headers);
        Headers.Remove('Content-Type');
        Headers.Add('Content-Type', 'application/json');

        Client.DefaultRequestHeaders().Add('x-functions-key', Setup."Azure Function Key");

        SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Processing;
        SalesInvHeader."KPHG KSeF Send Attempts" := SalesInvHeader."KPHG KSeF Send Attempts" + 1;
        SalesInvHeader."KPHG KSeF Error Message" := '';
        SalesInvHeader.Modify(true);
        Commit();

        Success := Client.Post(Setup."Azure Function URL" + '/invoice/submit', Content, ResponseMessage);

        if not Success then begin
            // Couldn't reach the function — transient; retry until the cap, then give up.
            if SalesInvHeader."KPHG KSeF Send Attempts" < MaxSendAttempts() then
                SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Error
            else
                SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Rejected;
            SalesInvHeader."KPHG KSeF Error Message" := 'Auto-send failed: HTTP request failed.';
            SalesInvHeader.Modify(true);
            exit;
        end;

        ResponseMessage.Content().ReadAs(ResponseText);
        JsonResponse.ReadFrom(ResponseText);

        if not TryGetJsonText(JsonResponse, 'success', TextValue) then
            TextValue := '';

        if TextValue = 'true' then begin
            SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Sent;
            SalesInvHeader."KPHG KSeF Submission DT" := CurrentDateTime();
            SalesInvHeader."KPHG KSeF Error Message" := '';

            if TryGetJsonText(JsonResponse, 'elementReferenceNumber', TextValue) then
                SalesInvHeader."KPHG KSeF Element Ref." := CopyStr(TextValue, 1, 100);

            if TryGetJsonText(JsonResponse, 'sessionReferenceNumber', TextValue) then
                SalesInvHeader."KPHG KSeF Session Ref." := CopyStr(TextValue, 1, 100);

            if TryGetJsonText(JsonResponse, 'kSeFReferenceNumber', TextValue) then begin
                SalesInvHeader."KPHG KSeF Number" := CopyStr(TextValue, 1, 100);
                SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Accepted;
                SalesInvHeader."KPHG KSeF Acceptance DT" := CurrentDateTime();
            end;

            if TryGetJsonText(JsonResponse, 'qrVerificationUrl', TextValue) then
                SalesInvHeader."KPHG KSeF QR Reference" := CopyStr(TextValue, 1, 250);

            SalesInvHeader.Modify(true);
        end else begin
            // Non-success: classify terminal vs transient (and duplicate => already accepted).
            NewStatus := ClassifyFailure(JsonResponse, SalesInvHeader."KPHG KSeF Send Attempts", ErrText, DupNumber);
            SalesInvHeader."KPHG KSeF Status" := NewStatus;
            SalesInvHeader."KPHG KSeF Error Message" := CopyStr('Auto-send: ' + ErrText, 1, 250);
            if NewStatus = NewStatus::Accepted then begin
                if DupNumber <> '' then
                    SalesInvHeader."KPHG KSeF Number" := CopyStr(DupNumber, 1, 100);
                SalesInvHeader."KPHG KSeF Acceptance DT" := CurrentDateTime();
            end;
            SalesInvHeader.Modify(true);
        end;
    end;

    procedure AutoSendCrMemoToKSeF(var SalesCrMemoHeader: Record "Sales Cr.Memo Header")
    var
        Setup: Record "KPHG KSeF Setup";
        Client: HttpClient;
        Content: HttpContent;
        Headers: HttpHeaders;
        ResponseMessage: HttpResponseMessage;
        RequestBody: Text;
        ResponseText: Text;
        TextValue: Text;
        JsonResponse: JsonObject;
        Success: Boolean;
        NewStatus: Enum "KPHG KSeF Status";
        ErrText: Text;
        DupNumber: Text;
    begin
        if not SalesCrMemoHeader."KPHG KSeF Required" then
            exit;

        if SalesCrMemoHeader."KPHG KSeF Status" = SalesCrMemoHeader."KPHG KSeF Status"::Accepted then
            exit;

        Setup.GetSetup();
        if Setup."Azure Function URL" = '' then
            exit;

        RequestBody := BuildCrMemoJson(SalesCrMemoHeader);

        Content.WriteFrom(RequestBody);
        Content.GetHeaders(Headers);
        Headers.Remove('Content-Type');
        Headers.Add('Content-Type', 'application/json');

        Client.DefaultRequestHeaders().Add('x-functions-key', Setup."Azure Function Key");

        SalesCrMemoHeader."KPHG KSeF Status" := SalesCrMemoHeader."KPHG KSeF Status"::Processing;
        SalesCrMemoHeader."KPHG KSeF Send Attempts" := SalesCrMemoHeader."KPHG KSeF Send Attempts" + 1;
        SalesCrMemoHeader."KPHG KSeF Error Message" := '';
        SalesCrMemoHeader.Modify(true);
        Commit();

        Success := Client.Post(Setup."Azure Function URL" + '/invoice/submit', Content, ResponseMessage);

        if not Success then begin
            // Couldn't reach the function — transient; retry until the cap, then give up.
            if SalesCrMemoHeader."KPHG KSeF Send Attempts" < MaxSendAttempts() then
                SalesCrMemoHeader."KPHG KSeF Status" := SalesCrMemoHeader."KPHG KSeF Status"::Error
            else
                SalesCrMemoHeader."KPHG KSeF Status" := SalesCrMemoHeader."KPHG KSeF Status"::Rejected;
            SalesCrMemoHeader."KPHG KSeF Error Message" := 'Auto-send failed: HTTP request failed.';
            SalesCrMemoHeader.Modify(true);
            exit;
        end;

        ResponseMessage.Content().ReadAs(ResponseText);
        JsonResponse.ReadFrom(ResponseText);

        if not TryGetJsonText(JsonResponse, 'success', TextValue) then
            TextValue := '';

        if TextValue = 'true' then begin
            SalesCrMemoHeader."KPHG KSeF Status" := SalesCrMemoHeader."KPHG KSeF Status"::Sent;
            SalesCrMemoHeader."KPHG KSeF Submission DT" := CurrentDateTime();
            SalesCrMemoHeader."KPHG KSeF Error Message" := '';

            if TryGetJsonText(JsonResponse, 'elementReferenceNumber', TextValue) then
                SalesCrMemoHeader."KPHG KSeF Element Ref." := CopyStr(TextValue, 1, 100);

            if TryGetJsonText(JsonResponse, 'sessionReferenceNumber', TextValue) then
                SalesCrMemoHeader."KPHG KSeF Session Ref." := CopyStr(TextValue, 1, 100);

            if TryGetJsonText(JsonResponse, 'kSeFReferenceNumber', TextValue) then begin
                SalesCrMemoHeader."KPHG KSeF Number" := CopyStr(TextValue, 1, 100);
                SalesCrMemoHeader."KPHG KSeF Status" := SalesCrMemoHeader."KPHG KSeF Status"::Accepted;
                SalesCrMemoHeader."KPHG KSeF Acceptance DT" := CurrentDateTime();
            end;

            if TryGetJsonText(JsonResponse, 'qrVerificationUrl', TextValue) then
                SalesCrMemoHeader."KPHG KSeF QR Reference" := CopyStr(TextValue, 1, 250);

            SalesCrMemoHeader.Modify(true);
        end else begin
            // Non-success: classify terminal vs transient (and duplicate => already accepted).
            NewStatus := ClassifyFailure(JsonResponse, SalesCrMemoHeader."KPHG KSeF Send Attempts", ErrText, DupNumber);
            SalesCrMemoHeader."KPHG KSeF Status" := NewStatus;
            SalesCrMemoHeader."KPHG KSeF Error Message" := CopyStr('Auto-send: ' + ErrText, 1, 250);
            if NewStatus = NewStatus::Accepted then begin
                if DupNumber <> '' then
                    SalesCrMemoHeader."KPHG KSeF Number" := CopyStr(DupNumber, 1, 100);
                SalesCrMemoHeader."KPHG KSeF Acceptance DT" := CurrentDateTime();
            end;
            SalesCrMemoHeader.Modify(true);
        end;
    end;

    // Map a non-success KSeF response to a TERMINAL or RETRYABLE status, using the function's
    // 'duplicate' / 'retryable' flags. Duplicate => Accepted (it IS in KSeF). A genuinely terminal
    // failure (validation / KSeF reject) => Rejected. A transient failure => Error until the attempt
    // cap, then Rejected — so a failing document can never loop forever.
    local procedure ClassifyFailure(JsonResponse: JsonObject; CurrentAttempts: Integer; var ErrorMsg: Text; var DuplicateKSeFNumber: Text): Enum "KPHG KSeF Status"
    var
        ResultStatus: Enum "KPHG KSeF Status";
        FlagValue: Text;
        TextValue: Text;
    begin
        DuplicateKSeFNumber := '';
        if TryGetJsonText(JsonResponse, 'error', TextValue) then
            ErrorMsg := FormatErrorMessage(TextValue)
        else
            ErrorMsg := 'Unknown error from Azure Function.';

        if not TryGetJsonText(JsonResponse, 'duplicate', FlagValue) then
            FlagValue := '';
        if FlagValue = 'true' then begin
            if TryGetJsonText(JsonResponse, 'kSeFReferenceNumber', TextValue) then
                DuplicateKSeFNumber := TextValue;
            ErrorMsg := 'Already in KSeF (duplicate).';
            exit(ResultStatus::Accepted);
        end;

        if not TryGetJsonText(JsonResponse, 'retryable', FlagValue) then
            FlagValue := '';
        if (FlagValue = 'true') and (CurrentAttempts < MaxSendAttempts()) then
            exit(ResultStatus::Error);     // transient — eligible for another run
        exit(ResultStatus::Rejected);      // terminal, or out of retries
    end;

    local procedure MaxSendAttempts(): Integer
    begin
        exit(5);
    end;

    procedure CheckStatus(var SalesInvHeader: Record "Sales Invoice Header")
    var
        Setup: Record "KPHG KSeF Setup";
        Client: HttpClient;
        ResponseMessage: HttpResponseMessage;
        ResponseText: Text;
        TextValue: Text;
        JsonResponse: JsonObject;
        JsonToken: JsonToken;
        Url: Text;
        Success: Boolean;
    begin
        if SalesInvHeader."KPHG KSeF Element Ref." = '' then
            Error('No KSeF Element Reference found for invoice %1. Submit the invoice first.', SalesInvHeader."No.");

        Setup.GetSetup();
        if Setup."Azure Function URL" = '' then
            Error('Azure Function URL is not configured.');

        Url := Setup."Azure Function URL" + '/invoice/status/' + SalesInvHeader."KPHG KSeF Element Ref."
            + '?nip=' + Setup."Company NIP"
            + '&sessionRef=' + SalesInvHeader."KPHG KSeF Session Ref.";

        Client.DefaultRequestHeaders().Add('x-functions-key', Setup."Azure Function Key");
        Success := Client.Get(Url, ResponseMessage);

        if not Success then
            Error('Failed to connect to Azure Function.');

        ResponseMessage.Content().ReadAs(ResponseText);
        JsonResponse.ReadFrom(ResponseText);

        if not TryGetJsonText(JsonResponse, 'success', TextValue) then
            TextValue := '';

        if TextValue = 'true' then begin
            if TryGetJsonText(JsonResponse, 'kSeFReferenceNumber', TextValue) then begin
                SalesInvHeader."KPHG KSeF Number" := CopyStr(TextValue, 1, 100);
                SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Accepted;
                SalesInvHeader."KPHG KSeF Acceptance DT" := CurrentDateTime();
                SalesInvHeader."KPHG KSeF Error Message" := '';
                if TryGetJsonText(JsonResponse, 'qrVerificationUrl', TextValue) then
                    SalesInvHeader."KPHG KSeF QR Reference" := CopyStr(TextValue, 1, 250);
                SalesInvHeader.Modify(true);
                Message('Invoice %1 accepted by KSeF. Number: %2', SalesInvHeader."No.", SalesInvHeader."KPHG KSeF Number");
            end else begin
                if TryGetJsonText(JsonResponse, 'processingDescription', TextValue) then
                    Message('Invoice %1 still processing: %2', SalesInvHeader."No.", TextValue)
                else
                    Message('Invoice %1 still processing.', SalesInvHeader."No.");
            end;
        end else begin
            if TryGetJsonText(JsonResponse, 'error', TextValue) then
                SalesInvHeader."KPHG KSeF Error Message" := CopyStr(FormatErrorMessage(TextValue), 1, 250);
            SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Error;
            SalesInvHeader.Modify(true);
            Error('Status check failed: %1', SalesInvHeader."KPHG KSeF Error Message");
        end;
    end;

    procedure TestConnection()
    var
        Setup: Record "KPHG KSeF Setup";
        Client: HttpClient;
        ResponseMessage: HttpResponseMessage;
        ResponseText: Text;
        Success: Boolean;
    begin
        Setup.GetSetup();
        if Setup."Azure Function URL" = '' then
            Error('Azure Function URL is not configured.');

        Success := Client.Get(Setup."Azure Function URL" + '/health', ResponseMessage);

        if Success and ResponseMessage.IsSuccessStatusCode() then
            Message('Connection successful! Azure Function is healthy.')
        else begin
            if ResponseMessage.Content().ReadAs(ResponseText) then
                Error('Connection failed: %1', ResponseText)
            else
                Error('Connection failed: Unable to reach Azure Function.');
        end;
    end;

    procedure MarkAccepted(var SalesInvHeader: Record "Sales Invoice Header"; KSeFNumber: Code[100])
    begin
        SalesInvHeader."KPHG KSeF Number" := KSeFNumber;
        SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Accepted;
        SalesInvHeader."KPHG KSeF Acceptance DT" := CurrentDateTime();
        SalesInvHeader."KPHG KSeF Error Message" := '';
        SalesInvHeader.Modify(true);
    end;

    procedure MarkRejected(var SalesInvHeader: Record "Sales Invoice Header"; ErrorMessage: Text[250])
    begin
        SalesInvHeader."KPHG KSeF Status" := SalesInvHeader."KPHG KSeF Status"::Rejected;
        SalesInvHeader."KPHG KSeF Error Message" := ErrorMessage;
        SalesInvHeader.Modify(true);
    end;

    procedure SendCrMemoToKSeF(var SalesCrMemoHeader: Record "Sales Cr.Memo Header")
    var
        Setup: Record "KPHG KSeF Setup";
        Client: HttpClient;
        Content: HttpContent;
        Headers: HttpHeaders;
        ResponseMessage: HttpResponseMessage;
        RequestBody: Text;
        ResponseText: Text;
        TextValue: Text;
        JsonResponse: JsonObject;
        JsonToken: JsonToken;
        Success: Boolean;
    begin
        if not SalesCrMemoHeader."KPHG KSeF Required" then
            Error('KSeF is not required for credit memo %1.', SalesCrMemoHeader."No.");

        if SalesCrMemoHeader."KPHG KSeF Status" = SalesCrMemoHeader."KPHG KSeF Status"::Accepted then
            Error('Credit memo %1 has already been accepted by KSeF.', SalesCrMemoHeader."No.");

        Setup.GetSetup();
        if Setup."Azure Function URL" = '' then
            Error('Azure Function URL is not configured.');

        RequestBody := BuildCrMemoJson(SalesCrMemoHeader);

        Content.WriteFrom(RequestBody);
        Content.GetHeaders(Headers);
        Headers.Remove('Content-Type');
        Headers.Add('Content-Type', 'application/json');

        Client.DefaultRequestHeaders().Add('x-functions-key', Setup."Azure Function Key");

        SalesCrMemoHeader."KPHG KSeF Status" := SalesCrMemoHeader."KPHG KSeF Status"::Processing;
        SalesCrMemoHeader."KPHG KSeF Send Attempts" := SalesCrMemoHeader."KPHG KSeF Send Attempts" + 1;
        SalesCrMemoHeader."KPHG KSeF Error Message" := '';
        SalesCrMemoHeader.Modify(true);
        Commit();

        Success := Client.Post(Setup."Azure Function URL" + '/invoice/submit', Content, ResponseMessage);

        if not Success then begin
            SalesCrMemoHeader."KPHG KSeF Status" := SalesCrMemoHeader."KPHG KSeF Status"::Error;
            SalesCrMemoHeader."KPHG KSeF Error Message" := 'HTTP request failed.';
            SalesCrMemoHeader.Modify(true);
            Error('Failed to connect to Azure Function.');
        end;

        ResponseMessage.Content().ReadAs(ResponseText);
        JsonResponse.ReadFrom(ResponseText);

        if not TryGetJsonText(JsonResponse, 'success', TextValue) then
            TextValue := '';

        if TextValue = 'true' then begin
            SalesCrMemoHeader."KPHG KSeF Status" := SalesCrMemoHeader."KPHG KSeF Status"::Sent;
            SalesCrMemoHeader."KPHG KSeF Submission DT" := CurrentDateTime();
            SalesCrMemoHeader."KPHG KSeF Error Message" := '';

            if TryGetJsonText(JsonResponse, 'elementReferenceNumber', TextValue) then
                SalesCrMemoHeader."KPHG KSeF Element Ref." := CopyStr(TextValue, 1, 100);

            if TryGetJsonText(JsonResponse, 'sessionReferenceNumber', TextValue) then
                SalesCrMemoHeader."KPHG KSeF Session Ref." := CopyStr(TextValue, 1, 100);

            if TryGetJsonText(JsonResponse, 'kSeFReferenceNumber', TextValue) then begin
                SalesCrMemoHeader."KPHG KSeF Number" := CopyStr(TextValue, 1, 100);
                SalesCrMemoHeader."KPHG KSeF Status" := SalesCrMemoHeader."KPHG KSeF Status"::Accepted;
                SalesCrMemoHeader."KPHG KSeF Acceptance DT" := CurrentDateTime();
            end;

            if TryGetJsonText(JsonResponse, 'qrVerificationUrl', TextValue) then
                SalesCrMemoHeader."KPHG KSeF QR Reference" := CopyStr(TextValue, 1, 250);

            SalesCrMemoHeader.Modify(true);
            Message('Credit memo %1 submitted to KSeF successfully.', SalesCrMemoHeader."No.");
        end else begin
            SalesCrMemoHeader."KPHG KSeF Status" := SalesCrMemoHeader."KPHG KSeF Status"::Error;
            if TryGetJsonText(JsonResponse, 'error', TextValue) then
                SalesCrMemoHeader."KPHG KSeF Error Message" := CopyStr(FormatErrorMessage(TextValue), 1, 250)
            else
                SalesCrMemoHeader."KPHG KSeF Error Message" := 'Unknown error from Azure Function.';
            SalesCrMemoHeader.Modify(true);
            Error('KSeF submission failed: %1', SalesCrMemoHeader."KPHG KSeF Error Message");
        end;
    end;

    procedure CheckCrMemoStatus(var SalesCrMemoHeader: Record "Sales Cr.Memo Header")
    var
        Setup: Record "KPHG KSeF Setup";
        Client: HttpClient;
        ResponseMessage: HttpResponseMessage;
        ResponseText: Text;
        TextValue: Text;
        JsonResponse: JsonObject;
        JsonToken: JsonToken;
        Url: Text;
        Success: Boolean;
    begin
        if SalesCrMemoHeader."KPHG KSeF Element Ref." = '' then
            Error('No KSeF Element Reference found for credit memo %1. Submit it first.', SalesCrMemoHeader."No.");

        Setup.GetSetup();
        if Setup."Azure Function URL" = '' then
            Error('Azure Function URL is not configured.');

        Url := Setup."Azure Function URL" + '/invoice/status/' + SalesCrMemoHeader."KPHG KSeF Element Ref."
            + '?nip=' + Setup."Company NIP"
            + '&sessionRef=' + SalesCrMemoHeader."KPHG KSeF Session Ref.";

        Client.DefaultRequestHeaders().Add('x-functions-key', Setup."Azure Function Key");
        Success := Client.Get(Url, ResponseMessage);

        if not Success then
            Error('Failed to connect to Azure Function.');

        ResponseMessage.Content().ReadAs(ResponseText);
        JsonResponse.ReadFrom(ResponseText);

        if not TryGetJsonText(JsonResponse, 'success', TextValue) then
            TextValue := '';

        if TextValue = 'true' then begin
            if TryGetJsonText(JsonResponse, 'kSeFReferenceNumber', TextValue) then begin
                SalesCrMemoHeader."KPHG KSeF Number" := CopyStr(TextValue, 1, 100);
                SalesCrMemoHeader."KPHG KSeF Status" := SalesCrMemoHeader."KPHG KSeF Status"::Accepted;
                SalesCrMemoHeader."KPHG KSeF Acceptance DT" := CurrentDateTime();
                SalesCrMemoHeader."KPHG KSeF Error Message" := '';
                if TryGetJsonText(JsonResponse, 'qrVerificationUrl', TextValue) then
                    SalesCrMemoHeader."KPHG KSeF QR Reference" := CopyStr(TextValue, 1, 250);
                SalesCrMemoHeader.Modify(true);
                Message('Credit memo %1 accepted by KSeF. Number: %2', SalesCrMemoHeader."No.", SalesCrMemoHeader."KPHG KSeF Number");
            end else begin
                if TryGetJsonText(JsonResponse, 'processingDescription', TextValue) then
                    Message('Credit memo %1 still processing: %2', SalesCrMemoHeader."No.", TextValue)
                else
                    Message('Credit memo %1 still processing.', SalesCrMemoHeader."No.");
            end;
        end else begin
            if TryGetJsonText(JsonResponse, 'error', TextValue) then
                SalesCrMemoHeader."KPHG KSeF Error Message" := CopyStr(FormatErrorMessage(TextValue), 1, 250);
            SalesCrMemoHeader."KPHG KSeF Status" := SalesCrMemoHeader."KPHG KSeF Status"::Error;
            SalesCrMemoHeader.Modify(true);
            Error('Status check failed: %1', SalesCrMemoHeader."KPHG KSeF Error Message");
        end;
    end;

    local procedure BuildCrMemoJson(SalesCrMemoHeader: Record "Sales Cr.Memo Header"): Text
    var
        SalesCrMemoLine: Record "Sales Cr.Memo Line";
        CompanyInfo: Record "Company Information";
        Customer: Record Customer;
        Setup: Record "KPHG KSeF Setup";
        OriginalInvHeader: Record "Sales Invoice Header";
        JsonObj: JsonObject;
        SellerObj: JsonObject;
        BuyerObj: JsonObject;
        LinesArray: JsonArray;
        LineObj: JsonObject;
        LineNo: Integer;
        TaxCat: Text;
        ExemptionBasis: Text;
    begin
        CompanyInfo.Get();
        Customer.Get(SalesCrMemoHeader."Sell-to Customer No.");
        Setup.GetSetup();

        JsonObj.Add('invoiceNumber', SalesCrMemoHeader."No.");
        JsonObj.Add('issueDate', Format(SalesCrMemoHeader."Posting Date", 0, '<Year4>-<Month,2>-<Day,2>'));
        JsonObj.Add('currencyCode', SalesCrMemoHeader."Currency Code");
        JsonObj.Add('isCreditMemo', true);

        if SalesCrMemoHeader."Currency Code" = '' then
            JsonObj.Replace('currencyCode', 'PLN');

        // Original invoice reference
        if SalesCrMemoHeader."KPHG Original Invoice KSeF No." <> '' then
            JsonObj.Add('originalInvoiceKSeFNumber', SalesCrMemoHeader."KPHG Original Invoice KSeF No.");

        if SalesCrMemoHeader."Applies-to Doc. No." <> '' then begin
            JsonObj.Add('originalInvoiceNumber', SalesCrMemoHeader."Applies-to Doc. No.");
            if OriginalInvHeader.Get(SalesCrMemoHeader."Applies-to Doc. No.") then
                JsonObj.Add('originalInvoiceDate', Format(OriginalInvHeader."Posting Date", 0, '<Year4>-<Month,2>-<Day,2>'));
        end;

        JsonObj.Add('correctionReason', 'Korekta faktury');

        // Seller
        SellerObj.Add('nip', Setup."Company NIP");
        SellerObj.Add('name', CompanyInfo.Name);
        SellerObj.Add('street', CompanyInfo.Address);
        SellerObj.Add('buildingNumber', '');
        SellerObj.Add('city', CompanyInfo.City);
        SellerObj.Add('postalCode', CompanyInfo."Post Code");
        SellerObj.Add('countryCode', 'PL');
        JsonObj.Add('seller', SellerObj);

        // Buyer
        BuyerObj.Add('nip', Customer."VAT Registration No.");
        BuyerObj.Add('name', SalesCrMemoHeader."Sell-to Customer Name");
        BuyerObj.Add('street', SalesCrMemoHeader."Sell-to Address");
        BuyerObj.Add('buildingNumber', '');
        BuyerObj.Add('city', SalesCrMemoHeader."Sell-to City");
        BuyerObj.Add('postalCode', SalesCrMemoHeader."Sell-to Post Code");
        BuyerObj.Add('countryCode', SalesCrMemoHeader."Sell-to Country/Region Code");
        JsonObj.Add('buyer', BuyerObj);

        // Payment
        JsonObj.Add('paymentMethod', 'transfer');
        if SalesCrMemoHeader."Due Date" <> 0D then
            JsonObj.Add('paymentDueDate', Format(SalesCrMemoHeader."Due Date", 0, '<Year4>-<Month,2>-<Day,2>'));

        // Lines
        LineNo := 0;
        SalesCrMemoLine.SetRange("Document No.", SalesCrMemoHeader."No.");
        SalesCrMemoLine.SetFilter(Type, '<>%1', SalesCrMemoLine.Type::" ");
        if SalesCrMemoLine.FindSet() then
            repeat
                LineNo += 1;
                Clear(LineObj);
                LineObj.Add('lineNumber', LineNo);
                LineObj.Add('description', SalesCrMemoLine.Description);
                LineObj.Add('quantity', SalesCrMemoLine.Quantity);
                LineObj.Add('unitOfMeasure', SalesCrMemoLine."Unit of Measure Code");
                LineObj.Add('unitPrice', SalesCrMemoLine."Unit Price");
                LineObj.Add('netAmount', SalesCrMemoLine."Line Amount");
                LineObj.Add('vatRate', SalesCrMemoLine."VAT %");
                LineObj.Add('vatAmount', SalesCrMemoLine."Amount Including VAT" - SalesCrMemoLine.Amount);
                LineObj.Add('grossAmount', SalesCrMemoLine."Amount Including VAT");
                TaxCat := DetermineTaxCategory(
                    SalesCrMemoLine."VAT Calculation Type" = SalesCrMemoLine."VAT Calculation Type"::"Reverse Charge VAT",
                    SalesCrMemoLine."VAT %", SalesCrMemoLine."VAT Bus. Posting Group", SalesCrMemoLine."VAT Prod. Posting Group",
                    SalesCrMemoHeader."Sell-to Country/Region Code");
                LineObj.Add('taxCategory', TaxCat);
                if (TaxCat = 'ZW') and (ExemptionBasis = '') then
                    ExemptionBasis := GetExemptionBasis(SalesCrMemoLine."VAT Bus. Posting Group", SalesCrMemoLine."VAT Prod. Posting Group");
                LinesArray.Add(LineObj);
            until SalesCrMemoLine.Next() = 0;

        if ExemptionBasis <> '' then
            JsonObj.Add('exemptionLegalBasis', ExemptionBasis);

        JsonObj.Add('lines', LinesArray);

        exit(Format(JsonObj));
    end;

    local procedure FormatErrorMessage(RawError: Text): Text
    begin
        if RawError.Contains('ServiceUnavailable') or RawError.Contains('zamkni') then
            exit('KSeF service is temporarily unavailable. Please try again later.');
        if RawError.Contains('Unauthorized') or RawError.Contains('401') then
            exit('KSeF authentication failed. Please check the KSeF token in Azure Function settings.');
        if RawError.Contains('Forbidden') or RawError.Contains('403') then
            exit('Access denied by KSeF. Please verify your NIP and token permissions.');
        if RawError.Contains('BadRequest') or RawError.Contains('400') then
            exit('Invalid invoice data. Please check the invoice fields and try again.');
        if RawError.Contains('session init failed') then
            exit('Could not connect to KSeF. The service may be down for maintenance.');
        if RawError.Contains('invoice send failed') then
            exit('KSeF rejected the invoice. Please verify the invoice data is correct.');
        if StrLen(RawError) > 250 then
            exit(CopyStr(RawError, 1, 247) + '...');
        exit(RawError);
    end;

    // Map a sales line to its FA(3) KSeF tax category. The numeric VAT% cannot tell these
    // apart (0% / WDT / export / exempt / reverse-charge all have VAT% = 0), so use BC's
    // VAT Calculation Type (Reverse Charge VAT) and VAT Clause (exemption) plus destination.
    local procedure DetermineTaxCategory(IsReverseCharge: Boolean; VATPct: Decimal; VATBusGroup: Code[20]; VATProdGroup: Code[20]; CountryCode: Code[10]): Text
    var
        VATPostingSetup: Record "VAT Posting Setup";
    begin
        if IsReverseCharge then
            exit('OO');
        if VATPct > 0 then
            exit('STD');
        // 0% — exempt (a VAT Clause is assigned) vs zero-rated by destination.
        if VATPostingSetup.Get(VATBusGroup, VATProdGroup) then
            if VATPostingSetup."VAT Clause Code" <> '' then
                exit('ZW');
        if (CountryCode = '') or (CountryCode = 'PL') then
            exit('KR');
        if IsEuCountry(CountryCode) then
            exit('WDT');
        exit('EXP');
    end;

    local procedure IsEuCountry(CountryCode: Code[10]): Boolean
    var
        CountryRegion: Record "Country/Region";
    begin
        if CountryCode = '' then
            exit(false);
        if not CountryRegion.Get(CountryCode) then
            exit(false);
        exit(CountryRegion."EU Country/Region Code" <> '');
    end;

    // FA(3) P_19A statutory basis for an exempt line, taken from the VAT Clause text.
    local procedure GetExemptionBasis(VATBusGroup: Code[20]; VATProdGroup: Code[20]): Text
    var
        VATPostingSetup: Record "VAT Posting Setup";
        VATClause: Record "VAT Clause";
        BasisText: Text;
    begin
        if not VATPostingSetup.Get(VATBusGroup, VATProdGroup) then
            exit('');
        if VATPostingSetup."VAT Clause Code" = '' then
            exit('');
        if not VATClause.Get(VATPostingSetup."VAT Clause Code") then
            exit('');
        BasisText := VATClause.Description;
        if VATClause."Description 2" <> '' then
            BasisText += ' ' + VATClause."Description 2";
        exit(BasisText);
    end;

    local procedure BuildInvoiceJson(SalesInvHeader: Record "Sales Invoice Header"): Text
    var
        SalesInvLine: Record "Sales Invoice Line";
        CompanyInfo: Record "Company Information";
        Customer: Record Customer;
        Setup: Record "KPHG KSeF Setup";
        JsonObj: JsonObject;
        SellerObj: JsonObject;
        BuyerObj: JsonObject;
        LinesArray: JsonArray;
        LineObj: JsonObject;
        LineNo: Integer;
        TaxCat: Text;
        ExemptionBasis: Text;
    begin
        CompanyInfo.Get();
        Customer.Get(SalesInvHeader."Sell-to Customer No.");
        Setup.GetSetup();

        JsonObj.Add('invoiceNumber', SalesInvHeader."No.");
        JsonObj.Add('issueDate', Format(SalesInvHeader."Posting Date", 0, '<Year4>-<Month,2>-<Day,2>'));
        JsonObj.Add('currencyCode', SalesInvHeader."Currency Code");

        if SalesInvHeader."Currency Code" = '' then
            JsonObj.Replace('currencyCode', 'PLN');

        // Seller
        SellerObj.Add('nip', Setup."Company NIP");
        SellerObj.Add('name', CompanyInfo.Name);
        SellerObj.Add('street', CompanyInfo.Address);
        SellerObj.Add('buildingNumber', '');
        SellerObj.Add('city', CompanyInfo.City);
        SellerObj.Add('postalCode', CompanyInfo."Post Code");
        SellerObj.Add('countryCode', 'PL');
        JsonObj.Add('seller', SellerObj);

        // Buyer
        BuyerObj.Add('nip', Customer."VAT Registration No.");
        BuyerObj.Add('name', SalesInvHeader."Sell-to Customer Name");
        BuyerObj.Add('street', SalesInvHeader."Sell-to Address");
        BuyerObj.Add('buildingNumber', '');
        BuyerObj.Add('city', SalesInvHeader."Sell-to City");
        BuyerObj.Add('postalCode', SalesInvHeader."Sell-to Post Code");
        BuyerObj.Add('countryCode', SalesInvHeader."Sell-to Country/Region Code");
        JsonObj.Add('buyer', BuyerObj);

        // Payment
        JsonObj.Add('paymentMethod', 'transfer');
        if SalesInvHeader."Due Date" <> 0D then
            JsonObj.Add('paymentDueDate', Format(SalesInvHeader."Due Date", 0, '<Year4>-<Month,2>-<Day,2>'));

        // Lines
        LineNo := 0;
        SalesInvLine.SetRange("Document No.", SalesInvHeader."No.");
        SalesInvLine.SetFilter(Type, '<>%1', SalesInvLine.Type::" ");
        if SalesInvLine.FindSet() then
            repeat
                LineNo += 1;
                Clear(LineObj);
                LineObj.Add('lineNumber', LineNo);
                LineObj.Add('description', SalesInvLine.Description);
                LineObj.Add('quantity', SalesInvLine.Quantity);
                LineObj.Add('unitOfMeasure', SalesInvLine."Unit of Measure Code");
                LineObj.Add('unitPrice', SalesInvLine."Unit Price");
                LineObj.Add('netAmount', SalesInvLine."Line Amount");
                LineObj.Add('vatRate', SalesInvLine."VAT %");
                LineObj.Add('vatAmount', SalesInvLine."Amount Including VAT" - SalesInvLine.Amount);
                LineObj.Add('grossAmount', SalesInvLine."Amount Including VAT");
                TaxCat := DetermineTaxCategory(
                    SalesInvLine."VAT Calculation Type" = SalesInvLine."VAT Calculation Type"::"Reverse Charge VAT",
                    SalesInvLine."VAT %", SalesInvLine."VAT Bus. Posting Group", SalesInvLine."VAT Prod. Posting Group",
                    SalesInvHeader."Sell-to Country/Region Code");
                LineObj.Add('taxCategory', TaxCat);
                if (TaxCat = 'ZW') and (ExemptionBasis = '') then
                    ExemptionBasis := GetExemptionBasis(SalesInvLine."VAT Bus. Posting Group", SalesInvLine."VAT Prod. Posting Group");
                LinesArray.Add(LineObj);
            until SalesInvLine.Next() = 0;

        if ExemptionBasis <> '' then
            JsonObj.Add('exemptionLegalBasis', ExemptionBasis);

        JsonObj.Add('lines', LinesArray);

        exit(Format(JsonObj));
    end;

    [TryFunction]
    local procedure TryGetJsonText(JsonObj: JsonObject; PropertyName: Text; var Result: Text)
    var
        JsonToken: JsonToken;
    begin
        JsonObj.Get(PropertyName, JsonToken);
        Result := JsonToken.AsValue().AsText();
        if Result = '' then
            Error('');
    end;
}
