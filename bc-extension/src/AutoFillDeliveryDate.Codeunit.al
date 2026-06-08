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
        SetDateFieldIfEmpty(RecRef, 52063188, DocDate); // ITI Delivery Date
        SetDateFieldIfEmpty(RecRef, 52063044, DocDate); // VAT Settlement Date
        RecRef.SetTable(Rec);

        Rec.Modify(false);
    end;

    [EventSubscriber(ObjectType::Table, Database::"Sales Header", 'OnAfterModifyEvent', '', false, false)]
    local procedure AutoFillDeliveryDateOnModify(var Rec: Record "Sales Header"; var xRec: Record "Sales Header"; RunTrigger: Boolean)
    var
        RecRef: RecordRef;
        DocDate: Date;
        NeedModify: Boolean;
    begin
        NeedModify := false;
        DocDate := Rec."Document Date";
        if DocDate = 0D then
            DocDate := WorkDate();

        RecRef.GetTable(Rec);
        if SetDateFieldIfEmpty(RecRef, 52063188, DocDate) then // ITI Delivery Date
            NeedModify := true;
        if SetDateFieldIfEmpty(RecRef, 52063044, DocDate) then // VAT Settlement Date
            NeedModify := true;
        if NeedModify then
            RecRef.SetTable(Rec);

        if Rec."Shipment Date" = 0D then begin
            Rec."Shipment Date" := DocDate;
            NeedModify := true;
        end;

        if NeedModify then
            Rec.Modify(false);
    end;

    local procedure SetDateFieldIfEmpty(var RecRef: RecordRef; FieldNo: Integer; DateValue: Date): Boolean
    var
        FldRef: FieldRef;
    begin
        if not RecRef.FieldExist(FieldNo) then
            exit(false);
        FldRef := RecRef.Field(FieldNo);
        if Format(FldRef.Value) <> '' then
            exit(false);
        FldRef.Value := DateValue;
        exit(true);
    end;
}
