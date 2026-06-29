page 50201 "KPHG KSeF Setup"
{
    PageType = Card;
    SourceTable = "KPHG KSeF Setup";
    ApplicationArea = All;
    UsageCategory = Administration;
    Caption = 'KSeF Setup';
    InsertAllowed = false;
    DeleteAllowed = false;

    layout
    {
        area(Content)
        {
            cuegroup("KSeF Transfer")
            {
                Caption = 'KSeF Transfer';

                field("Inv In KSeF"; Rec."Inv In KSeF") { ApplicationArea = All; ToolTip = 'Invoices accepted by KSeF.'; }
                field("Inv Pending"; Rec."Inv Pending") { ApplicationArea = All; ToolTip = 'Invoices waiting to be sent / processing / sent, not yet accepted.'; }
                field("Inv Failed"; Rec."Inv Failed") { ApplicationArea = All; ToolTip = 'Invoices rejected or errored.'; }
                field("CrMemo In KSeF"; Rec."CrMemo In KSeF") { ApplicationArea = All; ToolTip = 'Credit memos accepted by KSeF.'; }
                field("CrMemo Pending"; Rec."CrMemo Pending") { ApplicationArea = All; ToolTip = 'Credit memos waiting / processing / sent, not yet accepted.'; }
                field("CrMemo Failed"; Rec."CrMemo Failed") { ApplicationArea = All; ToolTip = 'Credit memos rejected or errored.'; }
            }
            group(General)
            {
                Caption = 'General';

                field("Company NIP"; Rec."Company NIP")
                {
                    ApplicationArea = All;
                    ToolTip = 'The NIP (tax identification number) of the company for KSeF submissions.';
                }
                field("KSeF Environment"; Rec."KSeF Environment")
                {
                    ApplicationArea = All;
                    ToolTip = 'Select the KSeF environment: Demo for testing, Test for UAT, Production for live submissions.';
                }
                field("Default KSeF Required"; Rec."Default KSeF Required")
                {
                    ApplicationArea = All;
                    ToolTip = 'If enabled, new sales invoices will have KSeF Required set by default.';
                }
            }
            group(AzureFunction)
            {
                Caption = 'Azure Function Connection';

                field("Azure Function URL"; Rec."Azure Function URL")
                {
                    ApplicationArea = All;
                    ToolTip = 'The URL of the Azure Function endpoint for KSeF integration (e.g. https://your-function.azurewebsites.net/api).';
                }
                field("Azure Function Key"; Rec."Azure Function Key")
                {
                    ApplicationArea = All;
                    ToolTip = 'The function key for authenticating with the Azure Function.';
                }
            }
        }
    }

    actions
    {
        area(Processing)
        {
            action("Test Connection")
            {
                ApplicationArea = All;
                Caption = 'Test Connection';
                Image = TestReport;

                trigger OnAction()
                var
                    KSeFMgmt: Codeunit "KPHG KSeF Management";
                begin
                    KSeFMgmt.TestConnection();
                end;
            }
            action("Test KSeF Auth")
            {
                ApplicationArea = All;
                Caption = 'Test KSeF Authentication';
                Image = TestReport;
                ToolTip = 'Performs the real KSeF authentication (InitSession) for the Company NIP to verify the KSeF token. Unlike Test Connection (which only checks the Azure Function is reachable), this surfaces a bad or missing KSeF token.';

                trigger OnAction()
                var
                    KSeFMgmt: Codeunit "KPHG KSeF Management";
                begin
                    KSeFMgmt.TestKSeFAuth();
                end;
            }
            action("Activate KSeF Dispatch")
            {
                ApplicationArea = All;
                Caption = 'Activate KSeF Dispatch Job';
                Image = Job;
                ToolTip = 'Create (if missing) the recurring Job Queue Entry that paces KSeF submission of Ready documents.';

                trigger OnAction()
                var
                    Dispatch: Codeunit "KPHG KSeF Dispatch";
                begin
                    Dispatch.EnsureDispatchJob();
                    Message('KSeF dispatch job is active.');
                end;
            }
        }
    }

    trigger OnOpenPage()
    begin
        Rec.GetSetup();
    end;
}
