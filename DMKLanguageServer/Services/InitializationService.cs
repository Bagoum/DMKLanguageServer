﻿using JsonRpc.Contracts;
using JsonRpc.Messages;
using JsonRpc.Server;
using LanguageServer.VsCode.Contracts;
using Newtonsoft.Json.Linq;

namespace DMKLanguageServer.Services;
public class InitializationService : DMKLanguageServiceBase {
    [JsonRpcMethod(AllowExtensionData = true)]
    public InitializeResult Initialize(int processId, Uri rootUri, ClientCapabilities capabilities,
        JToken? initializationOptions = null, string? trace = null) {
        //When adding a new capability, add its provider information here
        // This will tell the client that it can safely make requests
        //You do not need to handle the client capability information
        // unless you need it to set settings
        //You also do not need to manually register any new Service classes,
        // as that is done automatically in Program.cs by
        //  `builder.Register(typeof(Program).GetTypeInfo().Assembly);`
        //However, for new features, you may need to update the client package
        // (eg. vscode-languageclient for VSCode/Node) for the client
        // to send the requests
        return new InitializeResult(new ServerCapabilities {
            HoverProvider = new HoverOptions(),
            SignatureHelpProvider = new SignatureHelpOptions("()"),
            CompletionProvider = new CompletionOptions(true, "."),
            TextDocumentSync = new TextDocumentSyncOptions {
                OpenClose = true,
                WillSave = true,
                Change = TextDocumentSyncKind.Incremental
            },
            DocumentSymbolProvider = new DocumentSymbolOptions(),
            SemanticTokensProvider = Diagnostics.SemanticTokens,
            InlayHintProvider = Session.InitResults.InlayHints ?
                new InlayHintOptions() { ResolveProvider = false } :
                null
        });
    }

    [JsonRpcMethod(IsNotification = true)]
    public async Task Initialized() {
        await Client.Window.ShowMessage(MessageType.Info,
            $"The DMK scripting language server is now running.");
        if (Session.InitResults.CustomDLL is {} dll)
            await Client.Window.ShowMessage(MessageType.Info, dll.success ?
                $"Using custom DLLs from {dll.path}" :
                $"Failed to load custom DLLs from {dll.path}");
        if (Session.InitResults.CustomYML is {} yml)
            await Client.Window.ShowMessage(MessageType.Info, yml.success ?
                $"Using custom documentation from {yml.path}" :
                $"Failed to load custom documentation from {yml.path}");
        /*{Environment.CurrentDirectory}.
        var choice = await Client.Window.ShowMessage(MessageType.Warning, "Wanna drink?", "Yes", "No");
        await Client.Window.ShowMessage(MessageType.Info, $"You chose {choice?.ToString() ?? "Nothing"}.");*/
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