using System.Reflection;

namespace AppBlocks.Tests;

/// <summary>An interface answered by a function of the method called and its arguments, for a test that needs one or two members.</summary>
internal static class TestStubs
{
    public static T Stub<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Call = invoke; return proxy;
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args);
    }
}
