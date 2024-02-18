using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
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
    public Documentation Docs { get; }

    public LanguageServerSettings Settings { get; set; } = new();
    public ProgramInitResults InitResults { get; }

    public LanguageServerSession(JsonRpcClient rpcClient, IJsonRpcContractResolver contractResolver, string? ymlPath, ProgramInitResults initRes) {
        RpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
        var builder = new JsonRpcProxyBuilder { ContractResolver = contractResolver };
        Client = new ClientProxy(builder, rpcClient);
        Documents = new ConcurrentDictionary<Uri, SessionDocument>();
        (bool, string)? ymlLoadSuccess = null;
        if (!string.IsNullOrWhiteSpace(ymlPath) && Directory.Exists(ymlPath) && Directory.EnumerateFiles(ymlPath).Any(x => x.EndsWith(".yml"))) {
            ymlLoadSuccess = (true, ymlPath);
        } else {
            ymlLoadSuccess = string.IsNullOrWhiteSpace(ymlPath) ? null : (false, ymlPath);
            ymlPath = AppDomain.CurrentDomain.BaseDirectory;
        }
        InitResults = initRes with { CustomYML = ymlLoadSuccess };
        Docs = new Documentation(Directory.GetFiles(ymlPath)
                .Where(f => {
                    var lf = Path.GetFileName(f);
                    return lf.EndsWith(".yml") && lf.StartsWith("Danmokou.");
                })
                .Select(File.ReadAllText),
            Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory)
                .Where(f => Path.GetFileName(f).EndsWith(".xml"))
                .Select(XDocument.Load));
    }

    public void StopServer() {
        cts.Cancel();
    }
}