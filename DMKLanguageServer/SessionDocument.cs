using BagoumLib;
using BagoumLib.Events;
using BagoumLib.Functional;
using Danmokou.Reflection;
using R2 = Danmokou.Reflection2;
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
    /// <summary>
    /// The last parse that passed all stages and could be compiled.
    /// </summary>
    public IDebugAST? LastSuccessfulParse { get; private set; }
    
    /// <summary>
    /// The last parse that passed lexing, but may have failed typechecking.
    /// </summary>
    public (IDebugAST ast, string content, Reflector.ReflCtx? ctx)? LastParse { get; private set; }

    /// <summary>
    /// Returns the last BDSL2 parse (successful or not); or the last BDSL1 parse (successful only).
    /// <br/>In BDSL1, there is limited metadata available on unsuccessful parses.
    /// </summary>
    public IDebugAST? LastBDSL2ParseOrLastSuccessfulBDSL1Parse =>
        LastParse?.ast is R2.IAST bdsl2 ?
            bdsl2 :
            LastSuccessfulParse;
    
    /// <summary>
    /// True if the most recent change was successfully lexed into <see cref="LastParse"/>.
    /// </summary>
    public bool LastChangeWasLexed { get; private set; }
    public bool LastChangeWasFullyParsed => LastChangeWasLexed && (ReferenceEquals(LastParse?.ast, LastSuccessfulParse));
    
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
        MakeChanges();
        /* This causes problems with ordering of change -> request autocomplete
        if (updateChangesDelayTask == null || updateChangesDelayTask.IsCompleted) {
            updateChangesDelayTask = Task.Delay(RenderChangesDelay);
            updateChangesDelayTask.ContinueWith(t => Task.Run((Action)MakeChanges));
        }*/
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
        var (parse, success, errs) = Diagnostics.LintDocument(Document, Session.Settings.MaxNumberOfProblems, out var content);
        lock (syncLock) {
            if (parse.Try(out var p)) {
                LastChangeWasLexed = true;
                if (p.IsRight) {
                    if (success)
                        LastSuccessfulParse = p.Right;
                    LastParse = (p.Right, content, null);
                } else {
                    if (success)
                        LastSuccessfulParse = p.Left.ast;
                    LastParse = (p.Left.ast, content, p.Left.ctx);
                }
            } else
                LastChangeWasLexed = false;
        }
        return errs;
    }

}