page 50154 "Auction Vendor API"
{
    APIPublisher = 'auctionSystem';
    APIGroup = 'integration';
    APIVersion = 'v1.0';
    EntityName = 'vendor';
    EntitySetName = 'vendors';
    PageType = API;
    SourceTable = Vendor;
    DelayedInsert = true;
    ODataKeyFields = SystemId;

    layout
    {
        area(Content)
        {
            repeater(Group)
            {
                field(systemId; Rec.SystemId) { }
                field(number; Rec."No.") { }
                field(displayName; Rec.Name) { }
                field(addressLine1; Rec.Address) { }
                field(addressLine2; Rec."Address 2") { }
                field(city; Rec.City) { }
                field(country; Rec."Country/Region Code") { }
                field(postalCode; Rec."Post Code") { }
                field(phoneNumber; Rec."Phone No.") { }
                field(email; Rec."E-Mail") { }
                field(website; Rec."Home Page") { }
                field(currencyCode; Rec."Currency Code") { }
                field(blocked; Rec.Blocked) { }
                field(genBusPostingGroup; Rec."Gen. Bus. Posting Group") { }
                field(vatBusPostingGroup; Rec."VAT Bus. Posting Group") { }
                field(vendorPostingGroup; Rec."Vendor Posting Group") { }
                field(paymentTermsCode; Rec."Payment Terms Code") { }
                field(paymentMethodCode; Rec."Payment Method Code") { }
                field(vatRegistrationNo; Rec."VAT Registration No.") { }
                field(lastModifiedDateTime; Rec.SystemModifiedAt) { }
            }
        }
    }
}
