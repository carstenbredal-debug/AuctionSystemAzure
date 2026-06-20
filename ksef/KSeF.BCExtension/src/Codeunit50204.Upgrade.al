codeunit 50204 "KPHG KSeF Upgrade"
{
    // Seeds the KSeF setup on UPGRADE for companies that already have the app installed (OnInstall only
    // fires on a fresh install, not on an upgrade). Runs the same seed as the Install codeunit so an
    // existing company gets KSeF set to automatic when this version is published. Guarded by an upgrade
    // tag so it runs ONCE per company — a deliberate "manual" choice made later won't be re-clobbered by
    // a future upgrade.
    Subtype = Upgrade;

    trigger OnUpgradePerCompany()
    var
        Install: Codeunit "KPHG KSeF Install";
        UpgradeTag: Codeunit "Upgrade Tag";
    begin
        if UpgradeTag.HasUpgradeTag(DefaultAutomaticTag()) then
            exit;
        Install.SeedSetup();
        UpgradeTag.SetUpgradeTag(DefaultAutomaticTag());
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Upgrade Tag", 'OnGetPerCompanyUpgradeTags', '', false, false)]
    local procedure RegisterPerCompanyTags(var PerCompanyUpgradeTags: List of [Code[250]])
    begin
        PerCompanyUpgradeTags.Add(DefaultAutomaticTag());
    end;

    local procedure DefaultAutomaticTag(): Code[250]
    begin
        exit('KPHG-KSeF-DefaultAutomatic-20260620');
    end;
}
