table 50151 "Unapply Buffer"
{
    TableType = Temporary;
    DataClassification = SystemMetadata;

    fields
    {
        field(1; Id; Guid)
        {
            DataClassification = SystemMetadata;
        }
        field(2; EntryNo; Integer)
        {
            DataClassification = SystemMetadata;
        }
        field(3; ResultStatus; Text[20])
        {
            DataClassification = SystemMetadata;
        }
        field(4; ResultMessage; Text[250])
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
