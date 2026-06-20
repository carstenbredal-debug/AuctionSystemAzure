page 50301 "KPHG JPK Export Card"
{
    PageType = Card;
    ApplicationArea = All;
    SourceTable = "KPHG JPK Export";
    Caption = 'JPK Export';

    layout
    {
        area(Content)
        {
            group(General)
            {
                Caption = 'General';
                field("Entry No."; Rec."Entry No.") { ApplicationArea = All; Editable = false; }
                field("Report Type"; Rec."Report Type") { ApplicationArea = All; }
                field(Year; Rec.Year) { ApplicationArea = All; }
                field(Month; Rec.Month) { ApplicationArea = All; }
                field(Purpose; Rec.Purpose) { ApplicationArea = All; }
                field("Correction No."; Rec."Correction No.") { ApplicationArea = All; }
                field(Status; Rec.Status) { ApplicationArea = All; Editable = false; }
            }
            group(Results)
            {
                Caption = 'Results';
                field("No. of Sales Records"; Rec."No. of Sales Records") { ApplicationArea = All; Editable = false; }
                field("No. of Purchase Records"; Rec."No. of Purchase Records") { ApplicationArea = All; Editable = false; }
                field("Tax Due (Output)"; Rec."Tax Due (Output)") { ApplicationArea = All; Editable = false; }
                field("Tax Deductible (Input)"; Rec."Tax Deductible (Input)") { ApplicationArea = All; Editable = false; }
            }
            group(Validation)
            {
                Caption = 'Schema Validation';
                field("Schema Valid"; Rec."Schema Valid") { ApplicationArea = All; Editable = false; }
                field("Validation Errors"; Rec."Validation Errors") { ApplicationArea = All; Editable = false; MultiLine = true; }
            }
            group(Info)
            {
                Caption = 'Information';
                field("File Name"; Rec."File Name") { ApplicationArea = All; Editable = false; }
                field("Created DateTime"; Rec."Created DateTime") { ApplicationArea = All; Editable = false; }
                field("Generated DateTime"; Rec."Generated DateTime") { ApplicationArea = All; Editable = false; }
                field("Created By"; Rec."Created By") { ApplicationArea = All; Editable = false; }
                field("Error Message"; Rec."Error Message") { ApplicationArea = All; Editable = false; }
            }
        }
    }

    actions
    {
        area(Processing)
        {
            action("KPHG Generate JPK")
            {
                ApplicationArea = All;
                Caption = 'Generate JPK_V7M';
                Image = XMLFile;
                Promoted = true;
                PromotedCategory = Process;

                trigger OnAction()
                var
                    JPKManagement: Codeunit "KPHG JPK Management";
                begin
                    JPKManagement.GenerateJPK(Rec);
                    CurrPage.Update(false);
                end;
            }
            action("KPHG Download XML")
            {
                ApplicationArea = All;
                Caption = 'Download XML';
                Image = ExportFile;
                Promoted = true;
                PromotedCategory = Process;

                trigger OnAction()
                var
                    JPKManagement: Codeunit "KPHG JPK Management";
                begin
                    JPKManagement.DownloadXml(Rec);
                end;
            }
        }
    }
}
