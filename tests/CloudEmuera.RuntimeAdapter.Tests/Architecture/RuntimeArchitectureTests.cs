using System.Reflection;
using CloudEmuera.Application.Abstractions;
using CloudEmuera.Domain.Sessions;
using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Architecture;

[Trait("Category", "Architecture")]
public sealed class RuntimeArchitectureTests
{
    [Fact]
    public void RuntimeAssembliesDoNotReferenceDesktopMediaOrApplicationProjects()
    {
        foreach (Assembly assembly in RuntimeAssemblies())
        {
            string[] references = assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name ?? string.Empty)
                .ToArray();

            foreach (string forbiddenReference in ForbiddenAssemblyReferences)
            {
                Assert.DoesNotContain(forbiddenReference, references, StringComparer.Ordinal);
            }

            Assert.DoesNotContain(references, name =>
                name.Contains("Worker", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("CloudEmuera.Api", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void RuntimePublicApiDoesNotLeakDesktopTypes()
    {
        // Application now intentionally exposes identity/application contracts. It is not a runtime
        // public API and is therefore excluded from the desktop-type surface assertion.
        foreach (Assembly assembly in RuntimeAssemblies().Where(assembly => assembly != typeof(IClock).Assembly))
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                Assert.False(ContainsDesktopType(type.BaseType), type.FullName);
                Assert.DoesNotContain(type.GetInterfaces(), ContainsDesktopType);

                foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (member is MethodInfo method)
                    {
                        Assert.False(ContainsDesktopType(method.ReturnType), $"{type.FullName}.{method.Name}");
                        Assert.All(method.GetParameters(), parameter =>
                            Assert.False(ContainsDesktopType(parameter.ParameterType), $"{type.FullName}.{method.Name}"));
                        Assert.DoesNotContain(method.GetGenericArguments(), ContainsDesktopType);
                    }
                    else if (member is ConstructorInfo constructor)
                    {
                        Assert.All(constructor.GetParameters(), parameter =>
                            Assert.False(ContainsDesktopType(parameter.ParameterType), $"{type.FullName}.{constructor.Name}"));
                    }
                    else if (member is PropertyInfo property)
                    {
                        Assert.False(ContainsDesktopType(property.PropertyType), $"{type.FullName}.{property.Name}");
                    }
                    else if (member is FieldInfo field)
                    {
                        Assert.False(ContainsDesktopType(field.FieldType), $"{type.FullName}.{field.Name}");
                    }
                    else if (member is EventInfo eventInfo)
                    {
                        Assert.False(ContainsDesktopType(eventInfo.EventHandlerType), $"{type.FullName}.{eventInfo.Name}");
                    }
                }
            }
        }
    }

    private static IReadOnlyList<Assembly> RuntimeAssemblies() =>
    [
        typeof(RuntimeBaseline).Assembly,
        typeof(SessionState).Assembly,
        typeof(IClock).Assembly
    ];

    private static readonly string[] ForbiddenAssemblyReferences =
    [
        "System.Drawing",
        "System.Drawing.Common",
        "System.Drawing.Primitives",
        "System.Windows.Forms",
        "PresentationFramework",
        "WindowsBase",
        "NAudio",
        "NAudio.Core",
        "NAudio.Wave",
        "Interop.WMPLib",
        "WMPLib",
        "CloudEmuera.Application"
    ];

    private static bool ContainsDesktopType(Type? type)
    {
        if (type is null)
        {
            return false;
        }

        if (type.IsArray || type.IsByRef || type.IsPointer)
        {
            return ContainsDesktopType(type.GetElementType()!);
        }

        if (type.IsGenericType && type.GetGenericArguments().Any(ContainsDesktopType))
        {
            return true;
        }

        string? namespaceName = type.Namespace;
        return IsForbiddenAssembly(type.Assembly.GetName().Name) ||
            namespaceName?.StartsWith("System.Drawing", StringComparison.Ordinal) == true ||
            namespaceName?.StartsWith("System.Windows.Forms", StringComparison.Ordinal) == true ||
            namespaceName?.StartsWith("PresentationFramework", StringComparison.Ordinal) == true ||
            namespaceName?.StartsWith("WindowsBase", StringComparison.Ordinal) == true ||
            namespaceName?.StartsWith("NAudio", StringComparison.Ordinal) == true;
    }

    private static bool IsForbiddenAssembly(string? name) =>
        name is not null && ForbiddenAssemblyReferences.Contains(name, StringComparer.Ordinal);
}
