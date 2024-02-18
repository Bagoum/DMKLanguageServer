using JetBrains.Annotations;
using JsonRpc.Contracts;
using LanguageServer.Contracts;
using LanguageServer.VsCode.Contracts;

namespace DMKLanguageServer.Services;
[JsonRpcScope(MethodPrefix = "textDocument/semanticTokens/")] [PublicAPI]
public class SemanticTokensService : DMKLanguageServiceBase {
    [JsonRpcMethod]
    public SemanticTokensResponse Full(TextDocumentIdentifier textDocument) {
        if (Session.Documents.TryGetValue(textDocument.Uri, out var doc) && doc.LastBDSL2ParseOrLastSuccessfulBDSL1Parse is { } ast) {
            return new(Diagnostics.GetSemanticTokens(ast));
        }
        return new(Array.Empty<uint>());
    }
}