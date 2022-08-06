using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using JetBrains.Annotations;
using JsonRpc.Contracts;
using LanguageServer.VsCode.Contracts;
using Newtonsoft.Json.Linq;

namespace DMKLanguageServer.Services {
[JsonRpcScope(MethodPrefix = "workspace/")] [PublicAPI]
public class WorkspaceService : DMKLanguageServiceBase {
    [JsonRpcMethod(IsNotification = true)]
    public async Task DidChangeConfiguration(SettingsRoot settings) {
        Session.Settings = settings.DMKLanguageServer;
        foreach (var doc in Session.Documents.Values) {
            await Client.Document.PublishDiagnostics(doc.Document.Uri, doc.Lint());
        }
    }

    [JsonRpcMethod(IsNotification = true)]
    public async Task DidChangeWatchedFiles(ICollection<FileEvent> changes) {
        foreach (var change in changes) {
            if (!change.Uri.IsFile) continue;
            var localPath = change.Uri.AbsolutePath;
            if (string.Equals(Path.GetExtension(localPath), ".demo")) {
                // If the file has been removed, we will clear the lint result about it.
                // Note that pass null to PublishDiagnostics may mess up the client.
                if (change.Type == FileChangeType.Deleted) {
                    await Client.Document.PublishDiagnostics(change.Uri, Array.Empty<Diagnostic>());
                }
            }
        }
    }
}
}