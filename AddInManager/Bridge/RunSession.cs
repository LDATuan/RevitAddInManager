using System.Diagnostics;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Application = Autodesk.Revit.ApplicationServices.Application;

namespace RevitAddinManager.Bridge;

public sealed class RunOptions
{
    /// <summary>Run, record every change, then roll everything back.</summary>
    public bool DryRun { get; set; }

    /// <summary>none | after | beforeAfter</summary>
    public string Screenshot { get; set; } = "none";

    /// <summary>Elements whose parameters are snapshotted before the run. Defaults to the selection.</summary>
    public List<ElementId> WatchIds { get; set; }
}

public sealed class RunOutcome
{
    public string Result { get; set; }
    public string Message { get; set; }
    public string Exception { get; set; }
    public object ReturnValue { get; set; }
}

/// <summary>
/// Wraps a run of add-in code in a TransactionGroup and records what it did to the model:
/// added/modified/deleted elements, parameter diffs, Revit warnings, screenshots.
/// Successful runs are assimilated into a single undo step that undo_last_run can revert.
/// </summary>
public static class RunSession
{
    private const int MaxListed = 100;
    private const int MaxWatched = 200;

    public static object Run(UIApplication app, string label, RunOptions options, Func<RunOutcome> body)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc?.Document;
        var traceStart = TraceBuffer.LastSeq;
        var stopwatch = Stopwatch.StartNew();
        var screenshots = new List<object>();
        var notes = new List<string>();

        if (doc != null && options.Screenshot == "beforeAfter") TryCapture(doc, uidoc.ActiveView, "before", screenshots, notes);

        var watchIds = (options.WatchIds ?? uidoc?.Selection.GetElementIds().ToList() ?? new List<ElementId>())
            .Take(MaxWatched).ToList();
        var watchedBefore = ElementSnapshot.Take(doc, watchIds);

        var recorder = new ChangeRecorder(doc);
        recorder.Attach(app.Application);

        TransactionGroup group = null;
        if (doc != null && !doc.IsReadOnly && !doc.IsModifiable)
        {
            group = new TransactionGroup(doc, $"AI: {label}");
            group.Start();
        }

        RunOutcome outcome;
        using (UndoTracker.Suppress())
        {
            try
            {
                outcome = body();
            }
            finally
            {
                recorder.Detach(app.Application);
            }

            var succeeded = outcome.Result == "Succeeded";
            var touchedIds = recorder.Modified.Concat(recorder.Added).Concat(watchIds).Distinct().Take(MaxListed * 2).ToList();
            var after = ElementSnapshot.Take(doc, touchedIds);

            if (succeeded && doc != null && doc.IsValidObject && options.Screenshot != "none")
                TryCapture(doc, uidoc.ActiveView, "after", screenshots, notes);

            string undo;
            var before = watchedBefore;
            if (group == null || !group.IsValidObject || !group.HasStarted())
            {
                undo = "notTracked";
            }
            else if (!succeeded)
            {
                group.RollBack();
                undo = "rolledBack: command did not succeed, so its changes were discarded like Revit does";
            }
            else if (options.DryRun)
            {
                group.RollBack();
                undo = "rolledBack: dry run, the model is unchanged";
                // The model is back in its original state, so the true "before" values can be read now.
                before = ElementSnapshot.Take(doc, recorder.Modified.Concat(watchIds).Distinct());
            }
            else if (!recorder.HasChanges)
            {
                // Nothing to undo: marking this run would make undo_last_run revert the user's previous step.
                group.Assimilate();
                undo = "noChanges";
            }
            else
            {
                group.Assimilate();
                undo = "committed: undo_last_run can revert it as one step";
                UndoTracker.MarkRun(doc, label);
            }

            if (!options.DryRun && succeeded && recorder.Modified.Any(id => !watchedBefore.ContainsKey(id.ToString())))
                notes.Add("Parameter values before the run are only known for watched/selected elements. Use dryRun for a full diff.");

            stopwatch.Stop();
            return new
            {
                label,
                result = outcome.Result,
                message = outcome.Message,
                exception = outcome.Exception,
                returnValue = outcome.ReturnValue,
                durationMs = stopwatch.ElapsedMilliseconds,
                undo,
                changes = BuildChanges(doc, recorder, before, after),
                failures = recorder.Failures,
                trace = TraceBuffer.ReadSince(traceStart, 500).Select(x => x.Message),
                traceSeq = TraceBuffer.LastSeq,
                screenshots,
                notes,
            };
        }
    }

    private static object BuildChanges(Document doc, ChangeRecorder recorder,
        Dictionary<string, ElementSnapshot> before, Dictionary<string, ElementSnapshot> after)
    {
        var modified = recorder.Modified
            .Select(id => id.ToString())
            .Union(before.Keys.Where(after.ContainsKey))
            .Select(id =>
            {
                after.TryGetValue(id, out var a);
                before.TryGetValue(id, out var b);
                var parameters = a != null && b != null ? ElementSnapshot.Diff(b, a) : null;
                return new { id, name = a?.Name ?? b?.Name, category = a?.Category ?? b?.Category, parameters };
            })
            // Elements that were only watched but did not change are noise.
            .Where(x => x.parameters == null || x.parameters.Count > 0 || recorder.Modified.Any(m => m.ToString() == x.id))
            // Elements with a category and real parameter changes are the interesting ones.
            .OrderByDescending(x => x.parameters?.Count ?? 0)
            .ThenBy(x => x.category == null)
            .ToList();

        return new
        {
            transactions = recorder.TransactionNames.Distinct(),
            added = new
            {
                count = recorder.Added.Count,
                elements = recorder.Added.Take(MaxListed).Select(id => after.TryGetValue(id.ToString(), out var s)
                    ? (object)new { id = s.Id, name = s.Name, category = s.Category }
                    : new { id = id.ToString() }),
            },
            modified = new { count = modified.Count, elements = modified.Take(MaxListed) },
            deleted = new { count = recorder.Deleted.Count, ids = recorder.Deleted.Take(MaxListed).Select(x => x.ToString()) },
        };
    }

    private static void TryCapture(Document doc, Autodesk.Revit.DB.View view, string tag, List<object> screenshots, List<string> notes)
    {
        try
        {
            screenshots.Add(new { tag, view = view?.Name, path = ViewCapture.Capture(doc, view, tag) });
        }
        catch (Exception ex)
        {
            notes.Add($"Screenshot '{tag}' failed: {ex.Message}");
        }
    }

    private sealed class ChangeRecorder
    {
        private readonly Document doc;

        public ChangeRecorder(Document doc)
        {
            this.doc = doc;
        }

        public HashSet<ElementId> Added { get; } = new HashSet<ElementId>();
        public HashSet<ElementId> Modified { get; } = new HashSet<ElementId>();
        public HashSet<ElementId> Deleted { get; } = new HashSet<ElementId>();
        public List<string> TransactionNames { get; } = new List<string>();
        public List<object> Failures { get; } = new List<object>();

        public bool HasChanges => Added.Count > 0 || Modified.Count > 0 || Deleted.Count > 0;

        public void Attach(Application application)
        {
            application.DocumentChanged += OnDocumentChanged;
            application.FailuresProcessing += OnFailuresProcessing;
        }

        public void Detach(Application application)
        {
            application.DocumentChanged -= OnDocumentChanged;
            application.FailuresProcessing -= OnFailuresProcessing;
        }

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (doc != null && !e.GetDocument().Equals(doc)) return;
            TransactionNames.AddRange(e.GetTransactionNames());
            foreach (var id in e.GetAddedElementIds()) Added.Add(id);
            foreach (var id in e.GetDeletedElementIds())
            {
                Deleted.Add(id);
                Added.Remove(id);
                Modified.Remove(id);
            }
            foreach (var id in e.GetModifiedElementIds())
            {
                if (!Added.Contains(id)) Modified.Add(id);
            }
        }

        private void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            // Observe only; resolving failures stays with the add-in and Revit.
            foreach (var failure in e.GetFailuresAccessor().GetFailureMessages())
            {
                Failures.Add(new
                {
                    severity = failure.GetSeverity().ToString(),
                    description = failure.GetDescriptionText(),
                    elementIds = failure.GetFailingElementIds().Select(x => x.ToString()),
                });
            }
        }
    }
}

/// <summary>
/// Remembers the last committed run so it can be undone, but only while nothing else has changed the model since.
/// </summary>
public static class UndoTracker
{
    private static long changeCounter;
    private static int suppressDepth;
    private static Document lastDoc;
    private static string lastLabel;
    private static long lastCounter;

    public static void Attach(ControlledApplication application) => application.DocumentChanged += (_, _) =>
    {
        if (suppressDepth == 0) changeCounter++;
    };

    public static IDisposable Suppress() => new SuppressScope();

    public static void MarkRun(Document doc, string label)
    {
        lastDoc = doc;
        lastLabel = label;
        lastCounter = changeCounter;
    }

    public static object UndoLast(UIApplication app)
    {
        if (lastDoc == null || !lastDoc.IsValidObject) throw new InvalidOperationException("There is no committed run to undo.");
        if (changeCounter != lastCounter)
            throw new InvalidOperationException(
                $"The model changed after '{lastLabel}' ran (user edit or another command), so undoing now would revert the wrong step. Use Undo in Revit instead.");
        if (app.ActiveUIDocument == null || !app.ActiveUIDocument.Document.Equals(lastDoc))
            throw new InvalidOperationException($"Activate the document '{lastDoc.Title}' first.");

        app.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.Undo));
        var label = lastLabel;
        lastDoc = null;
        return new { posted = true, undone = label, note = "Revit performs the undo right after this call returns." };
    }

    private sealed class SuppressScope : IDisposable
    {
        public SuppressScope() => suppressDepth++;

        public void Dispose() => suppressDepth--;
    }
}
