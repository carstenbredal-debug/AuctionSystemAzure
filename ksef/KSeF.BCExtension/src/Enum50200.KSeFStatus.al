enum 50200 "KPHG KSeF Status"
{
    Extensible = true;

    value(0; "Not Required") { Caption = 'Not Required'; }
    value(1; Required) { Caption = 'Required'; }
    value(2; Ready) { Caption = 'Ready'; }
    value(3; Processing) { Caption = 'Processing'; }
    value(4; Sent) { Caption = 'Sent'; }
    value(5; Accepted) { Caption = 'Accepted'; }
    value(6; Rejected) { Caption = 'Rejected'; }
    value(7; Error) { Caption = 'Error'; }
}
