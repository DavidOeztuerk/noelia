using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Passwords;
using Noelia.Passwords.Argon2;

namespace Noelia.Passwords.Argon2.Probe;

public sealed class Argon2PackageTests
{
    [Fact]
    public void Argon2_module_hashes_and_verifies_without_retaining_the_password()
    {
        const string password = "ARGON2-PROBE-CANARY-PASSWORD";
        var host = Host.CreateApplicationBuilder();
        var noelia = new NoeliaBuilder(
            host.Services, host.Configuration, host.Environment, "argon2-probe", [], []);

        noelia.UseArgon2Passwords(new Argon2Cost(8, 1, 1));
        var composition = noelia.Build();
        using var provider = host.Services.BuildServiceProvider();
        var hasher = provider.GetRequiredKeyedService<IPasswordHasher>(PasswordHashing.PrimaryKey);
        var encoded = hasher.Hash(password);

        composition.Included.Should().Equal(Argon2NoeliaModule.Module);
        encoded.Should().StartWith("$argon2id$").And.NotContain(password);
        hasher.Verify(password, encoded).Should().Be(PasswordVerification.Success);
        hasher.Verify("wrong", encoded).Should().Be(PasswordVerification.Failed);
    }
}
