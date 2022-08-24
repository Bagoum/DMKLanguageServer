using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using DMKLanguageServer;
using JetBrains.Annotations;
using JsonRpc.Contracts;
using LanguageServer.VsCode.Contracts;

namespace DMKLanguageServer.Services {
[JsonRpcScope(MethodPrefix = "completionItem/")]
[PublicAPI]
public class CompletionItemService : DMKLanguageServiceBase {
    // The request is sent from the client to the server to resolve additional information
    // for a given completion item.
    [JsonRpcMethod(AllowExtensionData = true)]
    public CompletionItem Resolve() {
        var item = RequestContext.Request.Parameters.ToObject<CompletionItem>(Helpers.CamelCaseJsonSerializer);
        return item;
    }
}
}