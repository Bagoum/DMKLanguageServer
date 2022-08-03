using System;
using System.Collections.Generic;
using System.Text;
using LanguageServer.VsCode.Contracts;
using Mizuhashi;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Position = LanguageServer.VsCode.Contracts.Position;
using Range = LanguageServer.VsCode.Contracts.Range;

namespace DMKLanguageServer;

internal static class Helpers {
    public static readonly JsonSerializer CamelCaseJsonSerializer = new JsonSerializer {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    public static string GetTimeStamp() {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
    }
}