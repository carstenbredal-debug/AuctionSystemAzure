table 50150 "Payment Application Buffer"
{
    TableType = Temporary;
    DataClassification = SystemMetadata;

    fields
    {
        field(1; Id; Guid)
        {
            DataClassification = SystemMetadata;
        }
        field(2; CustomerNo; Code[20])
        {
            DataClassification = SystemMetadata;
        }
        field(3; PaymentEntryNo; Integer)
        {
            DataClassification = SystemMetadata;
        }
        field(4; InvoiceDocumentNo; Code[20])
        {
            DataClassification = SystemMetadata;
        }
        field(5; AmountToApply; Decimal)
        {
            DataClassification = SystemMetadata;
        }
        field(6; ResultStatus; Text[20])
        {
            DataClassification = SystemMetadata;
        }
        field(7; ResultMessage; Text[250])
        {
            DataClassification = SystemMetadata;
        }
        field(8; SourceDocumentType; Text[20])
        {
            DataClassification = SystemMetadata;
        }
    }

    keys
    {
        key(PK; Id)
        {
            Clustered = true;
        }
    }
}
