using System.Reflection;
using Destiny2Report.API.Features.Crawler;

namespace Destiny2Report.Tests.TestSupport;

internal static class CrawlerReflection
{
    private static readonly Type CrawlerServiceType = typeof(CrawlerService);

    public static object? Invoke(string methodName, params object?[] arguments)
    {
        var method = CrawlerServiceType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Where(method => method.Name == methodName)
            .Single(method => method.GetParameters().Length == arguments.Length);

        return method.Invoke(null, arguments);
    }

    public static Type NestedType(string name)
    {
        return CrawlerServiceType.GetNestedType(name, BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Could not find CrawlerService nested type {name}.");
    }
}
