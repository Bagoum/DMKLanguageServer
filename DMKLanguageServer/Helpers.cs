using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace DMKLanguageServer;

internal static class Helpers {
    public static readonly JsonSerializer CamelCaseJsonSerializer = new JsonSerializer {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    public static string GetTimeStamp() {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
    }

    public static string EscapeTypeMD(this string typ) => typ.Replace("<", "\\<");
}