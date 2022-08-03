using System;
using System.Threading.Tasks;
using Danmokou.Core;
using JsonRpc;
using JsonRpc.Contracts;
using JsonRpc.Messages;
using JsonRpc.Server;
using LanguageServer.VsCode.Contracts;
using Newtonsoft.Json.Linq;

namespace DMKLanguageServer.Services;
public class InitializationService : DMKLanguageServiceBase {
    [JsonRpcMethod(AllowExtensionData = true)]
    public InitializeResult Initialize(int processId, Uri rootUri, ClientCapabilities capabilities,
        JToken? initializationOptions = null, string? trace = null) {
        //Register new capabilities here
        return new InitializeResult(new ServerCapabilities {
            HoverProvider = new HoverOptions(),
            SignatureHelpProvider = new SignatureHelpOptions("()"),
            CompletionProvider = new CompletionOptions(true, "."),
            TextDocumentSync = new TextDocumentSyncOptions {
                OpenClose = true,
                WillSave = true,
                Change = TextDocumentSyncKind.Incremental
            },
            DocumentSymbolProvider = new DocumentSymbolOptions()
        });
    }

    [JsonRpcMethod(IsNotification = true)]
    public async Task Initialized() {
        await Client.Window.ShowMessage(MessageType.Info,
            $"Hello from language server {Environment.CurrentDirectory}. Params: {Environment.CommandLine}");
        var choice = await Client.Window.ShowMessage(MessageType.Warning, "Wanna drink?", "Yes", "No");
        await Client.Window.ShowMessage(MessageType.Info, $"You chose {choice?.ToString() ?? "Nothing"}.");
    }

    [JsonRpcMethod]
    public void Shutdown() { }

    [JsonRpcMethod(IsNotification = true)]
    public void Exit() {
        Session.StopServer();
    }

    [JsonRpcMethod("$/cancelRequest", IsNotification = true)]
    public void CancelRequest(MessageId id) {
        RequestContext.Features.Get<IRequestCancellationFeature>().TryCancel(id);
    }
}