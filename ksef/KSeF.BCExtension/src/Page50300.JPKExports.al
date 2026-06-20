page 50300 "KPHG JPK Exports"
{
    PageType = List;
    ApplicationArea = All;
    UsageCategory = Lists;
    SourceTable = "KPHG JPK Export";
    Caption = 'JPK Exports';
    CardPageId = "KPHG JPK Export Card";
    Editable = false;

    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field("Entry No."; Rec."Entry No.") { ApplicationArea = All; }
                field("Report Type"; Rec."Report Type") { ApplicationArea = All; }
                field(Year; Rec.Year) { ApplicationArea = All; }
                field(Month; Rec.Month) { ApplicationArea = All; }
                field(Purpose; Rec.Purpose) { ApplicationArea = All; }
                field(Status; Rec.Status) { ApplicationArea = All; }
                field("No. of Sales Records"; Rec."No. of Sales Records") { ApplicationArea = All; }
                field("Tax Due (Output)"; Rec."Tax Due (Output)") { ApplicationArea = All; }
                field("Schema Valid"; Rec."Schema Valid") { ApplicationArea = All; }
                field("Generated DateTime"; Rec."Generated DateTime") { ApplicationArea = All; }
                field("Created By"; Rec."Created By") { ApplicationArea = All; }
                field("Error Message"; Rec."Error Message") { ApplicationArea = All; }
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
            action("KPHG New Export")
            {
                ApplicationArea = All;
                Caption = 'New JPK Export';
                Image = New;
                Promoted = true;
                PromotedCategory = New;

                trigger OnAction()
                var
                    JPKExport: Record "KPHG JPK Export";
                    JPKManagement: Codeunit "KPHG JPK Management";
                begin
                    JPKManagement.CreateNewExport(JPKExport);
                    CurrPage.Update(false);
                end;
            }
        }
    }
}
