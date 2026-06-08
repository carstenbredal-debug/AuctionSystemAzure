codeunit 50150 "Auto Fill Delivery Date"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnBeforePostSalesDoc', '', false, false)]
    local procedure AutoFillDeliveryDateOnPost(var SalesHeader: Record "Sales Header"; CommitIsSuppressed: Boolean; PreviewMode: Boolean; var HideProgressWindow: Boolean; var IsHandled: Boolean)
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        // Set standard Shipment Date (field 10) if empty — Polish localization validates this
        if SalesHeader."Shipment Date" = 0D then begin
            SalesHeader."Shipment Date" := SalesHeader."Document Date";
        end;

        // Also set Polish ITI Delivery Date (field 52063189) if it exists and is empty
        RecRef.GetTable(SalesHeader);
        if RecRef.FieldExist(52063189) then begin
            FldRef := RecRef.Field(52063189);
            if Format(FldRef.Value) = '' then begin
                FldRef.Value := SalesHeader."Document Date";
                RecRef.SetTable(SalesHeader);
            end;
        end;
    end;
}
