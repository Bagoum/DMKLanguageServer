using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BagoumLib;
using Danmokou.Reflection;
using Danmokou.SM;
using JetBrains.Annotations;
using JsonRpc.Contracts;
using JsonRpc.Server;
using LanguageServer.Contracts;
using LanguageServer.VsCode;
using LanguageServer.VsCode.Contracts;
using LanguageServer.VsCode.Server;
using Diagnostic = LanguageServer.VsCode.Contracts.Diagnostic;
using Range = LanguageServer.VsCode.Contracts.Range;

namespace DMKLanguageServer.Services;
[JsonRpcScope(MethodPrefix = "textDocument/")] [PublicAPI]
public class TextDocumentService : DMKLanguageServiceBase {
    private static bool IsFallthrough(IAST? ast) =>
        ast is AST.BaseMethodInvoke { BaseMethod.IsFallthrough: true } ||
        ast is AST.Failure f && IsFallthrough(f.Basis);
    
    
    [JsonRpcMethod]
    public async Task<Hover> Hover(TextDocumentIdentifier textDocument, Position position, CancellationToken ct) {
        //can be done with failed AST
        if (Session.Documents.TryGetValue(textDocument.Uri, out var doc) && doc.LastParse is {} root) {
            var dmkp = position.ToDMKPosition(doc.Document.Content);
            if (root.ast.NarrowestASTForPosition(new(dmkp, dmkp)) is { } _asts) {
                var asts = _asts.ToArray();
                var sb = new StringBuilder();
                bool firstFunc = true;
                bool nxtRequiresDoubleSpace = false;
                var used = 0;
                foreach (var (ast, child) in asts) {
                    if (ast is AST.BaseSequence || used >= 5 || IsFallthrough(ast)) continue;
                    var bmi = ast as AST.BaseMethodInvoke;
                    sb.Append(nxtRequiresDoubleSpace ? "\n\n" : "  \n");
                    nxtRequiresDoubleSpace = false;
                    if (used == 1 && bmi != null && child.Try(out var c)) {
                        sb.Append($"as {bmi.BaseMethod.Params[c].AsParameter.EscapeTypeMD()} (argument #{c+1})");
                        if (Session.Docs.FindBySignature(bmi.BaseMethod) is { } docs &&
                            docs.Syntax?.Parameters?.Try(c) is { Description.Length: > 0 } paramDocs) {
                            sb.Append($": *{paramDocs.Description}*");
                            nxtRequiresDoubleSpace = true;
                        }
                        sb.Append("  \n");
                    }
                    if (used > 0) {
                        sb.Append("in ");
                    }
                    sb.Append(ast.Explain().Replace("<", "\\<"));
                    if ((firstFunc || used <= 1) && bmi != null) {
                        if (Session.Docs.FindBySignature(bmi.BaseMethod) is { Summary: {Length: > 0} summary })
                            sb.Append($"  \n*{summary}*");
                        firstFunc = false;
                        nxtRequiresDoubleSpace = true;
                    } else if (used == 0 && ast is AST.Preconstructed<object?> prec && prec.Value!.GetType().IsEnum) {
                        if (Session.Docs.FindEnum(prec.Value) is { Summary: { Length: > 0 } summary }) {
                            sb.Append($"  \n*{summary}*");
                            nxtRequiresDoubleSpace = true;
                        }
                    }
                    ++used;
                }return new Hover() { Contents = MarkupContent.Markdown(sb.ToString()) };
            }
            return new Hover() { Contents = $"Couldn't find anything at {position}" };
        } else {
            return new Hover() { Contents = "Couldn't parse the code" };
        }
        // Note that Hover is cancellable.
        //await Task.Delay(1000, ct);
    }

    [JsonRpcMethod]
    public DocumentSymbol[] DocumentSymbol(TextDocumentIdentifier textDocument) {
        if (Session.Documents.TryGetValue(textDocument.Uri, out var doc) && doc.LastSuccessfulParse is {} ast) {
            return new[] { ast.ToSymbolTree() };
        }
        return Array.Empty<DocumentSymbol>();
    }

    [JsonRpcMethod]
    public SignatureHelp SignatureHelp(TextDocumentIdentifier textDocument, Position position,
        SignatureHelpContext? context = null) {
        var sigs = new List<SignatureInformation>();
        //can be done with failed AST
        if (Session.Documents.TryGetValue(textDocument.Uri, out var doc) && doc.LastParse is {} root) {
            var dmkp = position.ToDMKPosition(doc.Document.Content);
            if (root.ast.NarrowestASTForPosition(new(dmkp, dmkp)) is { } _asts) {
                var asts = _asts.ToArray();
                foreach (var (ast, child) in asts) {
                    if (ast is AST.BaseSequence || IsFallthrough(ast)) continue;
                    var bmi = ast as AST.BaseMethodInvoke;
                    if (bmi != null) {
                        var docs = Session.Docs.FindBySignature(bmi.BaseMethod);
                        var paramDocs = bmi.BaseMethod.Params.Select((p, i) => 
                            new ParameterInformation(p.AsParameter, 
                                docs?.Syntax?.Parameters?.Try(i) is {Description.Length: > 0} pd ?
                                    new MarkupContent(MarkupKind.Markdown, pd.Description) : null
                            )).ToList();
                        
                        sigs.Add(new SignatureInformation(bmi.BaseMethod.AsSignature, docs?.AsMarkup, paramDocs, child));
                        if (sigs.Count == 2)
                            break;
                    }
                }
            }
        }
        return new SignatureHelp(sigs);
    }

    [JsonRpcMethod(IsNotification = true)]
    public Task DidOpen(TextDocumentItem textDocument) {
        var session = Session;
        var doc = new SessionDocument(session, textDocument);
        async Task DocChanged(SessionDocument sender) {
            // Lint the document when it's changed.
            var doc1 = sender.Document;
            var diag = sender.Lint();
            if (session.Documents.ContainsKey(doc1.Uri)) {
                // In case the document has been closed when we were linting…
                await session.Client.Document.PublishDiagnostics(doc1.Uri, diag);
            }
        }
        doc.DocumentChanged.Subscribe(x => _ = DocChanged(x));
        Session.Documents.TryAdd(textDocument.Uri, doc);
        return DocChanged(doc);
    }

    [JsonRpcMethod(IsNotification = true)]
    public void DidChange(TextDocumentIdentifier textDocument,
        ICollection<TextDocumentContentChangeEvent> contentChanges) {
        Session.Documents[textDocument.Uri].NotifyChanges(contentChanges);
    }

    [JsonRpcMethod(IsNotification = true)]
    public void WillSave(TextDocumentIdentifier textDocument, TextDocumentSaveReason reason) {
        //Client.Window.LogMessage(MessageType.Log, "-----------");
        //Client.Window.LogMessage(MessageType.Log, Documents[textDocument].Content);
    }

    [JsonRpcMethod(IsNotification = true)]
    public async Task DidClose(TextDocumentIdentifier textDocument) {
        if (textDocument.Uri.IsUntitled()) {
            await Client.Document.PublishDiagnostics(textDocument.Uri, Array.Empty<Diagnostic>());
        }
        Session.Documents.TryRemove(textDocument.Uri, out _);
    }
    
    [JsonRpcMethod]
    public CompletionList Completion(TextDocumentIdentifier textDocument, Position position,
        CompletionContext context) {
        //can be done with failed AST
        if (Session.Documents.TryGetValue(textDocument.Uri, out var doc) && doc.LastParse is { } root) {
            var dmkp = position.ToDMKPosition(doc.Document.Content);
            if (root.ast.NarrowestASTForPosition(new(dmkp, dmkp)) is {} asts) {
                //Get the first ast whose parent is not a fallthrough/compiler
                //The reason for this is to correctly handle cases such as
                // gtr { wait m|
                //The standard failed parse will return a failure at the level of "float",
                // due to fallthroughs into GCXF11 and then Const
                foreach (var (ast, parent) in asts.PairSuccessive((null!, null))) {
                    if (IsFallthrough(parent.tree))
                        continue;
                    if (GetCompletions(ast.tree.ResultType) is { Count: > 0 } comps)
                        return new CompletionList(comps);
                    else
                        return new CompletionList(Array.Empty<CompletionItem>());
                }
            } else {
                //Try looking in nonfatal errors
                foreach (var failure in root.ctx.NonfatalErrors)
                    if (failure.exc.Position.ContainsInclusiveEnd(dmkp) && 
                        GetCompletions(failure.targetType) is { Count: > 0 } comps)
                        return new CompletionList(comps);
            }
        }
        return new CompletionList(AllCompletionItems);
    }

    [JsonRpcMethod]
    public InlayHint[]? InlayHint(TextDocumentIdentifier textDocument, Range range) {
        
        //can be done with failed AST
        if (Session.Documents.TryGetValue(textDocument.Uri, out var doc) && doc.LastParse is { } root) {
            var dmkr = range.ToDMKRange(doc.Document.Content);
            var hints = new List<InlayHint>();
            bool IsInRange(IAST ast) =>
                ast.Position.Start.Index <= dmkr.End.Index && ast.Position.End.Index >= dmkr.Start.Index;
            void Process(IAST ast) {
                if (IsInRange(ast)) {
                    if (ast is AST.BaseMethodInvoke bmi && !bmi.BaseMethod.IsFallthrough && bmi.Parenthesized && bmi.Params.Length > 1) {
                        bool isSm = bmi is AST.MethodInvoke { Type: AST.MethodInvoke.InvokeType.SM };
                        foreach (var (i, c) in bmi.Params.Enumerate()) {
                            if (isSm && i == 0 && bmi.BaseMethod.Params[0].Type == StateMachine.SMChildStatesType)
                                continue;
                            if (bmi.BaseMethod.Params[i].NonExplicit)
                                continue;
                            //This filters out macro cases
                            if (c.Position.Start.Index <= ast.Position.Start.Index || c.Position.End.Index >= ast.Position.End.Index)
                                continue;
                            var tt = new StringBuilder();
                            tt.Append(bmi.BaseMethod.AsSignatureWithParamMod((p, j) =>
                                (j == i ? $"**{p.AsParameter}**" : p.AsParameter).EscapeTypeMD()));
                            if (Session.Docs.FindBySignature(bmi.BaseMethod) is { } docs &&
                                docs.Syntax?.Parameters?.Try(i) is { Description.Length: > 0 } pDocs) {
                                tt.Append($"\n\n*{pDocs.Description}*");
                            }
                            
                            hints.Add(new(c.Position.Start.ToPosition(), bmi.BaseMethod.Params[i].Name + ":", InlayHintKind.Parameter) {
                                PaddingRight = true,
                                Tooltip = MarkupContent.Markdown(tt.ToString())
                            });
                        }
                    }
                    foreach (var c in ast.Children)
                        Process(c);
                }
            }
            Process(root.ast);
            return hints.ToArray();
        }
        return null;
        /*return new[] {
            new InlayHint(new Position(3, 6), "hello:", InlayHintKind.Type),
            new InlayHint(new Position(4, 9), "world:", InlayHintKind.Parameter) {
                PaddingRight = true,
                Tooltip = "foo bar"
            }
        };*/
    }

    //handle implicit conversions via constructor types, TaskPattern/TTaskpattern, fallthrough/compile

    private Dictionary<Type, List<CompletionItem>> CompletionItemsByReturnType = new();

    private List<CompletionItem> GetCompletions(Type rt) {
        if (CompletionItemsByReturnType.TryGetValue(rt, out var ret))
            return ret;
        var items = Reflector.ReflectionData.AllMethodsForReturnType(rt).Select(AsItem).ToList();
        if (Reflector.FallThroughOptions.TryGetValue(rt, out var ftmi))
            items.AddRange(GetCompletions(ftmi.mi.Params[0].Type));
        if (Reflector.TryCompileOption(rt, out var cmp))
            items.AddRange(GetCompletions(cmp.source));
        if (Reflector.FuncifySimplifications.TryGetValue(rt, out var st))
            items.AddRange(GetCompletions(st));
        if (rt == typeof(StateMachine)) {
            items.AddRange(GetCompletions(typeof(TaskPattern)));
            items.AddRange(GetCompletions(typeof(TTaskPattern)));
            items.AddRange(StateMachine.SMInitMap.Select(kv => AsItem(kv.Key, Reflector.GetConstructorSignature(kv.Value))));
        } 
        //how do we make a dependency on the type of the parent object? :(
        /*else if (rt.IsSubclassOf(typeof(StateMachine))) {
            if (StateMachine.CheckCreatableChild(rt, typeof(ReflectableLASM)))
                items.AddRange(GetCompletions(typeof(TaskPattern)));
            if (StateMachine.CheckCreatableChild(rt, typeof(ReflectableSLSM)))
                items.AddRange(GetCompletions(typeof(TTaskPattern)));
            var childMapper = rt;
            while (childMapper != null && !StateMachine.SMChildMap.ContainsKey(childMapper))
                childMapper = childMapper.BaseType;
            if (childMapper != null)
                items.AddRange(StateMachine.SMChildMap[childMapper].Select);
        }*/

        return CompletionItemsByReturnType[rt] = items;
    }

    private CompletionItem AsItem(string method, Reflector.MethodSignature sig) =>
        new CompletionItem(method, CompletionItemKind.Method,
            sig.AsSignature,
            Session.Docs.FindBySignature(sig)?.AsMarkup) {
            InsertText = method.ToLower(),
        };

    private CompletionItem AsItem(string method, MethodBase mi) =>
        AsItem(method, Reflector.MethodSignature.FromMethod(mi));

    private CompletionItem AsItem(KeyValuePair<string, MethodInfo> kv) => AsItem(kv.Key, kv.Value);

    private CompletionItem[]? _completionItems = null;
    private CompletionItem[] AllCompletionItems => _completionItems ??= 
        Reflector.ReflectionData.AllMethods().Select(AsItem).ToArray();
}