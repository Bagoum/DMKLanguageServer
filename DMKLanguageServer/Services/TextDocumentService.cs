using System.Reflection;
using System.Text;
using BagoumLib;
using BagoumLib.Expressions;
using BagoumLib.Reflection;
using BagoumLib.Unification;
using Danmokou.Danmaku;
using Danmokou.Reflection;
using R2 = Scriptor.Compile;
using Danmokou.SM;
using JetBrains.Annotations;
using JsonRpc.Contracts;
using LanguageServer.Contracts;
using LanguageServer.VsCode;
using LanguageServer.VsCode.Contracts;
using Scriptor;
using Scriptor.Analysis;
using Scriptor.Compile;
using Scriptor.Definition;
using Scriptor.Reflection;
using AST = Danmokou.Reflection.AST;
using Diagnostic = LanguageServer.VsCode.Contracts.Diagnostic;
using IAST = Danmokou.Reflection.IAST;
using Range = LanguageServer.VsCode.Contracts.Range;

namespace DMKLanguageServer.Services;
[JsonRpcScope(MethodPrefix = "textDocument/")] [PublicAPI]
public class TextDocumentService : DMKLanguageServiceBase {
    private static bool IsFallthrough(IDebugAST? ast) =>
        //new R2.AST has no fallthrough methods
        ast is AST.BaseMethodInvoke { BaseMethod.Mi.IsFallthrough: true } ||
        ast is AST.Failure f && IsFallthrough(f.Basis);
    
    private IMethodSignature[] Methods(IDebugAST ast) => (ast switch {
        AST.BaseMethodInvoke bmi => new[] { bmi.BaseMethod.Mi },
        R2.AST.MethodCall meth => meth.SelectedOverload?.method is { } im ?
            new[]{ im.Mi } :
            meth.Methods.Select(m => m.Mi),
        R2.AST.Failure { Completions: {} c} => c.IsLeft ? c.Left : FromTypeCompletion(c.Right), 
        _ => null
    })?.ToArray() ?? Array.Empty<IMethodSignature>();

    private IEnumerable<MethodSignature>? FromTypeCompletion((Type, string)? comp) {
        if (!comp.HasValue)
            return null;
        var (typ, memb) = comp.Value;
        return MethodsForInstance(typ, true, memb, true);
    }
    
    [JsonRpcMethod]
    public async Task<Hover> Hover(TextDocumentIdentifier textDocument, Position position, CancellationToken ct) {
        //can be done with failed AST
        if (Session.Documents.TryGetValue(textDocument.Uri, out var doc) && doc.LastParse is {} root) {
            var dmkp = position.ToDMKPosition(root.content);
            if (root.ast.NarrowestASTForPosition(new(dmkp, dmkp)) is { } _asts) {
                var asts = _asts.ToArray();
                var sb = new StringBuilder();
                bool firstFunc = true;
                bool nxtRequiresDoubleSpace = false;
                string nextInString = "in ";
                var used = 0;
                foreach (var (ast, child) in asts) {
                    if (ast is AST.BaseSequence or R2.AST.Array or R2.AST.Block || used >= 5 || IsFallthrough(ast)) continue;
                    if (ast is R2.AST.Return) {
                        nextInString = "returned in ";
                        continue;
                    }
                    var methods = Methods(ast);
                    //Failure AST may have methods that don't match up with params. Don't use them for hover info.
                    var mi = methods.Length > 0 ? 
                        methods.FirstOrDefault(m => m.Params.Length == ast.Children.Count()) : null;
                    sb.Append(nxtRequiresDoubleSpace ? "\n\n" : "  \n");
                    nxtRequiresDoubleSpace = false;
                    if (used == 1 && mi != null && child.Try(out var c)) {
                        sb.Append($"as {mi.Params[c].AsParameter.EscapeTypeMD()} (argument #{c+1})");
                        if (Session.Docs.FindBySignature(mi) is { } docs &&
                            docs.Syntax?.Parameters?.Try(c) is { Description.Length: > 0 } paramDocs) {
                            sb.Append($": *{paramDocs.Description}*");
                            nxtRequiresDoubleSpace = true;
                        }
                        sb.Append("  \n");
                    }
                    if (used > 0) {
                        sb.Append(nextInString);
                        nextInString = "in ";
                    }
                    var explain = ast.Explain();
                    nxtRequiresDoubleSpace |= explain.Contains("  \n") && explain.EndsWith("*");
                    sb.Append(explain.Replace("<", "\\<").Replace("`\\<", "`<"));
                    if ((firstFunc || used <= 1) && mi != null) {
                        if (Session.Docs.FindBySignature(mi) is { Summary: {Length: > 0} summary })
                            sb.Append($"  \n*{summary}*");
                        firstFunc = false;
                        nxtRequiresDoubleSpace = true;
                    } else if (used == 0) {
                        object enumVal = null!;
                        if (ast is AST.Preconstructed<object?> prec && prec.Value!.GetType().IsEnum) {
                            enumVal = prec.Value;
                        } else if (ast is R2.AST.Reference refr && refr.TryGetAsEnum(out enumVal, out _)) {
                        } else
                            goto next_loop;
                        if (Session.Docs.FindEnum(enumVal) is { Summary: { Length: > 0 } summary }) {
                            sb.Append($"  \n*{summary}*");
                            nxtRequiresDoubleSpace = true;
                        }
                    }
                    next_loop:
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
        if (Session.Documents.TryGetValue(textDocument.Uri, out var doc) && doc.LastBDSL2ParseOrLastSuccessfulBDSL1Parse is {} ast) {
            var topSym = ast.ToSymbolTree();
            //unpack top-level block
            return topSym.Name == "Block" ? 
                topSym.Children ?? Array.Empty<DocumentSymbol>() : 
                new[] { topSym };
        }
        return Array.Empty<DocumentSymbol>();
    }

    [JsonRpcMethod]
    public SignatureHelp SignatureHelp(TextDocumentIdentifier textDocument, Position position,
        SignatureHelpContext? context = null) {
        var sigs = new List<SignatureInformation>();
        int height = 0;
        //can be done with failed AST
        if (Session.Documents.TryGetValue(textDocument.Uri, out var doc) && doc.LastParse is {} root) {
            var dmkp = position.ToDMKPosition(root.content);
            if (root.ast.NarrowestASTForPosition(new(dmkp, dmkp)) is { } _asts) {
                var asts = _asts.ToArray();
                foreach (var (ast, child) in asts) {
                    if (ast is AST.BaseSequence || IsFallthrough(ast)) continue;
                    if (ast is R2.AST.ScriptFunctionCall sfc) {
                        var sdef = sfc.Definition;
                        var paramDocs = sdef.Args.Select((p, i) => new ParameterInformation(p.AsParam, null)).ToList();
                        var si = new SignatureInformation(sdef.AsSignature(sfc.InImport?.Name),
                            sdef.DocComment is { } docs ? MarkupContent.PlainText(docs) : null, paramDocs, child);
                        sigs.Add(si);
                    } else {
                        var methods = Methods(ast);
                        if (methods.Length == 0) continue;
                        sigs.AddRange(methods.Select(m => {
                            var docs = Session.Docs.FindBySignature(m);
                            var paramDocs = m.Params.Select((p, i) =>
                                new ParameterInformation(p.AsParameter,
                                    docs?.Syntax?.Parameters?.Try(i) is { Description.Length: > 0 } pd ?
                                        new MarkupContent(MarkupKind.Markdown, pd.Description) :
                                        null
                                )).ToList();
                            return new SignatureInformation(m.AsSignature, docs?.AsMarkup, paramDocs, child);
                        }));
                    }
                    if (++height >= 2)
                        break;
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
            CompletionList Finish(IEnumerable<CompletionItem> items) => 
                //always set incomplete because that will always refresh completions on type
                new(items.ToArray(), true); 
            var dmkp = position.ToDMKPosition(root.content);
            if (root.ast.NarrowestASTForPosition(new(dmkp, dmkp)) is {} asts) {
                //Get the first ast whose parent is not a fallthrough/compiler
                //The reason for this is to correctly handle cases such as
                // gtr { wait m|
                //The standard failed parse will return a failure at the level of "float",
                // due to fallthroughs into GCXF11 and then Const
                foreach (var (ast, parent) in asts.PairSuccessive((null!, null))) {
                    if (IsFallthrough(parent.tree))
                        continue;
                    if (ast.tree is IAST iast1) {
                        var resultType = iast1.ResultType;
                        if (resultType != null! && GetCompletions1(resultType) is { Length: > 0 } comps)
                            return Finish(comps);
                    }
                    if (ast.tree is R2.AST iast2) {
                        var rtd = ((R2.IAST)iast2).SelectedOverloadReturnTypeNoCast;
                        var asImport = (iast2 as R2.AST.Failure)?.ImportedScript;
                        var useImportPrefix = (iast2 as R2.AST.Failure)?.IsImportedScriptMember is not true;
                        var scope = asImport?.Ef.Scope ?? iast2.Scope;
                        CompletionList AllBDSL2Completions() =>
                            Finish(GetCompletions2(new TypeDesignation.Variable(), scope, asImport, useImportPrefix));
                        if (ast.tree is R2.AST.InstanceFailure inf &&
                            inf.SelectedOverload?.simplified.Arguments[0].Resolve().LeftOrNull is { } typ) {
                            return Finish(CompletionsForInstance(typ, false));
                        } else if (ast.tree is R2.AST.Failure { Completions:{} c}) {
                            if (c.TryL(out var sigs)) {
                                return sigs != null ? 
                                    Finish(sigs.Select(c => AsItem(null, c)).ToArray()) : 
                                    AllBDSL2Completions();
                            } else {
                                //don't use the member name to get completions
                                return Finish(CompletionsForInstance(c.Right.Item1, true));
                            }
                        } else if (ast.tree is R2.AST.Failure { IsTypeCompletion: true })
                            return Finish(BDSL2TypeCompletions);
                        else if (ast.tree is R2.AST.PartialMethodCall)
                            return AllBDSL2Completions();
                        else if (rtd != null && iast2 is not R2.AST.IAnyTypedValueAST && GetCompletions2(rtd, scope, asImport, useImportPrefix).ToArray() is { Length: > 0 } comps)
                            return Finish(comps);
                        else if (ast.tree is R2.AST.Failure f) {
                            if (f.PossibleTypes.Length > 0)
                                return Finish(f.PossibleTypes.SelectMany(t => GetCompletions2(t, scope, asImport, useImportPrefix)).ToArray());
                            else if (asImport != null)
                                return AllBDSL2Completions();
                        } else if (ast.tree is R2.AST.InstanceMethodCall imc && (imc.Overloads?.Count ?? 0) == 0) {
                            var arg0Types = imc.Arg0PossibleTypes ?? (imc.Params[0]
                                .PossibleUnifiers(imc.Scope.GlobalRoot.Resolver, Unifier.Empty).LeftOrNull ?? new());
                            return Finish(arg0Types
                                    .SelectNotNull(x => {
                                        var td = x.Item1;
                                        if (td.IsResolved) {
                                            return td.Resolve().LeftOrThrow;
                                        } else if (td is TypeDesignation.Known k) {
                                            return k.Typ;
                                        } else
                                            return null;
                                    }).SelectMany(t => CompletionsForInstance(t, false)));
                        }
                    }
                    return Finish(Array.Empty<CompletionItem>());
                }
            } else {
                //Try looking in nonfatal errors
                if (root.ctx != null)
                    foreach (var failure in root.ctx.NonfatalErrors)
                        if (failure.exc.Position.ContainsInclusiveEnd(dmkp) && 
                            GetCompletions1(failure.targetType) is { Length: > 0 } comps)
                            return Finish(comps);
            }
        }
        return new CompletionList(AllCompletionItems, true);
    }

    private IEnumerable<MethodSignature> MethodsForInstance(Type instTyp, bool isStatic, string? name = null, 
        bool showToStringVariants=false) { //only show toString variants for signature help, not completion
        var members = name == null ? instTyp.GetMembers() : instTyp.GetMember(name);
        var sigs = members.Where(x => x switch {
                MethodBase mb => !mb.IsSpecialName && mb.Name != "Equals" && mb.Name != "GetTypeCode" &&
                                 (showToStringVariants || mb.Name != "ToString" || mb.GetParameters().Length == 0),
                PropertyInfo pi => pi.Name != "Item",
                _ => true
            }).SelectNotNull(MethodSignature.MaybeGet).Where(m => m.IsStatic == isStatic);
        if (!isStatic)
            sigs = sigs.Concat(GlobalScope.Singleton.ExtensionMethods(instTyp, name));
        return sigs;
    }
    private IEnumerable<CompletionItem> CompletionsForInstance(Type instTyp, bool isStatic) {
        return MethodsForInstance(instTyp, isStatic)
                    .Where(sig => sig.Member is { BaseMi: not MethodBase {IsSpecialName:true} })
                    .Select(AsInstanceItem);
    }

    [JsonRpcMethod]
    public InlayHint[]? InlayHint(TextDocumentIdentifier textDocument, Range range) {
        
        //can be done with failed AST
        if (Session.Documents.TryGetValue(textDocument.Uri, out var doc) && doc.LastParse is { } root) {
            var dmkr = range.ToDMKRange(root.content);
            var hints = new List<InlayHint>();
            bool IsInRange(IDebugAST ast) =>
                ast.Position.Start.Index <= dmkr.End.Index && ast.Position.End.Index >= dmkr.Start.Index;
            void Process(IDebugAST ast) {
                if (IsInRange(ast)) {
                    if (ast is AST.BaseMethodInvoke { BaseMethod.Mi.IsFallthrough: false, Parenthesized: true, Params.Length: > 1 } bmi) {
                        foreach (var (i, c) in bmi.Params.Enumerate()) {
                            var ft = bmi.BaseMethod.Mi.FeaturesAt(i);
                            if (ft?.BDSL1ImplicitSMList is true || ft?.NonExplicit is true)
                                continue;
                            //This filters out macro cases
                            if (c.Position.Start.Index <= ast.Position.Start.Index || c.Position.End.Index >= ast.Position.End.Index)
                                continue;
                            var tt = new StringBuilder();
                            tt.Append(bmi.BaseMethod.Mi.AsSignatureWithParamMod((p, j) =>
                                (j == i ? $"**{p.AsParameter}**" : p.AsParameter).EscapeTypeMD()));
                            if (Session.Docs.FindBySignature(bmi.BaseMethod.Mi) is { } docs &&
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

    private Dictionary<Type, CompletionItem[]> CompletionItemsByReturnTypeBDSL1 = new();
    private Dictionary<TypeDesignation, CompletionItem[]> CompletionItemsByReturnTypeBDSL2 = new();
    private static readonly HashSet<Type> doShowTypes = new() { typeof(UncompiledCode<>) };
    private static readonly HashSet<Type> dontShowTypes = new() { typeof(BulletManager.exBulletControl) };
    private static readonly string[] dontUseNS = { "Unity.IO", "Unity.Jobs", "Danmokou.Reflection", "Danmokou.UI",
        "Danmokou.GameInstance", "Danmokou.Graphics", "Danmokou.Expressions",
        "MiniProjects.VN", "BagoumLib.Expressions", "BagoumLib.Reflection", "BagoumLib.Unification", "BagoumLib.Assertions",
        "Mizuhashi", "System.Threading", "System.Reflection", "System.IO", "System.Text", "System.Globalization",
        "System.Diagnostics", "System.Security", "System.Resources", "Unity.Profiling",
    };
    private CompletionItem[] BDSL2TypeCompletions =
        LangParser.TypeDef.GenericToType.Values.SelectMany(x => x.Where(kv => {
                    var (name, typ) = kv;
                    if (doShowTypes.Contains(typ)) return true;
                    if (CSharpTypePrinter.SimpleTypeNameMap.ContainsKey(typ)) return false;
                    if (typ.Namespace is { } ns) {
                        if (ns.StartsWith("UnityEngine")) {
                            if (ns != "UnityEngine") return false;
                            if (name.StartsWith("Light")) return false;
                        }
                        if (ns.StartsWith("Unity") && name.EndsWith("Exception")) return false;
                        if (dontUseNS.Any(dns => ns.StartsWith(dns))) return false;
                    }
                    if (dontShowTypes.Contains(typ)) return false;
                    if (typ.IsAbstract && !typ.IsInterface) return false;
                    return true;
                }))
            .DistinctBy(kv => kv.Key)
            .Select(kv => new CompletionItem(kv.Key, 
                kv.Value.IsInterface ? CompletionItemKind.Interface :
                kv.Value.IsEnum ? CompletionItemKind.Enum :
                CompletionItemKind.Class, Documentation.TypePrinter.Print(kv.Value), null))
            .Concat(CSharpTypePrinter.SimpleTypeNameMap.Values
                .Select(typ => new CompletionItem(typ, CompletionItemKind.Class) {
                    SortText = "_" + typ
                }))
            .ToArray();

    private IEnumerable<CompletionItem> GetCompletions2(TypeDesignation rtd, LexicalScope scope, ScriptImport? asImport, bool prependImportScope) {
        var vars = new Dictionary<(string, Type?), VarDecl>();
        foreach (var v in scope.AllVisibleVars) {
            vars.TryAdd((v.Name, v.FinalizedType), v);
        }
        var rt = rtd.Resolve().LeftOrNull;
        var locals = vars.Values
            //.Where(v => (v.FinalizedTypeDesignation?.IsResolved is not true) || !rtd.IsResolved || v.FinalizedTypeDesignation == rtd)
            .SelectNotNull(AsItem)
            .Concat(scope.AllVisibleScriptFns
                .Where(v => rt is null || v.ReturnType == rt).Select(AsItem))
            .Concat(scope.ImportDecls.Values.Select(AsItem))
            .Select(x => {
                if (asImport?.Name is { } n) {
                    if (prependImportScope) 
                        x.FilterText = x.InsertText = $"{n}.{x.InsertText}";
                    x = x with {
                        Label = $"{n}.{x.Label}",
                    };
                }
                return x;
            });
        if (asImport is not null)
            return locals;
        return locals
            .Concat(GetMethodCompletions2(rtd));
    }
    private CompletionItem[] GetMethodCompletions2(TypeDesignation rtd) {
        if (CompletionItemsByReturnTypeBDSL2.TryGetValue(rtd, out var ret))
            return ret;
        List<CompletionItem> items = new();
        var rt = rtd.Resolve().LeftOrNull;
        items.AddRange(Reflector.ReflectionData.AllBDSL2Methods.Values
            .SelectMany(x => x)
            .Distinct() //removes aliases
            .Where(mi => {
                if (mi.Name.StartsWith('_'))
                    return false;
                if (mi.SharedType.Last.Unify(rtd, Unifier.Empty).IsLeft)
                    return true;
                if (rt == typeof(StateMachine) && mi.SharedType.Last is TypeDesignation.Known { Typ: {} kt} &&
                    (kt == typeof(ReflectableLASM) || kt == typeof(TaskPattern) || kt == typeof(TTaskPattern))) {
                    return true;
                }
                return DMKScope.Singleton.TryFindConversion(rtd, mi.SharedType.Last) is not null;
            })
            .SelectMany(mi => mi.Member.BaseMi.GetCustomAttributes<AliasAttribute>()
                .Select(alias => AsItem(alias.alias, mi)).Prepend(AsItem(null, mi)))
        );

        if (rt != null && Helpers.bdsl2EnumResolvers.TryGetValue(rt, out var vals)) {
            items.AddRange(vals.Select(kv => AsEnumItem(kv.name, kv.val)));
        }
        if (rt == typeof(StateMachine) || rtd is TypeDesignation.Variable) {
            items.AddRange(StateMachine.SMInitMap.Select(kv => AsItem(kv.Key, Reflector.GetConstructorSignature(kv.Value))));
        } 
        return CompletionItemsByReturnTypeBDSL2[rtd] = items.ToArray();
    }
    private CompletionItem[] GetCompletions1(Type rt) {
        if (CompletionItemsByReturnTypeBDSL1.TryGetValue(rt, out var ret))
            return ret;
        List<CompletionItem> items = new();
        items.AddRange(Reflector.ReflectionData.MethodsAndGenericsForType(rt)
            .Select(kv => AsItem(kv.Item1, kv.Item2)));
        if (Reflector.FallThroughOptions.TryGetValue(rt, out var ftmi))
            items.AddRange(GetCompletions1(ftmi.mi.Params[0].Type));
        if (Reflector.TryCompileOption(rt, out var cmp))
            items.AddRange(GetCompletions1(cmp.source));
        if (Reflector.FuncifySimplifications.TryGetValue(rt, out var st))
            items.AddRange(GetCompletions1(st));
        if (rt == typeof(StateMachine)) {
            items.AddRange(GetCompletions1(typeof(TaskPattern)));
            items.AddRange(GetCompletions1(typeof(TTaskPattern)));
            items.AddRange(StateMachine.SMInitMap.Select(kv => AsItem(kv.Key, Reflector.GetConstructorSignature(kv.Value))));
        } 
        return CompletionItemsByReturnTypeBDSL1[rt] = items.ToArray();
    }

    private static IEnumerable<char> MethodCommitters = new[] { '(' };
    private CompletionItem? AsItem(VarDecl decl) =>
        decl.Name.StartsWith("$") ? null :
        new(decl.Name, CompletionItemKind.Variable, decl.AsParam, decl.DocComment ?? (
            decl.SourceImplicit == null ? "User-defined variable": "Function parameter")) {
            InsertText = decl.Name
        };
    
    private CompletionItem AsEnumItem(string name, object val) =>
            new(name, CompletionItemKind.Enum, $"{val.GetType().RName()}.{name}", 
                Session.Docs.FindEnum(val) is { Summary: { Length: > 0 } summary } ? MarkupContent.Markdown(summary) : null) {
                InsertText = name
            };
    
    private CompletionItem AsItem(ScriptFnDecl decl) =>
        new(decl.Name, CompletionItemKind.Function, decl.AsSignature(), decl.DocComment ?? "User-defined function") {
            InsertText = decl.Name,
            CommitCharacters = MethodCommitters
        };
    
    private CompletionItem AsItem(ScriptImport decl) =>
        new(decl.ImportAs ?? decl.FileKey, CompletionItemKind.Function, 
            $"Script {decl.FileKey}" + (decl.ImportAs is null ? "" : $" (as {decl.ImportAs})"), 
            decl.DocComment ?? (
            decl.ImportFrom is null ? "Imported script" : $"Script imported from `{decl.ImportFrom}`")) {
            InsertText = (decl.ImportAs ?? decl.FileKey) + "."
        };


    private static CompletionItemKind MethCompletion(MethodSignature sig) => sig.Member.Symbol() switch {
        SymbolKind.Enum => CompletionItemKind.Enum,
        SymbolKind.Property => CompletionItemKind.Property,
        SymbolKind.Field => CompletionItemKind.Field,
        SymbolKind.Constructor => CompletionItemKind.Class,
        _ => CompletionItemKind.Method
    };
    private CompletionItem AsInstanceItem(MethodSignature sig) =>
        new CompletionItem(sig.Name, MethCompletion(sig),
            sig.AsSignature,
            Session.Docs.FindBySignature(sig)?.AsMarkup) {
            InsertText = sig.Name, //case is important
            CommitCharacters = MethodCommitters,
            SortText = sig.Name is "ToString" or "GetType" or "GetHashCode" ? "}" + sig.Name : sig.Name
        };
    private CompletionItem AsItem(string? method, MethodSignature sig) =>
        new CompletionItem(method ?? sig.Name, MethCompletion(sig),
            sig.AsSignature,
            Session.Docs.FindBySignature(sig)?.AsMarkup) {
            InsertText = method?.ToLower() ?? sig.Name.ToLower(),
            CommitCharacters = MethodCommitters
        };
    private CompletionItem AsItem(KeyValuePair<string, MethodInfo> kv) => 
        AsItem(kv.Key, MethodSignature.Get(kv.Value));

    private CompletionItem[]? _completionItems = null;
    private CompletionItem[] AllCompletionItems => _completionItems ??= 
        Reflector.ReflectionData.AllMethods().Select(AsItem).ToArray();
}