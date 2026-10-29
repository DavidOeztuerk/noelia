namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>Stub.</summary>
public sealed class ServiceClientSecret
{
    private ServiceClientSecret() { }
    public string ClientId => throw new NotImplementedException();
    public string Secret => throw new NotImplementedException();
    public string Hash => throw new NotImplementedException();
    public static ServiceClientSecret Generate(string clientId) => throw new NotImplementedException();
    public string ToEnvironment() => throw new NotImplementedException();
    public static string HashOf(string secret) => throw new NotImplementedException();
    public static bool IsWellFormed(string? secret) => throw new NotImplementedException();
}
