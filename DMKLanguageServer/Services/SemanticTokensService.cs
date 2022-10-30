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
[JsonRpcScope(MethodPrefix = "textDocument/semanticTokens/")] [PublicAPI]
public class SemanticTokensService : DMKLanguageServiceBase {
    [JsonRpcMethod]
    public SemanticTokensResponse Full(TextDocumentIdentifier textDocument) {
        if (Session.Documents.TryGetValue(textDocument.Uri, out var doc) && doc.LastSuccessfulParse is { } ast) {
            return new(Diagnostics.GetSemanticTokens(ast));
        }
        return new(Array.Empty<uint>());
    }
}