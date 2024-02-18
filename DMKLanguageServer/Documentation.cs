using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BagoumLib;
using BagoumLib.Expressions;
using Danmokou.Reflection;
using LanguageServer.VsCode.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DMKLanguageServer; 

public class Documentation {
    private static readonly Regex xref = new("<xref href=\"([^\"(]+)(\\([^)]*\\))?\"[^>]*></xref>");
    private static readonly MatchEvaluator xrefReplace = m => m.Groups[1].Value;
    private static readonly Regex xmlType = new(@"\{`*([^\}]+)\}");
    private static readonly Regex xmlTypeVar = new(@"`+(\d+)");
    private static readonly MatchEvaluator xmlTypeReplace = m =>
        (m.Value[1] == '`' ? $"<T{m.Groups[1].Value}>" : $"<{m.Groups[1].Value}>");
    private static readonly MatchEvaluator xmlTypeVarReplace = m => 
            $"<{string.Join(",", int.Parse(m.Groups[1].Value).Range().Select(x => $"T{x}"))}>";
    public record Parameter(string Id, string Type, string Description) {
        public Parameter() : this("", "", "") { }
        
        public Parameter Simplify() => 
            this with { Description = xref.Replace(Description.Trim(), xrefReplace) };
    }

    public record Signature(string Content, Parameter[]? Parameters, Parameter? Return) {
        public Signature() : this("", null, null) { }

        public Signature Simplify() => Parameters == null ?
            this :
            this with { Parameters = Parameters.Select(p => p.Simplify()).ToArray() };
    }

    public record Object(string Parent, string[] Children, string Name, string FullName, string Type, string Summary,
        string[] Example, Signature? Syntax) {
        public Object() : this("", Array.Empty<string>(), "", "", "", "", 
            Array.Empty<string>(), null) { }

        public MarkupContent? AsMarkup =>
            Summary is { Length: > 0 } s ? new MarkupContent(MarkupKind.Markdown, s) : null;

        public Object Simplify() =>
            this with { Summary = xref.Replace(Summary.Trim(), xrefReplace), 
                Syntax = Syntax?.Simplify() };
    }

    private record DocfxFile(Object[] Items) {
        public DocfxFile() : this(Array.Empty<Object>()) { }
    }

    private static readonly IDeserializer YmlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly Dictionary<string, Object> MethodsByFullName = new();
    private readonly Dictionary<string, Object> ClassesByFullName = new();
    private readonly HashSet<string> EnumRoots = new();
    private readonly Dictionary<string, Object> EnumsByFullName = new();
    public Documentation(IEnumerable<string> ymlContents, IEnumerable<XDocument> xmlContents) {
        void HandleObject(Object _obj) {
            var obj = _obj.Simplify();
            if (obj.Type == "Method") {
                var splitOn = obj.Name.Contains('<') ? 
                    //check if it's a generic like Method<T>(T arg)
                    // or a constructed argument like Method(List<float> arg)
                    Math.Min(obj.Name.IndexOf('<'), obj.Name.IndexOf('('))
                    : obj.Name.IndexOf('(');
                var splitName = splitOn < 0 ? obj.Name : obj.Name[..splitOn];
                var key = $"{obj.Parent}.{splitName}";
                MethodsByFullName[key] = obj;
            } else if (obj.Type is "Class" or "Struct") {
                var splitOn = obj.Name.Contains('<') ?
                    obj.Name.IndexOf('<') :
                    obj.Name.Length;
                var key = $"{obj.Parent}.{obj.Name[..splitOn]}";
                ClassesByFullName[key] = obj;
            } else if (obj.Type == "Enum")
                EnumRoots.Add(obj.FullName);
            else if (obj.Type == "Field" && EnumRoots.Contains(obj.Parent)) {
                //The enum class, eg. PhaseType, is marked as type Enum,
                // but its components, eg. PhaseType.Nonspell, are marked as type field
                EnumsByFullName[obj.FullName] = obj;
            }
        }
        
        foreach (var yml in ymlContents) {
            var data = YmlDeserializer.Deserialize<DocfxFile>(yml);
            foreach (var _obj in data.Items) {
                HandleObject(_obj);
            }
        }
        foreach (var xml in xmlContents) {
            foreach (var member in xml.Root!.Element("members")!.Elements("member")
                         .Where(x => x.Attribute("name")?.Value[0] is 'M' or 'P' or 'F')) {
                var fullSig = xmlTypeVar.Replace(xmlType.Replace(member.Attribute("name")!.Value[2..], xmlTypeReplace), xmlTypeVarReplace);
                if (fullSig.Contains('#')) continue;
                var parts = fullSig.Split('(', 2);
                var methParts = parts[0].Split('.');
                var methName = parts.Length > 1 ? $"{methParts[^1]}({parts[1]}" : methParts[^1];
                var parent = string.Join('.', methParts[..^1]);
                var summ = member.Element("summary")?.Value.Trim();
                if (string.IsNullOrEmpty(summ)) continue;
                var prms = member.Elements("param").Select(x => (x.Attribute("name")?.Value, ElementToString(x))).ToArray();
                var sig = prms.Length == 0 ?
                    null :
                    new Signature("", prms.Select(p => new Parameter(p.Value ?? "", "", p.Item2)).ToArray(), null);
                HandleObject(new Object(parent, new string[0], methName, parent + "." + methName, "Method", summ,
                    new string[0], sig));
            }
        }
    }
    
    private static string ElementToString(XElement xe) {
        var sb = new StringBuilder();
        ElementToString(xe, sb);
        return sb.ToString();
    }
    private static void ElementToString(XElement xe, StringBuilder sb) {
        if (xe.FirstNode == null) {
            if (xe.Attribute("name") is { } n)
                sb.Append(n.Value);
        }
        for (var n = xe.FirstNode; n != null; n = n.NextNode) {
            if (n is XElement nxe)
                ElementToString(nxe, sb);
            else if (n is XText txt)
                sb.Append(txt.Value);
            else
                throw new NotImplementedException(n.GetType().ToString());
        }
    }

    public static readonly CSharpTypePrinter TypePrinter = new() {
        UseSimpleTypeNames = false,
        PrintTypeNamespace = _ => true
    };

    public Object? FindBySignature(IMethodSignature sig) {
        if (sig is not MethodSignature ms)
            return null;
        if (ms.Member.BaseMi is not ConstructorInfo) {
            return FindBySignature(ms.Member.BaseMi);
        } else {
            var name = TypePrinter.Print(ms.Member.BaseMi.DeclaringType!);
            return ClassesByFullName.TryGetValue(name, out var v) ? v : null;
        }
    }
    public Object? FindBySignature(MemberInfo mi) {
        var name = $"{TypePrinter.Print(mi.DeclaringType!)}.{mi.Name}";
        return MethodsByFullName.TryGetValue(name, out var v) ? v : null;
    }

    public Object? FindEnum(object enumValue) {
        var name = $"{TypePrinter.Print(enumValue.GetType())}.{enumValue.ToString()}";
        return EnumsByFullName.TryGetValue(name, out var v) ? v : null;
    }
}