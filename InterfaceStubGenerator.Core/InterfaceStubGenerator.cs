﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Nustache.Core;

namespace Refit.Generator
{
    // * Search for all Interfaces, find the method definitions 
    //   and make sure there's at least one Refit attribute on one
    // * Generate the data we need for the template based on interface method
    //   defn's
    // * Get this into an EXE in tools, write a targets file to beforeBuild execute it
    // * Get a props file that adds a dummy file to the project
    // * Write an implementation of RestService that just takes the interface name to
    //   guess the class name based on our template
    //
    // What if the Interface is in another module? (since we copy usings, should be fine)
    public class InterfaceStubGenerator
    {
        static readonly HashSet<string> HttpMethodAttributeNames = new HashSet<string>(
            new[] { "Get", "Head", "Post", "Put", "Delete", "Patch", "Options" }
                .SelectMany(x => new[] { "{0}", "{0}Attribute" }.Select(f => string.Format(f, x))));

        public InterfaceStubGenerator() : this(null, null) { }

        public InterfaceStubGenerator(Action<string> logWarning) : this(null, logWarning) { }

        public InterfaceStubGenerator(string refitInternalNamespace) : this(refitInternalNamespace, null) { }

        public InterfaceStubGenerator(string refitInternalNamespace, Action<string> logWarning)
        {
            Log = logWarning;

            if (!string.IsNullOrWhiteSpace(refitInternalNamespace))
            {
                RefitInternalNamespace = $"{refitInternalNamespace.Trim().TrimEnd('.')}.";
            }
        }

        public string RefitInternalNamespace { get; }

        public Action<string> Log { get; }

        public static string ExtractTemplateSource()
        {
            var ourPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "GeneratedInterfaceStubTemplate.mustache");

            // Try to return a flat file from the same directory, if it doesn't
            // exist, use the built-in resource version
            if (File.Exists(ourPath))
            {
                return File.ReadAllText(ourPath, Encoding.UTF8);
            }

            using var src = typeof(InterfaceStubGenerator).Assembly.GetManifestResourceStream("Refit.Generator.GeneratedInterfaceStubTemplate.mustache");
            var ms = new MemoryStream();
            src.CopyTo(ms);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        public List<InterfaceDeclarationSyntax> FindInterfacesToGenerate(SyntaxTree tree)
        {
            var nodes = tree.GetRoot().DescendantNodes().ToList();

            // Make sure this file imports Refit. If not, we're not going to 
            // find any Refit interfaces
            // NB: This falls down in the tests unless we add an explicit "using Refit;",
            // but we can rely on this being there in any other file
            if (nodes.OfType<UsingDirectiveSyntax>().All(u => u.Name.ToFullString() != "Refit"))
                return new List<InterfaceDeclarationSyntax>();

            var list = nodes.OfType<InterfaceDeclarationSyntax>();

            return list
                .Where(i => i.DescendantsAndSelf(a => a.BaseList?.Types.Select(b => b.GetSimpleName())
                    .Where(b => b != null).Select(b => b.GetInterfaceDeclaration(list)))
                    .Any(b => b.Members.OfType<MethodDeclarationSyntax>().Any(HasRefitHttpMethodAttribute)))
                .ToList();
        }

        public ClassTemplateInfo GenerateClassInfoForInterface(InterfaceDeclarationSyntax interfaceTree)
        {
            var ret = new ClassTemplateInfo();
            var parent = interfaceTree.Parent;
            while (parent != null && !(parent is NamespaceDeclarationSyntax)) parent = parent.Parent;

            ret.InterfaceName = GetInterfaceName(interfaceTree.Identifier);
            ret.GeneratedClassSuffix = ret.InterfaceName.Replace(".", "");
            ret.Modifiers = interfaceTree.Modifiers.Select(t => t.ValueText).FirstOrDefault(m => m == "public" || m == "internal");
            ret.BaseClasses = interfaceTree.BaseList?.Types.Select(b => b.GetSimpleName()).Where(b => b != null)
                .Select(t => new BaseClassInfo
                {
                    Name = t.Identifier.ValueText,
                    TypeParametersInfo = (t is GenericNameSyntax g ? g.TypeArgumentList.Arguments.Select(a => a.GetTypeInfo()).ToList() : null)
                }).ToList();

            var ns = parent as NamespaceDeclarationSyntax;
            ret.Namespace = ns?.Name?.ToString() ?? $"AutoGenerated{ret.GeneratedClassSuffix}";

            if (interfaceTree.TypeParameterList != null)
            {
                var typeParameters = interfaceTree.TypeParameterList.Parameters;
                if (typeParameters.Any())
                {
                    ret.TypeParametersInfo = typeParameters.Select(p => p.Identifier.ValueText).ToList();
                }

                ret.ConstraintClauses = interfaceTree.ConstraintClauses.ToFullString().Trim();
            }

            var rootNode = interfaceTree.Parent;
            while (rootNode.Parent != null) rootNode = rootNode.Parent;

            var usingsInsideNamespace = ns?.DescendantNodes()
                            .OfType<UsingDirectiveSyntax>()
                            .Select(x => $"{x.Alias} {x.StaticKeyword} {x.Name}".TrimStart())
                            ?? Enumerable.Empty<string>();

            var usingsOutsideNamespace = rootNode.DescendantNodes(x => !x.IsKind(SyntaxKind.NamespaceDeclaration))
                            .OfType<UsingDirectiveSyntax>()
                            .Select(x =>
                            {
                                // Globally qualify namespace name to avoid conflicts when put inside namespace.
                                var name = x.Name.ToString();
                                var globallyQualifiedName = name.StartsWith("global::")
                                    ? name
                                    : "global::" + name;

                                return $"{x.Alias} {x.StaticKeyword} {globallyQualifiedName}".TrimStart();
                            });

            var usings = usingsInsideNamespace.Concat(usingsOutsideNamespace)
                            .Distinct()
                            .Where(x => x != "System" && x != "System.Net.Http" && x != "System.Collections.Generic" && x != "System.Linq")
                            .Select(x => new UsingDeclaration { Item = x });

            ret.UsingList = usings.ToList();

            ret.MethodList = interfaceTree.Members
                                          .OfType<MethodDeclarationSyntax>()
                                          .Select(x =>
                                          {
                                              var mti = new MethodTemplateInfo
                                              {
                                                  Name = x.Identifier.Text,
                                                  InterfaceName = ret.InterfaceName,
                                                  TypeParametersInfo = ret.TypeParametersInfo?.ToList(),
                                                  ReturnTypeInfo = x.ReturnType.GetTypeInfo(),
                                                  ArgumentListInfo = x.ParameterList.Parameters
                                                    .Select(a => new ArgumentInfo { Name = a.Identifier.Text, TypeInfo = a.Type.GetTypeInfo() })
                                                    .ToList(),
                                                  IsRefitMethod = HasRefitHttpMethodAttribute(x)
                                              };
                                              if (x.TypeParameterList != null)
                                              {
                                                  var typeParameters = x.TypeParameterList.Parameters;
                                                  if (typeParameters.Any())
                                                  {
                                                      mti.MethodTypeParameterListInfo = typeParameters.Select(p => p.Identifier.ValueText).ToList();
                                                  }
                                              }
                                              return mti;
                                          })
                                          .ToList();
            return ret;
        }

        public string GenerateInterfaceStubs(string[] paths)
        {
            var trees = paths.Select(x => CSharpSyntaxTree.ParseText(File.ReadAllText(x))).ToList();

            var interfacesToGenerate = trees.SelectMany(FindInterfacesToGenerate).ToList();

            var templateInfo = GenerateTemplateInfoForInterfaceList(interfacesToGenerate);

            GenerateWarnings(interfacesToGenerate);

            Encoders.HtmlEncode = s => s;
            var text = Render.StringToString(ExtractTemplateSource(), templateInfo);
            return text;
        }

        public TemplateInformation GenerateTemplateInfoForInterfaceList(List<InterfaceDeclarationSyntax> interfaceList)
        {
            interfaceList = interfaceList.OrderBy(i => i.Identifier.Text).ToList();

            var ret = new TemplateInformation
            {
                RefitInternalNamespace = RefitInternalNamespace ?? string.Empty,
                ClassList = interfaceList.Select(GenerateClassInfoForInterface).ToList(),
            };

            AddInheritedMethods(ret.ClassList);
            FixInheritedMethods(ret.ClassList);

            return ret;
        }

        void AddInheritedMethods(List<ClassTemplateInfo> allClassList)
        {
            allClassList.ForEach(classInfo => AddInheritedMethods(allClassList, classInfo));
        }

        void AddInheritedMethods(List<ClassTemplateInfo> allClassList, ClassTemplateInfo classInfo)
        {
            classInfo.BaseClasses?.ForEach(baseClass =>
            {
                var baseClassInfo = allClassList.SingleOrDefault(oc => oc.InterfaceName == baseClass.Name &&
                    oc.TypeParametersInfo?.Count == baseClass.TypeParametersInfo?.Count);

                if (baseClassInfo == null)
                    return;

                AddInheritedMethods(allClassList, baseClassInfo);

                var methodsToAdd = baseClassInfo.MethodList
                    .Where(a => !classInfo.MethodList.Any(b => b.InterfaceName == a.InterfaceName &&
                        b.TypeParametersInfo?.Count == a.TypeParametersInfo?.Count && b.Name == a.Name))
                    .Select(a => new MethodTemplateInfo
                    {
                        ArgumentListInfo = a.ArgumentListInfo?
                            .Select(b => new ArgumentInfo { Name = b.Name, TypeInfo = b.TypeInfo.Clone() })
                            .ToList(),
                        IsRefitMethod = a.IsRefitMethod,
                        Name = a.Name,
                        ReturnTypeInfo = a.ReturnTypeInfo.Clone(),
                        MethodTypeParameterListInfo = a.MethodTypeParameterListInfo?.ToList(),
                        InterfaceName = a.InterfaceName,
                        TypeParametersInfo = a.TypeParametersInfo?.ToList(),
                    });

                classInfo.MethodList.AddRange(methodsToAdd);
            });
        }

        void FixInheritedMethods(List<ClassTemplateInfo> allClassList)
        {
            allClassList.ForEach(classInfo => FixInheritedMethods(allClassList, classInfo));
        }

        List<MethodTemplateInfo> FixInheritedMethods(List<ClassTemplateInfo> allClassList, ClassTemplateInfo rootClassInfo, ClassTemplateInfo classInfo = null)
        {
            List<MethodTemplateInfo> outResult = null;

            (classInfo ?? rootClassInfo).BaseClasses?.ForEach(baseClass =>
            {
                var baseClassInfo = allClassList.SingleOrDefault(oc => oc.InterfaceName == baseClass.Name &&
                    oc.TypeParametersInfo?.Count == baseClass.TypeParametersInfo?.Count);

                if (baseClassInfo == null)
                    return;

                var baseMethods = FixInheritedMethods(allClassList, rootClassInfo, baseClassInfo);

                var parametersMap = baseClassInfo.TypeParametersInfo?.Select((a, i) =>
                    {
                        var typeInfo = baseClass.TypeParametersInfo[i];
                        return a != typeInfo.ToString() ? new { Key = a, Value = typeInfo } : null;
                    }).Where(a => a != null).ToDictionary(a => a.Key, a => a.Value);

                void replaceGenericTypes(TypeInfo typeInfo)
                {
                    if (parametersMap?.Count > 0)
                    {
                        foreach (var itemMap in parametersMap)
                        {
                            if (typeInfo.Name == itemMap.Key)
                            {
                                typeInfo.Name = itemMap.Value.Name;
                                typeInfo.Children = itemMap.Value.Children?.Select(a => a.Clone()).ToList();
                            }
                            else if (typeInfo.Children != null)
                            {
                                foreach (var item in typeInfo.Children)
                                {
                                    replaceGenericTypes(item);
                                }
                            }
                        }
                    }
                }

                string replaceGenericType(string typeInfo)
                {
                    if (parametersMap?.Count > 0)
                    {
                        if (parametersMap.TryGetValue(typeInfo, out var result))
                            return result.ToString();
                    }

                    return typeInfo;
                }

                var methods = rootClassInfo.MethodList.Where(m => m.InterfaceName == baseClassInfo.InterfaceName &&
                    m.TypeParametersInfo?.Count == baseClassInfo.TypeParametersInfo?.Count).ToList();
                if (baseMethods != null)
                    methods.AddRange(baseMethods);

                foreach (var m in methods)
                {
                    if (m.ArgumentListInfo != null)
                        foreach (var a in m.ArgumentListInfo)
                        {
                            replaceGenericTypes(a.TypeInfo);
                        }
                    replaceGenericTypes(m.ReturnTypeInfo);
                    m.TypeParametersInfo = m.TypeParametersInfo?.Select(b => replaceGenericType(b)).ToList();
                }

                outResult = methods;
            });

            return outResult;
        }

        public void GenerateWarnings(List<InterfaceDeclarationSyntax> interfacesToGenerate)
        {
            var missingAttributeWarnings = interfacesToGenerate
                                           .SelectMany(i => i.Members.OfType<MethodDeclarationSyntax>().Select(m => new
                                           {
                                               Interface = i,
                                               Method = m
                                           }))
                                           .Where(x => !HasRefitHttpMethodAttribute(x.Method))
                                           .Select(x => new MissingRefitAttributeWarning(x.Interface, x.Method));


            var diagnostics = missingAttributeWarnings;

            foreach (var diagnostic in diagnostics)
            {
                Log?.Invoke(diagnostic.ToString());
            }
        }

        public bool HasRefitHttpMethodAttribute(MethodDeclarationSyntax method)
        {
            // We could also verify that the single argument is a string, 
            // but what if somebody is dumb and uses a constant?
            // Could be turtles all the way down.
            return method.AttributeLists.SelectMany(a => a.Attributes)
                         .Any(a => HttpMethodAttributeNames.Contains(a.Name.ToString().Split('.').Last()) &&
                                   a.ArgumentList.Arguments.Count == 1 &&
                                   a.ArgumentList.Arguments[0].Expression.Kind() == SyntaxKind.StringLiteralExpression);
        }

        string GetInterfaceName(SyntaxToken identifier)
        {
            if (identifier == null) return "";
            var interfaceParent = identifier.Parent != null ? identifier.Parent.Parent : identifier.Parent;

            if ((interfaceParent as ClassDeclarationSyntax) != null)
            {
                var classParent = (interfaceParent as ClassDeclarationSyntax).Identifier;
                return classParent + "." + identifier.ValueText;
            }

            return identifier.ValueText;
        }
    }

    public class UsingDeclaration
    {
        public string Item { get; set; }
    }

    public class ClassTemplateInfo
    {
        public string ConstraintClauses { get; set; }
        public string GeneratedClassSuffix { get; set; }
        public string InterfaceName { get; set; }
        public List<BaseClassInfo> BaseClasses { get; set; }
        public List<MethodTemplateInfo> MethodList { get; set; }
        public bool HasAnyMethodsWithNullableArguments => MethodList.SelectMany(ml => ml.ArgumentListInfo).Any(y => y.TypeInfo.ToString().EndsWith("?"));
        public string Modifiers { get; set; }
        public string Namespace { get; set; }
        public List<string> TypeParametersInfo { get; set; }
        public string TypeParameters => TypeParametersInfo != null ? string.Join(", ", TypeParametersInfo) : null;
        public List<UsingDeclaration> UsingList { get; set; }
    }

    public class TypeInfo
    {
        public string Name { get; set; }
        public List<TypeInfo> Children { get; set; }

        public override string ToString()
        {
            return Name + (Children?.Count > 0 ? $"<{string.Join(", ", Children.Select(a => a.ToString()))}>" : null);
        }

        public TypeInfo Clone()
        {
            return CloneImpl() as TypeInfo;
        }

        protected virtual object CloneImpl()
        {
            return new TypeInfo
            {
                Name = Name,
                Children = Children?.Select(a => a.Clone()).ToList(),
            };
        }
    }

    public class BaseClassInfo
    {
        public string Name { get; set; }
        public List<TypeInfo> TypeParametersInfo { get; set; }
    }

    public class MethodTemplateInfo
    {
        public List<ArgumentInfo> ArgumentListInfo { get; set; }
        public string ArgumentList => ArgumentListInfo != null ? string.Join(", ", ArgumentListInfo.Select(y => y.Name)) : null;
        public string ArgumentListWithTypes => ArgumentListInfo != null ? string.Join(", ", ArgumentListInfo.Select(y => $"{y.TypeInfo} {y.Name}")) : null;
        public string ArgumentTypesList => ArgumentListInfo != null ? string.Join(", ", ArgumentListInfo.Select(y => y.TypeInfo.ToString() is var typeName && typeName.EndsWith("?") ? $"ToNullable(typeof({typeName.Remove(typeName.Length - 1)}))" : $"typeof({typeName})")) : null;
        public bool IsRefitMethod { get; set; }
        public string Name { get; set; }
        public TypeInfo ReturnTypeInfo { get; set; }
        public string ReturnType => ReturnTypeInfo.ToString();
        public List<string> MethodTypeParameterListInfo { get; set; }
        public string MethodTypeParameters => MethodTypeParameterListInfo != null ? string.Join(", ", MethodTypeParameterListInfo) : null;
        public string MethodTypeParameterList => MethodTypeParameterListInfo != null ? string.Join(", ", MethodTypeParameterListInfo.Select(p => $"typeof({p})")) : null;
        public string MethodTypeParameterNames => MethodTypeParameterListInfo != null ? $"{string.Join(", ", MethodTypeParameterListInfo.Select(p => $"{{typeof({p}).AssemblyQualifiedName}}"))}" : null;
        public string InterfaceName { get; set; }
        public List<string> TypeParametersInfo { get; set; }
        public string TypeParameters => TypeParametersInfo != null ? string.Join(", ", TypeParametersInfo) : null;
    }

    public class ArgumentInfo
    {
        public string Name { get; set; }
        public TypeInfo TypeInfo { get; set; }
    }

    public class TemplateInformation
    {
        public string RefitInternalNamespace { get; set; }
        public List<ClassTemplateInfo> ClassList;
    }

    static class InterfaceDeclarationExtension
    {
        public static IEnumerable<InterfaceDeclarationSyntax> DescendantsAndSelf(this InterfaceDeclarationSyntax interfaceDeclaration,
            Func<InterfaceDeclarationSyntax, IEnumerable<InterfaceDeclarationSyntax>> childrenFunc)
        {
            yield return interfaceDeclaration;

            var children = childrenFunc(interfaceDeclaration)?.Where(x => x != null).SelectMany(x => x.DescendantsAndSelf(childrenFunc));

            if (children != null)
            {
                foreach (var item in children)
                {
                    yield return item;
                }
            }
        }

        public static SimpleNameSyntax GetSimpleName(this BaseTypeSyntax baseType)
        {
            if (baseType is SimpleBaseTypeSyntax simpleBaseType && simpleBaseType.Type is SimpleNameSyntax simpleName)
                return simpleName;

            return null;
        }

        public static InterfaceDeclarationSyntax GetInterfaceDeclaration(this SimpleNameSyntax simpleName, IEnumerable<InterfaceDeclarationSyntax> list)
        {
            if (simpleName == null)
                return null;

            var result = list.FirstOrDefault(c => c.Identifier.ValueText == simpleName.Identifier.ValueText &&
                c.TypeParameterList?.Parameters.Count ==
                    (simpleName is GenericNameSyntax genericName ? genericName.TypeArgumentList.Arguments.Count : 0));

            return result;
        }

        public static TypeInfo GetTypeInfo(this TypeSyntax typeSyntax)
        {
            if (typeSyntax is GenericNameSyntax g)
                return new TypeInfo { Name = g.Identifier.ValueText, Children = g.TypeArgumentList.Arguments.Select(a => a.GetTypeInfo()).ToList() };
            else
                return new TypeInfo { Name = typeSyntax.ToString() };
        }
    }
}
