codeunit 50150 "Auto Fill Delivery Date"
{
    [EventSubscriber(ObjectType::Table, Database::"Sales Header", 'OnAfterInsertEvent', '', false, false)]
    local procedure AutoFillDeliveryDateOnInsert(var Rec: Record "Sales Header"; RunTrigger: Boolean)
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        DocDate: Date;
    begin
        DocDate := Rec."Document Date";
        if DocDate = 0D then
            DocDate := WorkDate();

        // Set standard Shipment Date if empty
        if Rec."Shipment Date" = 0D then
            Rec."Shipment Date" := DocDate;

        // Set Polish ITI Delivery Date (field 52063188) if it exists and is empty
        RecRef.GetTable(Rec);
        if RecRef.FieldExist(52063188) then begin
            FldRef := RecRef.Field(52063188);
            if Format(FldRef.Value) = '' then begin
                FldRef.Value := DocDate;
                RecRef.SetTable(Rec);
            end;
        end;

        Rec.Modify(false);
    end;

    [EventSubscriber(ObjectType::Table, Database::"Sales Header", 'OnAfterModifyEvent', '', false, false)]
    local procedure AutoFillDeliveryDateOnModify(var Rec: Record "Sales Header"; var xRec: Record "Sales Header"; RunTrigger: Boolean)
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        DocDate: Date;
        NeedModify: Boolean;
    begin
        NeedModify := false;
        DocDate := Rec."Document Date";
        if DocDate = 0D then
            DocDate := WorkDate();

        // Set Polish ITI Delivery Date (field 52063188) if it exists and is empty
        RecRef.GetTable(Rec);
        if RecRef.FieldExist(52063188) then begin
            FldRef := RecRef.Field(52063188);
            if Format(FldRef.Value) = '' then begin
                FldRef.Value := DocDate;
                RecRef.SetTable(Rec);
                NeedModify := true;
            end;
        end;

        if Rec."Shipment Date" = 0D then begin
            Rec."Shipment Date" := DocDate;
            NeedModify := true;
        end;

        if NeedModify then
            Rec.Modify(false);
    end;
}
