using System.CommandLine;
using System.Reflection;
using BagoumLib;
using BagoumLib.Events;
using Danmokou.Core;
using Danmokou.Reflection;
using Danmokou.Reflection2;
using Danmokou.SM.Parsing;
using JsonRpc.Client;
using JsonRpc.Contracts;
using JsonRpc.Server;
using JsonRpc.Streams;
using LanguageServer.VsCode;
using LanguageServer.VsCode.Contracts;
using Microsoft.Extensions.Logging;

namespace DMKLanguageServer;

public static class Program {
    private static void ttt(ReadOnlySpan<char> s) {
        
    }
    public static void Main(string[] args) {
        var debug = new Option<bool>("--debug", description: "Set the server to debug mode");
        var dllPath = new Option<string?>("--dllpath", () => null, description: "Specify the path for built DMK DLLs");
        var ymlPath = new Option<string?>("--ymlpath", () => null,
            description: "Specify the path for DMK documentation YAMLs");
        var inlayHints = new Option<bool>("--inlayHints", description: "Enable inlay hints");
        
        var cmd = new RootCommand("Start DMK language server") { debug, dllPath, ymlPath, inlayHints };
        cmd.SetHandler(LoadDLLs, debug, dllPath, ymlPath, inlayHints);
        cmd.Invoke(args);
        Console.WriteLine("End");
    }

    private static void LoadDLLs(bool debugMode, string? dllPath, string? ymlPath, bool inlayHints) {
        if (debugMode) {
            //while (!Debugger.IsAttached) Thread.Sleep(100);
            //Debugger.Break();
        }
        //The strategy for loading the DMK dlls based on cmdline arguments is as follows:
        //1. In post-build/post-publish, we move the default DMK dlls into a nested directory,
        //    so they will not be found by the default DLL resolution. (See the csproj)
        //  - This is required because we cannot add handling to DLL resolution until the default handling fails,
        //     so if the runtime can find the default DLLs, we cannot use custom DLLs without dual-loading.
        //2. We add handling to AssemblyResolve to lookup the DLLs either in a custom directory
        //    or in the default nested directory. (The code immediately below performs verification
        //    on the custom directory)
        //3. We also immediately load all the DMK DLLs so they are captured by Reflector.

        (bool, string)? loadSuccess = null;
        if (!string.IsNullOrWhiteSpace(dllPath) && Directory.Exists(dllPath) &&
            Directory.EnumerateFiles(dllPath).Select(Path.GetFileName).ToArray() is { } provided &&
            new[] { "Danmokou.Core.dll", "Danmokou.Danmaku.dll" }.All(provided.Contains)) {
            loadSuccess = (true, dllPath);
        } else {
            loadSuccess = string.IsNullOrWhiteSpace(dllPath) ? null : (false, dllPath);
            //This is where the dlls are stored by the post-build/publish scripts
            dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configDLL");
        }
        
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
            var asmPath = Path.Combine(dllPath, new AssemblyName(args.Name).Name + ".dll");
            if (File.Exists(asmPath))
                return Assembly.LoadFrom(asmPath);
            return null;
        };
        
        foreach (var asm in Directory.GetFiles(dllPath, "*.dll")
                     .Where(x => {
                         var f = Path.GetFileName(x);
                         return f.StartsWith("Danmokou") 
                                //These will get double-loaded if you don't except them
                                && f != "Danmokou.Core.dll" 
                                && f != "Danmokou.Danmaku.dll";
                     })) {
            Assembly.LoadFile(asm);
        }
        //Since assembly loading is done at the beginning of a function (I think),
        // we can't have references to DMK stuff until assembly modifications are done
        StartServer(debugMode, ymlPath, new(loadSuccess) { InlayHints = inlayHints });
    }
    
    private static void StartServer(bool debugMode, string? ymlPath, ProgramInitResults initRes) {
        Reflector.SOFT_FAIL_ON_UNMATCHED_LSTRING = true;
        ServiceLocator.Register<IDMKLocaleProvider>(new Locale.DMKLocale());
        
        StreamWriter? logWriter = null;
        if (debugMode) {
            logWriter = File.CreateText("messages-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".log");
            logWriter.AutoFlush = true;
        }
        using (logWriter)
        using (var cin = Console.OpenStandardInput())
        using (var bcin = new BufferedStream(cin))
        using (var cout = Console.OpenStandardOutput())
        using (var reader = new PartwiseStreamMessageReader(bcin))
        using (var writer = new PartwiseStreamMessageWriter(cout)) {
            var contractResolver = new JsonRpcContractResolver {
                NamingStrategy = new CamelCaseJsonRpcNamingStrategy(),
                ParameterValueConverter = new LanguageServiceParameterValueConverter(),
            };
            var clientHandler = new StreamRpcClientHandler();
            var client = new JsonRpcClient(clientHandler);
            if (debugMode) {
                // We want to capture log all the LSP server-to-client calls as well
                clientHandler.MessageSending += (_, e) => {
                    lock (logWriter!) logWriter.WriteLine("{0} <C{1}", Helpers.GetTimeStamp(), e.Message);
                };
                clientHandler.MessageReceiving += (_, e) => {
                    lock (logWriter!) logWriter.WriteLine("{0} >C{1}", Helpers.GetTimeStamp(), e.Message);
                };
            }
            // Configure & build service host
            var session = new LanguageServerSession(client, contractResolver, ymlPath, initRes);
            var host = BuildServiceHost(logWriter, contractResolver, debugMode);
            var serverHandler = new StreamRpcServerHandler(host,
                StreamRpcServerHandlerOptions.ConsistentResponseSequence |
                StreamRpcServerHandlerOptions.SupportsRequestCancellation);
            serverHandler.DefaultFeatures.Set(session);
            // If we want server to stop, just stop the "source"
            using (serverHandler.Attach(reader, writer))
            using (clientHandler.Attach(reader, writer)) {
                // Wait for the "stop" request.
                session.CancellationToken.WaitHandle.WaitOne();
            }
            logWriter?.WriteLine("Exited");
        }
    }

    private static IJsonRpcServiceHost BuildServiceHost(TextWriter? logWriter,
        IJsonRpcContractResolver contractResolver, bool debugMode) {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
        if (debugMode) {
            loggerFactory.AddFile("logs-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".log");
        }
        var builder = new JsonRpcServiceHostBuilder {
            ContractResolver = contractResolver,
            LoggerFactory = loggerFactory
        };
        builder.UseCancellationHandling();
        builder.Register(typeof(Program).GetTypeInfo().Assembly);
        if (debugMode) {
            // Log all the client-to-server calls.
            builder.Intercept(async (context, next) => {
                lock (logWriter!) logWriter.WriteLine("{0} > {1}", Helpers.GetTimeStamp(), context.Request);
                await next();
                lock (logWriter) logWriter.WriteLine("{0} < {1}", Helpers.GetTimeStamp(), context.Response);
            });
        }
        return builder.Build();
    }
}

public record ProgramInitResults((bool success, string path)? CustomDLL,
    (bool success, string path)? CustomYML = null) {
    public bool InlayHints { get; init; } = false;
}
public static class Locale {
    public class DMKLocale : IDMKLocaleProvider {
        public Evented<string?> TextLocale { get; } = new(null);
        public Evented<string?> VoiceLocale { get; } = new(null);
    }
}