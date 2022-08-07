using System.Collections.Immutable;
using BagoumLib.Expressions;
using Danmokou.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DMKLanguageServer; 

public class Documentation {
    public record Parameter(string Id, string Type, string Description) {
        public Parameter() : this("", "", "") { }
    }

    public record Signature(string Content, Parameter[]? Parameters, Parameter? Return) {
        public Signature() : this("", null, null) { }
    }

    public record Object(string Uid, string CommentId, string Id, string Parent, string[] Children, string[] Langs,
        string Name, string NameWithType, string FullName, string Type, string Namespace, string Summary,
        string[] Example, Signature? Syntax, string Overload) {
        public Object() : this("", "", "", "", Array.Empty<string>(), Array.Empty<string>(), "", "", "", "", "", "",
            Array.Empty<string>(), null, "") { }
    }

    private record DocfxFile(Object[] Items) {
        public DocfxFile() : this(Array.Empty<Object>()) { }
    }

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly Dictionary<string, Object> MethodsByFullName = new();
    private readonly Dictionary<string, Object> ClassesByFullName = new();
    public Documentation(string[] ymlContents) {
        foreach (var yml in ymlContents) {
            var data = Deserializer.Deserialize<DocfxFile>(yml);
            foreach (var obj in data.Items) {
                if (obj.Type == "Method") {
                    var splitOn = obj.Name.Contains('<') ? 
                        //check if it's a generic like Method<T>(T arg)
                        // or a constructed argument like Method(List<float> arg)
                        Math.Min(obj.Name.IndexOf('<'), obj.Name.IndexOf('('))
                        : obj.Name.IndexOf('(');
                    var key = $"{obj.Parent}.{obj.Name[..splitOn]}";
                    MethodsByFullName[key] = obj;
                } else if (obj.Type is "Class" or "Struct") {
                    var splitOn = obj.Name.Contains('<') ?
                        obj.Name.IndexOf('<') :
                        obj.Name.Length;
                    var key = $"{obj.Parent}.{obj.Name[..splitOn]}";
                    ClassesByFullName[key] = obj;
                }
            }
        }
    }

    private static readonly CSharpTypePrinter TypePrinter = new() {
        PrintTypeNamespace = _ => true
    };

    public Object? FindBySignature(Reflector.MethodSignature mi) {
        if (mi.isCtor) {
            var name = TypePrinter.Print(mi.Mi.DeclaringType!);
            return ClassesByFullName.TryGetValue(name, out var v) ? v : null;
        } else {
            var name = $"{TypePrinter.Print(mi.Mi.DeclaringType!)}.{mi.Mi.Name}";
            return MethodsByFullName.TryGetValue(name, out var v) ? v : null;
        }
    }
}