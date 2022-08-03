using BagoumLib.Events;
using Danmokou.Reflection;
using LanguageServer.VsCode.Contracts;
using LanguageServer.VsCode.Server;
using Diagnostic = LanguageServer.VsCode.Contracts.Diagnostic;

namespace DMKLanguageServer; 



public class SessionDocument {
    /// <summary>
    /// Actually makes the changes to the inner document per this milliseconds.
    /// </summary>
    private const int RenderChangesDelay = 100;


    private Task? updateChangesDelayTask;
    private readonly object syncLock = new object();
    private List<TextDocumentContentChangeEvent>? impendingChanges = new();
    public readonly Event<SessionDocument> DocumentChanged = new();

    public LanguageServerSession Session { get; }
    public TextDocument Document { get; private set; }
    public IAST? LastSuccessfulParse { get; private set; }
    
    public SessionDocument(LanguageServerSession session, TextDocumentItem doc) {
        Session = session;
        Document = TextDocument.Load<FullTextDocument>(doc);
    }

    public void NotifyChanges(IEnumerable<TextDocumentContentChangeEvent> changes) {
        lock (syncLock) {
            if (impendingChanges == null)
                impendingChanges = changes.ToList();
            else
                impendingChanges.AddRange(changes);
        }
        if (updateChangesDelayTask == null || updateChangesDelayTask.IsCompleted) {
            updateChangesDelayTask = Task.Delay(RenderChangesDelay);
            updateChangesDelayTask.ContinueWith(t => Task.Run((Action)MakeChanges));
        }
    }
    private void MakeChanges() {
        List<TextDocumentContentChangeEvent>? localChanges;
        lock (syncLock) {
            if (impendingChanges is not { Count: > 0 }) return;
            localChanges = impendingChanges;
            impendingChanges = null;
        }
        Document = Document.ApplyChanges(localChanges);
        DocumentChanged.OnNext(this);
    }

    public IEnumerable<Diagnostic> Lint() {
        var (ast, errs) = Diagnostics.LintDocument(Document, Session.Settings.MaxNumberOfProblems);
        if (ast != null)
            lock (syncLock)
                LastSuccessfulParse = ast;
        return errs;
    }

}