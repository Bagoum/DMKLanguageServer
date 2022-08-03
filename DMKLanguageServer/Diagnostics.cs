using System;
using System.Collections.Generic;
using System.Text;
using Danmokou.Core;
using Danmokou.Reflection;
using Danmokou.SM;
using Danmokou.SM.Parsing;
using LanguageServer.VsCode.Contracts;
using LanguageServer.VsCode.Server;
using Mizuhashi;
using Diagnostic = LanguageServer.VsCode.Contracts.Diagnostic;
using Range = LanguageServer.VsCode.Contracts.Range;

namespace DMKLanguageServer;

public static class Diagnostics {

    private static readonly string[] Keywords =
        { ".NET Framework", ".NET Core", ".NET Standard", ".NET Compact", ".NET" };

    public static (IAST? ast, IEnumerable<Diagnostic> diagnostics) LintDocument(TextDocument document, int maxNumberOfProblems) {
        var diag = new List<Diagnostic>();
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
        IAST ast;
        try {
            ast = StateMachine.Create(q);
        } catch (Exception exc) {
            //We get the position from the most deeply nested ReflectedException
            // and construct the message in reverse
            ReflectionException? re = null;
            var msgs = new List<string>();
            var e = exc;
            for (; e != null; e = e.InnerException) {
                if (e is ReflectionException refl) {
                    msgs.Add(refl.WithPositionInMessage().Message);
                    re = refl;
                } else
                    msgs.Add(e.Message);
            }
            msgs.Reverse();
            var msg = string.Join("\n", msgs);
            var showLoc = re?.HighlightedPosition ?? re?.Position;
            
            //TODO partial ast construction
            return (null, new[] { new Diagnostic(DiagnosticSeverity.Error, 
                showLoc?.ToRange() ?? new Range(0, 0, 9999, 999), "Typechecking", 
                msg) });
        }
        var warnings = ast.WarnUsage(q.Ctx).Select(d => new Diagnostic(
            d switch {
                ReflectDiagnostic.Warning => DiagnosticSeverity.Warning,
                _ => DiagnosticSeverity.Error
            }, d.Position.ToRange(), "Typechecking", d.Message
        ));
        return (ast, warnings);
    }
}
