using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Danmokou.Reflection;
using JetBrains.Annotations;
using JsonRpc.Contracts;
using JsonRpc.Server;
using LanguageServer.VsCode;
using LanguageServer.VsCode.Contracts;
using LanguageServer.VsCode.Server;
using Diagnostic = LanguageServer.VsCode.Contracts.Diagnostic;
using Range = LanguageServer.VsCode.Contracts.Range;

namespace DMKLanguageServer.Services;
[JsonRpcScope(MethodPrefix = "textDocument/")] [PublicAPI]
public class TextDocumentService : DMKLanguageServiceBase {
    [JsonRpcMethod]
    public async Task<Hover> Hover(TextDocumentIdentifier textDocument, Position position, CancellationToken ct) {
        if (Session.Documents.TryGetValue(textDocument.Uri, out var doc) && doc.LastSuccessfulParse is {} ast) {
            var dmkp = position.ToDMKPosition(doc.Document.Content);
            if (ast.NarrowestASTForPosition(new(dmkp, dmkp)) is {} asts)
                return new Hover() { Contents = string.Join("\nin ", asts.Select(a => a.Explain())) };
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
        return new SignatureHelp(new List<SignatureInformation> {
            new SignatureInformation("**Function1**", "Documentation1"),
            new SignatureInformation("**Function2** <strong>test</strong>", "Documentation2"),
        });
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

    private static readonly CompletionItem[] PredefinedCompletionItems = {
        new CompletionItem(".NET", CompletionItemKind.Keyword,
            "Keyword1",
            MarkupContent.Markdown(
                "Short for **.NET Framework**, a software framework by Microsoft (possibly its subsets) or later open source .NET Core."),
            null),
        new CompletionItem(".NET Standard", CompletionItemKind.Keyword,
            "Keyword2",
            "The .NET Standard is a formal specification of .NET APIs that are intended to be available on all .NET runtimes.",
            null),
        new CompletionItem(".NET Framework", CompletionItemKind.Keyword,
            "Keyword3",
            ".NET Framework (pronounced dot net) is a software framework developed by Microsoft that runs primarily on Microsoft Windows.",
            null),
    };

    [JsonRpcMethod]
    public CompletionList Completion(TextDocumentIdentifier textDocument, Position position,
        CompletionContext context) {
        return new CompletionList(PredefinedCompletionItems);
    }
}