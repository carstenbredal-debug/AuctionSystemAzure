codeunit 50203 "KPHG KSeF Install"
{
    // Seeds the KSeF setup whenever the app is installed into a company — including a freshly (re)created
    // company after a reset — so KSeF comes up AUTOMATIC instead of reverting to manual. Runs as the
    // install context, so it doesn't depend on the user having the KSeF Admin permission set.
    //
    // Only NON-SECRET, environment-safe values are seeded here: the automatic default + a safe (non-prod)
    // test environment. Connection secrets (Azure Function Key) and tenant-specific values (Company NIP,
    // Azure Function URL) are intentionally NOT seeded — configure those once on the KSeF Setup page.
    // (The "KPHG KSeF Setup" table grants InherentPermissions = RIMD, and install runs elevated, so the
    // seed can read/write the setup without the user holding the KSeF Admin permission set.)
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    begin
        SeedSetup();
    end;

    procedure SeedSetup()
    var
        Setup: Record "KPHG KSeF Setup";
        Dispatch: Codeunit "KPHG KSeF Dispatch";
    begin
        Setup.GetSetup(); // creates the SETUP record from Init() defaults if it doesn't exist yet

        // Automatic: new sales documents are KSeF-flagged by default, so they're marked Ready for dispatch.
        Setup."Default KSeF Required" := true;

        // Never default a fresh company straight to Production (auto-send is a legal submission). If the
        // env is still the Init default (Demo = 0), move it to Test; leave any explicit choice untouched.
        if Setup."KSeF Environment" = Setup."KSeF Environment"::Demo then
            Setup."KSeF Environment" := Setup."KSeF Environment"::Test;

        Setup.Modify();

        // Create the recurring Job Queue Entry that paces KSeF submission (survives resets). Wrapped so a
        // job-queue restriction in this environment can't break app install — the job can also be created
        // from the KSeF Setup page action if this is skipped.
        Dispatch.TryEnsureDispatchJob();
    end;
}
