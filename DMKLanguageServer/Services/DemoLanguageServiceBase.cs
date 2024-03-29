﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using JetBrains.Annotations;
using JsonRpc.Server;
using LanguageServer.VsCode.Contracts;
using LanguageServer.VsCode.Contracts.Client;
using LanguageServer.VsCode.Server;

namespace DMKLanguageServer.Services;

public class DMKLanguageServiceBase : JsonRpcService {
    protected LanguageServerSession Session => RequestContext.Features.Get<LanguageServerSession>();

    protected ClientProxy Client => Session.Client;

    protected TextDocument GetDocument(Uri uri) {
        if (Session.Documents.TryGetValue(uri, out var sd))
            return sd.Document;
        return null;
    }

    protected TextDocument GetDocument(TextDocumentIdentifier id) => GetDocument(id.Uri);
}
