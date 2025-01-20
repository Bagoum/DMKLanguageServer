using System.Text;
using BagoumLib;
using BagoumLib.Functional;
using Danmokou.Core;
using Danmokou.Reflection;
using Danmokou.SM;
using Danmokou.SM.Parsing;
using LanguageServer.VsCode.Contracts;
using LanguageServer.VsCode.Server;
using Mizuhashi;
using Scriptor;
using Scriptor.Analysis;
using Scriptor.Compile;
using Scriptor.Definition;
using Scriptor.Expressions;
using Scriptor.Reflection;
using Diagnostic = LanguageServer.VsCode.Contracts.Diagnostic;
using Range = LanguageServer.VsCode.Contracts.Range;
using IAST = Danmokou.Reflection.IAST;

namespace DMKLanguageServer;

public static class Diagnostics {
    public static readonly SemanticTokensOptions SemanticTokens = new() {
        Legend = new(SemanticTokenTypes.Values,
            SemanticTokenModifiers.Values),
        Full = new SemanticTokensOptions.OptionsFull()
    };

    private static Diagnostic ToDiagnostic(Exception topErr, string purpose) {
        ReflectionException? innermost = null;
        for (Exception? e = topErr; e != null; e = e.InnerException)
            if (e is ReflectionException re)
                innermost = re;
        return new Diagnostic(DiagnosticSeverity.Error,
            (innermost?.HighlightedPosition ?? innermost?.Position ?? 
                new(new(1, 1, 0), new(2, 1, 0))).ToRange(),
            purpose, Exceptions.PrintNestedExceptionInverted(topErr, false));
    }

    private static Diagnostic ToDiagnostic(ReflectDiagnostic d) => new Diagnostic(
            d switch {
                ReflectDiagnostic.Warning => DiagnosticSeverity.Warning,
                _ => DiagnosticSeverity.Error
            }, d.Position.ToRange(), "Typechecking", d.Message
        );

    private const string argAnnotation = "vscode:";
    private static (Scriptor.Compile.IAST? parse, bool success, IEnumerable<Diagnostic> diagnostics) ParseBDSL2(ref string content) {
        ST.Block pblock = null!;
        LexerMetadata metadata;
        try {
            pblock = CompileHelpers.Parse(ref content, out metadata);
        } catch (Exception e) {
            return (null, false, new[] { ToDiagnostic(e, "Lexing/Parsing") });
        }
        List<ReflectionException> errs = new();
        var rs = LexicalScope.NewTopLevelScope();
        var args = metadata.Comments.Where(x => x.text.StartsWith(argAnnotation))
            .SelectNotNull(x => {
                var split = x.text[argAnnotation.Length..].Split("::");
                var typ = LangParser.TypeFromString(split[1].Trim());
                if (typ.IsRight) {
                    errs.Add(new ReflectionException(x.pos, typ.Right));
                    return null;
                }
                return new DelegateArg(split[0].Trim(), typ.Left) as IDelegateArg;
            }).ToArray();
        
        var ast = pblock.AnnotateWithParameters(new(rs), args).LeftOrRight<Scriptor.Compile.AST.Block, Scriptor.Compile.AST.Failure, Scriptor.Compile.IAST>();
        rs.SetDocComments(metadata);
        var typ = Scriptor.Compile.IAST.Typecheck(ast, rs.GlobalRoot.Resolver, rs);
        errs.AddRange(ast.FirstPassExceptions());
        //typecheck before returning initial errors so we can possibly get Failure typechecks
        if (errs.Count > 0)
            return (ast, false, errs.Select(e => ToDiagnostic(e, "Parsing")));
        if (typ.IsRight)
            try {
                return (ast, false, new[] { ToDiagnostic(Scriptor.Compile.IAST.EnrichError(typ.Right), "Typechecking") });
            } catch (Exception e) {
                return (ast, false, new[] { ToDiagnostic(e, "Typechecking") });
            }
        errs.AddRange(ast.Verify());
        return (ast, !errs.Any(), ast.WarnUsage().Select(ToDiagnostic).Concat(
                    errs.Select(e => ToDiagnostic(e, "Verification"))));
    }

    private static readonly Dictionary<string, (string content, Either<EnvFrame,ReflectionException> result)> 
        mostRecentImported = new();
    private static Either<EnvFrame,ReflectionException> LoadImport(ST.Import imp, string fileFullName, string content) {
        if (mostRecentImported.TryGetValue(fileFullName, out var lastParse) && lastParse.content == content)
            return lastParse.result;
        var result = _RunLoadImport(imp, fileFullName, content);
        mostRecentImported[fileFullName] = (content, result);
        return result;
    }

    private static readonly Stack<string> importStack = new();
    private static Either<EnvFrame,ReflectionException> _RunLoadImport(ST.Import imp, string fileFullName, string content) {
        var shortname = Path.GetFileName(fileFullName);
        if (importStack.Contains(fileFullName)) {
            var sb = new StringBuilder();
            sb.Append(
                $"There is a circular import for `{shortname}`. Circular imports are not permitted. The import stack is as follows:\n{shortname}");
            foreach (var x in importStack) {
                sb.Append($"\nis imported by {Path.GetFileName(x)}");
                if (x == fileFullName) break;
            }
            return new ReflectionException(imp.KwPos, sb.ToString());
        }
        importStack.Push(fileFullName);
        try {
            return CompileHelpers.ParseAndCompileErased(content);
        } catch (Exception e) {
            if (e is ReflectionException re && e.Message.Contains("There is a circular import"))
                return re;
            return new ReflectionException(imp.KwPos, $"Failed to import the file `{shortname}`." +
                                                      $"\nThe below errors are from the imported file, not the current file:\n\n" +
                                                      e.Message);
        } finally {
            importStack.Pop();
        }
    }
    
    public static (Either<(IAST ast, Reflector.ReflCtx ctx), Scriptor.Compile.IAST>? parse, bool success, IEnumerable<Diagnostic> diagnostics) LintDocument(
        TextDocument document, int maxNumberOfProblems, out string content) {
        content = document.Content;
        if (!content.TrimStart().StartsWith("<#>")) {
            ServiceLocator.Find<ILangCustomizer>().Import = imp => {
                if (!imp.Location.Try(out var loc))
                    return new ReflectionException(imp.Position,
                        "For VSCode to locate an import, you must declare the relative filepath of the import. Eg: `import myRefFile at \"./bdsl2 reference file.bdsl\" as mrf");
                var relPath = loc.Filename.Content;
                var file = new FileInfo(
                    Path.GetDirectoryName(document.Uri.GetComponents(UriComponents.Path, UriFormat.Unescaped)) +
                    relPath);
                if (!file.Exists)
                    return new ReflectionException(imp.Position, $"Could not find the file `{file.FullName}`");
                return LoadImport(imp, file.FullName, File.ReadAllText(file.FullName));
            };
            var (ast2, succ, diags) = ParseBDSL2(ref content);
            return (ast2 is null ? null : new(ast2), succ, diags);
        } else {
            var parse = SMParser.ExportSMParserToParsedUnits(content, out var stream);
            if (parse.IsRight)
                return (null, false, parse.Right.Select(err => err.ToDiagnostic(stream)));
            PUListParseQueue q;
            try {
                q = new PUListParseQueue((parse.Left, parse.Left.ToRange()), null);
            } catch (ReflectionException e) {
                return (null, false, new[] {
                    new Diagnostic(DiagnosticSeverity.Error,
                        (e.HighlightedPosition ?? e.Position).ToRange(), "Parsing",
                        Exceptions.PrintNestedExceptionInverted(e, false))
                });
            }
            q.Ctx.UseFileLinks = false;
            var ast = StateMachine.Create(q);
            var excs = ast.Exceptions.ToList();
            var errs = excs.Select(err => ToDiagnostic(err, "Typechecking")).ToList();
            if (q.HasLeftovers(out var qpi)) {
                var loc = q.GetCurrentUnit(out _).Position;
                if (q.Ctx.NonfatalErrorsForPosition(loc).ToList() is { Count: > 0 } nfExcs)
                    errs = errs.Concat(nfExcs.Select(nfExc =>
                        new Diagnostic(DiagnosticSeverity.Error, loc.ToRange(), "Typechecking",
                            Exceptions.PrintNestedExceptionInverted(nfExc.Item1, false)))).ToList();
                else
                    errs = errs.Append(new Diagnostic(DiagnosticSeverity.Error, loc.ToRange(), "Parsing",
                        Exceptions.PrintNestedExceptionInverted(q.WrapThrowLeftovers(qpi), false))).ToList();
            }
            var warnings = ast.WarnUsage(q.Ctx).Select(ToDiagnostic);
            return ((ast, q.Ctx), !errs.Any(), errs.Concat(warnings));
        }
    }

    //Most functions here should operate over AST so they can be
    // called with LastSuccessfulParse from callers

    public static uint[] GetSemanticTokens(IDebugAST ast) {
        var prevLoc = new Range(0, 0, 0, 0);
        var output = new List<uint>();
        //It's possible for tokens to arrive out-of-order due to 
        // exceptional structures like phase arguments being out-of-order
        foreach (var token in ast.ToSemanticTokens().OrderBy(t => t.Position.Start.Index)) {
            if (token.Position.Empty) continue;
            var nloc = token.Position.ToRange();
            //Note that orderBy is stable, so this should always preserve the "topmost" token
            // when a macro or other overlapping structure is used
            if (nloc.Start.Line < prevLoc.End.Line ||
                nloc.Start.Line == prevLoc.End.Line && nloc.Start.Character < prevLoc.End.Character)
                continue;
            if (nloc.Start.Line == prevLoc.Start.Line) {
                output.Add(0);
                output.Add((uint)(nloc.Start.Character - prevLoc.Start.Character));
            } else {
                output.Add((uint)(nloc.Start.Line - prevLoc.Start.Line));
                output.Add((uint)nloc.Start.Character);
            }
            output.Add((uint)(nloc.End.Character - nloc.Start.Character));
            output.Add(SemanticTokens.Legend.EncodeType(token.TokenType));
            output.Add(SemanticTokens.Legend.EncodeMods(token.TokenMods));
            prevLoc = nloc;
        }
        return output.ToArray();
    }
}
