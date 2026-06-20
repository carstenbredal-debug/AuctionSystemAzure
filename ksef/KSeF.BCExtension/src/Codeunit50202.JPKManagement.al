codeunit 50202 "KPHG JPK Management"
{
    Permissions =
        tabledata "KPHG JPK Export" = RIMD,
        tabledata "Sales Invoice Header" = R,
        tabledata "Sales Invoice Line" = R,
        tabledata "Sales Cr.Memo Header" = R,
        tabledata "Sales Cr.Memo Line" = R,
        tabledata "KPHG KSeF Setup" = R;

    procedure CreateNewExport(var JPKExport: Record "KPHG JPK Export")
    begin
        JPKExport.Init();
        JPKExport."Report Type" := JPKExport."Report Type"::"JPK_V7M";
        JPKExport.Year := Date2DMY(Today(), 3);
        JPKExport.Month := Date2DMY(Today(), 2);
        if JPKExport.Month = 1 then begin
            JPKExport.Month := 12;
            JPKExport.Year -= 1;
        end else
            JPKExport.Month -= 1;
        JPKExport.Status := JPKExport.Status::New;
        JPKExport.Purpose := JPKExport.Purpose::Original;
        JPKExport.Insert(true);
    end;

    procedure GenerateJPK(var JPKExport: Record "KPHG JPK Export")
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
        InStream: InStream;
        OutStream: OutStream;
        TempBlob: Codeunit "Temp Blob";
        Base64Convert: Codeunit "Base64 Convert";
        XmlContent: Text;
        Success: Boolean;
        ValidationErrors: JsonArray;
        ValidationToken: JsonToken;
        ValidationErrorsText: Text;
        i: Integer;
    begin
        if JPKExport.Year = 0 then
            Error('Please specify the year.');
        if (JPKExport.Month < 1) or (JPKExport.Month > 12) then
            Error('Please specify a valid month (1-12).');

        Setup.GetSetup();
        if Setup."Azure Function URL" = '' then
            Error('Azure Function URL is not configured.');

        JPKExport.Status := JPKExport.Status::Processing;
        JPKExport."Error Message" := '';
        JPKExport.Modify(true);
        Commit();

        RequestBody := BuildJPKRequest(JPKExport);

        Content.WriteFrom(RequestBody);
        Content.GetHeaders(Headers);
        Headers.Remove('Content-Type');
        Headers.Add('Content-Type', 'application/json');

        Client.DefaultRequestHeaders().Add('x-functions-key', Setup."Azure Function Key");
        Client.Timeout(120000);

        Success := Client.Post(Setup."Azure Function URL" + '/jpk/v7m/generate', Content, ResponseMessage);

        if not Success then begin
            JPKExport.Status := JPKExport.Status::Error;
            JPKExport."Error Message" := 'HTTP request failed.';
            JPKExport.Modify(true);
            Error('Failed to connect to Azure Function.');
        end;

        ResponseMessage.Content().ReadAs(ResponseText);
        JsonResponse.ReadFrom(ResponseText);

        if not TryGetJsonText(JsonResponse, 'success', TextValue) then
            TextValue := '';

        if TextValue = 'true' then begin
            if TryGetJsonText(JsonResponse, 'xml', XmlContent) then begin
                TempBlob.CreateOutStream(OutStream, TextEncoding::UTF8);
                OutStream.WriteText(XmlContent);
                TempBlob.CreateInStream(InStream);
                JPKExport."XML Content".CreateOutStream(OutStream);
                CopyStream(OutStream, InStream);
            end;

            if TryGetJsonText(JsonResponse, 'fileName', TextValue) then
                JPKExport."File Name" := CopyStr(TextValue, 1, 250);

            if TryGetJsonInt(JsonResponse, 'salesRecordCount', JPKExport."No. of Sales Records") then;
            if TryGetJsonInt(JsonResponse, 'purchaseRecordCount', JPKExport."No. of Purchase Records") then;
            if TryGetJsonDec(JsonResponse, 'taxDue', JPKExport."Tax Due (Output)") then;
            if TryGetJsonDec(JsonResponse, 'taxDeductible', JPKExport."Tax Deductible (Input)") then;

            if TryGetJsonText(JsonResponse, 'schemaValid', TextValue) then
                JPKExport."Schema Valid" := (TextValue = 'true');

            JPKExport."Validation Errors" := '';
            if TryGetJsonArray(JsonResponse, 'validationErrors', ValidationErrors) then begin
                ValidationErrorsText := '';
                for i := 0 to ValidationErrors.Count() - 1 do begin
                    ValidationErrors.Get(i, ValidationToken);
                    ValidationErrorsText += ValidationToken.AsValue().AsText();
                    if i < ValidationErrors.Count() - 1 then
                        ValidationErrorsText += '; ';
                end;
                JPKExport."Validation Errors" := CopyStr(ValidationErrorsText, 1, 2048);
            end;

            JPKExport.Status := JPKExport.Status::Generated;
            JPKExport."Generated DateTime" := CurrentDateTime();
            JPKExport."Error Message" := '';
            JPKExport.Modify(true);

            if JPKExport."Schema Valid" then
                Message('JPK_V7M generated and validated successfully for %1/%2.\Sales records: %3\Schema: Valid',
                    JPKExport.Month, JPKExport.Year, JPKExport."No. of Sales Records")
            else
                Message('JPK_V7M generated for %1/%2.\Sales records: %3\Schema: INVALID — %4',
                    JPKExport.Month, JPKExport.Year, JPKExport."No. of Sales Records",
                    JPKExport."Validation Errors");
        end else begin
            JPKExport.Status := JPKExport.Status::Error;
            if TryGetJsonText(JsonResponse, 'error', TextValue) then
                JPKExport."Error Message" := CopyStr(TextValue, 1, 250)
            else
                JPKExport."Error Message" := 'Unknown error from Azure Function.';
            JPKExport.Modify(true);
            Error('JPK generation failed: %1', JPKExport."Error Message");
        end;
    end;

    procedure DownloadXml(var JPKExport: Record "KPHG JPK Export")
    var
        InStream: InStream;
        FileName: Text;
    begin
        if JPKExport.Status <> JPKExport.Status::Generated then
            Error('Generate the JPK file first.');

        JPKExport.CalcFields("XML Content");
        if not JPKExport."XML Content".HasValue() then
            Error('No XML content found. Please regenerate.');

        JPKExport."XML Content".CreateInStream(InStream, TextEncoding::UTF8);
        FileName := JPKExport."File Name";
        if FileName = '' then
            FileName := StrSubstNo('JPK_V7M_%1_%2.xml', JPKExport.Year, JPKExport.Month);

        DownloadFromStream(InStream, 'Download JPK_V7M', '', 'XML Files (*.xml)|*.xml', FileName);
    end;

    local procedure BuildJPKRequest(JPKExport: Record "KPHG JPK Export"): Text
    var
        Setup: Record "KPHG KSeF Setup";
        CompanyInfo: Record "Company Information";
        SalesInvHeader: Record "Sales Invoice Header";
        SalesInvLine: Record "Sales Invoice Line";
        SalesCrMemoHeader: Record "Sales Cr.Memo Header";
        SalesCrMemoLine: Record "Sales Cr.Memo Line";
        Customer: Record Customer;
        JsonObj: JsonObject;
        CompanyObj: JsonObject;
        SalesArray: JsonArray;
        SaleObj: JsonObject;
        PeriodStart: Date;
        PeriodEnd: Date;
    begin
        Setup.GetSetup();
        CompanyInfo.Get();

        PeriodStart := DMY2Date(1, JPKExport.Month, JPKExport.Year);
        PeriodEnd := CalcDate('<CM>', PeriodStart);

        // Company info
        CompanyObj.Add('nip', Setup."Company NIP");
        CompanyObj.Add('name', CompanyInfo.Name);
        CompanyObj.Add('email', CompanyInfo."E-Mail");
        CompanyObj.Add('phone', CompanyInfo."Phone No.");
        JsonObj.Add('company', CompanyObj);

        // Period
        JsonObj.Add('year', JPKExport.Year);
        JsonObj.Add('month', JPKExport.Month);
        JsonObj.Add('purpose', Format(JPKExport.Purpose));
        JsonObj.Add('correctionNo', JPKExport."Correction No.");

        // Sales invoices
        SalesInvHeader.SetRange("Posting Date", PeriodStart, PeriodEnd);
        if SalesInvHeader.FindSet() then
            repeat
                Clear(SaleObj);
                if Customer.Get(SalesInvHeader."Sell-to Customer No.") then;

                SaleObj.Add('documentType', 'FP');
                SaleObj.Add('invoiceNumber', SalesInvHeader."No.");
                SaleObj.Add('issueDate', Format(SalesInvHeader."Posting Date", 0, '<Year4>-<Month,2>-<Day,2>'));
                if SalesInvHeader."Shipment Date" <> 0D then
                    SaleObj.Add('saleDate', Format(SalesInvHeader."Shipment Date", 0, '<Year4>-<Month,2>-<Day,2>'))
                else
                    SaleObj.Add('saleDate', Format(SalesInvHeader."Posting Date", 0, '<Year4>-<Month,2>-<Day,2>'));

                SaleObj.Add('counterpartyNip', Customer."VAT Registration No.");
                SaleObj.Add('counterpartyName', SalesInvHeader."Sell-to Customer Name");
                SaleObj.Add('countryCode', SalesInvHeader."Sell-to Country/Region Code");

                if SalesInvHeader."Due Date" <> 0D then
                    SaleObj.Add('paymentDueDate', Format(SalesInvHeader."Due Date", 0, '<Year4>-<Month,2>-<Day,2>'));

                if SalesInvHeader."KPHG KSeF Number" <> '' then
                    SaleObj.Add('kSeFReferenceNumber', SalesInvHeader."KPHG KSeF Number");

                SaleObj.Add('isCreditMemo', false);
                SaleObj.Add('netAmount', CalcInvNetAmount(SalesInvHeader."No."));
                SaleObj.Add('vatAmount', CalcInvVatAmount(SalesInvHeader."No."));
                SaleObj.Add('grossAmount', CalcInvGrossAmount(SalesInvHeader."No."));
                AddVatBreakdown(SaleObj, SalesInvHeader."No.", "Gen. Journal Document Type"::Invoice, SalesInvHeader."Posting Date");

                SalesArray.Add(SaleObj);
            until SalesInvHeader.Next() = 0;

        // Credit memos
        SalesCrMemoHeader.SetRange("Posting Date", PeriodStart, PeriodEnd);
        if SalesCrMemoHeader.FindSet() then
            repeat
                Clear(SaleObj);
                if Customer.Get(SalesCrMemoHeader."Sell-to Customer No.") then;

                SaleObj.Add('documentType', 'KOREKTA');
                SaleObj.Add('invoiceNumber', SalesCrMemoHeader."No.");
                SaleObj.Add('issueDate', Format(SalesCrMemoHeader."Posting Date", 0, '<Year4>-<Month,2>-<Day,2>'));
                SaleObj.Add('saleDate', Format(SalesCrMemoHeader."Posting Date", 0, '<Year4>-<Month,2>-<Day,2>'));

                SaleObj.Add('counterpartyNip', Customer."VAT Registration No.");
                SaleObj.Add('counterpartyName', SalesCrMemoHeader."Sell-to Customer Name");
                SaleObj.Add('countryCode', SalesCrMemoHeader."Sell-to Country/Region Code");

                if SalesCrMemoHeader."Due Date" <> 0D then
                    SaleObj.Add('paymentDueDate', Format(SalesCrMemoHeader."Due Date", 0, '<Year4>-<Month,2>-<Day,2>'));

                if SalesCrMemoHeader."KPHG KSeF Number" <> '' then
                    SaleObj.Add('kSeFReferenceNumber', SalesCrMemoHeader."KPHG KSeF Number");

                SaleObj.Add('isCreditMemo', true);
                SaleObj.Add('netAmount', -CalcCrMemoNetAmount(SalesCrMemoHeader."No."));
                SaleObj.Add('vatAmount', -CalcCrMemoVatAmount(SalesCrMemoHeader."No."));
                SaleObj.Add('grossAmount', -CalcCrMemoGrossAmount(SalesCrMemoHeader."No."));
                AddVatBreakdown(SaleObj, SalesCrMemoHeader."No.", "Gen. Journal Document Type"::"Credit Memo", SalesCrMemoHeader."Posting Date");

                SalesArray.Add(SaleObj);
            until SalesCrMemoHeader.Next() = 0;

        JsonObj.Add('salesRecords', SalesArray);

        exit(Format(JsonObj));
    end;

    // JPK/FA(3) tax category from the VAT Bus. Posting Group (per the auction VAT setup):
    //   EU -> intra-EU reverse-charge (OO), NONEU -> outside-scope (NP),
    //   POLAND -> exempt (ZW) when a VAT Clause is set, otherwise domestic rated (STD).
    local procedure JpkCategory(VATBusGroup: Code[20]; VATProdGroup: Code[20]): Text
    var
        VATPostingSetup: Record "VAT Posting Setup";
    begin
        case UpperCase(VATBusGroup) of
            'EU':
                exit('OO');
            'NONEU':
                exit('NP');
        end;
        if VATPostingSetup.Get(VATBusGroup, VATProdGroup) then
            if VATPostingSetup."VAT Clause Code" <> '' then
                exit('ZW');
        exit('STD');
    end;

    // Emit the per-category VAT breakdown ('vat' array) for a posted document, summed from its
    // VAT Entries (positive amounts; the Function applies the credit-memo sign).
    local procedure AddVatBreakdown(var SaleObj: JsonObject; DocNo: Code[20]; DocType: Enum "Gen. Journal Document Type"; PostingDate: Date)
    var
        VATEntry: Record "VAT Entry";
        NetByCat: Dictionary of [Text, Decimal];
        VatByCat: Dictionary of [Text, Decimal];
        VatArray: JsonArray;
        VatObj: JsonObject;
        Cat: Text;
        CurNet: Decimal;
        CurVat: Decimal;
    begin
        VATEntry.SetRange(Type, VATEntry.Type::Sale);
        VATEntry.SetRange("Document No.", DocNo);
        VATEntry.SetRange("Document Type", DocType);
        VATEntry.SetRange("Posting Date", PostingDate);
        if VATEntry.FindSet() then
            repeat
                Cat := JpkCategory(VATEntry."VAT Bus. Posting Group", VATEntry."VAT Prod. Posting Group");
                if not NetByCat.ContainsKey(Cat) then begin
                    NetByCat.Add(Cat, 0);
                    VatByCat.Add(Cat, 0);
                end;
                NetByCat.Get(Cat, CurNet);
                VatByCat.Get(Cat, CurVat);
                NetByCat.Set(Cat, CurNet + Abs(VATEntry.Base));
                VatByCat.Set(Cat, CurVat + Abs(VATEntry.Amount));
            until VATEntry.Next() = 0;

        foreach Cat in NetByCat.Keys() do begin
            Clear(VatObj);
            NetByCat.Get(Cat, CurNet);
            VatByCat.Get(Cat, CurVat);
            VatObj.Add('category', Cat);
            VatObj.Add('netAmount', CurNet);
            VatObj.Add('vatAmount', CurVat);
            VatArray.Add(VatObj);
        end;
        SaleObj.Add('vat', VatArray);
    end;

    local procedure CalcInvNetAmount(DocNo: Code[20]): Decimal
    var
        SalesInvLine: Record "Sales Invoice Line";
        Total: Decimal;
    begin
        SalesInvLine.SetRange("Document No.", DocNo);
        SalesInvLine.SetFilter(Type, '<>%1', SalesInvLine.Type::" ");
        if SalesInvLine.FindSet() then
            repeat
                Total += SalesInvLine.Amount;
            until SalesInvLine.Next() = 0;
        exit(Total);
    end;

    local procedure CalcInvVatAmount(DocNo: Code[20]): Decimal
    var
        SalesInvLine: Record "Sales Invoice Line";
        Total: Decimal;
    begin
        SalesInvLine.SetRange("Document No.", DocNo);
        SalesInvLine.SetFilter(Type, '<>%1', SalesInvLine.Type::" ");
        if SalesInvLine.FindSet() then
            repeat
                Total += SalesInvLine."Amount Including VAT" - SalesInvLine.Amount;
            until SalesInvLine.Next() = 0;
        exit(Total);
    end;

    local procedure CalcInvGrossAmount(DocNo: Code[20]): Decimal
    var
        SalesInvLine: Record "Sales Invoice Line";
        Total: Decimal;
    begin
        SalesInvLine.SetRange("Document No.", DocNo);
        SalesInvLine.SetFilter(Type, '<>%1', SalesInvLine.Type::" ");
        if SalesInvLine.FindSet() then
            repeat
                Total += SalesInvLine."Amount Including VAT";
            until SalesInvLine.Next() = 0;
        exit(Total);
    end;

    local procedure CalcCrMemoNetAmount(DocNo: Code[20]): Decimal
    var
        SalesCrMemoLine: Record "Sales Cr.Memo Line";
        Total: Decimal;
    begin
        SalesCrMemoLine.SetRange("Document No.", DocNo);
        SalesCrMemoLine.SetFilter(Type, '<>%1', SalesCrMemoLine.Type::" ");
        if SalesCrMemoLine.FindSet() then
            repeat
                Total += SalesCrMemoLine.Amount;
            until SalesCrMemoLine.Next() = 0;
        exit(Total);
    end;

    local procedure CalcCrMemoVatAmount(DocNo: Code[20]): Decimal
    var
        SalesCrMemoLine: Record "Sales Cr.Memo Line";
        Total: Decimal;
    begin
        SalesCrMemoLine.SetRange("Document No.", DocNo);
        SalesCrMemoLine.SetFilter(Type, '<>%1', SalesCrMemoLine.Type::" ");
        if SalesCrMemoLine.FindSet() then
            repeat
                Total += SalesCrMemoLine."Amount Including VAT" - SalesCrMemoLine.Amount;
            until SalesCrMemoLine.Next() = 0;
        exit(Total);
    end;

    local procedure CalcCrMemoGrossAmount(DocNo: Code[20]): Decimal
    var
        SalesCrMemoLine: Record "Sales Cr.Memo Line";
        Total: Decimal;
    begin
        SalesCrMemoLine.SetRange("Document No.", DocNo);
        SalesCrMemoLine.SetFilter(Type, '<>%1', SalesCrMemoLine.Type::" ");
        if SalesCrMemoLine.FindSet() then
            repeat
                Total += SalesCrMemoLine."Amount Including VAT";
            until SalesCrMemoLine.Next() = 0;
        exit(Total);
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

    [TryFunction]
    local procedure TryGetJsonInt(JsonObj: JsonObject; PropertyName: Text; var Result: Integer)
    var
        JsonToken: JsonToken;
    begin
        JsonObj.Get(PropertyName, JsonToken);
        Result := JsonToken.AsValue().AsInteger();
    end;

    [TryFunction]
    local procedure TryGetJsonDec(JsonObj: JsonObject; PropertyName: Text; var Result: Decimal)
    var
        JsonToken: JsonToken;
    begin
        JsonObj.Get(PropertyName, JsonToken);
        Result := JsonToken.AsValue().AsDecimal();
    end;

    [TryFunction]
    local procedure TryGetJsonArray(JsonObj: JsonObject; PropertyName: Text; var Result: JsonArray)
    var
        JsonToken: JsonToken;
    begin
        JsonObj.Get(PropertyName, JsonToken);
        Result := JsonToken.AsArray();
    end;
}
