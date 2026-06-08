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

        if Rec."Shipment Date" = 0D then
            Rec."Shipment Date" := DocDate;

        RecRef.GetTable(Rec);

        // Validate ITI Delivery Date — triggers Polish Localization logic
        // which auto-populates VAT Settlement Date and other derived fields
        if RecRef.FieldExist(52063188) then begin
            FldRef := RecRef.Field(52063188);
            if Format(FldRef.Value) = '' then
                FldRef.Validate(DocDate);
        end;

        // Also directly set VAT Settlement Date as fallback
        if RecRef.FieldExist(52063044) then begin
            FldRef := RecRef.Field(52063044);
            if Format(FldRef.Value) = '' then
                FldRef.Value := DocDate;
        end;

        RecRef.SetTable(Rec);
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

        RecRef.GetTable(Rec);

        if RecRef.FieldExist(52063188) then begin
            FldRef := RecRef.Field(52063188);
            if Format(FldRef.Value) = '' then begin
                FldRef.Validate(DocDate);
                NeedModify := true;
            end;
        end;

        if RecRef.FieldExist(52063044) then begin
            FldRef := RecRef.Field(52063044);
            if Format(FldRef.Value) = '' then begin
                FldRef.Value := DocDate;
                NeedModify := true;
            end;
        end;

        if NeedModify then
            RecRef.SetTable(Rec);

        if Rec."Shipment Date" = 0D then begin
            Rec."Shipment Date" := DocDate;
            NeedModify := true;
        end;

        if NeedModify then
            Rec.Modify(false);
    end;
}
