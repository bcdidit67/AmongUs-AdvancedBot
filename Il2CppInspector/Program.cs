using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;

// 纯元数据读取器：用 Mono.Cecil 直接解析 il2cpp interop 程序集，
// 不需要解析依赖、不需要 il2cpp 运行时。
internal static class Program
{
    private static readonly List<AssemblyDefinition> Asms = new List<AssemblyDefinition>();
    private static TextWriter W = Console.Out;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法:");
            Console.Error.WriteLine("  il2cpp-inspect <dll|目录> find   <正则>    # 按类型全名搜类型");
            Console.Error.WriteLine("  il2cpp-inspect <dll|目录> type   <正则>    # 完整 dump 匹配的类型");
            Console.Error.WriteLine("  il2cpp-inspect <dll|目录> member <正则>    # 搜成员名");
            Console.Error.WriteLine("  il2cpp-inspect <dll|目录> names           # 只列类型名");
            return 1;
        }

        Load(args[0]);
        string cmd = args[1];
        string q = args.Length > 2 ? args[2] : "";
        var re = new Regex(q, RegexOptions.IgnoreCase);

        try
        {
            foreach (var a in Asms)
            {
                if (cmd == "find" || cmd == "type")
                {
                    foreach (var t in AllTypes(a))
                    {
                        if (!re.IsMatch(t.FullName)) continue;
                        if (cmd == "find") W.WriteLine($"{a.MainModule.Name,-28} {t.FullName}");
                        else { DumpType(t); W.WriteLine(); }
                    }
                }
                else if (cmd == "member")
                {
                    foreach (var t in AllTypes(a))
                    {
                        foreach (var m in t.Methods.Where(x => re.IsMatch(x.Name)))
                            W.WriteLine($"{a.MainModule.Name,-26} {t.FullName}::{Sig(m)}");
                        foreach (var f in t.Fields.Where(x => re.IsMatch(x.Name)))
                            W.WriteLine($"{a.MainModule.Name,-26} {t.FullName}::{Fmt(f.FieldType)} {f.Name}");
                        foreach (var p in t.Properties.Where(x => re.IsMatch(x.Name)))
                            W.WriteLine($"{a.MainModule.Name,-26} {t.FullName}::{Fmt(p.PropertyType)} {p.Name}");
                    }
                }
                else if (cmd == "names")
                {
                    foreach (var t in AllTypes(a)) W.WriteLine(t.FullName);
                }
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("查询出错: " + e.Message);
            return 2;
        }

        W.Flush();
        return 0;
    }

    private static void Load(string path)
    {
        var files = new List<string>();
        if (Directory.Exists(path))
            files.AddRange(Directory.GetFiles(path, "*.dll").OrderBy(x => x));
        else
            files.Add(path);

        foreach (var f in files)
        {
            try { Asms.Add(AssemblyDefinition.ReadAssembly(f, new ReaderParameters { ReadSymbols = false })); }
            catch { /* 非托管 dll 或无法解析的跳过 */ }
        }
    }

    private static IEnumerable<TypeDefinition> AllTypes(AssemblyDefinition a)
    {
        foreach (var m in a.Modules)
            foreach (var t in m.Types)
                foreach (var x in Flatten(t))
                    yield return x;
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes)
            foreach (var x in Flatten(n))
                yield return x;
    }

    private static void DumpType(TypeDefinition t)
    {
        var flags = new StringBuilder();
        if (t.IsPublic || t.IsNestedPublic) flags.Append("public ");
        else if (t.IsNotPublic) flags.Append("internal ");
        if (t.IsInterface) flags.Append("interface ");
        else if (t.IsEnum) flags.Append("enum ");
        else if (t.IsValueType) flags.Append("struct ");
        else if (t.IsAbstract && t.IsSealed) flags.Append("static class ");
        else flags.Append(t.IsAbstract ? "abstract class " : (t.IsSealed ? "sealed class " : "class "));

        W.WriteLine($"=== {flags}{t.FullName} ===");
        if (t.BaseType != null) W.WriteLine($"  基类: {Fmt(t.BaseType)}");
        var ifaces = t.Interfaces.Select(i => Fmt(i.InterfaceType)).ToList();
        if (ifaces.Count > 0) W.WriteLine($"  接口: {string.Join(", ", ifaces)}");

        var attrs = Attrs(t);
        if (attrs.Count > 0) W.WriteLine($"  特性: {string.Join(", ", attrs)}");

        var fields = t.Fields.Where(f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly).ToList();
        if (fields.Count > 0)
        {
            W.WriteLine("  -- 字段 --");
            foreach (var f in fields)
                W.WriteLine($"    {(f.IsStatic ? "static " : "")}{(f.IsInitOnly ? "readonly " : "")}{Fmt(f.FieldType)} {f.Name}");
        }

        var props = t.Properties.ToList();
        if (props.Count > 0)
        {
            W.WriteLine("  -- 属性 --");
            foreach (var p in props)
            {
                var acc = new List<string>();
                if (p.GetMethod != null) acc.Add((p.GetMethod.IsStatic ? "static " : "") + "get");
                if (p.SetMethod != null) acc.Add((p.SetMethod.IsStatic ? "static " : "") + "set");
                W.WriteLine($"    {Fmt(p.PropertyType)} {p.Name} {{ {string.Join("; ", acc)} }}");
            }
        }

        var methods = t.Methods.Where(m => (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly) && !m.IsGetter && !m.IsSetter).ToList();
        if (methods.Count > 0)
        {
            W.WriteLine("  -- 方法 --");
            foreach (var m in methods)
                W.WriteLine($"    {Sig(m)}");
        }

        var nested = t.NestedTypes.ToList();
        if (nested.Count > 0)
            W.WriteLine($"  -- 嵌套类型: {string.Join(", ", nested.Select(n => n.Name))}");
    }

    private static List<string> Attrs(ICustomAttributeProvider p)
    {
        try
        {
            return p.CustomAttributes.Select(a =>
            {
                var n = a.AttributeType.Name;
                if (a.ConstructorArguments.Count == 0) return n;
                var args = a.ConstructorArguments.Select(c => Lit(c.Value)).ToList();
                return $"{n}({string.Join(", ", args)})";
            }).ToList();
        }
        catch { return new List<string>(); }
    }

    private static string Lit(object v)
    {
        if (v == null) return "null";
        if (v is string s) return "\"" + s + "\"";
        return v.ToString();
    }

    private static string Sig(MethodDefinition m)
    {
        var sb = new StringBuilder();
        if (m.IsPublic) sb.Append("public ");
        else if (m.IsFamily) sb.Append("protected ");
        else if (m.IsFamilyOrAssembly) sb.Append("protected internal ");
        if (m.IsStatic) sb.Append("static ");
        if (m.IsVirtual && !m.IsFinal && !m.IsNewSlot) sb.Append("override ");
        else if (m.IsVirtual && m.IsNewSlot) sb.Append("virtual ");
        sb.Append(Fmt(m.ReturnType)).Append(' ').Append(m.Name);
        if (m.HasGenericParameters) sb.Append('<').Append(string.Join(", ", m.GenericParameters.Select(g => g.Name))).Append('>');
        sb.Append('(').Append(string.Join(", ", m.Parameters.Select(p => Fmt(p.ParameterType) + " " + p.Name))).Append(')');
        return sb.ToString();
    }

    private static string Fmt(TypeReference t)
    {
        if (t == null) return "?";
        if (t is GenericParameter gp) return gp.Name;
        if (t is ArrayType at) return Fmt(at.ElementType) + "[]";
        if (t is ByReferenceType br) return "ref " + Fmt(br.ElementType);
        if (t is PointerType pt) return Fmt(pt.ElementType) + "*";
        if (t is GenericInstanceType git)
        {
            var baseName = git.ElementType.Name;
            var tick = baseName.IndexOf('`');
            if (tick > 0) baseName = baseName.Substring(0, tick);
            var ns = git.ElementType.Namespace;
            var prefix = string.IsNullOrEmpty(ns) ? "" : ShortNs(ns) + ".";
            return prefix + baseName + "<" + string.Join(", ", git.GenericArguments.Select(Fmt)) + ">";
        }
        var js = t.Namespace;
        var pre = string.IsNullOrEmpty(js) ? "" : ShortNs(js) + ".";
        var name = t.Name;
        var tk = name.IndexOf('`');
        if (tk > 0) name = name.Substring(0, tk);
        return pre + name;
    }

    private static string ShortNs(string ns)
    {
        switch (ns)
        {
            case "UnityEngine": return "UE";
            case "UnityEngine.UI": return "UE.UI";
            case "System.Collections.Generic": return "SCG";
            case "System": return "Sys";
            default: return ns;
        }
    }
}
