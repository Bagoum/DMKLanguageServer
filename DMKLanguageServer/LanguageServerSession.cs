using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BagoumLib.Events;
using Danmokou.Reflection;
using JsonRpc.DynamicProxy.Client;
using JsonRpc.Client;
using JsonRpc.Contracts;
using JsonRpc.Server;
using LanguageServer.VsCode.Contracts;
using LanguageServer.VsCode.Contracts.Client;
using LanguageServer.VsCode.Server;

namespace DMKLanguageServer;

public class LanguageServerSession {
    private readonly CancellationTokenSource cts = new();
    public CancellationToken CancellationToken => cts.Token;
    public JsonRpcClient RpcClient { get; }
    public ClientProxy Client { get; }
    public ConcurrentDictionary<Uri, SessionDocument> Documents { get; }

    public LanguageServerSettings Settings { get; set; } = new();

    public LanguageServerSession(JsonRpcClient rpcClient, IJsonRpcContractResolver contractResolver) {
        RpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
        var builder = new JsonRpcProxyBuilder { ContractResolver = contractResolver };
        Client = new ClientProxy(builder, rpcClient);
        Documents = new ConcurrentDictionary<Uri, SessionDocument>();
    }

    public void StopServer() {
        cts.Cancel();
    }
}