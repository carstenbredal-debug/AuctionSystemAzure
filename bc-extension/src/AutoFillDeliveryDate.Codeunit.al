codeunit 50150 "Auto Fill Delivery Date"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnBeforePostSalesDoc', '', false, false)]
    local procedure AutoFillDeliveryDateOnPost(var SalesHeader: Record "Sales Header"; CommitIsSuppressed: Boolean; PreviewMode: Boolean; var HideProgressWindow: Boolean; var IsHandled: Boolean)
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
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
