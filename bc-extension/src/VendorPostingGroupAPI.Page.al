page 50155 "Vendor Posting Groups API"
{
    APIPublisher = 'auctionSystem';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'vendorPostingGroup';
    EntitySetName = 'vendorPostingGroups';
    PageType = API;
    SourceTable = "Vendor Posting Group";
    Editable = false;
    DelayedInsert = true;
    ODataKeyFields = SystemId;

    layout
    {
        area(Content)
        {
            repeater(Group)
            {
                field(systemId; Rec.SystemId) { }
                field("code"; Rec.Code) { }
                field(description; Rec.Description) { }
            }
        }
    }
}
