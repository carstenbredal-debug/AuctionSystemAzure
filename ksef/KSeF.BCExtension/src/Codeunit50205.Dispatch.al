codeunit 50205 "KPHG KSeF Dispatch"
{
    // Paced background dispatcher. Submits KSeF-required invoices / credit memos that are Ready (or in a
    // retryable Error state) to KSeF — a BOUNDED batch per run, with a small delay between each — instead
    // of submitting synchronously on posting. This decouples KSeF from BC posting so robot-volume posting
    // can't flood KSeF Test (rate limits / token exhaustion) or block the posting itself. Run by a
    // recurring Job Queue Entry (auto-created by EnsureDispatchJob from the Install/Upgrade seed).
    // Job Queue runs this as a record-based codeunit (the Job Queue Entry is the record). Without TableNo
    // the platform throws "No record is associated with the job queue entry." We ignore the passed Rec.
    TableNo = "Job Queue Entry";
    Permissions =
        tabledata "Sales Invoice Header" = RIMD,
        tabledata "Sales Cr.Memo Header" = RIMD,
        tabledata "KPHG KSeF Setup" = R;

    trigger OnRun()
    begin
        DispatchReady();
    end;

    procedure DispatchReady()
    var
        SalesInvHeader: Record "Sales Invoice Header";
        SalesCrMemoHeader: Record "Sales Cr.Memo Header";
        InvNos: List of [Code[20]];
        CrMemoNos: List of [Code[20]];
        DocNo: Code[20];
    begin
        // Collect a SMALL bounded batch of Ready / retryable-Error documents FIRST, so each run finishes
        // in seconds and never overlaps the next 1-minute run (overlapping runs were colliding on the same
        // records -> "we just updated this page" errors and BC killing the long session).
        SalesInvHeader.SetRange("KPHG KSeF Required", true);
        SalesInvHeader.SetFilter("KPHG KSeF Status", '%1|%2',
            SalesInvHeader."KPHG KSeF Status"::Ready, SalesInvHeader."KPHG KSeF Status"::Error);
        if SalesInvHeader.FindSet() then
            repeat
                InvNos.Add(SalesInvHeader."No.");
            until (SalesInvHeader.Next() = 0) or (InvNos.Count() >= MaxPerRun());

        // Submit each in isolation: a per-document failure (concurrency, KSeF reject) is CAUGHT so it can't
        // abort the whole run; Commit after each persists its outcome and frees locks before the next.
        foreach DocNo in InvNos do
            if SalesInvHeader.Get(DocNo) then begin
                if TrySendInvoice(SalesInvHeader) then;
                Commit();
            end;

        SalesCrMemoHeader.SetRange("KPHG KSeF Required", true);
        SalesCrMemoHeader.SetFilter("KPHG KSeF Status", '%1|%2',
            SalesCrMemoHeader."KPHG KSeF Status"::Ready, SalesCrMemoHeader."KPHG KSeF Status"::Error);
        if SalesCrMemoHeader.FindSet() then
            repeat
                CrMemoNos.Add(SalesCrMemoHeader."No.");
            until (SalesCrMemoHeader.Next() = 0) or (CrMemoNos.Count() >= MaxPerRun());

        foreach DocNo in CrMemoNos do
            if SalesCrMemoHeader.Get(DocNo) then begin
                if TrySendCrMemo(SalesCrMemoHeader) then;
                Commit();
            end;
    end;

    [TryFunction]
    local procedure TrySendInvoice(var SalesInvHeader: Record "Sales Invoice Header")
    var
        KSeFMgmt: Codeunit "KPHG KSeF Management";
    begin
        KSeFMgmt.AutoSendInvoiceToKSeF(SalesInvHeader);
    end;

    [TryFunction]
    local procedure TrySendCrMemo(var SalesCrMemoHeader: Record "Sales Cr.Memo Header")
    var
        KSeFMgmt: Codeunit "KPHG KSeF Management";
    begin
        KSeFMgmt.AutoSendCrMemoToKSeF(SalesCrMemoHeader);
    end;

    // Create the recurring Job Queue Entry that runs this dispatcher, if one doesn't already exist.
    // Idempotent. Wrapped by TryEnsureDispatchJob for the install/upgrade seed so a job-queue hiccup
    // never breaks app install.
    procedure EnsureDispatchJob()
    var
        JobQueueEntry: Record "Job Queue Entry";
    begin
        JobQueueEntry.SetRange("Object Type to Run", JobQueueEntry."Object Type to Run"::Codeunit);
        JobQueueEntry.SetRange("Object ID to Run", Codeunit::"KPHG KSeF Dispatch");
        if JobQueueEntry.FindFirst() then begin
            // Already exists — make sure it's actually running (recover it from an Error / On-Hold state,
            // e.g. after a code fix), by re-enqueuing it.
            if JobQueueEntry.Status <> JobQueueEntry.Status::Ready then
                Codeunit.Run(Codeunit::"Job Queue - Enqueue", JobQueueEntry);
            exit;
        end;

        JobQueueEntry.Init();
        JobQueueEntry.ID := CreateGuid();
        JobQueueEntry."Object Type to Run" := JobQueueEntry."Object Type to Run"::Codeunit;
        JobQueueEntry."Object ID to Run" := Codeunit::"KPHG KSeF Dispatch";
        JobQueueEntry.Description := CopyStr('KSeF paced dispatch', 1, MaxStrLen(JobQueueEntry.Description));
        JobQueueEntry."Recurring Job" := true;
        JobQueueEntry."No. of Minutes between Runs" := 1;
        JobQueueEntry."Run on Mondays" := true;
        JobQueueEntry."Run on Tuesdays" := true;
        JobQueueEntry."Run on Wednesdays" := true;
        JobQueueEntry."Run on Thursdays" := true;
        JobQueueEntry."Run on Fridays" := true;
        JobQueueEntry."Run on Saturdays" := true;
        JobQueueEntry."Run on Sundays" := true;
        JobQueueEntry.Insert(true);
        Codeunit.Run(Codeunit::"Job Queue - Enqueue", JobQueueEntry);
    end;

    [TryFunction]
    procedure TryEnsureDispatchJob()
    begin
        EnsureDispatchJob();
    end;

    local procedure MaxPerRun(): Integer
    begin
        // Batch per document type per run (so up to ~2x this in total). With per-doc isolation + commit,
        // an occasional overlap or a killed long run is safe (each doc's result is already committed, the
        // rest retry next run). 30 -> ~60 docs/min. Raise/lower to trade throughput vs KSeF rate limits.
        exit(30);
    end;
}
