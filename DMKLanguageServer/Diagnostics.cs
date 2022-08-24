using System;
using System.Collections.Generic;
using System.Text;
using BagoumLib;
using Danmokou.Core;
using Danmokou.Reflection;
using Danmokou.SM;
using Danmokou.SM.Parsing;
using LanguageServer.Contracts;
using LanguageServer.VsCode.Contracts;
using LanguageServer.VsCode.Server;
using Mizuhashi;
using Diagnostic = LanguageServer.VsCode.Contracts.Diagnostic;
using Position = LanguageServer.VsCode.Contracts.Position;
using Range = LanguageServer.VsCode.Contracts.Range;

namespace DMKLanguageServer;

public static class Diagnostics {
    public static readonly SemanticTokensOptions SemanticTokens = new() {
        Legend = new(SemanticTokenTypes.Values,
            SemanticTokenModifiers.Values),
        Full = new SemanticTokensOptions.OptionsFull()
    };

    public static ((IAST ast, Reflector.ReflCtx ctx)? parse, IEnumerable<Diagnostic> diagnostics) LintDocument(TextDocument document, int maxNumberOfProblems) {
        var content = document.Content;
        /*
        if (string.IsNullOrWhiteSpace(content)) {
            diag.Add(new Diagnostic(DiagnosticSeverity.Hint,
                new Range(new Position(0, 0), document.PositionAt(content?.Length ?? 0)),
                "Lexing/Parsing", "This document is empty."));
            return diag;
        }*/

        var parse = SMParser.ExportSMParserToParsedUnits(content);
        if (parse.IsRight)
            return (null, parse.Right.Select(err => err.ToDiagnostic(content)));
        var q = new PUListParseQueue((parse.Left, parse.Left.ToRange()), null);
        q.Ctx.UseFileLinks = false;
        var ast = StateMachine.Create(q);
        var excs = ast.Exceptions.ToList();
        var errs = excs.Select(err => {
                var innermost = err;
                for (Exception? e = err; e != null; e = e.InnerException)
                    if (e is ReflectionException re)
                        innermost = re;
                return new Diagnostic(DiagnosticSeverity.Error,
                    (innermost.HighlightedPosition ?? innermost.Position).ToRange(),
                    "Typechecking", Exceptions.PrintNestedExceptionInverted(err, false));
            });
        if (q.HasLeftovers(out var qpi)) {
            var loc = q.GetCurrentUnit(out _).Position;
            if (q.Ctx.NonfatalErrorsForPosition(loc).ToList() is { Count:>0 } nfExcs)
                errs = errs.Concat(nfExcs.Select(nfExc => 
                    new Diagnostic(DiagnosticSeverity.Error, loc.ToRange(), "Typechecking",
                        Exceptions.PrintNestedExceptionInverted(nfExc.Item1, false))));
            else
                errs = errs.Append(new Diagnostic(DiagnosticSeverity.Error, loc.ToRange(), "Parsing", 
                    Exceptions.PrintNestedExceptionInverted(q.WrapThrowLeftovers(qpi), false)));
        }
        var warnings = ast.WarnUsage(q.Ctx).Select(d => new Diagnostic(
            d switch {
                ReflectDiagnostic.Warning => DiagnosticSeverity.Warning,
                _ => DiagnosticSeverity.Error
            }, d.Position.ToRange(), "Typechecking", d.Message
        ));
        return ((ast, q.Ctx), errs.Concat(warnings));
    }

    //Most functions here should operate over AST so they can be
    // called with LastSuccessfulParse from callers
    
    public static uint[] GetSemanticTokens(IAST ast) {
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
